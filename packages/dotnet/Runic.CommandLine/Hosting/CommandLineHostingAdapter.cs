using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.CommandLine.Hosting;

/// <summary>
/// First-party bridge that delegates syntax analysis and command scope ownership
/// to the existing CommandLine parser and executor.
/// </summary>
public sealed class CommandLineHostingAdapter : IHostedCommandLineAdapter
{
    private readonly object _decisionOwner = new();
    private readonly CommandCatalog _catalog;
    private readonly ICommandSyntaxAdapter _syntaxAdapter;
    private readonly CommandExecutor? _executor;
    private readonly CommandApp? _app;

    /// <summary>Initializes the bridge with the application command catalog and executor.</summary>
    public CommandLineHostingAdapter(
        CommandCatalog catalog,
        CommandExecutor executor,
        ICommandSyntaxAdapter? syntaxAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executor);
        _catalog = catalog;
        _executor = executor;
        _syntaxAdapter = syntaxAdapter ?? PortableCommandSyntaxAdapter.Instance;
    }

    /// <summary>
    /// Initializes the bridge from the application's <see cref="CommandApp"/>, so the hosted path reuses its
    /// catalog, scope factory, exit policy, observers, outcome sink and console, and its presentation
    /// (<see cref="CommandApp.Name"/>, <see cref="CommandApp.Version"/>, <see cref="CommandApp.TextResolver"/>
    /// and help settings). The app's process signal handling and environment reads are not used: the host
    /// supplies cancellation and captured launch input.
    /// </summary>
    public CommandLineHostingAdapter(CommandApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        _catalog = app.Catalog;
        _syntaxAdapter = PortableCommandSyntaxAdapter.Instance;
        Presentation = app.CreatePresentation(app.ExceptionObserver);
    }

    /// <summary>Gets framework presentation shared with the standalone runner.</summary>
    public CommandPresentation Presentation { get; init; } = new();

    /// <inheritdoc />
    public HostedCommandLineDecision Classify(HostedCommandLineLaunchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Arguments.Count == 0 &&
            input.EmptyInputFallback == EmptyInputFallback.UserInterface)
        {
            return new HostedCommandLineDecision(
                HostedCommandLineDecisionKind.UserInterface,
                input,
                invocation: null,
                owner: _decisionOwner);
        }

        if (input.Arguments.Count == 2 && input.Arguments[0] == "completion" && !_catalog.TryGetCommand("completion", out _))
            return new HostedCommandLineDecision(HostedCommandLineDecisionKind.Completion, input, owner: _decisionOwner);

        ParseOutcome outcome = _syntaxAdapter.Parse(
            _catalog,
            input.Arguments.ToArray(),
            new ParseSettings(
                input.OutputEnvironmentValue,
                input.DefaultOutputMode,
                input.TransportOutputOptionName)
            {
                GetEnvironmentVariable = name => input.EnvironmentVariables.TryGetValue(name, out string? value) ? value : null,
            });
        CommandPath? matchedPath = PortableCommandSyntaxAdapter.MatchExplicitPath(_catalog, input.Arguments, input.TransportOutputOptionName);

        HostedCommandLineDecision decision = outcome.Kind switch
        {
            ParseOutcomeKind.Invocation when outcome.Invocation is not null =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Invocation,
                    input,
                    outcome.Invocation,
                    _decisionOwner,
                    outcome.Invocation.Path,
                    outcome.Diagnostics,
                    outcome.OutputClassification) { MatchedPath = matchedPath },
            ParseOutcomeKind.Help when outcome.HelpRequest is not null =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Help,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    path: outcome.HelpRequest.Path,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification) { ParseOutcome = outcome, MatchedPath = matchedPath },
            ParseOutcomeKind.Version =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Version,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification) { ParseOutcome = outcome, MatchedPath = matchedPath },
            ParseOutcomeKind.Error =>
                new HostedCommandLineDecision(
                    HostedCommandLineDecisionKind.Invalid,
                    input,
                    invocation: null,
                    owner: _decisionOwner,
                    diagnostics: outcome.Diagnostics,
                    outputClassification: outcome.OutputClassification) { ParseOutcome = outcome, MatchedPath = matchedPath },
            _ => throw new InvalidOperationException("The command syntax adapter returned an incomplete outcome."),
        };
        return decision;
    }

    /// <summary>Presents help, version, usage errors or completion without creating a scope or owning host cancellation.</summary>
    public ValueTask<int> PresentAsync(HostedCommandLineDecision decision, ICommandConsole console,
        CultureInfo culture, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        decision.ValidateOwner(_decisionOwner);
        if (decision.Kind == HostedCommandLineDecisionKind.Completion)
            return Presentation.GuardAsync(() => Presentation.WriteCompletionAsync(_catalog, decision.Arguments[1], decision.LaunchInput.TransportOutputOptionName, console, culture, cancellationToken), cancellationToken);
        if (decision.Kind is not (HostedCommandLineDecisionKind.Help or HostedCommandLineDecisionKind.Version or HostedCommandLineDecisionKind.Invalid) || decision.ParseOutcome is null)
            throw new InvalidOperationException("Presentation requires a framework decision created by this adapter.");
        if (_app?.PresentFrameworkRequest is { } present)
            return Presentation.GuardAsync(() => present(decision.ParseOutcome, console, cancellationToken), cancellationToken);
        return Presentation.GuardAsync(() => Presentation.WriteAsync(_catalog, decision.ParseOutcome, decision.LaunchInput.TransportOutputOptionName,
            console, culture, correlationId, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<HostedCommandLineExecutionResult> ExecuteAsync(
        HostedCommandLineExecutionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ParsedInvocation invocation = input.Decision.GetInvocation(_decisionOwner);
        var request = new CommandExecutionRequest(
            invocation,
            input.Console,
            input.Culture,
            input.CorrelationId) { ExceptionObserver = input.ExceptionObserver };
        CommandExecutor executor = _executor ?? _app!.CreateExecutor(invocation);
        CommandExecutionResult result = await executor.ExecuteAsync(
            request,
            input.OutcomeSink,
            cancellationToken).ConfigureAwait(false);
        return new HostedCommandLineExecutionResult(result);
    }

    /// <summary>
    /// Presents or executes a command-line decision and returns its process exit code, so the host needs no
    /// separate presentation and execution branches. Output goes to <paramref name="console"/>, else the
    /// <see cref="CommandApp.Console"/> of the app this adapter was created from, else the process console.
    /// The culture is <see cref="CommandApp.Culture"/> when set, otherwise <see cref="CultureInfo.CurrentUICulture"/>.
    /// </summary>
    /// <param name="decision">A decision created by this adapter. A user-interface decision belongs to the host.</param>
    /// <param name="console">An optional invocation console.</param>
    /// <param name="cancellationToken">The host's cancellation token; the adapter never subscribes to process signals.</param>
    public async ValueTask<int> RunAsync(HostedCommandLineDecision decision, ICommandConsole? console = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.ValidateOwner(_decisionOwner);
        if (decision.Kind == HostedCommandLineDecisionKind.UserInterface)
            throw new InvalidOperationException("A user-interface decision belongs to the host's own launch path.");
        console ??= _app?.Console ?? new SystemCommandConsole();
        CultureInfo culture = _app?.Culture ?? CultureInfo.CurrentUICulture;
        string correlationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (!decision.CanExecute)
            return await PresentAsync(decision, console, culture, correlationId, cancellationToken).ConfigureAwait(false);
        ICommandOutcomeSink sink = _app?.OutcomeSink ?? new CommandOutputDispatcher { TextResolver = Presentation.TextResolver };
        HostedCommandLineExecutionResult result = await ExecuteAsync(
            new HostedCommandLineExecutionInput(decision, console, culture, correlationId, sink) { ExceptionObserver = Presentation.ExceptionObserver },
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }
}
