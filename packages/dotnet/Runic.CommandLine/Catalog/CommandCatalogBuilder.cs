using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization.Metadata;

namespace Runic.CommandLine;

/// <summary>Builds and validates an immutable command catalog.</summary>
public sealed class CommandCatalogBuilder
{
    private readonly List<CommandBuilderNode> _commands = [];
    private string? _defaultCommandName;
    private bool _defaultCommandIsRoot;
    private string? _jsonOutputAlias;
    private JsonOutputShape _jsonOutputAliasShape;
    private string? _completionOption;
    private OutputSelectorDeclaration? _outputSelector;
    private bool _responseFiles;
    private readonly List<Action<CommandBuilderNode>> _globalOptions = [];

    /// <summary>Adds an option to every command. It may also precede the command path.</summary>
    public CommandCatalogBuilder GlobalOption(string id, string name, CommandArity arity, CommandHelp? help = null, params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        if (!((arity.Minimum == 0 && arity.Maximum == 0) || (arity.Minimum == 1 && arity.Maximum == 1)))
            throw new ArgumentException("Global options must be flags or single required values.", nameof(arity));
        string[] copy = (string[])aliases.Clone();
        _globalOptions.Add(node => node.AddGlobalOption(id, name, arity, help, copy));
        return this;
    }

    /// <summary>Selects a registered root command to receive tokens without a leading command name.</summary>
    public CommandCatalogBuilder DefaultCommand(string name)
    {
        _defaultCommandName = name ?? throw new ArgumentNullException(nameof(name));
        _defaultCommandIsRoot = false;
        return this;
    }

    /// <summary>
    /// Selects a registered root command as the application's unnamed root command, for an application whose
    /// grammar is that command: <c>tool --dry-run home switch</c> rather than <c>tool run --dry-run home switch</c>.
    /// </summary>
    /// <param name="name">The registered command's name.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// The command receives every command line that does not start with another root command, as a
    /// <see cref="DefaultCommand(string)"/> does, but its name is not a command spelling: <c>tool run</c> passes
    /// <c>run</c> to it as an argument. Root help (<c>tool --help</c>, <c>tool help</c>) is the command's help,
    /// with its description, arguments, options and examples, and lists the application's other commands. The
    /// name remains the command's identity in JSON output and in <see cref="ParsedInvocation.Path"/>.
    /// </para>
    /// <para>
    /// <see cref="Build"/> reports <c>RCLI0018</c> unless the name is a registered command without aliases or
    /// subcommands. This replaces an earlier <see cref="DefaultCommand(string)"/> call, and that call replaces it.
    /// </para>
    /// </remarks>
    public CommandCatalogBuilder RootCommand(string name)
    {
        _defaultCommandName = name ?? throw new ArgumentNullException(nameof(name));
        _defaultCommandIsRoot = true;
        return this;
    }

    /// <summary>
    /// Declares the long option that selects human or JSON output, replacing the default <c>--format</c>, and
    /// optionally hidden aliases that keep former spellings working: <c>OutputSelector("--format", "--output")</c>
    /// accepts <c>tool list --output json</c> as <c>tool list --format json</c>.
    /// </summary>
    /// <param name="name">A long option spelling such as <c>--format</c>.</param>
    /// <param name="hiddenAliases">
    /// Further long option spellings that parse exactly like the selector: with a value, separated or after
    /// <c>=</c>, wherever the selector is recognized (before <c>--</c>, also before the command). Help, completion
    /// and suggestions do not show them, and diagnostics about the value name the selector.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A spelling is not a long option spelling (<c>--</c>, a lowercase letter, then lowercase letters, digits or
    /// hyphens), is <c>--help</c> or <c>--version</c>, or is given twice.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The selector and its aliases select the same thing, so giving two different spellings is a usage error
    /// (<c>RCLI1016</c>), as is giving one spelling twice (<c>RCLI1007</c>). <c>RUNIC_COMMANDLINE_OUTPUT</c> is
    /// unchanged.
    /// </para>
    /// <para>
    /// <see cref="Build"/> reports <c>RCLI0026</c> for a command option spelled like the selector or one of its
    /// aliases, including the default <c>--format</c> when no selector is declared. <see cref="CommandCatalog.Combine"/>
    /// reports it when catalogs declare different selectors; a catalog that declares none takes the declared one.
    /// </para>
    /// </remarks>
    public CommandCatalogBuilder OutputSelector(string name, params IEnumerable<string> hiddenAliases)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(hiddenAliases);
        RequireFrameworkSpelling(name, nameof(name));
        var aliases = new List<string>();
        foreach (string alias in hiddenAliases)
        {
            RequireFrameworkSpelling(alias, nameof(hiddenAliases));
            if (alias == name || aliases.Contains(alias))
                throw new ArgumentException($"The output selector spelling '{alias}' is given more than once.", nameof(hiddenAliases));
            aliases.Add(alias);
        }

