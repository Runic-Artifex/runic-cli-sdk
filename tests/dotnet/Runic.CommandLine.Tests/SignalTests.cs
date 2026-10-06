using System.Diagnostics;

namespace Runic.CommandLine.Tests;

/// <summary>
/// Sends real signals to a child copy of this test executable. The child starts through
/// <c>env --default-signal=INT,QUIT</c> because background jobs of a noninteractive shell inherit
/// SIGINT and SIGQUIT as ignored, and an ignored signal never reaches the runtime's handlers.
/// </summary>
internal static class SignalTests
{
    private const string ChildSwitch = "--signal-test-child";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("signals/sigint-cancels-the-invocation", () => Cancels("INT")),
        new("signals/sigterm-cancels-the-invocation", () => Cancels("TERM")),
        new("signals/sigquit-cancels-the-invocation", () => Cancels("QUIT")),
        new("signals/second-sigint-forces-exit-130", () => ForcesExit("INT", "INT", 130)),
        new("signals/second-signal-after-sigterm-forces-exit", () => ForcesExit("TERM", "TERM", 143)),
        new("signals/opt-out-keeps-default-sigterm-termination", OptOutKeepsDefault),
    ];

    public static bool IsChildInvocation(string[] args) => args.Length == 2 && args[0] == ChildSwitch;

    public static async Task<int> RunChildAsync(string mode)
    {
        bool stubborn = mode == "stubborn";
        var handler = new TestHandler(async (_, _, cancellationToken) =>
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(static () => WriteLine("cancelling"));
            WriteLine("ready");
            await Task.Delay(Timeout.Infinite, stubborn ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new TestResult(0, "unreachable"));
        });
        CommandCatalog catalog = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("wait", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();
        return await new CommandApp(catalog)
        {
            Name = "signals", ParseSettings = ParseSettings.Default, HandleCancelKeyPress = mode != "unhandled",
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
        AssertEx.Equal(CommandExitCodes.Cancelled, await child.WaitForExitAsync(), $"SIG{signal} exit code");
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

    private static async ValueTask OptOutKeepsDefault()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var child = await SignalChild.StartAsync("unhandled");
        await child.SendAsync("TERM");
        AssertEx.Equal(143, await child.WaitForExitAsync(), "Default SIGTERM exit code");
        AssertEx.True(!child.Lines.Contains("cancelling"), "HandleCancelKeyPress = false must not cancel the invocation.");
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
            var start = new ProcessStartInfo("env") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
            await child.WaitForLineAsync("ready").ConfigureAwait(false);
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
