using System.Diagnostics;

namespace Runic.CommandLine.Tests;

/// <summary>
/// Sends real signals to a child copy of this test executable. The child starts through
/// <c>env --default-signal=INT,QUIT</c> because background jobs of a noninteractive shell inherit
/// SIGINT and SIGQUIT as ignored, and an ignored signal never reaches the runtime's handlers.
/// </summary>
[NotInParallel]
internal sealed class SignalTests
{
    private const string ChildSwitch = "--signal-test-child";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Test, DisplayName("signals/sigint-cancels-the-invocation")]
    public ValueTask SigintCancelsTheInvocation() => Cancels("INT");

    [Test, DisplayName("signals/sigterm-cancels-the-invocation")]
    public ValueTask SigtermCancelsTheInvocation() => Cancels("TERM");

    [Test, DisplayName("signals/sigquit-cancels-the-invocation")]
    public ValueTask SigquitCancelsTheInvocation() => Cancels("QUIT");

    [Test, DisplayName("signals/second-sigint-forces-exit-130")]
    public ValueTask SecondSigintForcesExit130() => ForcesExit("INT", "INT", 130);

    [Test, DisplayName("signals/second-signal-after-sigterm-forces-exit")]
    public ValueTask SecondSignalAfterSigtermForcesExit() => ForcesExit("TERM", "TERM", 143);

    [Test, DisplayName("signals/protected-shutdown-ignores-a-second-sigint-until-cleanup-ends")]
    public ValueTask ProtectedShutdownIgnoresSecondSigint() => ProtectedShutdown("INT");

    // A forced exit after SIGTERM would be 143, so 130 shows that the cancelled invocation ended normally.
    [Test, DisplayName("signals/protected-shutdown-ignores-sigterm-and-exits-130")]
    public ValueTask ProtectedShutdownIgnoresSigterm() => ProtectedShutdown("TERM");

    public static bool IsChildInvocation(string[] args) => args.Length == 2 && args[0] == ChildSwitch;

    public static async Task<int> RunChildAsync(string mode)
    {
        bool stubborn = mode == "stubborn";
        var handler = new TestHandler(async (_, context, cancellationToken) =>
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(static () => WriteLine("cancelling"));
            WriteLine("ready");
            if (mode is "protected" or "unhandled-protected")
            {
                // Cleanup that must finish: it runs under ProtectShutdown until the test releases it on standard input.
                using (context.ProtectShutdown())
                {
                    WriteLine("protected");
                    if (mode == "protected")
                    {
                        try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }

                    WriteLine("cleaning");
                    string? release = await Console.In.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                    WriteLine(release is null ? "cleaned without release" : "cleaned");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            await Task.Delay(Timeout.Infinite, stubborn ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new TestResult(0, "unreachable"));
        });
        CommandCatalog catalog = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("wait", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();
        return await new CommandApp(catalog)
        {
            Name = "signals", ParseSettings = ParseSettings.Default, HandleTerminationSignals = !mode.StartsWith("unhandled", StringComparison.Ordinal),
        }.RunAsync(["wait"]).ConfigureAwait(false);
    }

    private static void WriteLine(string text)
    {
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }

    private static async ValueTask Cancels(string signal)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var child = await SignalChild.StartAsync("graceful");
        await child.SendAsync(signal);
        AssertEx.Equal(130, await child.WaitForExitAsync(), $"SIG{signal} exit code");
        AssertEx.True(child.Lines.Contains("cancelling"), $"SIG{signal} did not cancel the invocation token.");
    }

    private static async ValueTask ForcesExit(string first, string second, int expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var child = await SignalChild.StartAsync("stubborn");
        await child.SendAsync(first);
        await child.WaitForLineAsync("cancelling");
        AssertEx.True(!child.HasExited, $"SIG{first} must only request cancellation.");
        await child.SendAsync(second);
        AssertEx.Equal(expected, await child.WaitForExitAsync(), $"SIG{first} then SIG{second} exit code");
    }

    private static async ValueTask ProtectedShutdown(string second)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var child = await SignalChild.StartAsync("protected");
        await child.SendAsync("INT");
        await child.WaitForLineAsync("cleaning");
        await child.SendAsync(second);
        // Give a forced exit time to happen before cleanup is released.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        AssertEx.True(!child.HasExited, $"SIG{second} must not exit while shutdown is protected.");
        await child.ReleaseAsync();
        AssertEx.Equal(CommandExitCodes.Cancelled, await child.WaitForExitAsync(), $"SIGINT then SIG{second} exit code");
        AssertEx.Equal(130, CommandExitCodes.Cancelled);
        AssertEx.True(child.Lines.Contains("cleaned"), "The protected cleanup did not finish.");
    }

    [Test, DisplayName("signals/protected-shutdown-without-signal-handling-has-no-effect")]
    public async ValueTask ProtectedShutdownWithoutSignalHandling()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var child = await SignalChild.StartAsync("unhandled-protected");
        await child.WaitForLineAsync("cleaning");
        await child.SendAsync("TERM");
        AssertEx.Equal(143, await child.WaitForExitAsync(), "Default SIGTERM exit code");
        AssertEx.True(!child.Lines.Contains("cleaned"), "The runtime's default SIGTERM handling must end the process.");
    }

    [Test, DisplayName("signals/protect-shutdown-is-a-no-op-scope-without-command-app-signal-handling")]
    public async ValueTask ProtectShutdownIsNoOpScope()
    {
        var scopes = new List<IDisposable>();
        var handler = new TestHandler((_, context, _) =>
        {
            scopes.Add(context.ProtectShutdown());
            scopes.Add(context.ProtectShutdown());
            foreach (IDisposable scope in scopes) scope.Dispose();
            foreach (IDisposable scope in scopes) scope.Dispose();
            return ValueTask.FromResult(CommandOutcome.Success(new TestResult(0, "ok")));
        });
        CommandCatalog catalog = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("wait", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();

        // A directly used executor.
        ParsedInvocation invocation = FixtureCatalog.ParseInvocation(catalog, "wait");
        CommandExecutionResult direct = await new CommandExecutor(new TrackingScopeFactory()).ExecuteAsync(
            new CommandExecutionRequest(invocation, new Runic.CommandLine.Testing.TestCommandConsole(), System.Globalization.CultureInfo.InvariantCulture, "direct"),
            new CommandOutputDispatcher());
        AssertEx.Equal(CommandExitCategory.Success, direct.ExitCategory);

        // The hosting adapter.
        var adapter = new Runic.CommandLine.Hosting.CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));
        Runic.CommandLine.Hosting.HostedCommandLineExecutionResult hosted = await adapter.ExecuteAsync(new(
            adapter.Classify(new(["wait"])), new Runic.CommandLine.Testing.TestCommandConsole(), System.Globalization.CultureInfo.InvariantCulture, "hosted", new CommandOutputDispatcher()));
        AssertEx.Equal(0, hosted.ExitCode);

        // CommandApp without signal handling.
        AssertEx.Equal(0, await new CommandApp(catalog)
        {
            Console = new Runic.CommandLine.Testing.TestCommandConsole(), ParseSettings = ParseSettings.Default, HandleTerminationSignals = false,
        }.RunAsync(["wait"]));

        // Every call returned the same inert scope, which counts nothing.
        AssertEx.Equal(6, scopes.Count);
        AssertEx.True(scopes.All(scope => ReferenceEquals(scope, scopes[0])), "ProtectShutdown must return the shared no-op scope.");
    }

    [Test, DisplayName("signals/opt-out-keeps-default-sigterm-termination")]
    public async ValueTask OptOutKeepsDefault()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var child = await SignalChild.StartAsync("unhandled");
        await child.SendAsync("TERM");
        AssertEx.Equal(143, await child.WaitForExitAsync(), "Default SIGTERM exit code");
        AssertEx.True(!child.Lines.Contains("cancelling"), "HandleTerminationSignals = false must not cancel the invocation.");
    }

    private sealed class SignalChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly List<string> _lines = [];
        private readonly SemaphoreSlim _lineAdded = new(0);

        private SignalChild(Process process) => _process = process;

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }

        public bool HasExited => _process.HasExited;

        public static async Task<SignalChild> StartAsync(string mode)
        {
            var start = new ProcessStartInfo("env") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { "--default-signal=INT,QUIT", Environment.ProcessPath!, ChildSwitch, mode }) start.ArgumentList.Add(argument);
            var child = new SignalChild(new Process { StartInfo = start });
            child._process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (child._lines) child._lines.Add(e.Data);
                child._lineAdded.Release();
            };
            child._process.ErrorDataReceived += static (_, _) => { };
            child._process.Start();
            child._process.BeginOutputReadLine();
            child._process.BeginErrorReadLine();
            try
            {
                await child.WaitForLineAsync("ready").ConfigureAwait(false);
            }
            catch
            {
                await child.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return child;
        }

        public async Task WaitForLineAsync(string line)
        {
            using var timeout = new CancellationTokenSource(Deadline);
            while (!Lines.Contains(line))
            {
                try { await _lineAdded.WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new TimeoutException($"The child did not write '{line}'. Output: {string.Join(" | ", Lines)}"); }
            }
        }

        public async Task SendAsync(string signal)
        {
            using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-s", signal, _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) } })!;
            await kill.WaitForExitAsync().ConfigureAwait(false);
            AssertEx.Equal(0, kill.ExitCode, $"kill -s {signal}");
        }

        public async Task ReleaseAsync()
        {
            await _process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        public async Task<int> WaitForExitAsync()
        {
            using var timeout = new CancellationTokenSource(Deadline);
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new TimeoutException($"The child did not exit. Output: {string.Join(" | ", Lines)}"); }
            return _process.ExitCode;
        }

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.Dispose();
            _lineAdded.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
