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
    private static readonly HashSet<string> CatalogMembers = ["jsonOutputAlias", "globalOptions", "commands"];
    private static readonly HashSet<string> CommandMembers = ["path", "aliases", "options", "arguments", "group"];
    private static readonly HashSet<string> OptionMembers =
        ["id", "long", "aliases", "arity", "repeat", "required", "choices", "environmentVariable", "acceptsNegativeNumbers"];
    private static readonly HashSet<string> ArgumentMembers = ["id", "arity", "acceptsNegativeNumbers"];

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

    private static CommandCatalog Build(JsonElement catalog)
    {
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
                if (option.TryGetProperty("required", out _) || option.TryGetProperty("choices", out _)
                    || option.TryGetProperty("environmentVariable", out _) || option.TryGetProperty("acceptsNegativeNumbers", out _)
                    || Text(option, "repeat") != "error")
                {
                    throw new UnsupportedException("a global option with more than an id, spelling, aliases and arity");
                }

                builder.GlobalOption(Text(option, "id"), Text(option, "long"), Arity(option), null, Texts(option, "aliases"));
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
                    || root.GetProperty("arguments").GetArrayLength() > 0
                    || commands.Any(command => Path(command).Length > 1 && Path(command)[0] == name))
                {
                    throw new UnsupportedException("a group with aliases, options, arguments or subcommands");
                }

                // A help-only group: invoking it shows its help.
                builder.Group(name, new CommandHelp());
                continue;
            }

            builder.Command<AdapterOptions, AdapterHandler, AdapterResult>(name, command => Configure(command, root, commands));
        }

        return builder.Build();
    }

    private static void Configure(
        CommandBuilder<AdapterOptions, AdapterHandler, AdapterResult> builder,
        JsonElement definition,
        JsonElement[] commands)
    {
        builder.Alias(Texts(definition, "aliases"))
            .BindWith(AdapterBinder.Instance)
            .CreateHandlerWith(AdapterHandlerFactory.Instance)
            .Produces(AdapterCodec.Instance);

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
                isRequired: Flag(option, "required"),
                aliases: Texts(option, "aliases"));
            string[]? choices = option.TryGetProperty("choices", out JsonElement value) ? Texts(option, "choices") : null;
            string? variable = option.TryGetProperty("environmentVariable", out JsonElement name) ? name.GetString() : null;
            bool negative = Flag(option, "acceptsNegativeNumbers");
            if (choices is not null || variable is not null || negative)
            {
                builder.ParameterHelp(id, new CommandHelp(choices: choices, acceptsNegativeNumbers: negative, environmentVariable: variable));
            }
        }

        foreach (JsonElement argument in definition.GetProperty("arguments").EnumerateArray())
        {
            RequireKnown(argument, ArgumentMembers, "argument");
            string id = Text(argument, "id");
            builder.Argument(id, id, Arity(argument));
            if (Flag(argument, "acceptsNegativeNumbers"))
            {
                builder.ParameterHelp(id, new CommandHelp(acceptsNegativeNumbers: true));
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

            builder.Subcommand<AdapterOptions, AdapterHandler, AdapterResult>(Path(child)[^1], command => Configure(command, child, commands));
        }
    }

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

internal sealed class AdapterHandler : ICommandHandler<AdapterOptions, AdapterResult>
{
    public ValueTask<CommandOutcome<AdapterResult>> ExecuteAsync(
        AdapterOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CommandOutcome.Success(new AdapterResult()));
}

internal sealed class AdapterHandlerFactory : ICommandHandlerFactory<AdapterHandler>
{
    public static readonly AdapterHandlerFactory Instance = new();

    public AdapterHandler Create(IServiceProvider services) => new();
}

internal sealed class AdapterCodec : ICommandResultCodec<AdapterResult>
{
    public static readonly AdapterCodec Instance = new();

    public string PayloadType => "conformance.result/1";

    public JsonTypeInfo<AdapterResult> TypeInfo => PayloadJsonContext.Default.AdapterResult;

    public ValueTask WriteHumanAsync(AdapterResult value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