        _outputSelector = new OutputSelectorDeclaration(name, aliases.AsReadOnly());
        return this;
    }

    private static void RequireFrameworkSpelling(string? spelling, string parameterName)
    {
        if (!ParseSettings.IsLongOptionSpelling(spelling) || spelling is "--help" or "--version")
        {
            throw new ArgumentException(
                "An output selector spelling must be a long option spelling other than --help and --version.",
                parameterName);
        }
    }

    /// <summary>
    /// Declares a flag that stands for the output selector with the value <c>json</c>, so that an application that
    /// already has a <c>--json</c> flag keeps its users' command lines: <c>tool list --json</c> is
    /// <c>tool list --format json</c>. The flag takes no value, is recognized wherever the selector is (before
    /// <c>--</c>, also before the command), and appears in help and completions. Giving both the flag and the
    /// selector is a usage error (<c>RCLI1016</c>), as is giving the flag twice (<c>RCLI1007</c>).
    /// </summary>
    /// <param name="name">A long option spelling such as <c>--json</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// The name is not a long option spelling (<c>--</c>, a lowercase letter, then lowercase letters, digits or
    /// hyphens), or is <c>--help</c> or <c>--version</c>.
    /// </exception>
    /// <remarks>
    /// <see cref="Build"/> reports <c>RCLI0024</c> for a command option spelled like the flag, and for a flag
    /// spelled like the output selector or one of its aliases.
    /// </remarks>
    public CommandCatalogBuilder JsonOutputAlias(string name) => JsonOutputAlias(name, JsonOutputShape.Envelope);

    /// <summary>
    /// Declares a flag that stands for the output selector with the value <c>json</c>, as
    /// <see cref="JsonOutputAlias(string)"/> does, and selects what the flag writes. With
    /// <see cref="JsonOutputShape.Payload"/>, an application keeps an existing <c>--json</c> contract whose
    /// consumers read the bare document rather than a <c>runic.commandline/1</c> envelope.
    /// </summary>
    /// <param name="name">A long option spelling such as <c>--json</c>.</param>
    /// <param name="shape">What the flag writes.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The name is not a valid alias, as for <see cref="JsonOutputAlias(string)"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shape"/> is not defined.</exception>
    /// <remarks>
    /// <para>
    /// With <see cref="JsonOutputShape.Payload"/>, a command invoked with the flag writes, to standard output, its
    /// success payload serialized with the result codec's JSON metadata and followed by one LF, and nothing else.
    /// A command whose result is <see cref="CommandUnit"/> writes nothing. A failed or cancelled command writes the
    /// payload of its <see cref="CommandOutcome{T}.FailureData"/> the same way, or nothing when it declared none;
    /// its diagnostics are human text on standard error, followed by its <see cref="CommandOutcome{T}.HumanOutput"/>
    /// when it supplied one and by the <c>CODE: message</c> fault line otherwise. The exit code is the one
    /// the envelope reports. Help, version and usage errors, including a usage error on a command line that gives
    /// the flag, are presented as human text, so <c>tool --bogus --json</c> writes its error to standard error.
    /// </para>
    /// <para>
    /// The output selector (<c>--format json</c>) and <c>RUNIC_COMMANDLINE_OUTPUT=json</c> still write the
    /// envelope, so machine consumers that read the protocol are unaffected. A payload is not bounded by the
    /// protocol's frame limit; declared failure data keeps its own bound
    /// (<see cref="CommandFailureData.MaximumPayloadBytes"/>), because the same outcome can also be written as an
    /// envelope.
    /// </para>
    /// </remarks>
    public CommandCatalogBuilder JsonOutputAlias(string name, JsonOutputShape shape)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ParseSettings.IsLongOptionSpelling(name) || name is "--help" or "--version")
        {
            throw new ArgumentException(
                "The JSON output alias must be a long option spelling other than --help and --version.",
                nameof(name));
        }

        if (!Enum.IsDefined(shape))
        {
            throw new ArgumentOutOfRangeException(nameof(shape));
        }

        _jsonOutputAlias = name;
        _jsonOutputAliasShape = shape;
        return this;
    }

    /// <summary>
    /// Declares an option that writes a shell completion script, such as <c>--completions</c>, for an application
    /// whose users already run <c>tool --completions bash</c>. It replaces the built-in <c>completion</c> command,
    /// which then is neither recognized nor listed, so the application's own commands and arguments keep the word.
    /// </summary>
    /// <param name="name">A long option spelling such as <c>--completions</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// The name is not a long option spelling (<c>--</c>, a lowercase letter, then lowercase letters, digits or
    /// hyphens), or is <c>--help</c> or <c>--version</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The option is recognized as the first argument, with the shell as its value: <c>--completions bash</c> or
    /// <c>--completions=bash</c>, and nothing after it. The shells are those of
    /// <see cref="CommandCompletion.Generate(CommandCatalog, string, string)"/>. A missing or unknown shell, or any
    /// further argument, is a usage error (exit code 2) with the message of an unknown shell. The option appears
    /// in root help and in the generated completions.
    /// </para>
    /// <para>
    /// <see cref="Build"/> reports <c>RCLI0025</c> for a command option spelled like it, and for an option spelled
    /// like the JSON output alias, the output selector or one of its aliases.
    /// </para>
    /// </remarks>
    public CommandCatalogBuilder CompletionOption(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ParseSettings.IsLongOptionSpelling(name) || name is "--help" or "--version")
        {
            throw new ArgumentException(
                "The completion option must be a long option spelling other than --help and --version.",
                nameof(name));
        }

        _completionOption = name;
        return this;
    }

    /// <summary>
    /// Reads arguments that start with <c>@</c> from response files: <c>tool @build.rsp</c> runs as if the arguments
    /// in <c>build.rsp</c> had been given in its place. Off unless declared, because an argument may legitimately
    /// start with <c>@</c>.
    /// </summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="CommandApp"/> and <see cref="Hosting.CommandLineHostingAdapter"/> expand the arguments before
    /// parsing, so a response file may hold commands, options, values and the output selector. A response file is
    /// UTF-8, with or without a byte order mark. Arguments are separated by white space, including line breaks, and
    /// quoted as on the Windows command line: <c>"two words"</c> is one argument, a backslash is literal unless it
    /// precedes a double quote, and <c>"</c> is a literal quote. A line whose first non-blank character is
    /// <c>#</c> is a comment. An argument in a response file that starts with <c>@</c> names another response file,
    /// relative to the directory of the file that names it; files nest at most 16 levels deep. A path on the
    /// command line is relative to the current directory. <c>@@</c> stands for a literal leading <c>@</c>
    /// (<c>@@scope</c> is the argument <c>@scope</c>), and <c>@</c> on its own is an ordinary argument.
    /// </para>
    /// <para>
    /// Expansion is bounded: a file holds at most 1 MiB, and all response files of one command line together at
    /// most 4 MiB and 65,536 arguments. A file past a bound, or one that is not seekable, such as a FIFO, a pipe or a
    /// terminal, is unreadable.
    /// A response file that cannot be read (<c>RCLI1017</c>), is not valid UTF-8 (<c>RCLI1018</c>), has an
    /// unterminated quote (<c>RCLI1019</c>), is nested too deeply (<c>RCLI1020</c>) or includes itself
    /// (<c>RCLI1021</c>) is a usage error (exit code 2) at the command-line argument that named it. Help lists
    /// <c>@&lt;file&gt;</c> with the options, and the generated completions complete a file name after <c>@</c>.
    /// <see cref="ICommandSyntaxAdapter.Parse"/> takes arguments as given and never reads response files.
    /// </para>
    /// </remarks>
    public CommandCatalogBuilder ResponseFiles()
    {
        _responseFiles = true;
        return this;
    }

    /// <summary>Adds a typed root command.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Command<TOptions, THandler, TResult>(string name)
        where THandler : notnull, ICommandHandler<TOptions, TResult>
    {
        var command = new CommandBuilder<TOptions, THandler, TResult>(name);
        _commands.Add(command.Node);
        return command;
    }

    /// <summary>Adds and configures a typed root command.</summary>
    public CommandCatalogBuilder Command<TOptions, THandler, TResult>(
        string name,
        Action<CommandBuilder<TOptions, THandler, TResult>> configure)
        where THandler : notnull, ICommandHandler<TOptions, TResult>
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Command<TOptions, THandler, TResult>(name));
        return this;
    }

    /// <summary>Registers a command at a space-separated path, creating help-only parent groups.</summary>
    public CommandCatalogBuilder CommandPath<TOptions, THandler, TResult>(string path, Action<CommandBuilder<TOptions, THandler, TResult>> configure)
        where THandler : notnull, ICommandHandler<TOptions, TResult>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configure);
        string[] segments = path.Split(' ');
        List<CommandBuilderNode> siblings = _commands;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            CommandBuilderNode? parent = siblings.Find(node => node.Name == segments[i]);
            if (parent is null) { parent = new CommandGroupBuilderNode(segments[i]); siblings.Add(parent); }
            siblings = parent.Children;
        }
        var command = new CommandBuilder<TOptions, THandler, TResult>(segments[^1]);
        configure(command);
        siblings.Add(command.Node);
        return this;
    }

    /// <summary>
    /// Describes the help-only group at a space-separated path, such as <c>config</c> for a <c>config show</c>
    /// command, creating it when no command has been registered below it yet.
    /// </summary>
    /// <exception cref="ArgumentException">The path names a command rather than a group.</exception>
    public CommandCatalogBuilder Group(string path, CommandHelp help, string? descriptionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(help);
        List<CommandBuilderNode> siblings = _commands;
        CommandBuilderNode? node = null;
        foreach (string segment in path.Split(' '))
        {
            if (node is not null and not CommandGroupBuilderNode) throw new ArgumentException("A group path must not pass through a command.", nameof(path));
            node = siblings.Find(candidate => candidate.Name == segment);
            if (node is null) { node = new CommandGroupBuilderNode(segment); siblings.Add(node); }
            siblings = node.Children;
        }
        if (node is not CommandGroupBuilderNode) throw new ArgumentException("The path names a command, not a help-only group.", nameof(path));
        node.Help = help;
        if (descriptionKey is not null) node.SetDescription(descriptionKey);
        return this;
    }

    /// <summary>Registers human rendering for an existing generated or manual command without replacing its JSON codec.</summary>
    public CommandCatalogBuilder Present<T>(string path, CommandHumanPresenter<T> presenter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(presenter);
        FindPresentedCommand(path).HumanPresenter = presenter;
        return this;
    }

    /// <summary>
    /// Registers human output for the whole outcome of an existing generated or manual command, success or failure.
    /// The presenter runs only in human mode, in place of the application's outcome sink; JSON output and the
    /// command's result codec are unchanged.
    /// </summary>
    /// <typeparam name="T">The command's result type.</typeparam>
    /// <param name="path">The canonical path of a registered command.</param>
    /// <param name="presenter">Writes the outcome; <see cref="CommandOutcomePresentation{T}.WriteDefaultAsync"/> writes the default output.</param>
    /// <exception cref="ArgumentException">The path does not name a registered command.</exception>
    public CommandCatalogBuilder PresentOutcome<T>(string path, CommandOutcomePresenter<T> presenter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(presenter);
        FindPresentedCommand(path).OutcomePresenter = presenter;
        return this;
    }

    private CommandBuilderNode FindPresentedCommand(string path)
    {
        List<CommandBuilderNode> siblings = _commands;
        CommandBuilderNode? node = null;
        foreach (string segment in path.Split(' '))
        {
            node = siblings.Find(candidate => candidate.Name == segment);
            if (node is null) throw new ArgumentException("The presentation path must name a registered command.", nameof(path));
            siblings = node.Children;
        }
        if (node is CommandGroupBuilderNode) throw new ArgumentException("A help-only group has no result to present.", nameof(path));
        return node!;
    }

    /// <summary>Validates and freezes all registered definitions.</summary>
    public CommandCatalog Build()
    {
        foreach (CommandBuilderNode command in _commands) AddGlobals(command);
        var issues = new List<CommandCatalogIssue>();
        ValidateSiblings(_commands, "root", issues);

        foreach (CommandBuilderNode command in _commands)
        {
            command.Validate(command.Name, issues);
        }

        if (issues.Count != 0)
        {
            throw new CommandCatalogValidationException(CommandDescriptor.Freeze(issues));
        }

        var descriptors = new List<CommandDescriptor>(_commands.Count);
        foreach (CommandBuilderNode command in _commands)
        {
            descriptors.Add(command.Freeze());
        }

        CommandCatalog.ValidateFrameworkOptions(_outputSelector, _jsonOutputAlias, _completionOption, descriptors, issues);
        if (issues.Count != 0) throw new CommandCatalogValidationException(CommandDescriptor.Freeze(issues));

        CommandDescriptor? defaultCommand = null;
        if (_defaultCommandName is not null)
        {
            defaultCommand = descriptors.Find(command => string.Equals(command.Name, _defaultCommandName, StringComparison.Ordinal));
            if (defaultCommand is null)
            {
                throw new CommandCatalogValidationException([new CommandCatalogIssue("RCLI0018", "root", $"The default command '{_defaultCommandName}' must name a registered root command.")]);
            }

            if (_defaultCommandIsRoot && (defaultCommand.Aliases.Count > 0 || defaultCommand.Subcommands.Count > 0))
            {
                throw new CommandCatalogValidationException([new CommandCatalogIssue("RCLI0018", defaultCommand.Name, $"The root command '{_defaultCommandName}' has no spelling, so it cannot have aliases or subcommands.")]);
            }
        }
        return new CommandCatalog(CommandDescriptor.Freeze(descriptors), defaultCommand, _jsonOutputAlias, _jsonOutputAliasShape, _defaultCommandIsRoot, _completionOption, _outputSelector, _responseFiles);
    }

    private void AddGlobals(CommandBuilderNode node)
    {
        foreach (Action<CommandBuilderNode> add in _globalOptions) add(node);
        foreach (CommandBuilderNode child in node.Children) AddGlobals(child);
    }

    internal static void ValidateSiblings(
        IReadOnlyList<CommandBuilderNode> commands,
        string location,
        List<CommandCatalogIssue> issues)
    {
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CommandBuilderNode command in commands)
        {
            RegisterSpelling(command.Name, command.Name);
            foreach (string alias in command.Aliases)
            {
                RegisterSpelling(alias, command.Name);
            }
        }

        void RegisterSpelling(string spelling, string owner)
        {
            if (!spellings.TryAdd(spelling, owner))
            {
                issues.Add(new CommandCatalogIssue(
                    "RCLI0004",
                    location,
                    $"Command spelling '{spelling}' is registered more than once."));
            }
        }
    }
}

