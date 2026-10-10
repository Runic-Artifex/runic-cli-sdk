using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// W250-008: each mistake from the CLI consumer trial names its cause, at build time where the generator can see it.
internal static class ErrorCauseTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("causes/generator-reports-optional-before-required-argument", OptionalBeforeRequired),
        new("causes/generator-reports-attribute-reachable-catalog-rules", CatalogRuleParity),
        new("causes/generator-splits-result-metadata-errors-by-cause", ResultMetadataCauses),
        new("causes/catalog-exception-lists-its-issues", CatalogExceptionListsIssues),
        new("causes/enum-defaults-render-by-member-name", EnumDefaults),
        new("causes/type-errors-precede-ranges-and-name-the-option", TypeBeforeRange),
        new("causes/ranges-say-between", Ranges),
        new("causes/argument-type-errors-name-the-argument", ArgumentTypeErrors),
        new("causes/invalid-choices-list-non-sensitive-choices", Choices),
        new("causes/invalid-output-mode-is-specific", OutputMode),
        new("causes/environment-values-name-their-variable", EnvironmentSource),
        new("causes/command-typos-are-suggested-with-a-default-command", DefaultCommandTypo),
        new("causes/handler-exceptions-hint-and-print-in-development", HandlerExceptions),
        new("causes/unknown-description-keys-are-reported", DescriptionKeys),
        new("causes/new-messages-are-localizable", Localizable),
    ];

    private const string HintKey = "faults.RCLI5000.hint";

    private static CommandApp App(TestCommandConsole console, Func<string, string?>? environment = null, ICommandTextResolver? resolver = null) =>
        new(GeneratedCommandCatalog.Create())
        {
            Name = "sample", Console = console, HandleCancelKeyPress = false, TextResolver = resolver,
            ParseSettings = new ParseSettings { GetEnvironmentVariable = environment ?? (static _ => null) },
        };

    private const string Usings = "using System.Text.Json.Serialization;\nusing Runic.CommandLine;\n";

    private static ImmutableArray<Diagnostic> Generate(string commands)
    {
        CSharpCompilation compilation = GeneratorTests.CreateCompilation(Usings + commands);
        return CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(compilation).GetRunResult().Results.Single().Diagnostics;
    }

    private static string Text(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static Diagnostic Single(ImmutableArray<Diagnostic> diagnostics, string id)
    {
        Diagnostic[] matches = diagnostics.Where(diagnostic => diagnostic.Id == id).ToArray();
        AssertEx.Equal(1, matches.Length, id + " expected once:\n" + string.Join("\n", diagnostics));
        return matches[0];
    }

    // The trial's Mistakes project built cleanly, then every invocation, even --help, aborted at startup.
    private static ValueTask OptionalBeforeRequired()
    {
        const string Mistake = """
            internal static class Commands
            {
                [Command("h")] internal static string H([Argument] string? a, [Argument] string b) => b;
            }
            """;
        Diagnostic diagnostic = Single(Generate(Mistake), "RCLI9035");
        AssertEx.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        AssertEx.Equal("Argument 'b' on command 'h' is required but follows optional argument 'a'; make it optional or move it first", Text(diagnostic));
        // Located at the required argument itself, so the IDE and build output point at the declaration to fix.
        AssertEx.Equal(Usings.Length + Mistake.IndexOf("string b)", StringComparison.Ordinal) + "string ".Length, diagnostic.Location.SourceSpan.Start);
        AssertEx.Equal(4, diagnostic.Location.GetLineSpan().StartLinePosition.Line);

        // Optional after required, and a trailing list after an optional value, stay valid.
        ImmutableArray<Diagnostic> valid = Generate("""
            internal static class Commands
            {
                [Command("ok")] internal static string Ok([Argument] string a, [Argument] string? b = null,
                    [Argument(AllowMultipleValues = true)] System.Collections.Generic.IReadOnlyList<string>? rest = null) => a;
            }
            """);
        AssertEx.Equal(0, valid.Length, string.Join("\n", valid));
        return ValueTask.CompletedTask;
    }

    private static ValueTask CatalogRuleParity()
    {
        ImmutableArray<Diagnostic> diagnostics = Generate("""
            internal static class Commands
            {
                [Command("bounds")] internal static string Bounds([Option("--name", Minimum = 1)] string name = "") => name;
                [Command("exists")] internal static string Exists([Option("--path", MustExist = true)] string path = "") => path;
                [Command("spelling")] internal static string Spelling([Option("--apply", Requires = ["--dry-run"])] bool apply = false, [Option("--dry-run")] bool dryRun = false) => "";
                [Command("self")] internal static string Self([Option("--apply", ConflictsWith = ["apply"])] bool apply = false) => "";
                [Command("global")] internal static string Global([Option("--apply", Requires = ["verbose"])] bool apply = false) => "";
                [Command("config show"), DefaultCommand] internal static string Show() => "";
                [Command("fine")] internal static string Fine([Option("--count", Minimum = 1, Maximum = 5)] int count = 1, [Option("--file", MustExist = true)] System.IO.FileInfo? file = null) => "";
            }
            """);
        AssertEx.Equal("Parameter 'name' on command 'bounds' sets Minimum or Maximum, which apply only to int, long, double and decimal values, not 'string'",
            Text(Single(diagnostics, "RCLI9036")));
        AssertEx.True(Text(Single(diagnostics, "RCLI9037")).StartsWith("Parameter 'path' on command 'exists' sets MustExist", StringComparison.Ordinal));
        Diagnostic[] relationship = diagnostics.Where(static diagnostic => diagnostic.Id == "RCLI9038").ToArray();
        AssertEx.Equal(2, relationship.Length, string.Join("\n", diagnostics));
        AssertEx.True(relationship.Any(static diagnostic => Text(diagnostic).StartsWith("Option '--apply' on command 'spelling' lists '--dry-run'", StringComparison.Ordinal)));
        AssertEx.True(relationship.Any(static diagnostic => Text(diagnostic).StartsWith("Option '--apply' on command 'self' lists 'apply'", StringComparison.Ordinal)));
        Diagnostic unknown = Single(diagnostics, "RCLI9039");
        AssertEx.Equal(DiagnosticSeverity.Warning, unknown.Severity);
        AssertEx.True(Text(unknown).Contains("'verbose'", StringComparison.Ordinal));
        AssertEx.Equal("Command 'config show' is marked [DefaultCommand], but only a single-segment root command can be the default",
            Text(Single(diagnostics, "RCLI9040")));
        AssertEx.True(diagnostics.All(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line > 2));
        AssertEx.Equal(6, diagnostics.Length, string.Join("\n", diagnostics));
        return ValueTask.CompletedTask;
    }

    private static ValueTask ResultMetadataCauses()
    {
        ImmutableArray<Diagnostic> diagnostics = Generate("""
            public sealed record Report(int Count);
            public sealed record Other(int X);
            [JsonSerializable(typeof(Other))] internal sealed partial class WrongContext : JsonSerializerContext;
            internal sealed class NotAContext { }
            internal static class Commands
            {
                [Command("count")] internal static int Count() => 1;
                [Command("missing")] internal static Report Missing() => new(1);
                [Command("wrong-type"), CommandResult("sample.report/1", typeof(WrongContext))] internal static Report WrongType() => new(1);
                [Command("not-context"), CommandResult("sample.report/1", typeof(NotAContext))] internal static Report NotContext() => new(1);
                [Command("empty-payload"), CommandResult("", typeof(WrongContext))] internal static Other EmptyPayload() => new(1);
            }
            """);
        Diagnostic[] missing = diagnostics.Where(static diagnostic => diagnostic.Id == "RCLI9028").ToArray();
        AssertEx.Equal(2, missing.Length, string.Join("\n", diagnostics));
        AssertEx.True(Text(missing[0]).StartsWith("Command 'count' returns 'int'", StringComparison.Ordinal), Text(missing[0]));
        AssertEx.True(Text(missing[0]).Contains("not the exit code", StringComparison.Ordinal));
        AssertEx.True(Text(missing[0]).Contains("CommandOutcome<T>", StringComparison.Ordinal));
        AssertEx.True(Text(missing[1]).StartsWith("Command 'missing' returns 'Report'", StringComparison.Ordinal), Text(missing[1]));
        AssertEx.True(!Text(missing[1]).Contains("exit code", StringComparison.Ordinal));
        AssertEx.Equal("Command 'wrong-type' returns 'Report', but JSON context 'WrongContext' has no [JsonSerializable(typeof(Report))]; add it to the context",
            Text(Single(diagnostics, "RCLI9042")));
        AssertEx.True(Text(Single(diagnostics, "RCLI9041")).Contains("'NotAContext'", StringComparison.Ordinal));
        AssertEx.True(Text(Single(diagnostics, "RCLI9005")).Contains("result payload type", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    private static ValueTask CatalogExceptionListsIssues()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("h", command => command
            .Argument("a", "a", CommandArity.ZeroOrOne)
            .Argument("b", "b", CommandArity.ExactlyOne)
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec()));
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() => builder.Build());
        AssertEx.Equal("Command catalog validation failed with 1 issue(s):\nRCLI0015 at 'h': Required argument 'b' cannot follow optional argument 'a'.", exception.Message);
        AssertEx.Equal("RCLI0015", exception.Issues.Single().Code);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask EnumDefaults()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["cause-values", "--help"]));
        AssertEx.True(console.StandardOutput.Contains("--priority <priority>  [default: Careful]", StringComparison.Ordinal), console.StandardOutput);
    }

    // Runs once for human output and once for JSON; returns stderr of the first and the frame of the second.
    private static async ValueTask<(string Human, JsonDocument Json)> Fail(int exitCode, string[] args, Func<string, string?>? environment = null)
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(exitCode, await App(console, environment).RunAsync(args));
        AssertEx.Equal(string.Empty, console.StandardOutput);
        string human = console.StandardError;
        console = new();
        AssertEx.Equal(exitCode, await App(console, environment).RunAsync([.. args, "--output=json"]));
        return (human, CommandTestEnvelope.Parse(console.StandardOutput));
    }

    private static void AssertDiagnostic(JsonElement diagnostic, string code, string kind, string message, string phase, params string[] arguments)
    {
        AssertEx.Equal(code, diagnostic.GetProperty("code").GetString());
        AssertEx.Equal(kind, diagnostic.GetProperty("kind").GetString());
        AssertEx.Equal("diagnostics." + kind, diagnostic.GetProperty("messageKey").GetString());
        AssertEx.Equal(message, diagnostic.GetProperty("message").GetString());
        AssertEx.Equal(phase, diagnostic.GetProperty("phase").GetString());
        AssertEx.SequenceEqual(arguments, diagnostic.GetProperty("arguments").EnumerateArray().Select(static item => item.GetString()!));
    }

    private static void AssertFault(JsonDocument frame, string code, string message)
    {
        AssertEx.Equal(false, frame.RootElement.GetProperty("success").GetBoolean());
        AssertEx.Equal(2, frame.RootElement.GetProperty("exitCode").GetInt32());
        AssertEx.Equal(code, frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
        AssertEx.Equal(message, frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
    }

    private static async ValueTask TypeBeforeRange()
    {
        (string human, JsonDocument json) = await Fail(2, ["cause-values", "--limit", "abc"]);
        using (json)
        {
            AssertEx.Equal("RCLI2005: --limit requires a whole number.\n", human);
            AssertFault(json, "RCLI2005", "--limit requires a whole number.");
            AssertEx.Equal(1, json.RootElement.GetProperty("diagnostics").GetArrayLength());
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2005", "invalid-integer", "--limit requires a whole number.", "binding", "--limit");
        }
        (human, json) = await Fail(2, ["cause-values", "--delay", "1.5"]);
        using (json) AssertEx.Equal("RCLI2005: --delay requires a whole number.\n", human);
        (human, json) = await Fail(2, ["cause-values", "--ratio", "x"]);
        using (json) AssertEx.Equal("RCLI2005: --ratio requires a number.\n", human);
        (human, json) = await Fail(2, ["cause-values", "--id", "x"]);
        using (json)
        {
            AssertEx.True(human.StartsWith("RCLI2005: --id requires a GUID", StringComparison.Ordinal), human);
            AssertEx.Equal("invalid-guid", json.RootElement.GetProperty("diagnostics")[0].GetProperty("kind").GetString());
        }
    }

    private static async ValueTask Ranges()
    {
        (string human, JsonDocument json) = await Fail(2, ["cause-values", "--limit", "99"]);
        using (json)
        {
            AssertEx.Equal("RCLI2002: --limit requires a number between 1 and 50.\n", human);
            AssertFault(json, "RCLI2002", "--limit requires a number between 1 and 50.");
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2002", "out-of-range", "--limit requires a number between 1 and 50.", "binding", "--limit", "1", "50");
        }
        (human, json) = await Fail(2, ["cause-values", "--ratio", "0.25"]);
        using (json)
        {
            JsonElement diagnostic = json.RootElement.GetProperty("diagnostics")[0];
            AssertEx.Equal("below-minimum", diagnostic.GetProperty("kind").GetString());
            AssertEx.Equal(human, "RCLI2002: " + diagnostic.GetProperty("message").GetString() + "\n");
            AssertEx.True(human.Contains("0.5", StringComparison.Ordinal), human);
        }
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["cause-values", "--limit", "50", "--ratio", "0.5"]));
    }

    private static async ValueTask ArgumentTypeErrors()
    {
        (string human, JsonDocument json) = await Fail(2, ["cause-argument", "many"]);
        using (json)
        {
            AssertEx.Equal("RCLI2005: <COUNT> requires a whole number.\n", human);
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2005", "invalid-integer", "<COUNT> requires a whole number.", "binding", "<COUNT>");
        }
    }

    private static async ValueTask Choices()
    {
        (string human, JsonDocument json) = await Fail(2, ["cause-values", "--format", "yaml"]);
        using (json)
        {
            AssertEx.Equal("RCLI1015: --format must be one of: json, csv.\n", human);
            AssertFault(json, "RCLI1015", "--format must be one of: json, csv.");
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI1015", "invalid-choice", "--format must be one of: json, csv.", "parse", "--format", "json, csv");
        }
        (human, json) = await Fail(2, ["cause-values", "--token", "nope"]);
        using (json)
        {
            AssertEx.Equal("RCLI1015: --token must be one of the choices shown in --help.\n", human);
            JsonElement diagnostic = json.RootElement.GetProperty("diagnostics")[0];
            AssertEx.Equal("diagnostics.invalid-choice-hidden", diagnostic.GetProperty("messageKey").GetString());
            AssertEx.SequenceEqual(["--token"], diagnostic.GetProperty("arguments").EnumerateArray().Select(static item => item.GetString()!));
            AssertEx.True(!json.RootElement.GetRawText().Contains("alpha", StringComparison.Ordinal));
        }
    }

    private static async ValueTask OutputMode()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["cause-values", "--output", "yaml"]));
        AssertEx.Equal("RCLI1010: --output must be human or json.\n", console.StandardError);
        ParseOutcome parsed = PortableCommandSyntaxAdapter.Instance.Parse(GeneratedCommandCatalog.Create(), ["cause-values", "--output=yaml"], ParseSettings.Default);
        AssertEx.Equal("invalid-output-mode", parsed.Diagnostics.Single().Kind);
        AssertEx.Equal("diagnostics.invalid-output-mode", parsed.Diagnostics.Single().MessageKey);
        AssertEx.SequenceEqual(["--output"], parsed.Diagnostics.Single().Arguments);
        parsed = PortableCommandSyntaxAdapter.Instance.Parse(GeneratedCommandCatalog.Create(), ["cause-values"], new ParseSettings("yaml"));
        AssertEx.Equal("RUNIC_COMMANDLINE_OUTPUT must be human or json.", parsed.Diagnostics.Single().Message);
        AssertEx.SequenceEqual(["RUNIC_COMMANDLINE_OUTPUT"], parsed.Diagnostics.Single().Arguments);
    }

    private static async ValueTask EnvironmentSource()
    {
        static string? Environment(string name) => name switch { "CAUSE_LIMIT" => "x", "CAUSE_VERBOSE" => "maybe", _ => null };
        (string human, JsonDocument json) = await Fail(2, ["cause-env"], Environment);
        using (json)
        {
            AssertEx.Equal("RCLI1014: CAUSE_VERBOSE must be true/false, yes/no or 1/0 for --verbose.\n", human);
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI1014", "invalid-environment-value",
                "CAUSE_VERBOSE must be true/false, yes/no or 1/0 for --verbose.", "parse", "--verbose", "CAUSE_VERBOSE");
        }
        (human, json) = await Fail(2, ["cause-env", "--verbose"], Environment);
        using (json)
        {
            AssertEx.Equal("RCLI2005: --limit requires a whole number.\nRCLI2006: The value for --limit came from environment variable CAUSE_LIMIT.\n", human);
            AssertFault(json, "RCLI2005", "--limit requires a whole number.");
            JsonElement diagnostics = json.RootElement.GetProperty("diagnostics");
            AssertEx.Equal(2, diagnostics.GetArrayLength());
            AssertDiagnostic(diagnostics[1], "RCLI2006", "environment-value-source", "The value for --limit came from environment variable CAUSE_LIMIT.", "binding", "--limit", "CAUSE_LIMIT");
            AssertEx.Equal("information", diagnostics[1].GetProperty("severity").GetString());
        }
        // An explicit value is not attributed to the environment.
        (human, json) = await Fail(2, ["cause-env", "--verbose", "--limit", "y"], Environment);
        using (json) AssertEx.Equal("RCLI2005: --limit requires a whole number.\n", human);
    }

    private static async ValueTask DefaultCommandTypo()
    {
        var builder = new CommandCatalogBuilder();
        foreach (string name in new[] { "list", "add" })
            builder.Command<TestOptions, TestHandler, TestResult>(name, command => command.BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec()));
        CommandCatalog catalog = builder.DefaultCommand("list").Build();
        CommandDiagnostic diagnostic = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["lst"], ParseSettings.Default).Diagnostics.Single();
        AssertEx.Equal("RCLI1002", diagnostic.Code);
        AssertEx.Equal("unknown-command", diagnostic.Kind);
        AssertEx.True(diagnostic.Message.EndsWith("Did you mean 'list'?", StringComparison.Ordinal), diagnostic.Message);
        AssertEx.SequenceEqual(["lst"], diagnostic.Arguments);
        AssertEx.Equal(0, diagnostic.TokenIndex);
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false, ParseSettings = ParseSettings.Default }.RunAsync(["lst", "--output=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("RCLI1002", frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
            AssertEx.Equal("diagnostics.unknown-command", frame.RootElement.GetProperty("diagnostics")[0].GetProperty("messageKey").GetString());
        }
        // A token that resembles no command is still an unexpected argument of the default command.
        AssertEx.Equal("RCLI1006", PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["zzzzzz"], ParseSettings.Default).Diagnostics.Single().Code);
        AssertEx.Equal(ParseOutcomeKind.Invocation, PortableCommandSyntaxAdapter.Instance.Parse(catalog, [], ParseSettings.Default).Kind);
    }

    private static async ValueTask HandlerExceptions()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(70, await App(console).RunAsync(["cause-throw"]));
        AssertEx.Equal("RCLI5000: The command could not be completed. Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr.\n", console.StandardError);
        console = new();
        AssertEx.Equal(70, await App(console).RunAsync(["cause-throw", "--output=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("RCLI5000", frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
            AssertEx.Equal("The command could not be completed.", frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
            AssertEx.Equal(0, frame.RootElement.GetProperty("diagnostics").GetArrayLength());
        }
        AssertEx.True(!console.StandardOutput.Contains("RUNIC_COMMANDLINE_DEBUG", StringComparison.Ordinal));
        AssertEx.Equal(string.Empty, console.StandardError);

        foreach (Func<string, string?> environment in new Func<string, string?>[]
        {
            static name => name == "RUNIC_COMMANDLINE_DEBUG" ? "1" : null,
            static name => name == "DOTNET_ENVIRONMENT" ? "Development" : null,
        })
        {
            console = new();
            AssertEx.Equal(70, await App(console, environment).RunAsync(["cause-throw"]));
            AssertEx.True(console.StandardError.Contains("System.ArgumentException: handler-detail", StringComparison.Ordinal), console.StandardError);
            AssertEx.True(console.StandardError.EndsWith("\nRCLI5000: The command could not be completed.\n", StringComparison.Ordinal), console.StandardError);
            console = new();
            AssertEx.Equal(70, await App(console, environment).RunAsync(["cause-throw", "--output=json"]));
            using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
            AssertEx.Equal("The command could not be completed.", frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
            AssertEx.True(!console.StandardOutput.Contains("handler-detail", StringComparison.Ordinal));
            AssertEx.True(console.StandardError.Contains("handler-detail", StringComparison.Ordinal));
        }

        // An application observer owns exception logging, so no hint is shown.
        console = new();
        Exception? observed = null;
        var app = new CommandApp(GeneratedCommandCatalog.Create())
        {
            Console = console, HandleCancelKeyPress = false, ParseSettings = ParseSettings.Default, ExceptionObserver = exception => observed = exception,
        };
        AssertEx.Equal(70, await app.RunAsync(["cause-throw"]));
        AssertEx.Equal("RCLI5000: The command could not be completed.\n", console.StandardError);
        AssertEx.True(observed is ArgumentException);
    }

    private static async ValueTask DescriptionKeys()
    {
        var resolver = new KeyResolver(new Dictionary<string, string> { ["commands.cause-keys"] = "Beschreibung" });
        var context = new CommandTextContext(CultureInfo.GetCultureInfo("de"), resolver);
        CommandCatalogIssue issue = context.ValidateDescriptionKeys(GeneratedCommandCatalog.Create()).Single(static issue => issue.Location == "cause-keys");
        AssertEx.Equal("RCLI0023", issue.Code);
        AssertEx.Equal("cause-keys", issue.Location);
        AssertEx.Equal("Description key 'options.cause-keys.limt' for option '--limit' did not resolve in culture 'de'; the literal description is shown instead.", issue.Message);
        AssertEx.Equal(0, new CommandTextContext(CultureInfo.InvariantCulture).ValidateDescriptionKeys(GeneratedCommandCatalog.Create()).Count);

        var console = new TestCommandConsole();
        var app = new CommandApp(GeneratedCommandCatalog.Create())
        {
            Console = console, HandleCancelKeyPress = false, TextResolver = resolver, Culture = CultureInfo.GetCultureInfo("de"),
            ParseSettings = new ParseSettings { GetEnvironmentVariable = static name => name == "RUNIC_COMMANDLINE_DEBUG" ? "1" : null },
        };
        AssertEx.Equal(0, await app.RunAsync(["cause-keys", "--help"]));
        AssertEx.True(console.StandardError.Contains("warning RCLI0023 at 'cause-keys': " + issue.Message + "\n", StringComparison.Ordinal), console.StandardError);
        console = new();
        AssertEx.Equal(0, await App(console, resolver: resolver).RunAsync(["cause-keys", "--help"]));
        AssertEx.Equal(string.Empty, console.StandardError);
    }

    private static async ValueTask Localizable()
    {
        var resolver = new KeyResolver(new Dictionary<string, string>
        {
            ["diagnostics.out-of-range"] = "{0} erwartet eine Zahl von {1} bis {2}.",
            ["diagnostics.invalid-choice"] = "{0} muss eines von {1} sein.",
            [HintKey] = "Mit RUNIC_COMMANDLINE_DEBUG=1 erscheint die Ausnahme auf stderr.",
        });
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console, resolver: resolver).RunAsync(["cause-values", "--limit", "99", "--output=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("--limit erwartet eine Zahl von 1 bis 50.", frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
            AssertEx.Equal("diagnostics.out-of-range", frame.RootElement.GetProperty("diagnostics")[0].GetProperty("messageKey").GetString());
        }
        console = new();
        AssertEx.Equal(2, await App(console, resolver: resolver).RunAsync(["cause-values", "--format", "yaml"]));
        AssertEx.Equal("RCLI1015: --format muss eines von json, csv sein.\n", console.StandardError);
        console = new();
        AssertEx.Equal(70, await App(console, resolver: resolver).RunAsync(["cause-throw"]));
        AssertEx.Equal("RCLI5000: The command could not be completed. Mit RUNIC_COMMANDLINE_DEBUG=1 erscheint die Ausnahme auf stderr.\n", console.StandardError);
    }

    private sealed class KeyResolver(Dictionary<string, string> texts) : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) =>
            texts.TryGetValue(key, out string? text) ? string.Format(CultureInfo.InvariantCulture, text, [.. arguments]) : null;
    }
}

