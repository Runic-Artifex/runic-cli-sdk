using System.Globalization;
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

        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteAsync(value, cancellationToken);
        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteOutAsync(value, cancellationToken);
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.WriteOutBytesAsync(value, cancellationToken);
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
    }
}