/// <summary>Configures one closed typed command registration.</summary>
public sealed class CommandBuilder<TOptions, THandler, TResult>
    where THandler : notnull, ICommandHandler<TOptions, TResult>
{
    private readonly TypedCommandBuilderNode<TOptions, THandler, TResult> _node;

    internal CommandBuilder(string name)
    {
        _node = new TypedCommandBuilderNode<TOptions, THandler, TResult>(name);
    }

    internal CommandBuilderNode Node => _node;

    /// <summary>Registers the closed options binder.</summary>
    public CommandBuilder<TOptions, THandler, TResult> BindWith(ICommandOptionsBinder<TOptions> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        _node.Binder = binder;
        return this;
    }

    /// <summary>Registers the closed handler factory.</summary>
    public CommandBuilder<TOptions, THandler, TResult> CreateHandlerWith(ICommandHandlerFactory<THandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _node.HandlerFactory = factory;
        return this;
    }

    /// <summary>Registers the closed result codec.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Produces(ICommandResultCodec<TResult> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _node.ResultCodec = codec;
        return this;
    }

    /// <summary>Adds a parser-neutral option.</summary>
    /// <param name="id">The stable option identifier.</param>
    /// <param name="name">The long option spelling, such as <c>--name</c>.</param>
    /// <param name="arity">How many values the option takes.</param>
    /// <param name="repeatPolicy">What happens when the option is given more than once.</param>
    /// <param name="isSensitive">Whether values are redacted from help, completion, diagnostics and suggestions.</param>
    /// <param name="descriptionKey">An optional localization key for the description.</param>
    /// <param name="isRequired">Whether the option must be given (or supplied by its environment variable).</param>
    /// <param name="aliases">Further spellings, such as <c>-n</c>.</param>
    /// <returns>This builder.</returns>
    public CommandBuilder<TOptions, THandler, TResult> Option(
        string id,
        string name,
        CommandArity arity,
        CommandOptionRepeatPolicy repeatPolicy = CommandOptionRepeatPolicy.Error,
        bool isSensitive = false,
        string? descriptionKey = null,
        bool isRequired = false,
        params string[] aliases)
    {
        _node.AddOption(id, name, arity, repeatPolicy, isSensitive, descriptionKey, isRequired, aliases);
        return this;
    }

    /// <summary>Adds a positional argument.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Argument(
        string id,
        string name,
        CommandArity arity,
        bool isSensitive = false,
        string? descriptionKey = null)
    {
        _node.AddArgument(id, name, arity, isSensitive, descriptionKey);
        return this;
    }

    /// <summary>
    /// Adds an argument that receives every token after <c>--</c>, for example native arguments the command forwards
    /// to another tool. Positional arguments then receive only the tokens before <c>--</c>, so forwarded tokens
    /// never mix with the command's own words or fail their choices.
    /// </summary>
    /// <param name="id">The stable identifier used by binders.</param>
    /// <param name="name">The help-facing name.</param>
    /// <param name="isSensitive">Whether the values must be redacted from diagnostics.</param>
    /// <param name="descriptionKey">The optional localization key for help text.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The command already has trailing arguments.</exception>
    /// <remarks>
    /// The argument accepts any number of values, including none, and binds like a list argument. A <c>--</c>
    /// after the first one is a value. The usage line shows it as <c>[-- name...]</c>.
    /// </remarks>
    public CommandBuilder<TOptions, THandler, TResult> TrailingArguments(
        string id,
        string name,
        bool isSensitive = false,
        string? descriptionKey = null)
    {
        _node.AddArgument(id, name, CommandArity.ZeroOrMore, isSensitive, descriptionKey, isTrailing: true);
        return this;
    }

    /// <summary>Adds command aliases.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Alias(params string[] aliases)
    {
        _node.AddAliases(aliases);
        return this;
    }

    /// <summary>Sets the localization key for help text.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Describe(string descriptionKey)
    {
        _node.SetDescription(descriptionKey);
        return this;
    }

    /// <summary>Sets shared command help metadata.</summary>
    public CommandBuilder<TOptions, THandler, TResult> WithHelp(CommandHelp help)
    {
        ArgumentNullException.ThrowIfNull(help);
        _node.Help = help;
        return this;
    }

    /// <summary>Sets shared metadata for a registered parameter identifier.</summary>
    public CommandBuilder<TOptions, THandler, TResult> ParameterHelp(string id, CommandHelp help)
    {
        ArgumentNullException.ThrowIfNull(help);
        _node.ParameterHelp.Add(id, help);
        return this;
    }

    /// <summary>Adds a typed child command.</summary>
    public CommandBuilder<TChildOptions, TChildHandler, TChildResult> Subcommand<TChildOptions, TChildHandler, TChildResult>(
        string name)
        where TChildHandler : notnull, ICommandHandler<TChildOptions, TChildResult>
    {
        var command = new CommandBuilder<TChildOptions, TChildHandler, TChildResult>(name);
        _node.AddSubcommand(command.Node);
        return command;
    }

    /// <summary>Adds and configures a typed child command.</summary>
    public CommandBuilder<TOptions, THandler, TResult> Subcommand<TChildOptions, TChildHandler, TChildResult>(
        string name,
        Action<CommandBuilder<TChildOptions, TChildHandler, TChildResult>> configure)
        where TChildHandler : notnull, ICommandHandler<TChildOptions, TChildResult>
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Subcommand<TChildOptions, TChildHandler, TChildResult>(name));
        return this;
    }

}

