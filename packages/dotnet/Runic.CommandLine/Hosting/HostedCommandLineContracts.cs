using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.CommandLine.Hosting;

/// <summary>Controls how an explicitly empty launch is classified.</summary>
/// <remarks>Open: a later minor release may add values, so code that switches over them must handle an unknown value.</remarks>
public enum EmptyInputFallback
{
    /// <summary>Preserve command-line grammar behavior and report an invalid launch.</summary>
    Invalid = 0,

    /// <summary>Select the host's user-interface mode without parsing a command.</summary>
    UserInterface = 1,
}

/// <summary>Identifies the neutral result of hosted command-line classification.</summary>
/// <remarks>Open: a later minor release may add values, so code that switches over them must handle an unknown value.</remarks>
public enum HostedCommandLineDecisionKind
{
    /// <summary>A valid command invocation is ready for command-line execution.</summary>
    Invocation = 0,

    /// <summary>Help was requested without invoking a command handler.</summary>
    Help = 1,

    /// <summary>Version information was requested without invoking a command handler.</summary>
    Version = 2,

    /// <summary>The captured arguments are not a valid command-line launch.</summary>
    Invalid = 3,

    /// <summary>An explicit empty-input policy selected the host user interface.</summary>
    UserInterface = 4,

    /// <summary>A shell completion script was requested.</summary>
    Completion = 5,
}

/// <summary>Contains replayable launch inputs without reading process-global state.</summary>
public sealed class HostedCommandLineLaunchInput
{
    /// <summary>Initializes captured arguments and output-environment input.</summary>
    public HostedCommandLineLaunchInput(
        IReadOnlyList<string> arguments,
        string? outputEnvironmentValue = null,
        EmptyInputFallback emptyInputFallback = EmptyInputFallback.Invalid,
        CommandOutputMode defaultOutputMode = CommandOutputMode.Human)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Enum.IsDefined(emptyInputFallback))
        {
            throw new ArgumentOutOfRangeException(nameof(emptyInputFallback));
        }

        if (!Enum.IsDefined(defaultOutputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultOutputMode));
        }

        var snapshot = new string[arguments.Count];
        for (int index = 0; index < arguments.Count; index++)
        {
            snapshot[index] = arguments[index] ?? throw new ArgumentException(
                "Launch arguments cannot contain null entries.",
                nameof(arguments));
        }

        Arguments = new ReadOnlyCollection<string>(snapshot);
        OutputEnvironmentValue = outputEnvironmentValue;
        EmptyInputFallback = emptyInputFallback;
        DefaultOutputMode = defaultOutputMode;
    }

    private IReadOnlyDictionary<string, string?> _environmentVariables = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>());

    /// <summary>Gets a copied snapshot of named parameter environment values. Missing entries never read the process environment.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables
    {
        get => _environmentVariables;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var entry in value) snapshot.Add(entry.Key, entry.Value);
            _environmentVariables = new ReadOnlyDictionary<string, string?>(snapshot);
        }
    }

    /// <summary>Gets the immutable captured argument sequence.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the captured <c>RUNIC_COMMANDLINE_OUTPUT</c> value, if any.</summary>
    public string? OutputEnvironmentValue { get; }

    /// <summary>Gets the explicit policy for an empty argument sequence.</summary>
    public EmptyInputFallback EmptyInputFallback { get; }

    /// <summary>Gets the output mode used when no higher-precedence source is supplied.</summary>
    public CommandOutputMode DefaultOutputMode { get; }
}

/// <summary>Contains a frozen parser-neutral decision for a hosted launch.</summary>
public sealed class HostedCommandLineDecision
{
    private readonly ParsedInvocation? _invocation;
    private readonly object? _owner;

    internal HostedCommandLineDecision(
        HostedCommandLineDecisionKind kind,
        HostedCommandLineLaunchInput input,
        ParsedInvocation? invocation = null,
        object? owner = null,
        CommandPath? path = null,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null,
        CommandOutputClassification? outputClassification = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ArgumentNullException.ThrowIfNull(input);
        LaunchInput = input;
        Arguments = input.Arguments;
        _invocation = invocation;
        _owner = owner;
        Path = path;
        Diagnostics = FreezeDiagnostics(diagnostics);
        OutputClassification = outputClassification;
    }

    /// <summary>Gets the selected neutral launch category.</summary>
    public HostedCommandLineDecisionKind Kind { get; }

    /// <summary>Gets the immutable argument snapshot that was classified.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the canonical command or help path when one was resolved.</summary>
    public CommandPath? Path { get; }

    /// <summary>Gets the canonical root command name when a command path was resolved.</summary>
    public string? CommandName => Path is { Count: > 0 } ? Path[0] : null;

    /// <summary>Gets parser-owned safe diagnostics for an invalid decision.</summary>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    /// <summary>Gets the frozen output classification when parsing reached that stage.</summary>
    public CommandOutputClassification? OutputClassification { get; }

    /// <summary>Gets whether this decision can be executed by the command-line engine.</summary>
    public bool CanExecute => Kind == HostedCommandLineDecisionKind.Invocation;

    /// <summary>
    /// Gets the catalog command or group named explicitly by the leading arguments, also when the rest of the
    /// launch is invalid. It is null when no command name matched, including when a default command was
    /// selected implicitly.
    /// </summary>
    public CommandPath? MatchedPath { get; init; }

    /// <summary>Gets whether the leading arguments name a catalog command or group.</summary>
    public bool MatchesKnownCommand => MatchedPath is not null;

