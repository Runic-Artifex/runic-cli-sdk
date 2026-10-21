using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// CommandApp.Run hands a command's continuation back to the thread that called it, as a UI host started from Main needs.
internal sealed class ContinuationTests
{
    [Test, DisplayName("continuation/runs-on-the-calling-thread-after-async-work")]
    public ValueTask RunsOnCallingThread()
    {
        var probe = new ContinuationProbe { ExitCode = 42 };
        (int exitCode, int callingThread) = RunOnDedicatedThread(probe, ["continue"]);

        AssertEx.Equal(42, exitCode);
        AssertEx.True(probe.CouldContinue);
        // The handler resumed on a pool thread after its await; the continuation came back to the caller.
        AssertEx.True(probe.HandlerThread is int handler && handler != callingThread, $"handler {probe.HandlerThread}, caller {callingThread}");
        AssertEx.Equal(callingThread, probe.ContinuationThread);
        // The invocation's output was complete before the continuation started.
        AssertEx.Equal("started\n", probe.OutputAtContinuation);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("continuation/is-discarded-on-failure-cancellation-and-help")]
    public ValueTask DiscardedUnlessSuccessful()
    {
        var probe = new ContinuationProbe();
        AssertEx.Equal(CommandExitCodes.CommandFailure, RunOnDedicatedThread(probe, ["continue", "--fail"]).ExitCode);
        AssertEx.True(probe.Registered && probe.ContinuationThread is null);

        probe = new ContinuationProbe();
        AssertEx.Equal(CommandExitCodes.Cancelled, RunOnDedicatedThread(probe, ["continue", "--cancel"]).ExitCode);
        AssertEx.True(probe.Registered && probe.ContinuationThread is null);

        // A command that finishes successfully after a stop request does not start what follows either.
        probe = new ContinuationProbe { ExitCode = 42 };
        AssertEx.Equal(0, RunOnDedicatedThread(probe, ["continue", "--cancel", "--ignore-cancel"]).ExitCode);
        AssertEx.True(probe.Registered && probe.ContinuationThread is null);

        probe = new ContinuationProbe { ExitCode = 42 };
        AssertEx.Equal(0, RunOnDedicatedThread(probe, ["continue", "--help"]).ExitCode);
        AssertEx.True(!probe.Registered && probe.ContinuationThread is null && probe.Console.StandardOutput.StartsWith("Usage", StringComparison.Ordinal));

        probe = new ContinuationProbe { ExitCode = 42 };
        AssertEx.Equal(CommandExitCodes.Usage, RunOnDedicatedThread(probe, ["continue", "--bogus"]).ExitCode);
        AssertEx.True(!probe.Registered && probe.ContinuationThread is null);

        // A second continuation is a programming error, reported as a host failure.
        probe = new ContinuationProbe { ExitCode = 42 };
        AssertEx.Equal(CommandExitCodes.HostFailure, RunOnDedicatedThread(probe, ["continue", "--twice"]).ExitCode);
        AssertEx.True(probe.ContinuationThread is null);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("continuation/tester-run-continues-on-the-calling-thread")]
    public ValueTask TesterRunContinues()
    {
        var probe = new ContinuationProbe { ExitCode = 42 };
        var tester = new CommandAppTester(console => new CommandApp(GeneratedCommandCatalog.Create())
        {
            Console = console,
            ParseSettings = ParseSettings.Default,
            HandleTerminationSignals = false,
            ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(probe)),
        });
        CommandAppTestResult? result = null;
        int callingThread = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                callingThread = Environment.CurrentManagedThreadId;
                result = tester.Run(["continue"]);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        AssertEx.True(thread.Join(TimeSpan.FromSeconds(30)), "Run did not return.");
        if (failure is not null) throw new InvalidOperationException("Run failed.", failure);

        AssertEx.Equal(new CommandAppTestResult(42, "started\n", ""), result);
        AssertEx.True(probe.CouldContinue);
        AssertEx.Equal(callingThread, probe.ContinuationThread);