internal sealed class TypedCommandBuilderNode<TOptions, THandler, TResult> : CommandBuilderNode
    where THandler : notnull, ICommandHandler<TOptions, TResult>
{
    private string? _frozenPayloadType;
    private JsonTypeInfo<TResult>? _frozenTypeInfo;

    internal TypedCommandBuilderNode(string name)
        : base(name)
    {
    }

    internal ICommandOptionsBinder<TOptions>? Binder { get; set; }

    internal ICommandHandlerFactory<THandler>? HandlerFactory { get; set; }

    internal ICommandResultCodec<TResult>? ResultCodec { get; set; }

    internal override void ValidateRegistration(string location, List<CommandCatalogIssue> issues)
    {
        if (HumanPresenter is not null && HumanPresenter is not CommandHumanPresenter<TResult>)
            issues.Add(new CommandCatalogIssue("RCLI0020", location, "The human presenter type must match the command result type."));
        if (OutcomePresenter is not null && OutcomePresenter is not CommandOutcomePresenter<TResult>)
            issues.Add(new CommandCatalogIssue("RCLI0020", location, "The outcome presenter type must match the command result type."));
        _frozenPayloadType = null;
        _frozenTypeInfo = null;

        if (Binder is null)
        {
            issues.Add(new CommandCatalogIssue("RCLI0010", location, "A closed options binder is required."));
        }

        if (HandlerFactory is null)
        {
            issues.Add(new CommandCatalogIssue("RCLI0011", location, "A closed handler factory is required."));
        }

        if (ResultCodec is null)
        {
            issues.Add(new CommandCatalogIssue("RCLI0012", location, "A closed result codec is required."));
        }
        else
        {
            try
            {
                string payloadType = ResultCodec.PayloadType;
                JsonTypeInfo<TResult> typeInfo = ResultCodec.TypeInfo;
                if (typeInfo is null)
                {
                    throw new ArgumentException("Source-generated type metadata is required.");
                }

                CommandResponseValidation.ValidatePayloadType(payloadType, nameof(ResultCodec.PayloadType));
                _frozenPayloadType = payloadType;
                _frozenTypeInfo = typeInfo;
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or
                AccessViolationException or
                AppDomainUnloadedException or
                BadImageFormatException))
            {
                issues.Add(new CommandCatalogIssue(
                    "RCLI0017",
                    location,
                    "The result codec requires a valid <lower-name>/<positive-major> payload identity."));
            }
        }
    }

    internal override CommandRegistration CreateRegistration() =>
        new CommandRegistration<TOptions, THandler, TResult>(
            Binder!,
            HandlerFactory!,
            new FrozenCommandResultCodec<TResult>(
                HumanPresenter is CommandHumanPresenter<TResult> presenter ? new PresentedCommandResultCodec<TResult>(ResultCodec!, presenter) : ResultCodec!,
                _frozenPayloadType!,
                _frozenTypeInfo!),
            OutcomePresenter as CommandOutcomePresenter<TResult>);
}

