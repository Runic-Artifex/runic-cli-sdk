using System.Globalization;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal sealed class LocalizationTests
{
    private static CommandCatalog Catalog() => GeneratedCommandCatalog.Create();
    private static CommandApp App(TestCommandConsole console, ICommandHelpPresenter? presenter = null) => new(Catalog())
    {
        Name = "sample", Console = console, HandleTerminationSignals = false, Culture = CultureInfo.GetCultureInfo("de-DE"),
        TextResolver = new GermanResolver(), HelpPresenter = presenter, ParseSettings = ParseSettings.Default,
    };
    [Test, DisplayName("localization/default-fallbacks-and-culture-snapshot")]
    public ValueTask Defaults()
    {
        var culture = new CultureInfo("de-DE");
        var context = new CommandTextContext(culture, new GermanResolver());
        culture.NumberFormat.NumberDecimalSeparator = "!";
        AssertEx.Equal(",", context.Culture.NumberFormat.NumberDecimalSeparator);
        AssertEx.True(context.Culture.IsReadOnly);
        AssertEx.Equal("Literal", context.Description(new CommandHelp("Literal"), "missing"));
        AssertEx.Equal("", context.Description(CommandHelp.Empty, "missing"));
        string help = CommandHelpFormatter.Format(Catalog(), "sample", new CommandPath(["localized"]));
        AssertEx.True(help.StartsWith("Usage: sample localized", StringComparison.Ordinal));
        AssertEx.True(help.Contains("Literal command", StringComparison.Ordinal));
        AssertEx.True(!help.Contains("commands.localized", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }
    [Test, DisplayName("localization/help-human-json-and-spectre")]
    public async ValueTask Help()
    {
        foreach (ICommandHelpPresenter? presenter in new ICommandHelpPresenter?[] { null, new SpectreHelpPresenter() })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await App(console, presenter).RunAsync(["localized", "--help"]));
            AssertEx.True(console.StandardOutput.StartsWith("Aufruf: sample localized", StringComparison.Ordinal), console.StandardOutput);
            AssertEx.True(console.StandardOutput.Contains("Lokalisierter Befehl", StringComparison.Ordinal));
            AssertEx.True(console.StandardOutput.Contains("--count", StringComparison.Ordinal));
            AssertEx.True(console.StandardOutput.Contains("Anzahl", StringComparison.Ordinal));
            AssertEx.True(console.StandardOutput.Contains("Literal name", StringComparison.Ordinal));
            console = new();
            AssertEx.Equal(0, await App(console, presenter).RunAsync(["localized", "--help", "--format=json"]));
            using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
            AssertEx.Equal("runic.text/1", frame.RootElement.GetProperty("payloadType").GetString());
            AssertEx.Equal("help", frame.RootElement.GetProperty("command").GetString());
            AssertEx.True(frame.RootElement.GetProperty("payload").GetString()!.Contains("Lokalisierter Befehl", StringComparison.Ordinal));
        }
    }
    [Test, DisplayName("localization/framework-diagnostics-preserve-wire-identities")]
    public async ValueTask Diagnostics()
    {
        ParseOutcome original = PortableCommandSyntaxAdapter.Instance.Parse(Catalog(), ["localized", "--unknown"], ParseSettings.Default);
        CommandDiagnostic diagnostic = original.Diagnostics[0];
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["localized", "--unknown", "--format=json"]));
        using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
        var localized = frame.RootElement.GetProperty("diagnostics")[0];
        AssertEx.Equal(diagnostic.Code, localized.GetProperty("code").GetString());
        AssertEx.Equal(diagnostic.Kind, localized.GetProperty("kind").GetString());
        AssertEx.Equal(diagnostic.MessageKey, localized.GetProperty("messageKey").GetString());
        AssertEx.Equal("localized", frame.RootElement.GetProperty("command").GetString());
        AssertEx.Equal("Unbekannte Option: --unknown", localized.GetProperty("message").GetString());
        AssertEx.SequenceEqual(diagnostic.Arguments, localized.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!));
        AssertEx.Equal(localized.GetProperty("message").GetString(), frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
        AssertEx.True(diagnostic.Message.StartsWith("An unrecognized option was supplied.", StringComparison.Ordinal));
        console = new();
        AssertEx.Equal(2, await App(console).RunAsync(["localized", "--unknown"]));
        AssertEx.True(console.StandardError.Contains("RCLI1001: Unbekannte Option: --unknown", StringComparison.Ordinal));
    }
    [Test, DisplayName("localization/standalone-execution-faults-and-diagnostics")]
    public async ValueTask Execution()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["localized", "--count", "invalid", "--format=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("RCLI2005", frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
            AssertEx.Equal("Ungültiger Wert.", frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
        }
        console = new();
        AssertEx.Equal(0, await App(console).RunAsync(["localized", "--count", "2", "--format=json"]));
        using var result = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("runic.text/1", result.RootElement.GetProperty("payloadType").GetString());
        AssertEx.Equal("Hinweis", result.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString());
        AssertEx.Equal("1,5:2", result.RootElement.GetProperty("payload").GetString());
    }
    [Test, DisplayName("localization/hosted-presentation-captures-caller-culture")]
    public async ValueTask Hosted()
    {
        var adapter = new CommandLineHostingAdapter(Catalog(), new CommandExecutor(CommandScopes.FromServices(new TestServiceProvider())))
        { Presentation = new CommandPresentation { Name = "hosted", TextResolver = new GermanResolver() } };
        HostedCommandLineDecision decision = adapter.Classify(new HostedCommandLineLaunchInput(["localized", "--help"]));
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await adapter.PresentAsync(decision, console, CultureInfo.GetCultureInfo("de-DE"), "localized-request"));
        AssertEx.True(console.StandardOutput.StartsWith("Aufruf: hosted localized", StringComparison.Ordinal));
        decision = adapter.Classify(new HostedCommandLineLaunchInput(["localized", "--unknown", "--format=json"]));
        console = new();
        AssertEx.Equal(2, await adapter.PresentAsync(decision, console, CultureInfo.GetCultureInfo("de-DE"), "localized-request"));
        using var result = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("localized-request", result.RootElement.GetProperty("requestId").GetString());
        AssertEx.Equal("RCLI1001", result.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }
    [Test, DisplayName("localization/resolved-diagnostic-content-is-sanitized")]
    public async ValueTask Sanitization()
    {
        var console = new TestCommandConsole();
        var diagnostic = new CommandDiagnostic("RCLI2100", "note", "Safe", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Information);
        CommandResponse<string> response = CommandResponse.Success("safe-request", "localized", "runic.text/1", "payload", [diagnostic]);
        var context = new CommandTextContext(CultureInfo.InvariantCulture, new UnsafeResolver());
        await CommandOutputDispatcher.DispatchAsync(CommandOutputMode.Json, console, context.Culture, response, CommandResultCodecs.String, context);
        AssertEx.True(!console.StandardOutput.Contains("private-secret", StringComparison.Ordinal));
        using var result = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("The diagnostic content was redacted.", result.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString());
    }
    private sealed class UnsafeResolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) => "System.InvalidOperationException: private-secret";
    }
    [Test, DisplayName("localization/completion-errors-use-explicit-culture")]
    public async ValueTask CompletionError()
    {
        // The main test catalog owns "completion"; use a standalone framework-only catalog here.
        var catalog = new CommandCatalogBuilder().Build();
        var console = new TestCommandConsole();
        var app = new CommandApp(catalog) { Console = console, HandleTerminationSignals = false,
            Culture = CultureInfo.GetCultureInfo("de-DE"), TextResolver = new GermanResolver() };
        AssertEx.Equal(2, await app.RunAsync(["completion", "unknown"]));
        AssertEx.Equal("Shell auswählen: bash, zsh, fish oder powershell.\n", console.StandardError);
        var adapter = new CommandLineHostingAdapter(catalog, new CommandExecutor(CommandScopes.FromServices(new TestServiceProvider())))
        { Presentation = new CommandPresentation { TextResolver = new GermanResolver() } };
        var decision = adapter.Classify(new HostedCommandLineLaunchInput(["completion", "unknown"]));
        console = new();
        AssertEx.Equal(2, await adapter.PresentAsync(decision, console, CultureInfo.GetCultureInfo("de-DE"), "completion-request"));
        AssertEx.Equal("Shell auswählen: bash, zsh, fish oder powershell.\n", console.StandardError);
    }
    // Every key the framework requests while presenting help, parse, binding and execution failures is a listed
    // framework key (or an application DescriptionKey/diagnostic) and receives at most the listed arguments.
    [Test, DisplayName("localization/command-text-keys-list-every-framework-key")]
    public async ValueTask FrameworkKeys()
    {
        var resolver = new RecordingResolver();
        string directory = Path.Combine(Path.GetTempPath(), "runic-text-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "input.txt");
        File.WriteAllText(file, "");
        string[][] invocations =
        [
            ["--help"], ["greet", "--help"], ["inspect-path", "--help"], ["relations", "--help"], ["env", "--help"], ["values", "--help"],
            ["config", "shwo"], ["values", "--coutn=1"], ["values", "--unknown"], ["values", "--count"], ["values", "--count", "x"],
            ["values", "--mode", "slow"], ["inspect-path", "--source", "missing-file", "--target", "missing-directory"],
            ["inspect-path", "--source", file, "--target", directory, "--count", "9"], ["relations", "--apply"], ["relations", "--apply", "--confirm", "--dry-run"],
            ["greet", "a", "b"], ["fail"], ["env"], ["--format", "yaml"], ["values", "--count", "1", "--count", "2"],
        ];
        foreach (string[] invocation in invocations)
            foreach (string mode in new[] { "human", "json" })
            {
                var console = new TestCommandConsole();
                await new CommandApp(GeneratedCommandCatalog.Create())
                {
                    Name = "sample", Console = console, HandleTerminationSignals = false, TextResolver = resolver,
                    ParseSettings = ParseSettings.Default,
                }.RunAsync([.. invocation, "--format=" + mode]);
            }
        Directory.Delete(directory, recursive: true);
        await new CommandApp(new CommandCatalogBuilder().Build()) { Console = new TestCommandConsole(), HandleTerminationSignals = false, TextResolver = resolver }
            .RunAsync(["completion", "unknown"]);
        foreach ((string key, int count) in resolver.Requests)
        {
            if (key.StartsWith("commands.", StringComparison.Ordinal) || key.StartsWith("options.", StringComparison.Ordinal) ||
                key == "missing" || key == "diagnostics.localized-note") continue;
            // faults.{code} applies to every fault code, without arguments.
            if (key.StartsWith("faults.RCLI", StringComparison.Ordinal) && key.Length == "faults.RCLI0000".Length && count == 0) continue;
            CommandTextKey? listed = CommandTextKeys.Find(key);
            AssertEx.True(listed is not null, "Unlisted framework key " + key);
            AssertEx.True(count <= listed!.Arguments.Count, key + " received " + count + " arguments");
        }
        foreach (string expected in new[]
        {
            CommandTextKeys.HelpUsage, CommandTextKeys.HelpExamples, CommandTextKeys.HelpPathKindFile, CommandTextKeys.HelpPathKindDirectory,
            CommandTextKeys.HelpExisting, CommandTextKeys.HelpRequires, CommandTextKeys.HelpEnvironment, CommandTextKeys.HelpDefaultCommand,
            CommandTextKeys.DiagnosticUnknownCommandSuggestion, CommandTextKeys.DiagnosticUnknownOptionSuggestion, CommandTextKeys.DiagnosticFileNotFound,
            CommandTextKeys.DiagnosticOutOfRange, CommandTextKeys.DiagnosticOptionRequires, CommandTextKeys.DiagnosticOptionConflict,
            CommandTextKeys.DiagnosticInvalidInteger, CommandTextKeys.FaultHostFailure, CommandTextKeys.FaultHostFailureHint,
            CommandTextKeys.CompletionInvalidShell, CommandTextKeys.DiagnosticInvalidOutputMode, CommandTextKeys.DiagnosticDuplicateOption,
        })
            AssertEx.True(resolver.Requests.Any(request => request.Key == expected), "Not exercised: " + expected);
        AssertEx.Equal(CommandTextKeys.All.Count, CommandTextKeys.All.Select(key => key.Key).Distinct(StringComparer.Ordinal).Count());
        AssertEx.Equal("diagnostics.out-of-range", CommandTextKeys.Diagnostic("out-of-range"));
        AssertEx.Equal("faults.RCLI4000", CommandTextKeys.Fault("RCLI4000"));
        AssertEx.SequenceEqual(["parameter", "minimum", "maximum"], CommandTextKeys.Find(CommandTextKeys.DiagnosticOutOfRange)!.Arguments);
        AssertEx.True(CommandTextKeys.Find("commands.greet") is null);
    }
    [Test, DisplayName("localization/command-text-keys-match-the-documented-table")]
    public ValueTask DocumentedKeys()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Runic.CommandLine.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        var rows = File.ReadAllLines(Path.Combine(root, "docs", "guides", "command-line", "text-keys.md"))
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('|'))
            .Select(cells => (Key: cells[1].Trim().Trim('`'), Arguments: string.Join(",", cells[2].Split(',')
                .Select(cell => cell.Trim()).Where(cell => cell.StartsWith('`')).Select(cell => cell[1..cell.IndexOf('`', 1)]))))
            .ToArray();
        AssertEx.SequenceEqual(CommandTextKeys.All.Select(key => key.Key + ":" + string.Join(",", key.Arguments)), rows.Select(row => row.Key + ":" + row.Arguments));
        return ValueTask.CompletedTask;
    }
    [Test, DisplayName("localization/suggestions-are-translatable-arguments")]
    public async ValueTask TranslatableSuggestions()
    {
        CommandDiagnostic option = PortableCommandSyntaxAdapter.Instance.Parse(Catalog(), ["values", "--coutn=private-secret"], ParseSettings.Default).Diagnostics[0];
        AssertEx.Equal("diagnostics.unknown-option-suggestion", option.MessageKey);
        AssertEx.SequenceEqual(["--coutn", "--count"], option.Arguments);
        AssertEx.True(option.Message.EndsWith("Did you mean '--count'?", StringComparison.Ordinal), option.Message);
        CommandDiagnostic command = PortableCommandSyntaxAdapter.Instance.Parse(Catalog(), ["config", "shwo"], ParseSettings.Default).Diagnostics[0];
        AssertEx.Equal("diagnostics.unknown-command-suggestion", command.MessageKey);
        AssertEx.SequenceEqual(["shwo", "show"], command.Arguments);
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["config", "shwo"]));
        AssertEx.Equal("RCLI1002: Unbekannter Befehl shwo. Meinten Sie show?\n", console.StandardError);
    }
    private sealed class RecordingResolver : ICommandTextResolver
    {
        public List<(string Key, int Count)> Requests { get; } = [];
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments)
        {
            lock (Requests) Requests.Add((key, arguments.Count));
            return null;
        }
    }
    private sealed class GermanResolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments)
        {
            AssertEx.Equal("de", culture.TwoLetterISOLanguageName);
            return key switch
            {
                "help.usage" => "Aufruf",
                "completion.invalid-shell" => "Shell auswählen: bash, zsh, fish oder powershell.",
                "commands.localized" => "Lokalisierter Befehl",
                "options.count" => "Anzahl",
                "diagnostics.unknown-option" => "Unbekannte Option: " + arguments[0],
                "diagnostics.unknown-command-suggestion" => "Unbekannter Befehl " + arguments[0] + ". Meinten Sie " + arguments[1] + "?",
                "diagnostics.localized-note" => "Hinweis",
                "faults.RCLI2005" => "Ungültiger Wert.",
                _ => null,
            };
        }
    }
}

internal static class LocalizedTestCommands
{
    [Command("localized", Description = "Literal command", DescriptionKey = "commands.localized")]
    public static CommandOutcome<string> Localized(
        CommandExecutionContext context,
        [Option("--count", Description = "Count", DescriptionKey = "options.count")] int count = 1,
        [Option("--name", Description = "Literal name", DescriptionKey = "missing")] string name = "world") =>
        CommandOutcome.Success(1.5m.ToString(context.Culture) + ":" + count,
            [new CommandDiagnostic("RCLI2100", "localized-note", "Note", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Information)]);
}
