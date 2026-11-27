using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Conformance;

// Builds a CommandCatalog from a corpus catalog (grammar-corpus.json "catalog") with the public builder API, as an
// application declares its commands. A catalog member this factory does not know is a feature it does not map yet, so
// the case is unsupported rather than run against a catalog that lacks it.
internal sealed class CatalogFactory
{
    private static readonly HashSet<string> CatalogMembers = ["jsonOutputAlias", "globalOptions", "commands", "defaultCommand", "rootCommand"];
    private static readonly HashSet<string> CommandMembers = ["path", "aliases", "options", "arguments", "group", "help"];
    private static readonly HashSet<string> OptionMembers =
        ["id", "long", "aliases", "arity", "repeat", "required", "choices", "environmentVariable", "acceptsNegativeNumbers", "sensitive",
            "requires", "conflictsWith", "minimum", "maximum", "pathKind", "mustExist", "help"];
    private static readonly HashSet<string> ArgumentMembers =
        ["id", "arity", "choices", "acceptsNegativeNumbers", "sensitive", "minimum", "maximum", "pathKind", "mustExist", "trailing", "help"];
    // Help text (help.md "Corpus"): a command's or group's description, long description, examples and exit codes; an
    // option's or argument's description, value name and default value; and whether help hides it.
    private static readonly HashSet<string> HelpMembers = ["description", "longDescription", "examples", "exitCodes", "valueName", "defaultValue", "hidden"];

    private readonly Dictionary<string, CommandCatalog> _built = new(StringComparer.Ordinal);

    // The driver sends the same catalog with every case, so each distinct catalog is built once.
    public CommandCatalog Get(JsonElement catalog)
    {
        string key = catalog.GetRawText();
        if (!_built.TryGetValue(key, out CommandCatalog? built))
        {
            built = Build(catalog);
            _built.Add(key, built);
        }

        return built;
    }

    // Every command parses only; its handler succeeds with a result that writes nothing.
    public static CommandCatalog Build(JsonElement catalog) =>
        Build(catalog, AdapterCodec.Instance, () => CommandOutcome.Success(new AdapterResult()));

    // Every command's handler returns the outcome `outcome` creates, which `codec` writes.
    public static CommandCatalog Build<TResult>(JsonElement catalog, ICommandResultCodec<TResult> codec, Func<CommandOutcome<TResult>> outcome)
    {
        var commandFactory = new Commands<TResult>(codec, new ScriptedHandlerFactory<TResult>(outcome));
        RequireKnown(catalog, CatalogMembers, "catalog");
        var builder = new CommandCatalogBuilder();
        if (catalog.TryGetProperty("jsonOutputAlias", out JsonElement alias))
        {
            builder.JsonOutputAlias(alias.GetString()!);
        }

        if (catalog.TryGetProperty("globalOptions", out JsonElement globals))
        {
            foreach (JsonElement option in globals.EnumerateArray())
            {
                RequireKnown(option, OptionMembers, "global option");
                // grammar.md "Global options": never repeated, required or sensitive; a catalog that says otherwise is
                // not one the grammar defines.
                if (Flag(option, "required") || Flag(option, "sensitive") || Text(option, "repeat") != "error")
                {
                    throw new UnsupportedException("a global option that is repeated, required or sensitive");
                }

                builder.GlobalOption(Text(option, "id"), Text(option, "long"), Arity(option), ParameterHelp(option), Texts(option, "aliases"));
            }
        }

        JsonElement[] commands = [.. catalog.GetProperty("commands").EnumerateArray()];
        foreach (JsonElement command in commands)
        {
            RequireKnown(command, CommandMembers, "command");
        }

        foreach (JsonElement root in commands.Where(static command => Path(command).Length == 1))
        {
            string name = Path(root)[0];
            if (Flag(root, "group"))
            {
                if (Texts(root, "aliases").Length > 0 || root.GetProperty("options").GetArrayLength() > 0
                    || root.GetProperty("arguments").GetArrayLength() > 0)
                {
                    throw new UnsupportedException("a group with aliases, options or arguments");
                }

                // A help-only group: invoking it shows its help. Its subcommands are registered below it.
                builder.Group(name, CommandHelpOf(root) ?? new CommandHelp());
                foreach (JsonElement child in commands.Where(command => Path(command).Length == 2 && Path(command)[0] == name))
                {
                    if (Flag(child, "group"))
                    {
                        throw new UnsupportedException("a nested group");
                    }

                    commandFactory.Register(builder, name + " " + Path(child)[1], child, commands);
                }

                continue;
            }

            commandFactory.Register(builder, name, root, commands);
        }

        // grammar.md "Default and root commands": a catalog names at most one of them.
        bool hasDefault = catalog.TryGetProperty("defaultCommand", out JsonElement defaultCommand);
        bool hasRoot = catalog.TryGetProperty("rootCommand", out JsonElement rootCommand);
        if (hasDefault && hasRoot)
        {
            throw new UnsupportedException("a catalog with both a default and a root command");
        }

        if (hasDefault)
        {
            builder.DefaultCommand(defaultCommand.GetString()!);
        }

        if (hasRoot)
        {
            builder.RootCommand(rootCommand.GetString()!);
        }

        return builder.Build();
    }