internal abstract class CommandBuilderNode
{
    private readonly List<string> _aliases = [];
    private readonly List<OptionDefinition> _options = [];
    private readonly List<ArgumentDefinition> _arguments = [];
    private readonly List<CommandBuilderNode> _subcommands = [];

    internal CommandBuilderNode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    internal object? HumanPresenter { get; set; }
    internal object? OutcomePresenter { get; set; }
    internal string Name { get; }

    internal IReadOnlyList<string> Aliases => _aliases;
    internal List<CommandBuilderNode> Children => _subcommands;

    private string? DescriptionKey { get; set; }
    internal CommandHelp Help { get; set; } = CommandHelp.Empty;
    internal Dictionary<string, CommandHelp> ParameterHelp { get; } = new(StringComparer.Ordinal);

    internal void AddAliases(IEnumerable<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        foreach (string alias in aliases)
        {
            ArgumentNullException.ThrowIfNull(alias);
            _aliases.Add(alias);
        }
    }

    internal void SetDescription(string descriptionKey)
    {
        ArgumentNullException.ThrowIfNull(descriptionKey);
        DescriptionKey = descriptionKey;
    }

    internal void AddOption(
        string id,
        string name,
        CommandArity arity,
        CommandOptionRepeatPolicy repeatPolicy,
        bool isSensitive,
        string? descriptionKey,
        bool isRequired,
        IEnumerable<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(aliases);
        var aliasCopy = new List<string>();
        foreach (string alias in aliases)
        {
            ArgumentNullException.ThrowIfNull(alias);
            aliasCopy.Add(alias);
        }

        _options.Add(new OptionDefinition(id, name, aliasCopy, arity, repeatPolicy, isRequired, isSensitive, descriptionKey));
    }