internal enum CausePriority { Low, Careful, High }

// Hidden so the shared generated catalog's root help listings stay unchanged.
internal static class SampleCauseCommands
{
    [Command("cause-values", Hidden = true)]
    internal static string Values(
        [Option("--limit", Minimum = 1, Maximum = 50)] int limit = 10,
        [Option("--delay")] int delay = 0,
        [Option("--ratio", Minimum = 0.5)] double ratio = 1,
        [Option("--id")] Guid? id = null,
        [Option("--priority")] CausePriority priority = CausePriority.Careful,
        [Option("--format", Choices = ["json", "csv"])] string format = "json",
        [Option("--token", Choices = ["alpha", "beta"], Sensitive = true)] string? token = null) => "ok";

    [Command("cause-argument", Hidden = true)]
    internal static string Argument([Argument(ValueName = "COUNT")] int count) => count.ToString(CultureInfo.InvariantCulture);

    [Command("cause-env", Hidden = true)]
    internal static string Environment(
        [Option("--limit", EnvironmentVariable = "CAUSE_LIMIT")] int limit = 1,
        [Option("--verbose", EnvironmentVariable = "CAUSE_VERBOSE")] bool verbose = false) => "ok";

    [Command("cause-throw", Hidden = true)]
    internal static string Throw() => throw new ArgumentException("handler-detail");

    [Command("cause-keys", Hidden = true, DescriptionKey = "commands.cause-keys", Description = "Keys")]
    internal static string Keys([Option("--limit", DescriptionKey = "options.cause-keys.limt", Description = "Limit")] int limit = 1) => "ok";
}
