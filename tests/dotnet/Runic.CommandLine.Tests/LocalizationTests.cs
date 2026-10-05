using System.Globalization;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static class LocalizationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("localization/default-fallbacks-and-culture-snapshot", Defaults),
        new("localization/help-human-json-and-spectre", Help),
        new("localization/framework-diagnostics-preserve-wire-identities", Diagnostics),
        new("localization/standalone-execution-faults-and-diagnostics", Execution),
        new("localization/hosted-presentation-captures-caller-culture", Hosted),
        new("localization/resolved-diagnostic-content-is-sanitized", Sanitization),
        new("localization/completion-errors-use-explicit-culture", CompletionError),
        new("localization/legacy-help-presenter-remains-compatible", LegacyPresenter),
    ];
    private static CommandCatalog Catalog() => GeneratedCommandCatalog.Create();
    private static CommandApp App(TestCommandConsole console, ICommandHelpPresenter? presenter = null) => new(Catalog())
    {
        Name = "sample", Console = console, HandleCancelKeyPress = false, Culture = CultureInfo.GetCultureInfo("de-DE"),
        TextResolver = new GermanResolver(), HelpPresenter = presenter, ParseSettings = ParseSettings.Default,
    };
    private static ValueTask Defaults()
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
    private static async ValueTask Help()
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
            AssertEx.Equal(0, await App(console, presenter).RunAsync(["localized", "--help", "--output=json"]));
            using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
            AssertEx.Equal("runic.text/1", frame.RootElement.GetProperty("payloadType").GetString());
            AssertEx.Equal("help", frame.RootElement.GetProperty("command").GetString());
            AssertEx.True(frame.RootElement.GetProperty("payload").GetString()!.Contains("Lokalisierter Befehl", StringComparison.Ordinal));
        }
    }
    private static async ValueTask Diagnostics()
    {
        ParseOutcome original = PortableCommandSyntaxAdapter.Instance.Parse(Catalog(), ["localized", "--unknown"], ParseSettings.Default);
        CommandDiagnostic diagnostic = original.Diagnostics[0];
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["localized", "--unknown", "--output=json"]));
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
    private static async ValueTask Execution()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["localized", "--count", "invalid", "--output=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("RCLI2005", frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
            AssertEx.Equal("Ungültiger Wert.", frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
        }
        console = new();
        AssertEx.Equal(0, await App(console).RunAsync(["localized", "--count", "2", "--output=json"]));
        using var result = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("runic.text/1", result.RootElement.GetProperty("payloadType").GetString());
        AssertEx.Equal("Hinweis", result.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString());
        AssertEx.Equal("1,5:2", result.RootElement.GetProperty("payload").GetString());
    }
    private static async ValueTask Hosted()
    {
        var adapter = new CommandLineHostingAdapter(Catalog(), new CommandExecutor(new TestExecutionScopeFactory(new TestServiceProvider())))
        { Presentation = new CommandPresentation { Name = "hosted", TextResolver = new GermanResolver() } };
        HostedCommandLineDecision decision = adapter.Classify(new HostedCommandLineLaunchInput(["localized", "--help"]));
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await adapter.PresentAsync(decision, console, CultureInfo.GetCultureInfo("de-DE"), "localized-request"));
        AssertEx.True(console.StandardOutput.StartsWith("Aufruf: hosted localized", StringComparison.Ordinal));
        decision = adapter.Classify(new HostedCommandLineLaunchInput(["localized", "--unknown", "--output=json"]));
        console = new();
        AssertEx.Equal(2, await adapter.PresentAsync(decision, console, CultureInfo.GetCultureInfo("de-DE"), "localized-request"));
        using var result = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("localized-request", result.RootElement.GetProperty("requestId").GetString());
        AssertEx.Equal("RCLI1001", result.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }
    private static async ValueTask Sanitization()
    {
        var console = new TestCommandConsole();
        var diagnostic = new CommandDiagnostic("RCLI2100", "note", "Safe", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Information);
        CommandResponse<string> response = CommandResponse.Succeeded("safe-request", "localized", "runic.text/1", "payload", [diagnostic]);
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
    private static async ValueTask CompletionError()
    {
        // The main test catalog owns "completion"; use a standalone framework-only catalog here.
        var catalog = new CommandCatalogBuilder().Build();
        var console = new TestCommandConsole();
        var app = new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false,
            Culture = CultureInfo.GetCultureInfo("de-DE"), TextResolver = new GermanResolver() };
        AssertEx.Equal(2, await app.RunAsync(["completion", "unknown"]));
        AssertEx.Equal("Shell auswählen: bash, zsh, fish oder powershell.\n", console.StandardError);
        var adapter = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TestExecutionScopeFactory(new TestServiceProvider())))
        { Presentation = new CommandPresentation { TextResolver = new GermanResolver() } };
        var decision = adapter.Classify(new HostedCommandLineLaunchInput(["completion", "unknown"]));
        console = new();
        AssertEx.Equal(2, await adapter.PresentAsync(decision, console, CultureInfo.GetCultureInfo("de-DE"), "completion-request"));
        AssertEx.Equal("Shell auswählen: bash, zsh, fish oder powershell.\n", console.StandardError);
    }
    private static async ValueTask LegacyPresenter()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, new LegacyHelpPresenter()).RunAsync(["localized", "--help"]));
        AssertEx.Equal("Legacy help", console.StandardOutput);
    }
    private sealed class LegacyHelpPresenter : ICommandHelpPresenter
    {
        public ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName,
            ICommandConsole console, CancellationToken cancellationToken) => console.WriteOutAsync("Legacy help".AsMemory(), cancellationToken);
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