    internal void AddGlobalOption(string id, string name, CommandArity arity, CommandHelp? help, string[] aliases)
    {
        OptionDefinition? existing = _options.Find(option => option.Id == id);
        if (existing is not null && (existing.Name != name || existing.Arity != arity || !new HashSet<string>(existing.Aliases, StringComparer.Ordinal).SetEquals(aliases)))
            throw new CommandCatalogValidationException([new CommandCatalogIssue("RCLI0019", Name, GlobalConflictMessage(id, name, arity, aliases, existing))]);
        if (existing is null) AddOption(id, name, arity, CommandOptionRepeatPolicy.Error, false, null, false, aliases);
        GlobalOptionIds.Add(id);
        if (help is not null) ParameterHelp[id] = help;
    }

    // Names the option and both definitions, so the author can see what to change. The arity appears only when it differs.
    private static string GlobalConflictMessage(string id, string name, CommandArity arity, string[] aliases, OptionDefinition local)
    {
        bool showArity = local.Arity != arity;
        string Describe(string optionName, CommandArity optionArity, IEnumerable<string> optionAliases) =>
            optionName + (showArity ? " (" + DescribeArity(optionArity) + ")" : "") + " with aliases [" + string.Join(", ", optionAliases) + "]";
        return $"Global option '{id}' conflicts with the command-local option of the same ID: the global option is " +
            Describe(name, arity, aliases) + ", the command-local option is " + Describe(local.Name, local.Arity, local.Aliases) +
            ". Use the same name, arity and aliases, or give the options different IDs.";
    }