        // A failed invocation reports its own exit code and runs nothing.
        probe = new ContinuationProbe { ExitCode = 42 };
        tester = new CommandAppTester(console => App(probe));
        AssertEx.Equal(CommandExitCodes.CommandFailure, tester.Run(["continue", "--fail"]).ExitCode);
        AssertEx.True(probe.Registered && probe.ContinuationThread is null);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("continuation/asynchronous-entry-points-cannot-continue")]
    public async ValueTask AsynchronousEntryPointsCannotContinue()
    {
        var probe = new ContinuationProbe { ExitCode = 42 };
        AssertEx.Equal(0, await App(probe).RunAsync(["continue"]));
        AssertEx.True(!probe.CouldContinue && !probe.Registered && probe.ContinuationThread is null);

        probe = new ContinuationProbe { ExitCode = 42 };
        AssertEx.Equal(CommandExitCodes.HostFailure, await App(probe).RunAsync(["continue", "--force"]));
        AssertEx.True(probe.ContinuationThread is null);

        var hosted = new Hosting.CommandLineHostingAdapter(App(probe = new ContinuationProbe { ExitCode = 42 }));
        AssertEx.Equal(0, await hosted.RunAsync(hosted.Classify(new Hosting.HostedCommandLineLaunchInput(["continue"]))));
        AssertEx.True(!probe.CouldContinue && probe.ContinuationThread is null);
    }

    // runic-cli-sdk#52: a host that accepts continuations receives one in the execution result and runs it itself.
    [Test, DisplayName("continuation/hosting-adapter-returns-an-accepted-continuation")]
    public async ValueTask HostingAdapterReturnsContinuation()
    {
        async ValueTask<Hosting.HostedCommandLineExecutionResult> Execute(ContinuationProbe probe, bool accepts, params string[] args)
        {
            var adapter = new Hosting.CommandLineHostingAdapter(App(probe));
            Hosting.HostedCommandLineDecision decision = adapter.Classify(new Hosting.HostedCommandLineLaunchInput(args));
            return await adapter.ExecuteAsync(
                new Hosting.HostedCommandLineExecutionInput(decision, probe.Console, System.Globalization.CultureInfo.InvariantCulture, "host", new CommandOutputDispatcher())
                {
                    AcceptsContinuation = accepts,
                },
                probe.Cancellation.Token);
        }

        var probe = new ContinuationProbe { ExitCode = 42 };
        Hosting.HostedCommandLineExecutionResult result = await Execute(probe, accepts: true, "continue");
        AssertEx.True(result.IsSuccess && probe.CouldContinue && probe.Registered && probe.ContinuationThread is null);
        AssertEx.Equal(42, result.Continuation!());
        AssertEx.Equal(Environment.CurrentManagedThreadId, probe.ContinuationThread);
        AssertEx.Equal("started\n", probe.OutputAtContinuation);

        // Without the opt-in the command cannot hand one back, as before.
        probe = new ContinuationProbe { ExitCode = 42 };
        result = await Execute(probe, accepts: false, "continue");
        AssertEx.True(result.IsSuccess && !probe.CouldContinue && !probe.Registered && result.Continuation is null);

        // As under CommandApp.Run, failure and cancellation discard it.
        foreach (string[] args in DiscardingArguments)
        {
            probe = new ContinuationProbe { ExitCode = 42 };
            result = await Execute(probe, accepts: true, args);
            AssertEx.True(probe.Registered && result.Continuation is null, string.Join(' ', args));
        }
    }

    private static readonly string[][] DiscardingArguments = [["continue", "--fail"], ["continue", "--cancel"], ["continue", "--cancel", "--ignore-cancel"]];

    [Test, DisplayName("continuation/run-sets-aside-the-callers-synchronization-context")]
    public ValueTask RunSetsAsideSynchronizationContext()
    {
        var probe = new ContinuationProbe { ExitCode = 7 };
        var callerContext = new RecordingSynchronizationContext();
        SynchronizationContext? afterRun = null;
        Exception? failure = null;
        int exitCode = -1;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(callerContext);
                exitCode = App(probe).Run(["continue"]);
                afterRun = SynchronizationContext.Current;
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        AssertEx.True(thread.Join(TimeSpan.FromSeconds(30)), "Run did not return; a handler await waited for the blocked caller.");
        if (failure is not null) throw new InvalidOperationException("Run failed.", failure);

        AssertEx.Equal(7, exitCode);
        AssertEx.Equal(0, callerContext.Posts);
        AssertEx.True(!probe.HandlerSawContext, "The handler ran on the caller's synchronization context.");
        AssertEx.True(ReferenceEquals(callerContext, afterRun), "Run did not restore the caller's synchronization context.");
        // The continuation runs on the caller's thread with its context restored.
        AssertEx.True(ReferenceEquals(callerContext, probe.ContinuationContext));
        return ValueTask.CompletedTask;
    }

    private static (int ExitCode, int CallingThread) RunOnDedicatedThread(ContinuationProbe probe, string[] args)
    {
        int exitCode = -1;
        int callingThread = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                callingThread = Environment.CurrentManagedThreadId;
                exitCode = App(probe).Run(args, probe.Cancellation.Token);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        AssertEx.True(thread.Join(TimeSpan.FromSeconds(30)), "Run did not return.");
        if (failure is not null) throw new InvalidOperationException("Run failed.", failure);
        return (exitCode, callingThread);
    }

    private static CommandApp App(ContinuationProbe probe) =>
        new(GeneratedCommandCatalog.Create())
        {
            Console = probe.Console,
            ParseSettings = ParseSettings.Default,
            HandleTerminationSignals = false,
            ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(probe)),
        };

    [Command("continue")]
    internal static async Task<CommandOutcome<string>> Continue(CommandExecutionContext context, [FromServices] ContinuationProbe probe,
        [Option("--fail")] bool fail = false, [Option("--cancel")] bool cancel = false, [Option("--ignore-cancel")] bool ignoreCancel = false,
        [Option("--force")] bool force = false, [Option("--twice")] bool twice = false, CancellationToken cancellationToken = default)
    {
        probe.HandlerSawContext = SynchronizationContext.Current is not null;
        await Task.Yield();
        probe.HandlerSawContext |= SynchronizationContext.Current is not null;
        probe.HandlerThread = Environment.CurrentManagedThreadId;
        probe.CouldContinue = context.CanContinueOnCallingThread;
        if (context.CanContinueOnCallingThread || force)
        {
            context.ContinueOnCallingThread(() =>
            {
                probe.ContinuationThread = Environment.CurrentManagedThreadId;
                probe.ContinuationContext = SynchronizationContext.Current;
                probe.OutputAtContinuation = probe.Console.StandardOutput;
                return probe.ExitCode;
            });
            probe.Registered = true;
            if (twice) context.ContinueOnCallingThread(() => 0);
        }

        if (cancel)
        {
            await probe.Cancellation.CancelAsync();
            if (!ignoreCancel) cancellationToken.ThrowIfCancellationRequested();
        }

        return fail
            ? CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("TEST0001", "The command failed."))
            : CommandOutcome.Success("started");
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);

        // Never runs the callback: the thread that owns this context is blocked in Run.
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);
    }
}

internal sealed class ContinuationProbe
{
    internal TestCommandConsole Console { get; } = new();
    internal CancellationTokenSource Cancellation { get; } = new();
    internal int ExitCode { get; init; }
    internal bool CouldContinue { get; set; }
    internal bool Registered { get; set; }
    internal bool HandlerSawContext { get; set; }
    internal int? HandlerThread { get; set; }
    internal int? ContinuationThread { get; set; }
    internal SynchronizationContext? ContinuationContext { get; set; }
    internal string? OutputAtContinuation { get; set; }
}