    // Registers commands whose handlers return one outcome type through one codec.
    private sealed class Commands<TResult>(ICommandResultCodec<TResult> codec, ScriptedHandlerFactory<TResult> handlers)
    {
        public void Register(CommandCatalogBuilder builder, string path, JsonElement definition, JsonElement[] commands) =>
            builder.CommandPath<AdapterOptions, ScriptedHandler<TResult>, TResult>(path, command => Configure(command, definition, commands));

        private void Configure(
            CommandBuilder<AdapterOptions, ScriptedHandler<TResult>, TResult> builder,
            JsonElement definition,
            JsonElement[] commands)
        {
            builder.Alias(Texts(definition, "aliases"))
                .BindWith(AdapterBinder.Instance)
                .CreateHandlerWith(handlers)
                .Produces(codec);
            if (CommandHelpOf(definition) is { } commandHelp)
            {
                builder.WithHelp(commandHelp);
            }

            foreach (JsonElement option in definition.GetProperty("options").EnumerateArray())
            {
                RequireKnown(option, OptionMembers, "option");
                string id = Text(option, "id");
                string repeat = Text(option, "repeat");
                builder.Option(
                    id,
                    Text(option, "long"),
                    Arity(option),
                    repeat switch
                    {
                        "append" => CommandOptionRepeatPolicy.Append,
                        "error" => CommandOptionRepeatPolicy.Error,
                        _ => throw new UnsupportedException($"the repeat policy {repeat}"),
                    },
                    isSensitive: Flag(option, "sensitive"),
                    isRequired: Flag(option, "required"),
                    aliases: Texts(option, "aliases"));
                if (ParameterHelp(option) is { } help)
                {
                    builder.ParameterHelp(id, help);
                }
            }

            foreach (JsonElement argument in definition.GetProperty("arguments").EnumerateArray())
            {
                RequireKnown(argument, ArgumentMembers, "argument");
                string id = Text(argument, "id");
                if (Flag(argument, "trailing"))
                {
                    builder.TrailingArguments(id, id, isSensitive: Flag(argument, "sensitive"));
                }
                else
                {
                    builder.Argument(id, id, Arity(argument), isSensitive: Flag(argument, "sensitive"));
                }

                if (ParameterHelp(argument) is { } help)
                {
                    builder.ParameterHelp(id, help);
                }
            }

            string[] path = Path(definition);
            foreach (JsonElement child in commands.Where(
                command => Path(command).Length == path.Length + 1 && Path(command).AsSpan(0, path.Length).SequenceEqual(path)))
            {
                if (Flag(child, "group"))
                {
                    throw new UnsupportedException("a nested group");
                }

                builder.Subcommand<AdapterOptions, ScriptedHandler<TResult>, TResult>(Path(child)[^1], command => Configure(command, child, commands));
            }
        }
    }

    // Choices, an environment fallback, negative numbers, relations, bounds, a path kind and help text are declared as
    // an option's or argument's help, or null when it declares none of them.
    private static readonly string[] ParameterHelpMembers =
        ["choices", "environmentVariable", "acceptsNegativeNumbers", "requires", "conflictsWith", "minimum", "maximum", "pathKind", "mustExist", "help"];