    private static string DescribeArity(CommandArity arity) =>
        arity == CommandArity.Zero ? "a flag" : arity == CommandArity.ExactlyOne ? "one value" :
            arity.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".." +
            (arity.Maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*") + " values";
    private HashSet<string> GlobalOptionIds { get; } = new(StringComparer.Ordinal);

    internal void AddArgument(string id, string name, CommandArity arity, bool isSensitive, string? descriptionKey, bool isTrailing = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        if (isTrailing && _arguments.Exists(static argument => argument.IsTrailing))
            throw new InvalidOperationException("A command can have only one trailing-arguments parameter.");
        _arguments.Add(new ArgumentDefinition(id, name, arity, isSensitive, descriptionKey, isTrailing));
    }

    internal void AddSubcommand(CommandBuilderNode command) => _subcommands.Add(command);

    internal void Validate(string path, List<CommandCatalogIssue> issues)
    {
        ValidateCommandName(Name, path, issues);
        foreach (string alias in _aliases)
        {
            ValidateCommandName(alias, path, issues);
        }

        if (DescriptionKey is not null && string.IsNullOrWhiteSpace(DescriptionKey))
        {
            issues.Add(new CommandCatalogIssue("RCLI0003", path, "The description key cannot be empty."));
        }

        ValidateOptions(path, issues);
        ValidateArguments(path, issues);
        ValidateRegistration(path, issues);
        CommandCatalogBuilder.ValidateSiblings(_subcommands, path, issues);
        foreach (CommandBuilderNode subcommand in _subcommands)
        {
            subcommand.Validate($"{path} {subcommand.Name}", issues);
        }
    }

    internal abstract void ValidateRegistration(string location, List<CommandCatalogIssue> issues);

    internal abstract CommandRegistration CreateRegistration();

    internal CommandDescriptor Freeze()
    {
        var options = new List<CommandOptionDescriptor>(_options.Count);
        foreach (OptionDefinition option in _options)
        {
            options.Add(new CommandOptionDescriptor(
                option.Id,
                option.Name,
                CommandOptionDescriptor.Freeze(option.Aliases),
                option.Arity,
                option.RepeatPolicy,
                option.IsRequired,
                option.IsSensitive,
                option.DescriptionKey,
                ParameterHelp.GetValueOrDefault(option.Id), GlobalOptionIds.Contains(option.Id)));
        }

        var arguments = new List<CommandArgumentDescriptor>(_arguments.Count);
        // Trailing arguments come last, after the positional arguments, whatever the registration order.
        foreach (ArgumentDefinition argument in _arguments.Where(static argument => !argument.IsTrailing).Concat(_arguments.Where(static argument => argument.IsTrailing)))
        {
            arguments.Add(new CommandArgumentDescriptor(
                argument.Id,
                argument.Name,
                argument.Arity,
                argument.IsSensitive,
                argument.DescriptionKey,
                ParameterHelp.GetValueOrDefault(argument.Id),
                argument.IsTrailing));
        }

        var children = new List<CommandDescriptor>(_subcommands.Count);
        foreach (CommandBuilderNode child in _subcommands)
        {
            children.Add(child.Freeze());
        }

        return new CommandDescriptor(
            Name,
            CommandOptionDescriptor.Freeze(_aliases),
            DescriptionKey,
            CommandDescriptor.Freeze(options),
            CommandDescriptor.Freeze(arguments),
            CommandDescriptor.Freeze(children),
            CreateRegistration(), Help);
    }

    private void ValidateOptions(string path, List<CommandCatalogIssue> issues)
    {
        foreach (var entry in ParameterHelp)
        {
            CommandHelp help = entry.Value;
            if (!Enum.IsDefined(help.PathKind) || (help.MustExist && help.PathKind == CommandPathKind.None) ||
                (help.Minimum is { } min && !double.IsFinite(min)) || (help.Maximum is { } max && !double.IsFinite(max)) || help.Minimum > help.Maximum)
                issues.Add(new CommandCatalogIssue("RCLI0021", path, $"Parameter '{entry.Key}' has invalid path or numeric validation metadata; MustExist needs a file or directory path kind and bounds must be finite with Minimum <= Maximum."));
            foreach (string target in System.Linq.Enumerable.Concat(help.Requires, help.ConflictsWith))
                if (target == entry.Key || !_options.Exists(option => option.Id == target) || !_options.Exists(option => option.Id == entry.Key))
                    issues.Add(new CommandCatalogIssue("RCLI0022", path, $"Parameter '{entry.Key}' lists '{target}' in Requires or ConflictsWith; relationships must name another registered option ID of this command."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var spellings = new HashSet<string>(StringComparer.Ordinal);
        foreach (OptionDefinition option in _options)
        {
            if (!IsIdentifier(option.Id))
            {
                issues.Add(new CommandCatalogIssue("RCLI0005", path, $"Option ID '{option.Id}' is invalid."));
            }

            if (!ids.Add(option.Id))
            {
                issues.Add(new CommandCatalogIssue("RCLI0006", path, $"Option ID '{option.Id}' is registered more than once."));
            }

            ValidateOptionSpelling(option.Name);
            foreach (string alias in option.Aliases)
            {
                ValidateOptionSpelling(alias);
            }

            if (!Enum.IsDefined(option.RepeatPolicy))
            {
                issues.Add(new CommandCatalogIssue("RCLI0009", path, $"Option '{option.Name}' has an invalid repeat policy."));
            }

            if (option.DescriptionKey is not null && string.IsNullOrWhiteSpace(option.DescriptionKey))
            {
                issues.Add(new CommandCatalogIssue("RCLI0003", path, $"Option '{option.Name}' has an empty description key."));
            }
        }

        void ValidateOptionSpelling(string spelling)
        {
            if (!IsOptionSpelling(spelling) || IsReservedOption(spelling))
            {
                issues.Add(new CommandCatalogIssue("RCLI0007", path, $"Option spelling '{spelling}' is invalid or reserved."));
            }

            if (!spellings.Add(spelling))
            {
                issues.Add(new CommandCatalogIssue("RCLI0008", path, $"Option spelling '{spelling}' is registered more than once."));
            }
        }
    }

    private void ValidateArguments(string path, List<CommandCatalogIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? optional = null;
        for (int index = 0; index < _arguments.Count; index++)
        {
            ArgumentDefinition argument = _arguments[index];
            if (!IsIdentifier(argument.Id) || !IsIdentifier(argument.Name))
            {
                issues.Add(new CommandCatalogIssue("RCLI0013", path, $"Argument '{argument.Name}' has an invalid name or ID."));
            }

            if (!ids.Add(argument.Id))
            {
                issues.Add(new CommandCatalogIssue("RCLI0014", path, $"Argument ID '{argument.Id}' is registered more than once."));
            }

            if (argument.DescriptionKey is not null && string.IsNullOrWhiteSpace(argument.DescriptionKey))
            {
                issues.Add(new CommandCatalogIssue("RCLI0003", path, $"Argument '{argument.Name}' has an empty description key."));
            }

            // Trailing arguments bind only after --, so positional order rules do not apply to them.
            if (argument.IsTrailing) continue;
            if (optional is not null && argument.Arity.Minimum != 0)
            {
                issues.Add(new CommandCatalogIssue("RCLI0015", path, $"Required argument '{argument.Name}' cannot follow optional argument '{optional}'."));
            }

            if (argument.Arity.Minimum == 0) optional ??= argument.Name;
            if (argument.Arity.Maximum is null && _arguments.Skip(index + 1).Any(static later => !later.IsTrailing))
            {
                issues.Add(new CommandCatalogIssue("RCLI0016", path, $"Argument '{argument.Name}' accepts any number of values, so it must be the final argument."));
            }
        }
    }

    private static void ValidateCommandName(string name, string path, List<CommandCatalogIssue> issues)
    {
        if (!IsIdentifier(name) || name is "help" or "version" or "output" or "root")
        {
            issues.Add(new CommandCatalogIssue("RCLI0002", path, $"Command name or alias '{name}' is invalid or reserved."));
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if ((character is < 'a' or > 'z') && (character is < '0' or > '9') && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOptionSpelling(string value)
    {
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            return IsIdentifier(value[2..]);
        }

        if (value.Length > 0 && value[0] == '/')
        {
            return IsIdentifier(value[1..]);
        }

        return value.Length == 2 && value[0] == '-' &&
            ((value[1] is >= 'a' and <= 'z') || (value[1] is >= 'A' and <= 'Z'));
    }

    private static bool IsReservedOption(string value) =>
        value is "--help" or "-h" or "--version";

    private sealed record OptionDefinition(
        string Id,
        string Name,
        IReadOnlyList<string> Aliases,
        CommandArity Arity,
        CommandOptionRepeatPolicy RepeatPolicy,
        bool IsRequired,
        bool IsSensitive,
        string? DescriptionKey);

    private sealed record ArgumentDefinition(
        string Id,
        string Name,
        CommandArity Arity,
        bool IsSensitive,
        string? DescriptionKey,
        bool IsTrailing = false);
}

internal sealed class CommandGroupBuilderNode(string name) : CommandBuilderNode(name)
{
    internal override void ValidateRegistration(string location, List<CommandCatalogIssue> issues) { }
    internal override CommandRegistration CreateRegistration() => new CommandGroupRegistration();
}

internal sealed class CommandGroupRegistration : CommandRegistration
{
    internal override System.Threading.Tasks.ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandDescriptor command, CommandExecutionRequest request, ICommandExecutionScopeFactory scopeFactory,
        IExitCodePolicy exitCodePolicy, ICommandOutcomeSink outcomeSink, ICommandExecutionObserver? observer,
        System.Threading.CancellationToken cancellationToken) => throw new InvalidOperationException("Command groups display help and cannot be executed.");
}
