using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Runs a catalog without requiring a dependency-injection container or Generic Host.</summary>
public sealed class CommandApp
{
    private readonly CommandCatalog _catalog;
    /// <summary>Initializes an application from an immutable catalog.</summary>
    public CommandApp(CommandCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    internal CommandCatalog Catalog => _catalog;
    /// <summary>Gets or sets the executable name shown in help.</summary>
    public string Name { get; init; } = "app";
    /// <summary>Gets or sets the executable name registered by completion scripts, when different from Name.</summary>
    public string? CompletionExecutableName { get; init; }
    /// <summary>Gets or sets the application version.</summary>
    public string Version { get; init; } = "0.0.0";
    /// <summary>Gets or sets invocation-local console IO.</summary>
    public ICommandConsole Console { get; init; } = new SystemCommandConsole();
    /// <summary>Gets or sets an optional scope factory.</summary>
    public ICommandExecutionScopeFactory? ScopeFactory { get; init; }
    /// <summary>Gets or sets an invocation-dependent scope factory for applications with launch-specific services.</summary>
    public Func<ParsedInvocation, ICommandExecutionScopeFactory>? CreateScopeFactory { get; init; }
    /// <summary>Gets or sets a presenter for framework requests when preserving an existing application wire contract.</summary>
    public Func<ParseOutcome, ICommandConsole, CancellationToken, ValueTask<int>>? PresentFrameworkRequest { get; init; }
    /// <summary>Gets or sets the exit policy, including parser errors.</summary>
    public IExitCodePolicy ExitCodePolicy { get; init; } = DefaultExitCodePolicy.Instance;
    /// <summary>Gets or sets the outcome presenter.</summary>
    public ICommandOutcomeSink OutcomeSink { get => _outcomeSink ?? new CommandOutputDispatcher { TextResolver = TextResolver }; init => _outcomeSink = value; }
    private readonly ICommandOutcomeSink? _outcomeSink;
    /// <summary>
    /// Gets or sets the invocation culture for help, diagnostics and handler formatting. Otherwise
    /// <see cref="CultureInfo.CurrentUICulture"/>, the user's display language, is captured at invocation time.
    /// </summary>
    public CultureInfo? Culture { get; init; }
    /// <summary>Gets or sets optional text resolution for framework help and diagnostics.</summary>
    public ICommandTextResolver? TextResolver { get; init; }
    /// <summary>Gets or sets explicit parse settings. Otherwise output configuration is read at invocation time.</summary>
    public ParseSettings? ParseSettings { get; init; }
    /// <summary>Gets or sets an observer for internal exceptions; these are never displayed to users.</summary>
    public Action<Exception>? ExceptionObserver { get; init; }
    /// <summary>Gets or sets an execution lifecycle observer.</summary>
    public ICommandExecutionObserver? Observer { get; init; }
    /// <summary>
    /// Gets whether process termination requests cancel the invocation: Ctrl+C (SIGINT), SIGTERM and
    /// SIGQUIT (Ctrl+Break on Windows). The default is true. A repeated request while the invocation is still
    /// running exits the process immediately with 128 plus the signal number, for example 130 after a second
    /// Ctrl+C, unless the command holds <see cref="CommandExecutionContext.ProtectShutdown"/>.
    /// </summary>
    public bool HandleTerminationSignals { get; init; } = true;
    /// <summary>Gets or sets a renderer for human catalog help.</summary>
    public ICommandHelpPresenter? HelpPresenter { get; init; }
    /// <summary>Gets or sets an optional application-specific help formatter.</summary>
    public Func<CommandPath, string>? FormatHelp { get; init; }

    /// <summary>Parses, presents framework requests, or executes one invocation, returning its process exit code.</summary>
    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, continuation: null, cancellationToken);

