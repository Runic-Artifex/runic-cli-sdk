using System.Globalization;
using System.Text.Json;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// runic-cli-sdk#88: an outcome selects its own exit code; the policy keeps mapping everything else.
internal sealed class OutcomeExitCodeTests
{
    internal enum LinkCase
    {
        Linked,
        MissingPolicy,
        MissingContainer,
        Unreachable,
        Malformed,
    }

    // The shape of a migrated tool's documented codes: 2, 3 and 4 for three domain failures of one command.
    [Command("xc-link", Hidden = true, Description = "Link a policy object with a container.")]
    [CommandExitCode(0, "The policy object was linked.")]
    [CommandExitCode(2, "The policy object does not exist.")]
    [CommandExitCode(3, "The container does not exist.", DescriptionKey = "tests.exit-codes.missing-container")]
    [CommandExitCode(4, "The directory cannot be reached.")]
    internal static CommandOutcome<string> Link([Option("--case")] LinkCase linkCase = LinkCase.Linked) => linkCase switch
    {
        LinkCase.MissingPolicy => Unavailable("XC_POLICY_NOT_FOUND", "The policy object does not exist.").WithExitCode(2),
        LinkCase.MissingContainer => Unavailable("XC_CONTAINER_NOT_FOUND", "The container does not exist.").WithExitCode(3),
        LinkCase.Unreachable => Unavailable("XC_DIRECTORY_UNREACHABLE", "The directory cannot be reached.").WithExitCode(4),
        LinkCase.Malformed => CommandOutcome.Failure<string>(CommandExitCategory.Validation, new CommandFault("XC_SHAPE", "The name is malformed.")),
        _ => CommandOutcome.Success("Linked."),
    };