    private static CommandHelp? ParameterHelp(JsonElement definition)
    {
        if (!ParameterHelpMembers.Any(member => definition.TryGetProperty(member, out _)))
        {
            return null;
        }

        JsonElement text = HelpText(definition, "option or argument");
        string[]? choices = definition.TryGetProperty("choices", out _) ? Texts(definition, "choices") : null;
        string? variable = definition.TryGetProperty("environmentVariable", out JsonElement name) ? name.GetString() : null;
        return new CommandHelp(
            OptionalText(text, "description"),
            OptionalText(text, "valueName"),
            OptionalText(text, "defaultValue"),
            choices,
            acceptsNegativeNumbers: Flag(definition, "acceptsNegativeNumbers"),
            environmentVariable: variable)
        {
            Hidden = Flag(text, "hidden"),
            Requires = Texts(definition, "requires"),
            ConflictsWith = Texts(definition, "conflictsWith"),
            Minimum = Number(definition, "minimum"),
            Maximum = Number(definition, "maximum"),
            PathKind = definition.TryGetProperty("pathKind", out JsonElement kind) ? kind.GetString() switch
            {
                "file" => CommandPathKind.File,
                "directory" => CommandPathKind.Directory,
                var other => throw new UnsupportedException($"the path kind {other}"),
            } : CommandPathKind.None,
            MustExist = Flag(definition, "mustExist"),
        };
    }

    // A command's or group's help, or null when it declares none.
    private static CommandHelp? CommandHelpOf(JsonElement definition)
    {
        if (!definition.TryGetProperty("help", out _))
        {
            return null;
        }

        JsonElement text = HelpText(definition, "command");
        return new CommandHelp(OptionalText(text, "description"), examples: Texts(text, "examples"))
        {
            LongDescription = OptionalText(text, "longDescription"),
            ExitCodes = text.TryGetProperty("exitCodes", out JsonElement codes)
                ? [.. codes.EnumerateArray().Select(static code => new CommandExitCodeHelp(code.GetProperty("code").GetInt32(), Text(code, "description")))]
                : [],
            Hidden = Flag(text, "hidden"),
        };
    }

    // The `help` member, or an empty object when there is none.
    private static JsonElement HelpText(JsonElement definition, string what)
    {
        if (!definition.TryGetProperty("help", out JsonElement help))
        {
            return EmptyObject;
        }

        RequireKnown(help, HelpMembers, $"{what} help");
        return help;
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetDouble() : null;

    private static void RequireKnown(JsonElement element, HashSet<string> known, string what)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                throw new UnsupportedException($"the {what} member {property.Name}");
            }
        }
    }

    private static string[] Path(JsonElement command) => Texts(command, "path");

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new InvalidOperationException($"{name} is null.");

    private static string[] Texts(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? [.. value.EnumerateArray().Select(static item => item.GetString()!)] : [];

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.GetBoolean();

    private static CommandArity Arity(JsonElement element)
    {
        JsonElement arity = element.GetProperty("arity");
        JsonElement maximum = arity.GetProperty("maximum");
        return new CommandArity(
            arity.GetProperty("minimum").GetInt32(),
            maximum.ValueKind == JsonValueKind.String ? null : maximum.GetInt32());
    }
}

// The commands of a corpus catalog are only parsed, never run; these satisfy the builder.
internal sealed record AdapterOptions;

internal sealed record AdapterResult;

internal sealed class AdapterBinder : ICommandOptionsBinder<AdapterOptions>
{
    public static readonly AdapterBinder Instance = new();

    public ValueTask<CommandOutcome<AdapterOptions>> BindAsync(ParsedInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(CommandOutcome.Success(new AdapterOptions()));
}

internal sealed class ScriptedHandler<TResult>(Func<CommandOutcome<TResult>> outcome) : ICommandHandler<AdapterOptions, TResult>
{
    public ValueTask<CommandOutcome<TResult>> ExecuteAsync(
        AdapterOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(outcome());
}

internal sealed class ScriptedHandlerFactory<TResult>(Func<CommandOutcome<TResult>> outcome) : ICommandHandlerFactory<ScriptedHandler<TResult>>
{
    public ScriptedHandler<TResult> Create(IServiceProvider services) => new(outcome);
}

internal sealed class AdapterCodec : ICommandResultCodec<AdapterResult>
{
    public static readonly AdapterCodec Instance = new();

    public string PayloadType => "conformance.result/1";

    public JsonTypeInfo<AdapterResult> TypeInfo => PayloadJsonContext.Default.AdapterResult;

    public ValueTask WriteHumanAsync(AdapterResult value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
