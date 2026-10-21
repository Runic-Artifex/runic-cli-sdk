using System.Text.RegularExpressions;
using Runic.CommandLine.Processes;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

/// <summary>
/// The documented recipe that connects a child process's live output to a task log: a four-line
/// <see cref="IProcessOutputHandler"/> passes each line to <see cref="CommandTaskLog.WriteLineAsync"/>. The child is
/// this test executable, which writes a few lines to both pipes.
/// </summary>
internal sealed partial class PromptProcessTests
{
    private const string ChildSwitch = "--prompt-process-child";
    private static readonly string[] BuildLines = ["building 1", "building 2", "building 3"];

    public static bool IsChildInvocation(string[] args) => args.Length == 1 && args[0] == ChildSwitch;

    public static int RunChild()
    {
        for (int index = 1; index <= 3; index++)
        {
            Console.Out.WriteLine("building " + index);
            Console.Out.Flush();
        }

        Console.Error.WriteLine("warning: \u001b[33mdeprecated\u001b[0m");
        return 0;
    }

    // The recipe from the Runic.CommandLine and Runic.CommandLine.Processes READMEs.
    private sealed class TaskLogLines(CommandTaskLog log) : IProcessOutputHandler
    {
        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken) =>
            log.WriteLineAsync(line.Text, cancellationToken);
    }

    [GeneratedRegex("\u001b\\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiPattern();

    private static CommandApp App(TestCommandConsole console)
    {
        var handler = new TestHandler(async (_, context, cancellationToken) =>
        {
            string executable = Environment.ProcessPath!;
            var runner = new ProcessRunner(new LocalExecutablePolicy(executableRoots: [Path.GetDirectoryName(executable)!]));
            var prompts = new CommandPrompts(context);
            await using CommandTaskLog log = await prompts.StartTaskLogAsync("Build", 2, cancellationToken);
            ProcessResult result = await runner.RunAsync(new ProcessRequest(executable, [ChildSwitch]), new TaskLogLines(log), cancellationToken);
            if (result.State == ProcessState.Exited && result.ExitCode == 0) await log.SucceedAsync("Built", cancellationToken: cancellationToken);
            else await log.FailAsync("Build failed", cancellationToken: cancellationToken);
            return CommandOutcome.Success(new TestResult(result.ExitCode ?? -1, "done"));
        });
        CommandCatalog catalog = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("build", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();
        return new CommandApp(catalog) { Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false };
    }

    [Test, DisplayName("prompts/process-output-reaches-a-plain-task-log")]
    public async ValueTask PlainTaskLog()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["build"]));
        AssertEx.Equal("0:done\n", console.StandardOutput);
        string[] lines = console.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AssertEx.Equal("Build", lines[0]);
        AssertEx.Equal("Build: Built", lines[^1]);
        // The two pipes interleave in read order; each keeps its own order.
        AssertEx.SequenceEqual(BuildLines, lines.Where(line => line.StartsWith("building", StringComparison.Ordinal)));
        AssertEx.True(lines.Contains("warning: deprecated"), console.StandardError);
    }

    [Test, DisplayName("prompts/process-output-reaches-a-terminal-task-log-that-clears-on-success")]
    public async ValueTask TerminalTaskLog()
    {
        var console = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true };
        AssertEx.Equal(0, await App(console).RunAsync(["build"]));
        string last = AnsiPattern().Replace(console.StandardError.Split("\u001b[J")[^1], string.Empty);
        AssertEx.Equal("◇  Build  Built\n", last);
    }
}