    /// <summary>
    /// Parses, presents framework requests, or executes one invocation as <see cref="RunAsync"/> does, blocking the
    /// calling thread until it completes, then runs the continuation that the command handed back with
    /// <see cref="CommandExecutionContext.ContinueOnCallingThread"/>, if any, on the calling thread.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="cancellationToken">Cancels the invocation; the continuation does not observe it.</param>
    /// <returns>
    /// The continuation's result when the command handed one back and succeeded, otherwise the invocation's
    /// process exit code.
    /// </returns>
    /// <remarks>
    /// Call it from a synchronous <c>Main</c> (one without <see langword="await"/>) so the calling thread is the
    /// process's main thread, for example to start a UI host that must own the main thread. The invocation runs
    /// without the caller's <see cref="SynchronizationContext"/>, so a handler's awaits never wait for the blocked
    /// thread. Process signal handling (<see cref="HandleTerminationSignals"/>) ends before the continuation runs.
    /// </remarks>
    public int Run(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var continuation = new CommandContinuation();
        SynchronizationContext? callerContext = SynchronizationContext.Current;
        int exitCode;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            exitCode = RunCoreAsync(args, continuation, cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
        }

        return continuation.Take() is { } next ? next() : exitCode;
    }

    private async Task<int> RunCoreAsync(string[] args, CommandContinuation? continuation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ProcessSignalCancellation? signals = HandleTerminationSignals ? new ProcessSignalCancellation(cancellation) : null;
        try
        {
            CultureInfo culture = CultureInfo.ReadOnly((CultureInfo)(Culture ?? CultureInfo.CurrentUICulture).Clone());
            ParseSettings settings = ParseSettings ?? new ParseSettings(Environment.GetEnvironmentVariable(CommandOutputClassifier.EnvironmentVariableName)) { GetEnvironmentVariable = Environment.GetEnvironmentVariable };
            // Explicit settings without an environment reader keep the process environment out, as for option fallbacks.
            CommandDebugOutput? debug = CommandDebugOutput.IsEnabled(settings.GetEnvironmentVariable ?? (static _ => null)) ? new CommandDebugOutput() : null;
            Action<Exception>? exceptionObserver = debug?.Observe(ExceptionObserver) ?? ExceptionObserver;
            CommandPresentation presentation = CreatePresentation(exceptionObserver);
            if (CommandCompletion.IsRequest(_catalog, args, out string? shell))
            {
                return await presentation.GuardAsync(() => presentation.WriteCompletionAsync(_catalog, shell, Console, culture, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            }
            ParseOutcome parsed = PortableCommandSyntaxAdapter.Instance.Parse(_catalog, args.Length == 0 && _catalog.DefaultCommand is null ? ["--help"] : args, settings);
            string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            if (parsed.Kind == ParseOutcomeKind.Invocation)
            {
                CommandExecutor executor = CreateExecutor(parsed.Invocation!);
                ICommandOutcomeSink sink = _outcomeSink ?? new CommandOutputDispatcher { TextResolver = TextResolver, ShowHostFailureHint = debug is null && ExceptionObserver is null };
                CommandExecutionResult result = await executor.ExecuteAsync(
                    new CommandExecutionRequest(parsed.Invocation!, Console, culture, requestId)
                    {
                        ExceptionObserver = exceptionObserver, TextResolver = TextResolver,
                        PromptsDisabled = CommandPrompts.IsDisabledBy(settings.GetEnvironmentVariable),
                        Continuation = continuation,
                        ShutdownProtection = signals?.Protection,
                    },
                    debug is null ? sink : new CommandDebugOutput.FlushingSink(sink, debug), cancellation.Token).ConfigureAwait(false);
                // Only a successful invocation that nobody asked to stop hands its continuation back.
                if (!result.IsSuccess || cancellation.IsCancellationRequested) continuation?.Take();
                if (debug is not null) await debug.FlushAsync(Console, CancellationToken.None).ConfigureAwait(false);
                return result.ExitCode;
            }

            if (debug is not null && parsed.Kind == ParseOutcomeKind.Help && TextResolver is not null)
                await WriteUnresolvedDescriptionKeysAsync(culture).ConfigureAwait(false);
            int exitCode = PresentFrameworkRequest is not null
                ? await presentation.GuardAsync(() => PresentFrameworkRequest(parsed, Console, cancellation.Token), cancellation.Token).ConfigureAwait(false)
                : await presentation.GuardAsync(() => presentation.WriteAsync(_catalog, parsed,
                    Console, culture, requestId, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            if (debug is not null) await debug.FlushAsync(Console, CancellationToken.None).ConfigureAwait(false);
            return exitCode;
        }
        finally
        {
            signals?.Dispose();
        }
    }

    internal CommandPresentation CreatePresentation(Action<Exception>? exceptionObserver) => new()
    {
        Name = Name, Version = Version, CompletionExecutableName = CompletionExecutableName,
        TextResolver = TextResolver, HelpPresenter = HelpPresenter, FormatHelp = FormatHelp, ExitCodePolicy = ExitCodePolicy, ExceptionObserver = exceptionObserver,
    };

    internal CommandExecutor CreateExecutor(ParsedInvocation invocation)
    {
        ICommandExecutionScopeFactory scopes = CreateScopeFactory is { } createScopes
            ? new InvocationScopeFactory(createScopes, invocation)
            : ScopeFactory ?? EmptyScopeFactory.Instance;
        return new CommandExecutor(scopes, ExitCodePolicy, Observer);
    }

    // Developer output: a DescriptionKey the resolver does not know silently falls back to the literal description.
    private async ValueTask WriteUnresolvedDescriptionKeysAsync(CultureInfo culture)
    {
        try
        {
            foreach (CommandCatalogIssue issue in new CommandTextContext(culture, TextResolver).ValidateDescriptionKeys(_catalog))
                await Console.WriteErrorAsync(("warning " + issue.Code + " at '" + issue.Location + "': " + issue.Message + "\n").AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException))
        {
            // Debug output is best effort; resolver failures surface when help is rendered.
        }
    }

    /// <summary>Cancels on the first termination signal and forces exit on the next one, unless shutdown is protected.</summary>
    private sealed class ProcessSignalCancellation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        internal ShutdownProtection Protection { get; } = new();
        private readonly List<PosixSignalRegistration> _registrations = new(3);
        private int _received;

        internal ProcessSignalCancellation(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            Register(PosixSignal.SIGINT);
            Register(PosixSignal.SIGQUIT);
            Register(PosixSignal.SIGTERM);
        }

        public void Dispose()
        {
            foreach (PosixSignalRegistration registration in _registrations) registration.Dispose();
        }

        private void Register(PosixSignal signal)
        {
            try { _registrations.Add(PosixSignalRegistration.Create(signal, Handle)); }
            catch (PlatformNotSupportedException) { }
        }

        private void Handle(PosixSignalContext context)
        {
            if (Interlocked.Increment(ref _received) > 1 && !Protection.IsActive) Environment.Exit(ForcedExitCode(context.Signal));
            context.Cancel = true;
            try { _ = _cancellation.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }

        private static int ForcedExitCode(PosixSignal signal) => signal switch
        {
            PosixSignal.SIGINT => 130,
            PosixSignal.SIGQUIT => 131,
            _ => 143,
        };
    }

    private sealed class InvocationScopeFactory(Func<ParsedInvocation, ICommandExecutionScopeFactory> create, ParsedInvocation invocation) : ICommandExecutionScopeFactory
    {
        public ICommandExecutionScope CreateScope() => create(invocation).CreateScope();
    }

    private sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
    {
        internal static EmptyScopeFactory Instance { get; } = new();
        public ICommandExecutionScope CreateScope() => new EmptyScope();
    }
    private sealed class EmptyScope : ICommandExecutionScope, IServiceProvider
    {
        public IServiceProvider Services => this;
        public object? GetService(Type serviceType) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
