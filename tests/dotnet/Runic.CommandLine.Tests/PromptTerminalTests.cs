using System.Diagnostics;
using System.Text;

namespace Runic.CommandLine.Tests;

/// <summary>
/// Runs a prompt in a child copy of this test executable on a real pseudo-terminal, through util-linux
/// <c>script</c>, and answers it with the bytes a terminal sends: arrow keys, Enter and Ctrl+C. This covers what
/// the in-memory cases cannot: the runtime's key decoding, terminal detection, and Ctrl+C arriving as SIGINT from the
/// terminal driver. The cases run on Linux when <c>script</c> is installed and are skipped otherwise.
/// </summary>
[NotInParallel]
internal sealed class PromptTerminalTests
{
    private const string ChildSwitch = "--prompt-terminal-child";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static bool IsChildInvocation(string[] args) => args.Length is 1 or 2 && args[0] == ChildSwitch;

    public static async Task<int> RunChildAsync(bool handleSignals = true)
    {
        CommandPromptChoice<string>[] choices = [new("web", "web"), new("cli", "cli"), new("lib", "lib")];
        var handler = new TestHandler(async (_, context, cancellationToken) =>
        {
            var prompts = new CommandPrompts(context);
            string template = await prompts.SelectAsync("Template", choices, cancellationToken: cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new TestResult(0, "picked " + template));
        });
        CommandCatalog catalog = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("ask", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();
        return await new CommandApp(catalog) { Name = "prompts", ParseSettings = ParseSettings.Default, HandleTerminationSignals = handleSignals }
            .RunAsync(["ask"]).ConfigureAwait(false);
    }

    [Test, DisplayName("prompts/pty-arrow-keys-and-enter-answer-a-select")]
    public async ValueTask ArrowKeysAnswer()
    {
        await using TerminalChild? child = await TerminalChild.StartAsync();
        if (child is null) return;
        await child.WaitForOutputAsync("Arrow keys");
        await child.SendAsync("\u001b[B\u001b[B\u001b[A\r");
        AssertEx.Equal(0, await child.WaitForExitAsync());
        string output = child.Output;
        AssertEx.True(output.Contains("0:picked cli", StringComparison.Ordinal), output);
        AssertEx.True(output.Contains("\u001b[?25l", StringComparison.Ordinal) && output.Contains("\u001b[?25h", StringComparison.Ordinal), output);
    }

    [Test, DisplayName("prompts/pty-ctrl-c-cancels-with-exit-code-130-and-restores-the-cursor")]
    public async ValueTask CtrlCCancels()
    {
        await using TerminalChild? child = await TerminalChild.StartAsync();
        if (child is null) return;
        await child.WaitForOutputAsync("Arrow keys");
        await child.SendAsync("\u0003");
        AssertEx.Equal(CommandExitCodes.Cancelled, await child.WaitForExitAsync());
        string output = child.Output;
        int hidden = output.LastIndexOf("\u001b[?25l", StringComparison.Ordinal);
        AssertEx.True(hidden >= 0 && output.IndexOf("\u001b[?25h", hidden, StringComparison.Ordinal) > hidden, output);
        AssertEx.True(output.Contains("RCLI4000", StringComparison.Ordinal), output);
    }

    [Test, DisplayName("prompts/pty-no-color-asks-a-plain-line-question")]
    public async ValueTask NoColorAsksPlainLines()
    {
        await using TerminalChild? child = await TerminalChild.StartAsync(noColor: "1");
        if (child is null) return;
        await child.WaitForOutputAsync("Enter a number from 1 to 3: ");
        await child.SendAsync("lib\r");
        AssertEx.Equal(0, await child.WaitForExitAsync());
        string output = child.Output;
        AssertEx.True(output.Contains("Template\r\n  1. web\r\n  2. cli\r\n  3. lib\r\n", StringComparison.Ordinal), output);
        AssertEx.True(output.Contains("0:picked lib", StringComparison.Ordinal), output);
        AssertEx.True(!output.Contains("\u001b[?25l", StringComparison.Ordinal), output);
    }

    [Test, DisplayName("prompts/pty-ctrl-c-cancels-a-plain-line-question-without-enter")]
    public async ValueTask CtrlCCancelsPlainLine()
    {
        await using TerminalChild? child = await TerminalChild.StartAsync(noColor: "1");
        if (child is null) return;
        await child.WaitForOutputAsync("Enter a number from 1 to 3: ");
        await child.SendAsync("\u0003");
        AssertEx.Equal(CommandExitCodes.Cancelled, await child.WaitForExitAsync());
        AssertEx.True(child.Output.Contains("RCLI4000", StringComparison.Ordinal), child.Output);
    }

    [Test, DisplayName("prompts/pty-asks-when-standard-output-is-piped")]
    public async ValueTask AsksWhenStandardOutputIsPiped()
    {
        // Prompts draw on standard error, so a command whose result is captured, as in cd "$(tool pick-dir)", still asks.
        await using TerminalChild? child = await TerminalChild.StartAsync(redirection: "| sed 's/^/piped:/'");
        if (child is null) return;
        await child.WaitForOutputAsync("Arrow keys");
        await child.SendAsync("\u001b[B\r");
        AssertEx.Equal(0, await child.WaitForExitAsync());
        AssertEx.True(child.Output.Contains("piped:0:picked cli", StringComparison.Ordinal), child.Output);
    }

    [Test, DisplayName("prompts/pty-cannot-ask-when-standard-error-is-redirected")]
    public async ValueTask CannotAskWhenStandardErrorIsRedirected()
    {
        // The question would be invisible, so the prompt fails with RCLI4001 (exit code 2) instead of waiting.
        await using TerminalChild? child = await TerminalChild.StartAsync(redirection: "2>/dev/null; echo \"exit=$?\"");
        if (child is null) return;
        await child.WaitForExitAsync();
        AssertEx.True(child.Output.Contains("exit=2", StringComparison.Ordinal), child.Output);
        AssertEx.True(!child.Output.Contains("Template", StringComparison.Ordinal), child.Output);
    }

    [Test, DisplayName("prompts/pty-ctrl-c-without-signal-handling-still-restores-the-cursor")]
    public async ValueTask CtrlCWithoutSignalHandling()
    {
        await using TerminalChild? child = await TerminalChild.StartAsync(mode: "unhandled");
        if (child is null) return;
        await child.WaitForOutputAsync("Arrow keys");
        await child.SendAsync("\u0003");
        // SIGINT ends the process (128 + 2, the same number as a cancelled invocation), so the
        // invocation was not cancelled when no cancellation fault was written.
        AssertEx.Equal(130, await child.WaitForExitAsync());
        string output = child.Output;
        AssertEx.True(!output.Contains("RCLI4000", StringComparison.Ordinal), output);
        int hidden = output.LastIndexOf("\u001b[?25l", StringComparison.Ordinal);
        AssertEx.True(hidden >= 0 && output.IndexOf("\u001b[?25h", hidden, StringComparison.Ordinal) > hidden, output);
    }

    private sealed class TerminalChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly SemaphoreSlim _changed = new(0);
        private Task? _reader;

        private TerminalChild(Process process) => _process = process;

        public string Output { get { lock (_output) return _output.ToString(); } }

        public static Task<TerminalChild?> StartAsync(string? noColor = null, string mode = "", string redirection = "")
        {
            if (!OperatingSystem.IsLinux() || FindScript() is not { } script) return Task.FromResult<TerminalChild?>(null);
            // script runs the command with sh -c on a new pseudo-terminal and copies its output to ours. env restores
            // SIGINT, which a noninteractive parent shell may have left ignored, so Ctrl+C reaches the runtime.
            string childCommand = "env --default-signal=INT " + Quote(Environment.ProcessPath!) + " " + ChildSwitch + (mode.Length > 0 ? " " + mode : "");
            string command = redirection.Length == 0 ? "exec " + childCommand : childCommand + " " + redirection;
            var start = new ProcessStartInfo(script)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string argument in new[] { "--quiet", "--flush", "--return", "--command", command, "/dev/null" }) start.ArgumentList.Add(argument);
            start.Environment["TERM"] = "xterm-256color";
            start.Environment["LANG"] = "C.UTF-8";
            if (noColor is null) start.Environment.Remove("NO_COLOR");
            else start.Environment["NO_COLOR"] = noColor;
            start.Environment.Remove("RUNIC_COMMANDLINE_NO_INPUT");
            start.Environment.Remove("RUNIC_COMMANDLINE_OUTPUT");
            var child = new TerminalChild(new Process { StartInfo = start });
            child._process.Start();
            child._process.ErrorDataReceived += static (_, _) => { };
            child._process.BeginErrorReadLine();
            child._reader = child.ReadAsync();
            return Task.FromResult<TerminalChild?>(child);
        }

        private static string? FindScript()
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory, "script");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private async Task ReadAsync()
        {
            var buffer = new char[4096];
            int read;
            while ((read = await _process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_output) _output.Append(buffer, 0, read);
                _changed.Release();
            }
        }

        public async Task WaitForOutputAsync(string text)
        {
            using var timeout = new CancellationTokenSource(Deadline);
            while (!Output.Contains(text, StringComparison.Ordinal))
            {
                if (_process.HasExited && _reader!.IsCompleted) throw new InvalidOperationException($"The child exited with {_process.ExitCode} before writing '{text}'. Output: {Output}");
                try { await _changed.WaitAsync(TimeSpan.FromMilliseconds(200), timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new TimeoutException($"The child did not write '{text}'. Output: {Output}"); }
            }

            // Let the prompt reach its key read after drawing.
            await Task.Delay(200).ConfigureAwait(false);
        }

        public async Task SendAsync(string keys)
        {
            await _process.StandardInput.WriteAsync(keys).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        public async Task<int> WaitForExitAsync()
        {
            using var timeout = new CancellationTokenSource(Deadline);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await _reader!.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw new TimeoutException($"The child did not exit. Output: {Output}"); }
            return _process.ExitCode;
        }

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.Dispose();
            _changed.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