    // Another command of the same catalog keeps the policy's codes.
    [Command("xc-other", Hidden = true)]
    internal static CommandOutcome<string> Other([Option("--missing")] bool missing = false) => missing
        ? Unavailable("XC_OTHER_MISSING", "The record does not exist.")
        : CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("XC_OTHER_REFUSED", "The operation was refused."));

    // An informational success code, such as Windows Installer's 3010 ("restart required").
    [Command("xc-install", Hidden = true)]
    internal static CommandOutcome<string> Install([Option("--restart")] bool restart = false) => restart
        ? CommandOutcome.Success("Installed; restart required.").WithExitCode(3010)
        : CommandOutcome.Success("Installed.");

    private static readonly string[] LeftRow = ["left"];

    private static CommandOutcome<string> Unavailable(string code, string message) =>
        CommandOutcome.Failure<string>(CommandExitCategory.Unavailable, new CommandFault(code, message));

    private static CommandApp App(ICommandConsole console, IExitCodePolicy? policy = null, ICommandExecutionObserver? observer = null,
        ParseSettings? settings = null, Action<CommandCatalogBuilder>? configure = null) =>
        new(GeneratedCommandCatalog.Create(configure))
        {
            Console = console,
            ParseSettings = settings ?? ParseSettings.Default,
            HandleTerminationSignals = false,
            Culture = CultureInfo.InvariantCulture,
            ExitCodePolicy = policy ?? DefaultExitCodePolicy.Instance,
            Observer = observer,
        };

    [Test, DisplayName("exit-code/outcome-selects-documented-codes-and-other-commands-keep-the-policy")]
    public async ValueTask OutcomeSelectsDocumentedCodes()
    {
        foreach ((string[] args, int expected) in new (string[], int)[]
        {
            (["xc-link"], 0),
            (["xc-link", "--case", "MissingPolicy"], 2),
            (["xc-link", "--case", "MissingContainer"], 3),
            (["xc-link", "--case", "Unreachable"], 4),
            (["xc-link", "--case", "Malformed"], CommandExitCodes.Validation),
            (["xc-link", "--bogus"], CommandExitCodes.Usage),
            (["xc-link", "--case", "Nope"], CommandExitCodes.Usage),
            (["xc-other"], CommandExitCodes.CommandFailure),
            (["xc-other", "--missing"], CommandExitCodes.Unavailable),
        })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(expected, await App(console).RunAsync(args), string.Join(' ', args));
        }

        // A custom policy keeps mapping usage, validation and every other command; the documented codes stay.
        var policy = new DistinctPolicy();
        foreach ((string[] args, int expected) in new (string[], int)[]
        {
            (["xc-link", "--case", "MissingPolicy"], 2),
            (["xc-link", "--case", "MissingContainer"], 3),
            (["xc-link", "--case", "Unreachable"], 4),
            (["xc-link", "--case", "Malformed"], 65),
            (["xc-link", "--bogus"], 64),
            (["xc-other"], 70),
            (["xc-other", "--missing"], 69),
        })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(expected, await App(console, policy).RunAsync(args), "policy: " + string.Join(' ', args));
        }
    }

    [Test, DisplayName("exit-code/json-observers-presenters-and-results-report-the-returned-code")]
    public async ValueTask EveryChannelReportsReturnedCode()
    {
        var console = new TestCommandConsole();
        var observer = new RecordingObserver();
        AssertEx.Equal(3, await App(console, observer: observer).RunAsync(["xc-link", "--case", "MissingContainer", "--format", "json"]));
        using (JsonDocument document = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
            AssertEx.Equal("XC_CONTAINER_NOT_FOUND", document.RootElement.GetProperty("fault").GetProperty("code").GetString());
        }

        CommandExecutionEvent completed = observer.Events.Single(static item => item.Kind == CommandExecutionEventKind.Completed);
        AssertEx.Equal(3, completed.ExitCode);
        AssertEx.Equal(CommandExitCategory.Unavailable, completed.ExitCategory);
        AssertEx.Equal("XC_CONTAINER_NOT_FOUND", completed.FaultCode);

        // A command's own presenter sees the code the invocation returns.
        int? presented = null;
        console = new TestCommandConsole();
        AssertEx.Equal(4, await App(console, configure: builder => builder.PresentOutcome<string>("xc-link", (presentation, token) =>
        {
            presented = presentation.ExitCode;
            return presentation.WriteDefaultAsync(token);
        })).RunAsync(["xc-link", "--case", "Unreachable"]));
        AssertEx.Equal(4, presented);
        AssertEx.Equal("The directory cannot be reached.\n", console.StandardError);

        // The envelope reader accepts what the writer wrote.
        console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["xc-link", "--case", "MissingPolicy", "--format", "json"]));
        CommandResponse<string> response = CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(console.StandardOutput), CommandResultCodecs.String.PayloadType, CommandResultCodecs.String.TypeInfo);
        AssertEx.Equal(2, response.ExitCode);
        AssertEx.True(!response.Succeeded);
    }

    [Test, DisplayName("exit-code/informational-success-code")]
    public async ValueTask InformationalSuccessCode()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(3010, await App(console).RunAsync(["xc-install", "--restart"]));
        AssertEx.Equal("Installed; restart required.\n", console.StandardOutput);
        AssertEx.Equal("", console.StandardError);

        console = new TestCommandConsole();
        var observer = new RecordingObserver();
        AssertEx.Equal(3010, await App(console, observer: observer).RunAsync(["xc-install", "--restart", "--format", "json"]));
        using (JsonDocument document = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.True(document.RootElement.GetProperty("success").GetBoolean());
            AssertEx.Equal(3010, document.RootElement.GetProperty("exitCode").GetInt32());
        }

        CommandResponse<string> response = CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(console.StandardOutput), CommandResultCodecs.String.PayloadType, CommandResultCodecs.String.TypeInfo);
        AssertEx.True(response.Succeeded);
        AssertEx.Equal(3010, response.ExitCode);
        CommandExecutionEvent completed = observer.Events.Single(static item => item.Kind == CommandExecutionEventKind.Completed);
        AssertEx.Equal(CommandExitCategory.Success, completed.ExitCategory);
        AssertEx.Equal(3010, completed.ExitCode);

        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["xc-install"]));
    }

    [Test, DisplayName("exit-code/run-runasync-hosting-and-tester-agree")]
    public async ValueTask EveryRunnerAgrees()
    {
        foreach ((string[] args, int expected) in new (string[], int)[]
        {
            (["xc-link", "--case", "MissingPolicy"], 2),
            (["xc-link", "--case", "MissingContainer", "--format", "json"], 3),
            (["xc-link", "--case", "Unreachable"], 4),
            (["xc-install", "--restart"], 3010),
            (["xc-other"], 70),
            (["xc-link", "--bogus"], 64),
        })
        {
            string label = string.Join(' ', args);
            var policy = new DistinctPolicy();
            AssertEx.Equal(expected, await App(new TestCommandConsole(), policy).RunAsync(args), "RunAsync " + label);
            AssertEx.Equal(expected, App(new TestCommandConsole(), policy).Run(args), "Run " + label);

            var tester = new CommandAppTester(console => App(console, policy));
            AssertEx.Equal(expected, (await tester.RunAsync(args)).ExitCode, "tester RunAsync " + label);
            AssertEx.Equal(expected, tester.Run(args).ExitCode, "tester Run " + label);

            var hosted = new CommandLineHostingAdapter(App(new TestCommandConsole(), policy));
            HostedCommandLineDecision decision = hosted.Classify(new HostedCommandLineLaunchInput(args));
            AssertEx.Equal(expected, await hosted.RunAsync(decision), "hosting RunAsync " + label);
            if (decision.CanExecute)
            {
                var console = new TestCommandConsole();
                HostedCommandLineExecutionResult result = await hosted.ExecuteAsync(new HostedCommandLineExecutionInput(
                    decision, console, CultureInfo.InvariantCulture, "req-1", new CommandOutputDispatcher()));
                AssertEx.Equal(expected, result.ExitCode, "hosting ExecuteAsync " + label);
            }
        }
    }

    [Test, DisplayName("exit-code/failure-codes-that-read-as-success-are-refused")]
    public ValueTask FailureCodesThatReadAsSuccessAreRefused()
    {
        CommandOutcome<string> failure = Unavailable("XC", "Missing.");
        foreach (int code in new[] { 0, 256, 512, -256 })
        {
            AssertEx.Throws<ArgumentOutOfRangeException>(() => failure.WithExitCode(code));
        }

        AssertEx.Equal(-1, failure.WithExitCode(-1).ExitCode);
        AssertEx.Equal(255, failure.WithExitCode(255).ExitCode);
        AssertEx.True(failure.ExitCode is null);

        // The copy keeps everything but the code; success may use zero or an informational code.
        var data = CommandFailureData.Create("tests.exit-code/1", LeftRow, RecoveryJsonContext.Default.StringArray);
        CommandOutcome<string> rich = CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("XC", "Failed."),
            [new CommandDiagnostic("RCLI3998", "note", "Note.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning)],
            "Failure text.\n", data).WithExitCode(7);
        AssertEx.Equal(7, rich.ExitCode);
        AssertEx.Equal(CommandExitCategory.CommandFailure, rich.ExitCategory);
        AssertEx.Equal("XC", rich.Fault!.Code);
        AssertEx.Equal(1, rich.Diagnostics.Count);
        AssertEx.Equal("Failure text.\n", rich.HumanOutput);
        AssertEx.True(ReferenceEquals(data, rich.FailureData));
        AssertEx.Equal(0, CommandOutcome.Success("ok").WithExitCode(0).ExitCode);
        AssertEx.Equal(1641, CommandOutcome.Success("ok").WithExitCode(1641).ExitCode);

        // A sink cannot report a success with a code its outcome did not select.
        AssertEx.Throws<ArgumentException>(() => CommandResponse.FromOutcome("req", "xc", 3010, "tests.string/1", CommandOutcome.Success("ok")));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("exit-code/default-and-custom-policies-are-unchanged")]
    public ValueTask PoliciesUnchanged()
    {
        foreach (CommandExitCategory category in Enum.GetValues<CommandExitCategory>())
        {
            AssertEx.Equal(CommandExitCodes.GetDefault(category), DefaultExitCodePolicy.Instance.GetExitCode(category));
        }

        AssertEx.Equal(1, CommandExitCodes.CommandFailure);
        AssertEx.Equal(2, CommandExitCodes.Usage);
        AssertEx.Equal(3, CommandExitCodes.HostFailure);
        AssertEx.Equal(130, CommandExitCodes.Cancelled);
        // An existing policy that maps a failure to a multiple of 256 is still accepted; only outcome codes are checked.
        _ = new CommandExecutor(new EmptyScopes(), new MultipleOf256Policy());
        AssertEx.Throws<ArgumentException>(() => new CommandExecutor(new EmptyScopes(), new ZeroFailurePolicy()));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("exit-code/help-lists-documented-codes")]
    public async ValueTask HelpListsDocumentedCodes()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["xc-link", "--help"]));
        AssertEx.True(console.StandardOutput.Contains(
            "\nExit codes:\n  0  The policy object was linked.\n  2  The policy object does not exist.\n  3  The container does not exist.\n  4  The directory cannot be reached.\n",
            StringComparison.Ordinal), console.StandardOutput);

        // The heading and an application key go through the text resolver.
        console = new TestCommandConsole();
        CommandApp translated = new(GeneratedCommandCatalog.Create())
        {
            Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false,
            Culture = CultureInfo.GetCultureInfo("de"), TextResolver = new GermanResolver(),
        };
        AssertEx.Equal(0, await translated.RunAsync(["xc-link", "--help"]));
        AssertEx.True(console.StandardOutput.Contains("\nExit-Codes:\n", StringComparison.Ordinal) &&
            console.StandardOutput.Contains("  3  Der Container existiert nicht.\n", StringComparison.Ordinal), console.StandardOutput);

        // A command without documented codes has no section, and the key is listed for translators.
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["xc-other", "--help"]));
        AssertEx.True(!console.StandardOutput.Contains("Exit codes", StringComparison.Ordinal), console.StandardOutput);
        AssertEx.True(CommandTextKeys.All.Any(static key => key.Key == CommandTextKeys.HelpExitCodes && key.Arguments.Count == 0));
        AssertEx.Equal("help.exit-codes", CommandTextKeys.HelpExitCodes);

        // A missing key is reported like other description keys.
        IReadOnlyList<CommandCatalogIssue> issues = new CommandTextContext(CultureInfo.GetCultureInfo("de"), new EmptyResolver())
            .ValidateDescriptionKeys(GeneratedCommandCatalog.Create());
        AssertEx.True(issues.Any(static issue => issue.Message.Contains("'tests.exit-codes.missing-container' for exit code 3", StringComparison.Ordinal)),
            string.Join("\n", issues.Select(static issue => issue.Message)));

        AssertEx.Throws<ArgumentNullException>(() => new CommandHelp { ExitCodes = null! });
        AssertEx.Throws<ArgumentNullException>(() => new CommandHelp { ExitCodes = [null!] });
        AssertEx.Throws<ArgumentException>(() => new CommandExitCodeHelp(2, " "));
        var help = new CommandHelp { ExitCodes = [new CommandExitCodeHelp(5, "Five.")] };
        AssertEx.Equal(5, help.ExitCodes.Single().ExitCode);
        AssertEx.Equal(0, CommandHelp.Empty.ExitCodes.Count);
    }

    private sealed class DistinctPolicy : IExitCodePolicy
    {
        public int GetExitCode(CommandExitCategory category) => category switch
        {
            CommandExitCategory.Success => 0,
            CommandExitCategory.Usage => 64,
            CommandExitCategory.Validation => 65,
            CommandExitCategory.Unavailable => 69,
            CommandExitCategory.CommandFailure => 70,
            CommandExitCategory.HostFailure => 71,
            CommandExitCategory.Cancelled => 130,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
    }

    private sealed class MultipleOf256Policy : IExitCodePolicy
    {
        public int GetExitCode(CommandExitCategory category) => category == CommandExitCategory.Success ? 0 : 256;
    }

    private sealed class ZeroFailurePolicy : IExitCodePolicy
    {
        public int GetExitCode(CommandExitCategory category) => category == CommandExitCategory.CommandFailure ? 0 : category == CommandExitCategory.Success ? 0 : 1;
    }

    private sealed class RecordingObserver : ICommandExecutionObserver
    {
        internal List<CommandExecutionEvent> Events { get; } = [];

        public void Observe(CommandExecutionEvent executionEvent) => Events.Add(executionEvent);
    }

    private sealed class GermanResolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) => key switch
        {
            CommandTextKeys.HelpExitCodes => "Exit-Codes",
            "tests.exit-codes.missing-container" => "Der Container existiert nicht.",
            _ => null,
        };
    }

    private sealed class EmptyResolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) => null;
    }

    private sealed class EmptyScopes : ICommandExecutionScopeFactory
    {
        public ICommandExecutionScope CreateScope() => throw new InvalidOperationException();
    }
}