    /// <summary>
    /// Gets the visible root command name or alias that closely matches an unknown command word, such as
    /// <c>list</c> for <c>lst</c>, when the launch names no known command. It is the suggestion the standalone
    /// command line shows as "Did you mean ...?". The launch still belongs to the user interface, because the word
    /// may be a document path; a host can mention the suggestion before it opens the interface, for example with
    /// <see cref="CommandLineHostingAdapter.WriteSuggestionAsync"/>. It is null when nothing is close.
    /// </summary>
    public string? SuggestedCommand { get; init; }

    /// <summary>
    /// Gets whether the launch belongs to the command line: an invocation, a help, version or completion
    /// request, or an invalid launch whose leading arguments name a known command. A host with a user
    /// interface typically opens it for every other launch, such as a document path or an unknown word.
    /// </summary>
    public bool IsCommandLineRequest => Kind switch
    {
        HostedCommandLineDecisionKind.Invocation or HostedCommandLineDecisionKind.Help or
            HostedCommandLineDecisionKind.Version or HostedCommandLineDecisionKind.Completion => true,
        HostedCommandLineDecisionKind.Invalid => MatchesKnownCommand,
        _ => false,
    };

    internal HostedCommandLineLaunchInput LaunchInput { get; }
    internal ParseOutcome? ParseOutcome { get; init; }

    internal void ValidateOwner(object owner)
    {
        if (!ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("The decision belongs to another adapter.");
    }

    internal ParsedInvocation GetInvocation(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!CanExecute || _invocation is null)
        {
            throw new InvalidOperationException("Only an invocation decision can be executed.");
        }

        if (!ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "The invocation decision can be executed only by the adapter that created it.");
        }

        return _invocation;
    }

    private static IReadOnlyList<CommandDiagnostic> FreezeDiagnostics(
        IReadOnlyList<CommandDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return Array.Empty<CommandDiagnostic>();
        }

        var snapshot = new CommandDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            snapshot[index] = diagnostics[index] ?? throw new ArgumentException(
                "Diagnostics cannot contain null entries.",
                nameof(diagnostics));
        }

        return new ReadOnlyCollection<CommandDiagnostic>(snapshot);
    }
}

/// <summary>Contains presentation inputs for one valid hosted command invocation.</summary>
public sealed class HostedCommandLineExecutionInput
{
    /// <summary>Initializes execution inputs without introducing a host service scope.</summary>
    public HostedCommandLineExecutionInput(
        HostedCommandLineDecision decision,
        ICommandConsole console,
        CultureInfo culture,
        string correlationId,
        ICommandOutcomeSink outcomeSink)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(outcomeSink);
        if (!decision.CanExecute)
        {
            throw new ArgumentException("Execution requires an invocation decision.", nameof(decision));
        }

        Decision = decision;
        Console = console;
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        CorrelationId = correlationId;
        OutcomeSink = outcomeSink;
    }

    /// <summary>Gets the invocation decision produced by classification.</summary>
    public HostedCommandLineDecision Decision { get; }

    /// <summary>Gets the invocation-local console.</summary>
    public ICommandConsole Console { get; }

    /// <summary>Gets the presentation culture.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Gets the opaque invocation correlation identifier.</summary>
    public string CorrelationId { get; }

    /// <summary>Gets an observer for internal exceptions, which are not exposed in public faults.</summary>
    public Action<Exception>? ExceptionObserver { get; init; }

    /// <summary>Gets the outcome presentation sink.</summary>
    public ICommandOutcomeSink OutcomeSink { get; }

    /// <summary>
    /// Gets whether the host runs a continuation that the command hands back with
    /// <see cref="CommandExecutionContext.ContinueOnCallingThread"/>. When true, the command sees
    /// <see cref="CommandExecutionContext.CanContinueOnCallingThread"/> as true, and the continuation is returned in
    /// <see cref="HostedCommandLineExecutionResult.Continuation"/> for the host to run on the thread it chooses. When
    /// false, the default, the command cannot hand one back.
    /// </summary>
    public bool AcceptsContinuation { get; init; }
}

/// <summary>Contains the completed result returned by the command-line engine.</summary>
public sealed class HostedCommandLineExecutionResult
{
    internal HostedCommandLineExecutionResult(CommandExecutionResult commandResult, Func<int>? continuation = null)
    {
        ArgumentNullException.ThrowIfNull(commandResult);
        ExitCategory = commandResult.ExitCategory;
        ExitCode = commandResult.ExitCode;
        Fault = commandResult.Fault;
        Continuation = continuation;
    }

    /// <summary>Gets the semantic command outcome category.</summary>
    public CommandExitCategory ExitCategory { get; }

    /// <summary>Gets the command engine's selected process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the safe command fault, when execution was unsuccessful.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets whether the command engine completed successfully.</summary>
    public bool Succeeded => ExitCategory == CommandExitCategory.Success;

    /// <summary>
    /// Gets the continuation that the command handed back with
    /// <see cref="CommandExecutionContext.ContinueOnCallingThread"/>, or null. It is set only when the input
    /// accepted one (<see cref="HostedCommandLineExecutionInput.AcceptsContinuation"/>), the command succeeded and
    /// the host's cancellation token was not cancelled.
    /// </summary>
    /// <remarks>
    /// The adapter never runs it. When <see cref="CommandLineHostingAdapter.ExecuteAsync"/> returns, the command's
    /// output is written and its scope disposed; the host invokes the continuation on the thread that must run it,
    /// typically the main thread, before or instead of its own shutdown, and usually returns its result as the
    /// process exit code. To stay on the main thread, a host blocks on the execution from a synchronous
    /// <c>Main</c> rather than awaiting it.
    /// </remarks>
    public Func<int>? Continuation { get; }
}
