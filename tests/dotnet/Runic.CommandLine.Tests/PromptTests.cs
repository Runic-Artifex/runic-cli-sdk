using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Runic.CommandLine.Spectre;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal sealed partial class PromptTests
{
    private const string ShowCursor = "\u001b[?25h", HideCursor = "\u001b[?25l";

    private static readonly CommandPromptChoice<string>[] Templates =
        [new("web", "web", "Web app"), new("cli", "cli"), new("lib", "lib")];

    [GeneratedRegex("\u001b\\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiPattern();

    private static string Plain(string text) => AnsiPattern().Replace(text, string.Empty);

    // The frames a redrawn prompt wrote: each redraw starts by erasing the previous one.
    private static string[] Frames(string standardError) => standardError.Split("\u001b[J").Select(frame => Plain(frame).Replace("\r", string.Empty, StringComparison.Ordinal)).ToArray();

    private static CommandApp App(ICommandConsole console, Func<CommandPrompts, CancellationToken, ValueTask<string>> body,
        ParseSettings? settings = null, ICommandTextResolver? resolver = null, bool interactive = true, bool jsonAlias = false)
    {
        var handler = new TestHandler(async (_, context, cancellationToken) =>
            CommandOutcome.Success(new TestResult(0, await body(new CommandPrompts(context, interactive), cancellationToken))));
        var builder = new CommandCatalogBuilder();
        if (jsonAlias) builder.JsonOutputAlias("--json");
        CommandCatalog catalog = builder.Command<TestOptions, TestHandler, TestResult>("ask", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();
        return new CommandApp(catalog)
        {
            Name = "prompts", Console = console, ParseSettings = settings ?? ParseSettings.Default, HandleCancelKeyPress = false,
            TextResolver = resolver, Culture = CultureInfo.InvariantCulture,
        };
    }

    private static TestCommandConsole Interactive() => new() { IsInteractive = true };

    private static TestCommandConsole Terminal() => new() { IsInteractive = true, SupportsAnsi = true };

    [Test, DisplayName("prompts/line-select-lists-numbered-choices-and-asks-again")]
    public async ValueTask LineSelect()
    {
        TestCommandConsole console = Interactive();
        console.QueueInput("9");
        console.QueueInput("CLI");
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("0:cli\n", console.StandardOutput);
        AssertEx.Equal("Template\n  1. web - Web app\n  2. cli\n  3. lib\nEnter a number from 1 to 3: Enter a number from 1 to 3: ", console.StandardError);

        console = Interactive();
        console.QueueInput("");
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, 2, token)).RunAsync(["ask"]));
        AssertEx.Equal("0:lib\n", console.StandardOutput);
        AssertEx.True(console.StandardError.EndsWith("Enter a number from 1 to 3 [lib]: ", StringComparison.Ordinal), console.StandardError);
    }

    [Test, DisplayName("prompts/line-multi-select-confirm-and-text")]
    public async ValueTask LinePrompts()
    {
        TestCommandConsole console = Interactive();
        console.QueueInput("1, lib");
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
            string.Join("+", await prompts.MultiSelectAsync("Parts", Templates, [1], cancellationToken: token))).RunAsync(["ask"]));
        AssertEx.Equal("0:web+lib\n", console.StandardOutput);
        AssertEx.True(console.StandardError.EndsWith("separated by commas [2]: ", StringComparison.Ordinal), console.StandardError);

        console = Interactive();
        console.QueueInput("");
        console.QueueInput("2");
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
            string.Join("+", await prompts.MultiSelectAsync("Parts", Templates, required: true, cancellationToken: token))).RunAsync(["ask"]));
        AssertEx.Equal("0:cli\n", console.StandardOutput);
        AssertEx.True(console.StandardError.Contains(": An answer is required.\n", StringComparison.Ordinal), console.StandardError);

        console = Interactive();
        console.QueueInput("maybe");
        console.QueueInput("n");
        console.QueueInput("");
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
            $"{await prompts.ConfirmAsync("Delete?", cancellationToken: token)},{await prompts.ConfirmAsync("Keep?", true, token)}").RunAsync(["ask"]));
        AssertEx.Equal("0:False,True\n", console.StandardOutput);
        AssertEx.Equal("Delete? [y/n]: Answer Yes or No.\nDelete? [y/n]: Keep? [Y/n]: ", console.StandardError);

        console = Interactive();
        console.QueueInput("");
        console.QueueInput("Bad Name");
        console.QueueInput("good-name");
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.TextAsync("Name",
            validate: value => value.Length == 0 ? "Enter a name." : value.Contains(' ', StringComparison.Ordinal) ? "Use no spaces." : null,
            cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("0:good-name\n", console.StandardOutput);
        AssertEx.Equal("Name: Enter a name.\nName: Use no spaces.\nName: ", console.StandardError);

        console = Interactive();
        console.QueueInput("");
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.TextAsync("Name", "app", cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("0:app\n", console.StandardOutput);
        AssertEx.Equal("Name [app]: ", console.StandardError);
    }

    [Test, DisplayName("prompts/tester-scripts-answers-with-a-console-factory")]
    public async ValueTask TesterScriptsAnswers()
    {
        var tester = new CommandAppTester(console => App(console, async (prompts, token) =>
            await prompts.SelectAsync("Template", Templates, cancellationToken: token) + "/" + await prompts.TextAsync("Name", cancellationToken: token)))
        {
            CreateConsole = () =>
            {
                var console = new TestCommandConsole { IsInteractive = true };
                console.QueueInput("2");
                console.QueueInput("demo");
                return console;
            },
        };
        CommandAppTestResult result = await tester.RunAsync(["ask"]);
        AssertEx.Equal(0, result.ExitCode);
        AssertEx.Equal("0:cli/demo\n", result.StandardOutput);

        // A test that keeps its console can check that every queued answer was read.
        var console = new TestCommandConsole { IsInteractive = true };
        console.QueueInput("1");
        console.QueueInput("unused");
        console.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal(1, console.PendingInputCount);
        AssertEx.Equal(1, console.PendingKeyCount);
    }

    [Test, DisplayName("prompts/noninteractive-uses-defaults-silently")]
    public async ValueTask NoninteractiveUsesDefaults()
    {
        var console = new TestCommandConsole();
        console.QueueInput("ignored");
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            AssertEx.True(!prompts.IsInteractive);
            string template = await prompts.SelectAsync("Template", Templates, 1, token);
            IReadOnlyList<string> parts = await prompts.MultiSelectAsync("Parts", Templates, cancellationToken: token);
            bool confirmed = await prompts.ConfirmAsync("Continue?", false, token);
            string name = await prompts.TextAsync("Name", "app", cancellationToken: token);
            return $"{template},{parts.Count},{confirmed},{name}";
        }).RunAsync(["ask"]));
        AssertEx.Equal("0:cli,0,False,app\n", console.StandardOutput);
        AssertEx.Equal(string.Empty, console.StandardError);
    }

    [Test, DisplayName("prompts/noninteractive-without-default-fails-with-rcli4001")]
    public async ValueTask NoninteractiveWithoutDefaultFails()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console, (prompts, token) => prompts.TextAsync("Project name", cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal(string.Empty, console.StandardOutput);
        AssertEx.Equal("RCLI4001: Project name needs an answer, but the command is not running in an interactive terminal. Pass the value on the command line.\n",
            console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console, async (prompts, token) =>
            string.Join(",", await prompts.MultiSelectAsync("Parts", Templates, required: true, cancellationToken: token))).RunAsync(["ask"]));
        AssertEx.True(console.StandardError.StartsWith("RCLI4001: Parts needs an answer", StringComparison.Ordinal), console.StandardError);
    }

    [Test, DisplayName("prompts/json-output-never-prompts")]
    public async ValueTask JsonOutputNeverPrompts()
    {
        foreach (string[] args in new[] { new[] { "ask", "--output=json" }, ["ask", "--json"] })
        {
            // Even an interactive terminal with queued answers is not asked.
            TestCommandConsole console = Terminal();
            console.QueueInput("2");
            console.QueueKey(ConsoleKey.DownArrow);
            AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, 0, token), jsonAlias: true).RunAsync(args));
            using (JsonDocument frame = CommandTestEnvelope.Parse(console.StandardOutput))
                AssertEx.Equal("web", frame.RootElement.GetProperty("payload").GetProperty("Text").GetString());
            AssertEx.Equal(string.Empty, console.StandardError);

            console = Terminal();
            AssertEx.Equal(CommandExitCodes.Usage, await App(console, async (prompts, token) =>
                (await prompts.ConfirmAsync("Continue?", cancellationToken: token)).ToString(), jsonAlias: true).RunAsync(args));
            using JsonDocument failure = CommandTestEnvelope.Parse(console.StandardOutput);
            JsonElement root = failure.RootElement;
            AssertEx.Equal(2, root.GetProperty("exitCode").GetInt32());
            AssertEx.Equal("RCLI4001", root.GetProperty("fault").GetProperty("code").GetString());
            JsonElement diagnostic = root.GetProperty("diagnostics")[0];
            AssertEx.Equal("prompt-json-output", diagnostic.GetProperty("kind").GetString());
            AssertEx.Equal("diagnostics.prompt-json-output", diagnostic.GetProperty("messageKey").GetString());
            AssertEx.Equal("Continue?", diagnostic.GetProperty("arguments")[0].GetString());
            AssertEx.Equal(string.Empty, console.StandardError);
        }
    }

    [Test, DisplayName("prompts/no-input-variable-and-interactive-false-disable-prompts")]
    public async ValueTask DisabledPrompts()
    {
        foreach (string value in new[] { "1", "true", "YES" })
        {
            TestCommandConsole console = Interactive();
            var settings = new ParseSettings(null) { GetEnvironmentVariable = name => name == "RUNIC_COMMANDLINE_NO_INPUT" ? value : null };
            AssertEx.Equal(CommandExitCodes.Usage, await App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token), settings).RunAsync(["ask"]));
            AssertEx.True(console.StandardError.StartsWith("RCLI4001: Name needs an answer, but prompts are disabled.", StringComparison.Ordinal), console.StandardError);
        }

        TestCommandConsole enabled = Interactive();
        enabled.QueueInput("ada");
        var off = new ParseSettings(null) { GetEnvironmentVariable = name => name == "RUNIC_COMMANDLINE_NO_INPUT" ? "0" : null };
        AssertEx.Equal(0, await App(enabled, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token), off).RunAsync(["ask"]));

        TestCommandConsole yes = Interactive();
        AssertEx.Equal(0, await App(yes, async (prompts, token) => (await prompts.ConfirmAsync("Continue?", true, token)).ToString(), interactive: false).RunAsync(["ask"]));
        AssertEx.Equal("0:True\n", yes.StandardOutput);
        AssertEx.Equal(string.Empty, yes.StandardError);
    }

    [Test, DisplayName("prompts/text-resolves-through-the-text-resolver")]
    public async ValueTask TextResolver()
    {
        var resolver = new DictionaryResolver(new Dictionary<string, string>
        {
            [CommandTextKeys.PromptYes] = "Ja",
            [CommandTextKeys.PromptNo] = "Nein",
            [CommandTextKeys.PromptAnswerYesNo] = "Antworte {0} oder {1}.",
            [CommandTextKeys.PromptChooseOne] = "Zahl von 1 bis {0}",
            [CommandTextKeys.DiagnosticPromptRequiresTerminal] = "{0} braucht eine Antwort.",
            ["app.template"] = "Vorlage",
        });
        TestCommandConsole console = Interactive();
        console.QueueInput("x");
        console.QueueInput("j");
        console.QueueInput("2");
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            bool confirmed = await prompts.ConfirmAsync("Weiter?", false, token);
            return confirmed + "," + await prompts.SelectAsync("Vorlage", Templates, cancellationToken: token);
        }, resolver: resolver).RunAsync(["ask"]));
        AssertEx.Equal("0:True,cli\n", console.StandardOutput);
        AssertEx.True(console.StandardError.StartsWith("Weiter? [j/N]: Antworte Ja oder Nein.\nWeiter? [j/N]: Vorlage\n", StringComparison.Ordinal), console.StandardError);
        AssertEx.True(console.StandardError.EndsWith("Zahl von 1 bis 3: ", StringComparison.Ordinal), console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token), resolver: resolver).RunAsync(["ask"]));
        AssertEx.Equal("RCLI4001: Name braucht eine Antwort.\n", console.StandardError);

        // Handlers resolve their own prompt text with the same resolver.
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, (prompts, token) => ValueTask.FromResult(
            new CommandTextContext(CultureInfo.InvariantCulture, resolver).Resolve("app.template", "Template")), resolver: resolver).RunAsync(["ask"]));
        AssertEx.Equal("0:Vorlage\n", console.StandardOutput);
    }

    [Test, DisplayName("prompts/terminal-select-moves-with-arrow-keys-and-restores-the-cursor")]
    public async ValueTask TerminalSelect()
    {
        TestCommandConsole console = Terminal();
        console.QueueKey(ConsoleKey.DownArrow);
        console.QueueKey(ConsoleKey.DownArrow);
        console.QueueKey(ConsoleKey.UpArrow);
        console.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("0:cli\n", console.StandardOutput);
        string error = console.StandardError;
        AssertEx.True(error.StartsWith(HideCursor, StringComparison.Ordinal) && error.EndsWith(ShowCursor, StringComparison.Ordinal), error);
        string[] frames = Frames(error);
        AssertEx.Equal("◆  Template\n│  › ● web (Web app)\n│    ○ cli\n│    ○ lib\n└  Arrow keys to move, Enter to confirm", frames[1]);
        AssertEx.True(frames.Any(frame => frame.Contains("│  › ● lib", StringComparison.Ordinal)), error);
        AssertEx.Equal("◇  Template  cli\n", frames[^1]);
        // Each redraw moves back to the first line of the four-line block before erasing it.
        AssertEx.True(error.Contains("\r\u001b[4A\u001b[J", StringComparison.Ordinal), error);
    }

    [Test, DisplayName("prompts/terminal-multi-select-confirm-and-text")]
    public async ValueTask TerminalPrompts()
    {
        TestCommandConsole console = Terminal();
        console.QueueKey(ConsoleKey.Enter);
        console.QueueKey(ConsoleKey.Spacebar);
        console.QueueKey(ConsoleKey.DownArrow);
        console.QueueKey(ConsoleKey.DownArrow);
        console.QueueKey(ConsoleKey.Spacebar);
        console.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
            string.Join("+", await prompts.MultiSelectAsync("Parts", Templates, required: true, cancellationToken: token))).RunAsync(["ask"]));
        AssertEx.Equal("0:web+lib\n", console.StandardOutput);
        string[] frames = Frames(console.StandardError);
        AssertEx.True(frames.Any(frame => frame.EndsWith("└  ▲ An answer is required.", StringComparison.Ordinal)), console.StandardError);
        AssertEx.Equal("◇  Parts  web, lib\n", frames[^1]);

        console = Terminal();
        console.QueueKey(ConsoleKey.Enter);
        console.QueueText("n");
        AssertEx.Equal(0, await App(console, async (prompts, token) => (await prompts.ConfirmAsync("Delete?", cancellationToken: token)).ToString()).RunAsync(["ask"]));
        AssertEx.Equal("0:False\n", console.StandardOutput);
        frames = Frames(console.StandardError);
        AssertEx.Equal("◆  Delete?\n└  ○ Yes / ○ No", frames[1]);
        AssertEx.Equal("◆  Delete?\n└  ○ Yes / ○ No  ▲ Answer Yes or No.", frames[2]);
        AssertEx.Equal("◇  Delete?  No\n", frames[^1]);

        console = Terminal();
        console.QueueText("a b\n");
        console.QueueKey(ConsoleKey.LeftArrow);
        console.QueueKey(ConsoleKey.Backspace);
        console.QueueText("-\n");
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.TextAsync("Name",
            validate: value => value.Contains(' ', StringComparison.Ordinal) ? "Use no spaces." : null, cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("0:a-b\n", console.StandardOutput);
        frames = Frames(console.StandardError);
        AssertEx.True(frames.Any(frame => frame == "◆  Name\n│  a b \n└  ▲ Use no spaces."), console.StandardError);
        AssertEx.Equal("◇  Name  a-b\n", frames[^1]);

        console = Terminal();
        console.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.TextAsync("Name", "app", cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("0:app\n", console.StandardOutput);
        AssertEx.Equal("◆  Name\n│  app\n└", Frames(console.StandardError)[1]);
    }

    [Test, DisplayName("prompts/terminal-uses-ascii-without-unicode-and-cuts-long-lines")]
    public async ValueTask TerminalAsciiAndWidth()
    {
        var console = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true, SupportsUnicode = false, Width = 20 };
        console.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("A rather long question", Templates, cancellationToken: token)).RunAsync(["ask"]));
        string[] frames = Frames(console.StandardError);
        AssertEx.Equal("*  A rather long...\n|  > (*) web (We...\n|    ( ) cli\n|    ( ) lib\n'  Arrow keys to...", frames[1]);
        AssertEx.True(frames[1].Split('\n').All(line => line.Length <= 19), frames[1]);
    }

    [Test, DisplayName("prompts/escape-and-end-of-input-cancel-with-exit-code-4")]
    public async ValueTask EscapeAndEndOfInputCancel()
    {
        TestCommandConsole console = Terminal();
        console.QueueKey(ConsoleKey.Escape);
        AssertEx.Equal(CommandExitCodes.Cancelled, await App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.True(Plain(console.StandardError).EndsWith("■  Template  Cancelled\nRCLI4000: The command was cancelled.\n", StringComparison.Ordinal), console.StandardError);
        AssertEx.True(console.StandardError.Contains(ShowCursor, StringComparison.Ordinal), console.StandardError);

        console = Interactive();
        console.QueueInput(null);
        AssertEx.Equal(CommandExitCodes.Cancelled, await App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.Equal("Name: \nRCLI4000: The command was cancelled.\n", console.StandardError);
    }

    [Test, DisplayName("prompts/ctrl-c-cancels-a-waiting-terminal-prompt-and-restores-the-cursor")]
    public async ValueTask CtrlCCancelsTerminalPrompt()
    {
        var console = new WaitingConsole(Terminal());
        using var cancellation = new CancellationTokenSource();
        Task<int> run = App(console, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"], cancellation.Token);
        await console.Waiting.WaitAsync(TimeSpan.FromSeconds(30));
        // CommandApp cancels this token on SIGINT; the signal itself is covered by the PTY and signal tests.
        await cancellation.CancelAsync();
        AssertEx.Equal(CommandExitCodes.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        string error = console.Inner.StandardError;
        AssertEx.True(error.EndsWith(ShowCursor + "RCLI4000: The command was cancelled.\n", StringComparison.Ordinal), error);
        AssertEx.True(Frames(error)[^1].StartsWith("■  Template  Cancelled\n", StringComparison.Ordinal), error);
    }

    [Test, DisplayName("prompts/ctrl-c-cancels-a-waiting-line-prompt")]
    public async ValueTask CtrlCCancelsLinePrompt()
    {
        var console = new WaitingConsole(Interactive());
        using var cancellation = new CancellationTokenSource();
        Task<int> run = App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token)).RunAsync(["ask"], cancellation.Token);
        await console.Waiting.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        AssertEx.Equal(CommandExitCodes.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.Equal("Name: \nRCLI4000: The command was cancelled.\n", console.Inner.StandardError);
    }

    [Test, DisplayName("prompts/task-log-writes-plain-lines-without-a-terminal")]
    public async ValueTask PlainTaskLog()
    {
        foreach (string[] args in new[] { new[] { "ask" }, ["ask", "--output=json"] })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await App(console, async (prompts, token) =>
            {
                await using CommandTaskLog log = await prompts.StartTaskLogAsync("Build", cancellationToken: token);
                await log.WriteLineAsync("step 1\r\nstep \u001b[31m2\u001b[0m\n", token);
                await log.SetStatusAsync("half", token);
                await log.SetStatusAsync("half", token);
                await log.SucceedAsync(cancellationToken: token);
                await log.WriteLineAsync("after", token);
                await using CommandTaskLog spinner = await prompts.StartSpinnerAsync("Upload", token);
                await spinner.FailAsync("Network down", cancellationToken: token);
                return "ok";
            }).RunAsync(args));
            AssertEx.Equal("Build\nstep 1\nstep 2\nBuild: half\nBuild: Done\nUpload\nUpload: Network down\n", console.StandardError);
        }
    }

    [Test, DisplayName("prompts/task-log-shows-recent-lines-and-clears-them-on-success")]
    public async ValueTask TerminalTaskLog()
    {
        TestCommandConsole console = Terminal();
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            await using CommandTaskLog log = await prompts.StartTaskLogAsync("Build", 3, token);
            for (int index = 0; index < 10; index++) await log.WriteLineAsync("line " + index, token);
            await log.SetStatusAsync("5s elapsed", token);
            await Task.Delay(300, token);
            await log.SucceedAsync("Built", cancellationToken: token);
            return "ok";
        }).RunAsync(["ask"]));
        string error = console.StandardError;
        string[] frames = Frames(error);
        AssertEx.True(error.StartsWith(HideCursor, StringComparison.Ordinal), error);
        AssertEx.True(frames.Any(frame => frame.EndsWith("  Build  5s elapsed\n│  line 7\n│  line 8\n│  line 9", StringComparison.Ordinal)), error);
        AssertEx.Equal("◇  Build  Built\n", frames[^1]);
        AssertEx.True(error.EndsWith(ShowCursor, StringComparison.Ordinal), error);

        console = Terminal();
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            await using CommandTaskLog log = await prompts.StartTaskLogAsync("Build", 2, token);
            for (int index = 0; index < 4; index++) await log.WriteLineAsync("line " + index, token);
            await log.FailAsync("Compiler error", cancellationToken: token);
            return "ok";
        }).RunAsync(["ask"]));
        AssertEx.Equal("■  Build  Compiler error\n│  line 0\n│  line 1\n│  line 2\n│  line 3\n", Frames(console.StandardError)[^1]);

        // Disposing a running task ends it as cancelled.
        console = Terminal();
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            await using (await prompts.StartSpinnerAsync("Wait", token)) { }
            return "ok";
        }).RunAsync(["ask"]));
        AssertEx.Equal("■  Wait  Cancelled\n", Frames(console.StandardError)[^1]);
    }

    [Test, DisplayName("prompts/intro-and-outro-frame-the-prompts-on-a-terminal")]
    public async ValueTask TerminalIntroAndOutro()
    {
        // Like a task log, the intro and outro take no input, so disabled prompts keep their styling.
        foreach (bool interactive in new[] { true, false })
        {
            TestCommandConsole console = Terminal();
            console.QueueKey(ConsoleKey.Enter);
            AssertEx.Equal(0, await App(console, async (prompts, token) =>
            {
                await prompts.IntroAsync("create-\u001b[31mapp\u001b[0m\t1", token);
                string name = await prompts.TextAsync("Name", "app", cancellationToken: token);
                await prompts.OutroAsync("You're all set", token);
                return name;
            }, interactive: interactive).RunAsync(["ask"]));
            AssertEx.Equal("0:app\n", console.StandardOutput);
            string error = console.StandardError;
            AssertEx.True(error.StartsWith("\u001b[90m┌  \u001b[0mcreate-app 1\n", StringComparison.Ordinal), error);
            AssertEx.True(error.EndsWith("\u001b[90m│\u001b[0m\n\u001b[90m└  \u001b[0mYou're all set\n", StringComparison.Ordinal), error);
            // A disabled prompt returns its default without writing anything.
            AssertEx.Equal(interactive ? "◇  Name  app\n│\n└  You're all set\n" : "┌  create-app 1\n│\n└  You're all set\n", Frames(error)[^1]);
        }

        var ascii = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true, SupportsUnicode = false, Width = 20 };
        AssertEx.Equal(0, await App(ascii, async (prompts, token) =>
        {
            await prompts.IntroAsync("A rather long introduction", token);
            await prompts.OutroAsync("Done", token);
            return "ok";
        }).RunAsync(["ask"]));
        AssertEx.Equal(".  A rather long...\n|\n'  Done\n", Plain(ascii.StandardError));
    }

    [Test, DisplayName("prompts/intro-and-outro-write-plain-lines-without-a-terminal")]
    public async ValueTask PlainIntroAndOutro()
    {
        foreach ((TestCommandConsole console, string[] args) in new[]
        {
            (new TestCommandConsole(), new[] { "ask" }),
            (Interactive(), ["ask"]),
            (new TestCommandConsole { IsInteractive = true, SupportsAnsi = false }, ["ask"]),
            (Terminal(), ["ask", "--output=json"]),
            (Terminal(), ["ask", "--json"]),
        })
        {
            AssertEx.Equal(0, await App(console, async (prompts, token) =>
            {
                await prompts.IntroAsync("create-\u001b[31mapp\u001b[0m", token);
                await prompts.OutroAsync("Done\u0007", token);
                return "ok";
            }, jsonAlias: true).RunAsync(args));
            AssertEx.Equal("create-app\nDone\n", console.StandardError, string.Join(" ", args));
            if (args.Length == 1) AssertEx.Equal("0:ok\n", console.StandardOutput);
            else
            {
                using JsonDocument frame = CommandTestEnvelope.Parse(console.StandardOutput);
                AssertEx.Equal("ok", frame.RootElement.GetProperty("payload").GetProperty("Text").GetString());
            }
        }
    }

    [Test, DisplayName("prompts/spectre-console-redraws-only-when-it-colours")]
    public async ValueTask SpectreConsole()
    {
        var inner = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true, IsInputRedirected = false, IsOutputRedirected = false, IsErrorRedirected = false };
        var spectre = new SpectreCommandConsole(inner, width: 40, color: true, unicode: false);
        AssertEx.True(spectre.SupportsAnsi && !spectre.SupportsUnicode);
        inner.QueueKey(ConsoleKey.DownArrow);
        inner.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(spectre, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"]));
        string[] frames = Frames(inner.StandardError);
        AssertEx.Equal("o  Template  cli\n", frames[^1]);

        inner = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true, IsInputRedirected = false, IsOutputRedirected = false, IsErrorRedirected = false };
        spectre = new SpectreCommandConsole(inner, width: 40, color: false);
        AssertEx.True(!spectre.SupportsAnsi);
        inner.QueueInput("3");
        AssertEx.Equal(0, await App(spectre, (prompts, token) => prompts.SelectAsync("Template", Templates, cancellationToken: token)).RunAsync(["ask"]));
        AssertEx.True(inner.StandardError.StartsWith("Template\n  1. web - Web app\n", StringComparison.Ordinal), inner.StandardError);
        AssertEx.True(!inner.StandardError.Contains('\u001b', StringComparison.Ordinal), inner.StandardError);
    }

    [Test, DisplayName("prompts/arguments-are-validated")]
    public async ValueTask ArgumentsAreValidated()
    {
        var context = new CommandExecutionContext(new TestServiceProvider(), Interactive(), CommandPath.Root, CommandOutputMode.Human, CultureInfo.InvariantCulture, "c");
        var prompts = new CommandPrompts(context);
        AssertEx.True(prompts.IsInteractive);
        AssertEx.Throws<ArgumentException>(() => new CommandPromptChoice<int>(1, " "));
        await AssertEx.ThrowsAsync<ArgumentException>(async () => { await prompts.SelectAsync("Pick", Array.Empty<CommandPromptChoice<string>>()); });
        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(async () => { await prompts.SelectAsync("Pick", Templates, 3); });
        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(async () => { await prompts.MultiSelectAsync("Pick", Templates, [-1]); });
        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(async () => { await prompts.StartTaskLogAsync("Build", 51); });
        await AssertEx.ThrowsAsync<ArgumentException>(async () => { await prompts.ConfirmAsync(""); });
        await AssertEx.ThrowsAsync<ArgumentException>(async () => { await prompts.IntroAsync(" "); });
        await AssertEx.ThrowsAsync<ArgumentNullException>(async () => { await prompts.OutroAsync(null!); });
    }

    [Test, DisplayName("prompts/wide-and-emoji-text-is-measured-in-columns-and-cut-on-text-elements")]
    public async ValueTask WideText()
    {
        CommandPromptChoice<string>[] choices = [new("a", "日本語のテンプレート名前", "説明"), new("b", "🚀🚀🚀🚀🚀🚀🚀🚀 rocket"), new("c", "👍🏽👍🏽👍🏽👍🏽👍🏽👍🏽👍🏽👍🏽👍🏽")];
        var console = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true, Width = 24 };
        console.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", choices, cancellationToken: token)).RunAsync(["ask"]));
        string[] lines = Frames(console.StandardError)[1].Split('\n');
        AssertEx.Equal("│  › ● 日本語のテンプ…", lines[1]);
        AssertEx.Equal("│    ○ 🚀🚀🚀🚀🚀🚀🚀…", lines[2]);
        // A skin-tone modifier stays with its emoji; the cut never splits a surrogate pair or a text element.
        AssertEx.Equal("│    ○ 👍🏽👍🏽👍🏽👍🏽👍🏽👍🏽👍🏽…", lines[3]);
        foreach (string line in lines)
        {
            AssertEx.True(ColumnWidth(line) <= 23, line);
            AssertEx.True(!line.Any(char.IsSurrogate) || line.EnumerateRunes().All(rune => rune != Rune.ReplacementChar), line);
        }

        console = new TestCommandConsole { IsInteractive = true, SupportsAnsi = true, Width = 20 };
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            await using CommandTaskLog log = await prompts.StartTaskLogAsync("构建", 2, token);
            await log.WriteLineAsync("编译中编译中编译中编译中编译中", token);
            await log.WriteLineAsync("ééé combining marks take no columns at all", token);
            await Task.Delay(250, token);
            await log.SucceedAsync(cancellationToken: token);
            return "ok";
        }).RunAsync(["ask"]));
        string frame = Frames(console.StandardError).Last(text => text.Contains("编译", StringComparison.Ordinal));
        string[] logLines = frame.Split('\n');
        AssertEx.Equal("│  编译中编译中编…", logLines[1]);
        AssertEx.Equal("│  ééé combining m…", logLines[2]);
        foreach (string line in logLines) AssertEx.True(ColumnWidth(line) <= 19, line);
    }

    // Columns as the prompts count them, for assertions: wide CJK and emoji 2, combining marks 0.
    private static int ColumnWidth(string text)
    {
        int width = 0;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int first = char.ConvertToUtf32(element, 0);
            width += first is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
                or >= 0xFF00 and <= 0xFF60 or >= 0x1F300 and <= 0x1FAFF ? 2 : 1;
        }

        return width;
    }

    [Test, DisplayName("prompts/frames-follow-the-terminal-size")]
    public async ValueTask FramesFollowTerminalSize()
    {
        // A task log with more visible lines than the terminal has rows keeps its block one row short of the height.
        var console = new ResizableConsole(Terminal()) { Height = 6 };
        AssertEx.Equal(0, await App(console, async (prompts, token) =>
        {
            await using CommandTaskLog log = await prompts.StartTaskLogAsync("Build", 30, token);
            for (int index = 0; index < 40; index++) await log.WriteLineAsync("line " + index, token);
            await Task.Delay(250, token);
            await log.SucceedAsync(cancellationToken: token);
            return "ok";
        }).RunAsync(["ask"]));
        string[] frames = Frames(console.Inner.StandardError);
        AssertEx.True(frames.Any(frame => frame.EndsWith("│  line 39", StringComparison.Ordinal)), console.Inner.StandardError);
        AssertEx.True(frames.All(frame => frame.Split('\n').Length <= 5), console.Inner.StandardError);

        // A long select scrolls within the height.
        CommandPromptChoice<int>[] numbers = [.. Enumerable.Range(1, 20).Select(number => new CommandPromptChoice<int>(number, "choice " + number))];
        console = new ResizableConsole(Terminal()) { Height = 8 };
        for (int index = 0; index < 12; index++) console.Inner.QueueKey(ConsoleKey.DownArrow);
        console.Inner.QueueKey(ConsoleKey.Enter);
        AssertEx.Equal(0, await App(console, async (prompts, token) => (await prompts.SelectAsync("Number", numbers, cancellationToken: token)).ToString(CultureInfo.InvariantCulture)).RunAsync(["ask"]));
        AssertEx.Equal("0:13\n", console.Inner.StandardOutput);
        frames = Frames(console.Inner.StandardError);
        AssertEx.True(frames.Skip(1).SkipLast(1).All(frame => frame.Split('\n').Length <= 7), console.Inner.StandardError);
        AssertEx.True(frames.Any(frame => frame.Contains("› ● choice 13", StringComparison.Ordinal) && frame.Contains("│    …", StringComparison.Ordinal)), console.Inner.StandardError);

        // After the terminal narrows, the previous frame's lines wrap, so the redraw moves up by the rows they take now.
        CommandPromptChoice<string>[] wide = [new("a", "a-template-with-a-rather-long-name-number-one"), new("b", "b-template-with-a-rather-long-name-number-two")];
        console = new ResizableConsole(Terminal()) { Width = 80 };
        console.Inner.QueueKey(ConsoleKey.DownArrow);
        console.Inner.QueueKey(ConsoleKey.Enter);
        console.OnKeyRead = () => console.Width = 20;
        AssertEx.Equal(0, await App(console, (prompts, token) => prompts.SelectAsync("Template", wide, cancellationToken: token)).RunAsync(["ask"]));
        string error = console.Inner.StandardError;
        int rows = Frames(error)[1].Split('\n').Sum(line => Math.Max(1, (line.Length + 19) / 20));
        AssertEx.True(error.Contains($"\r\u001b[{rows - 1}A\u001b[J", StringComparison.Ordinal), $"expected {rows - 1} rows up: {error}");
        AssertEx.True(Frames(error)[2].Split('\n').All(line => line.Length <= 19), error);
    }

    [Test, DisplayName("prompts/hosted-adapter-honours-the-no-input-variable")]
    public async ValueTask HostedAdapterNoInput()
    {
        TestCommandConsole console = Interactive();
        console.QueueInput("ada");
        var adapter = new Runic.CommandLine.Hosting.CommandLineHostingAdapter(App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token)));
        var input = new Runic.CommandLine.Hosting.HostedCommandLineLaunchInput(["ask"])
        {
            EnvironmentVariables = new Dictionary<string, string?> { ["RUNIC_COMMANDLINE_NO_INPUT"] = "1" },
        };
        AssertEx.Equal(CommandExitCodes.Usage, await adapter.RunAsync(adapter.Classify(input), console));
        AssertEx.True(console.StandardError.StartsWith("RCLI4001: Name needs an answer, but prompts are disabled.", StringComparison.Ordinal), console.StandardError);

        console = Interactive();
        console.QueueInput("ada");
        adapter = new Runic.CommandLine.Hosting.CommandLineHostingAdapter(App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token)));
        AssertEx.Equal(0, await adapter.RunAsync(adapter.Classify(new Runic.CommandLine.Hosting.HostedCommandLineLaunchInput(["ask"])), console));
        AssertEx.Equal("0:ada\n", console.StandardOutput);
    }

    [Test, DisplayName("prompts/a-line-typed-after-a-cancelled-prompt-answers-the-next-prompt")]
    public async ValueTask CancelledReadIsAdopted()
    {
        var console = new SlowLineConsole();
        using (var cancellation = new CancellationTokenSource())
        {
            Task<int> first = App(console, (prompts, token) => prompts.TextAsync("Name", cancellationToken: token)).RunAsync(["ask"], cancellation.Token);
            await console.Reading.WaitAsync(TimeSpan.FromSeconds(30));
            await cancellation.CancelAsync();
            AssertEx.Equal(CommandExitCodes.Cancelled, await first.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        Task<int> second = App(console, (prompts, token) => prompts.TextAsync("Name again", cancellationToken: token)).RunAsync(["ask"]);
        console.Answer("ada");
        AssertEx.Equal(0, await second.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.Equal("0:ada\n", console.Output.StandardOutput);
        AssertEx.Equal(1, console.Reads);
    }

    [Test, DisplayName("prompts/prompts-observe-the-invocation-token-without-one-being-passed")]
    public async ValueTask InvocationTokenWithoutArgument()
    {
        var console = new WaitingConsole(Terminal());
        using var cancellation = new CancellationTokenSource();
        // The handler deliberately passes no token.
#pragma warning disable CA2016
        Task<int> run = App(console, (prompts, _) => prompts.SelectAsync("Template", Templates)).RunAsync(["ask"], cancellation.Token);
#pragma warning restore CA2016
        await console.Waiting.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();
        AssertEx.Equal(CommandExitCodes.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        AssertEx.True(console.Inner.StandardError.EndsWith(ShowCursor + "RCLI4000: The command was cancelled.\n", StringComparison.Ordinal), console.Inner.StandardError);
    }

    [Test, DisplayName("prompts/task-logs-animate-when-prompts-are-disabled")]
    public async ValueTask TaskLogsAnimateWhenDisabled()
    {
        var settings = new ParseSettings(null) { GetEnvironmentVariable = name => name == "RUNIC_COMMANDLINE_NO_INPUT" ? "1" : null };
        foreach ((ParseSettings? parse, bool interactive) in new[] { ((ParseSettings?)settings, true), (null, false) })
        {
            TestCommandConsole console = Terminal();
            AssertEx.Equal(0, await App(console, async (prompts, token) =>
            {
                AssertEx.True(!prompts.IsInteractive);
                await using CommandTaskLog log = await prompts.StartSpinnerAsync("Upload", token);
                await log.SucceedAsync(cancellationToken: token);
                return await prompts.TextAsync("Name", "app", cancellationToken: token);
            }, parse, interactive: interactive).RunAsync(["ask"]));
            AssertEx.Equal("0:app\n", console.StandardOutput);
            AssertEx.True(console.StandardError.StartsWith(HideCursor, StringComparison.Ordinal), console.StandardError);
            AssertEx.Equal("◇  Upload  Done\n", Frames(console.StandardError)[^1]);
        }
    }

    private sealed class DictionaryResolver(Dictionary<string, string> texts) : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) =>
            texts.TryGetValue(key, out string? text) ? string.Format(CultureInfo.InvariantCulture, text, arguments.ToArray<object>()) : null;
    }

    /// <summary>A terminal whose user has not answered yet: reads wait for the token, and <see cref="Waiting"/> completes on the first read.</summary>
    private sealed class WaitingConsole(TestCommandConsole inner) : ICommandConsole, ICommandTerminal
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TestCommandConsole Inner => inner;
        public Task Waiting => _waiting.Task;
        public bool IsInteractive => inner.IsInteractive;
        public bool IsInputRedirected => inner.IsInputRedirected;
        public bool IsOutputRedirected => inner.IsOutputRedirected;
        public bool IsErrorRedirected => inner.IsErrorRedirected;
        public bool SupportsAnsi => inner.SupportsAnsi;
        public bool SupportsUnicode => inner.SupportsUnicode;
        public int Width => inner.Width;
        public int Height => inner.Height;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            _waiting.TrySetResult();
            // Like a console read on a terminal, this read ignores the token; the prompt must stop waiting by itself.
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
            return null;
        }

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken)
        {
            _waiting.TrySetResult();
            return inner.ReadKeyAsync(cancellationToken);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteOutAsync(value, cancellationToken);
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.WriteOutBytesAsync(value, cancellationToken);
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
    }

    /// <summary>A terminal whose size the test changes; <see cref="OnKeyRead"/> runs before each key is returned.</summary>
    private sealed class ResizableConsole(TestCommandConsole inner) : ICommandConsole, ICommandTerminal
    {
        public TestCommandConsole Inner => inner;
        public int Width { get; set; } = inner.Width;
        public int Height { get; set; } = inner.Height;
        public Action? OnKeyRead { get; set; }
        public bool IsInteractive => inner.IsInteractive;
        public bool IsInputRedirected => inner.IsInputRedirected;
        public bool IsOutputRedirected => inner.IsOutputRedirected;
        public bool IsErrorRedirected => inner.IsErrorRedirected;
        public bool SupportsAnsi => inner.SupportsAnsi;
        public bool SupportsUnicode => inner.SupportsUnicode;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => inner.ReadLineAsync(cancellationToken);

        public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken)
        {
            ConsoleKeyInfo key = await inner.ReadKeyAsync(cancellationToken);
            OnKeyRead?.Invoke();
            return key;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteOutAsync(value, cancellationToken);
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.WriteOutBytesAsync(value, cancellationToken);
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
    }

    /// <summary>A console whose line reads wait for <see cref="Answer"/>, ignoring their token like the process console.</summary>
    private sealed class SlowLineConsole : ICommandConsole
    {
        private readonly TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TestCommandConsole Output { get; } = new();
        public int Reads;
        public Task Reading => _reading.Task;
        public void Answer(string line) => _answer.TrySetResult(line);
        public bool IsInteractive => true;
        public bool IsInputRedirected => false;
        public bool IsOutputRedirected => true;
        public bool IsErrorRedirected => false;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            _reading.TrySetResult();
            return await _answer.Task.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        }

        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => Output.WriteOutAsync(value, cancellationToken);
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => Output.WriteOutBytesAsync(value, cancellationToken);
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => Output.WriteErrorAsync(value, cancellationToken);
    }
}
