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
internal sealed class ErrorCauseTests
{
    private const string HintKey = "hints.RCLI5000";

    private static CommandApp App(TestCommandConsole console, Func<string, string?>? environment = null, ICommandTextResolver? resolver = null) =>
        new(GeneratedCommandCatalog.Create())
        {
            Name = "sample", Console = console, HandleTerminationSignals = false, TextResolver = resolver,
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
    [Test, DisplayName("causes/generator-reports-optional-before-required-argument")]
    public ValueTask OptionalBeforeRequired()
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

    [Test, DisplayName("causes/generator-reports-attribute-reachable-catalog-rules")]
    public ValueTask CatalogRuleParity()
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
        AssertEx.Equal("Command 'config show' is marked [DefaultCommand] or [RootCommand], but only a single-segment root command can be the default",
            Text(Single(diagnostics, "RCLI9040")));
        AssertEx.True(diagnostics.All(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line > 2));
        AssertEx.Equal(6, diagnostics.Length, string.Join("\n", diagnostics));
        return ValueTask.CompletedTask;
    }

    // Issue #28: a Runic Translations text class injected with [FromServices] failed with a bare RCLI9003. Generators
    // run against the compilation before any generator output is added, so the command generator sees the text class
    // as an error type even when another generator in the same build declares it.
    [Test, DisplayName("causes/generator-explains-a-service-type-from-another-generator")]
    public ValueTask ServiceTypeFromAnotherGenerator()
    {
        CSharpCompilation compilation = GeneratorTests.CreateCompilation(Usings + """
            internal static class Commands
            {
                [Command("hello")] internal static string Hello([FromServices] Example.Translations.HelloText text) => "";
                [Command("option")] internal static string Option([Option("--text")] Example.Translations.HelloText? text = null) => "";
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator(), new TextClassGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        // The other generator's class is part of the build: the type itself resolves.
        AssertEx.True(output.GetTypeByMetadataName("Example.Translations.HelloText") is not null);
        ImmutableArray<Diagnostic> diagnostics = driver.GetRunResult().Results[0].Diagnostics;
        Diagnostic[] unsupported = diagnostics.Where(static diagnostic => diagnostic.Id == "RCLI9003").ToArray();
        AssertEx.Equal(2, unsupported.Length, string.Join("\n", diagnostics));
        AssertEx.Equal(2, diagnostics.Length, string.Join("\n", diagnostics));
        string hello = Text(unsupported.Single(static diagnostic => Text(diagnostic).Contains("command 'hello'", StringComparison.Ordinal)));
        AssertEx.Equal("Parameter 'text' on command 'hello' has unsupported type 'Example.Translations.HelloText'; the command generator cannot resolve this type: "
            + "it is undeclared or produced by another source generator (such as a Runic Translations text class), and source generators do not see each other's output. "
            + "Inject a type declared in source or a referenced assembly instead (for Runic Translations, ITranslationManager) and create the generated type from it in the command", hello);
        string option = Text(unsupported.Single(static diagnostic => Text(diagnostic).Contains("command 'option'", StringComparison.Ordinal)));
        AssertEx.True(option.EndsWith("Use a type declared in source or a referenced assembly", StringComparison.Ordinal), option);
        AssertEx.True(!option.Contains("ITranslationManager", StringComparison.Ordinal), option);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("causes/generator-names-why-a-parameter-type-is-unsupported")]
    public ValueTask UnsupportedTypeReasons()
    {
        ImmutableArray<Diagnostic> diagnostics = Generate("""
            internal static class Commands
            {
                private sealed class Hidden { }
                [Command("hidden")] internal static string HiddenService([FromServices] Hidden hidden) => "";
                [Command("uri")] internal static string Uri([Option("--uri")] System.Uri? uri = null) => "";
                [Command("flag")] internal static string Flag([Argument] bool value) => "";
            }
            """);
        Diagnostic[] unsupported = diagnostics.Where(static diagnostic => diagnostic.Id == "RCLI9003").ToArray();
        AssertEx.Equal(3, unsupported.Length, string.Join("\n", diagnostics));
        AssertEx.True(unsupported.Any(static diagnostic => Text(diagnostic) == "Parameter 'hidden' on command 'hidden' has unsupported type 'Commands.Hidden'; "
            + "a [FromServices] type and the types that contain it must be public or internal, and containing types must not be generic or file-local"),
            string.Join("\n", unsupported.Select(Text)));
        AssertEx.True(unsupported.Any(static diagnostic => Text(diagnostic).StartsWith("Parameter 'uri' on command 'uri' has unsupported type 'System.Uri?'; an argument or option binds string,", StringComparison.Ordinal)
            && Text(diagnostic).EndsWith("add [ConvertWith] for another type, or [FromServices] to inject it", StringComparison.Ordinal)),
            string.Join("\n", unsupported.Select(Text)));
        AssertEx.True(unsupported.Any(static diagnostic => Text(diagnostic) == "Parameter 'value' on command 'flag' has unsupported type 'bool'; a positional argument cannot be Boolean, use an [Option] flag"),
            string.Join("\n", unsupported.Select(Text)));
        return ValueTask.CompletedTask;
    }

    // Stands in for the Runic Translations generator, which declares a text class from its inputs in the same build.
    // (Post-initialization output would be visible to other generators; ordinary source output is not.)
    private sealed class TextClassGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(context.CompilationProvider, static (output, _) => output.AddSource("HelloText.g.cs",
                "namespace Example.Translations { public sealed partial class HelloText { public HelloText(object manager) { } } }"));
    }

    [Test, DisplayName("causes/generator-splits-result-metadata-errors-by-cause")]
    public ValueTask ResultMetadataCauses()
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

    [Test, DisplayName("causes/catalog-exception-lists-its-issues")]
    public ValueTask CatalogExceptionListsIssues()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("h", command => command
            .Argument("a", "a", CommandArity.ZeroOrOne)
            .Argument("b", "b", CommandArity.ExactlyOne)
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec()));
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() => builder.Build());
        // Each issue links to its catalog entry at the release tag of Runic.CommandLine.
        AssertEx.Equal("Command catalog validation failed with 1 issue(s):\nRCLI0015 at 'h': Required argument 'b' cannot follow optional argument 'a'. See "
            + DiagnosticCodeRangeTests.CatalogUrl(typeof(CommandCatalog)) + "rcli0015", exception.Message);
        AssertEx.Equal("RCLI0015", exception.Issues.Single().Code);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("causes/enum-defaults-render-by-member-name")]
    public async ValueTask EnumDefaults()
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
        AssertEx.Equal(exitCode, await App(console, environment).RunAsync([.. args, "--format=json"]));
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

    [Test, DisplayName("causes/type-errors-precede-ranges-and-name-the-option")]
    public async ValueTask TypeBeforeRange()
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

    [Test, DisplayName("causes/ranges-say-between")]
    public async ValueTask Ranges()
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

    // Order per value: conversion (RCLI2005), declared range (RCLI2002), then [ValidateWith] (RCLI2005).
    [Test, NotInParallel, DisplayName("causes/ranges-precede-custom-validators")]
    public async ValueTask RangesPrecedeValidators()
    {
        CauseCountValidator.Seen.Clear();
        (string human, JsonDocument json) = await Fail(2, ["cause-validated", "--count", "50"]);
        using (json)
        {
            AssertEx.Equal("RCLI2002: --count requires a number between 1 and 10.\n", human);
            AssertFault(json, "RCLI2002", "--count requires a number between 1 and 10.");
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2002", "out-of-range", "--count requires a number between 1 and 10.", "binding", "--count", "1", "10");
        }
        (human, json) = await Fail(2, ["cause-validated", "--count", "abc"]);
        using (json)
        {
            AssertEx.Equal("RCLI2005: --count requires a whole number.\n", human);
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2005", "invalid-integer", "--count requires a whole number.", "binding", "--count");
        }
        // A non-integral value is a type error even though it is also outside the range.
        (human, json) = await Fail(2, ["cause-validated", "--count", "99.5"]);
        using (json) AssertEx.Equal("RCLI2005: --count requires a whole number.\n", human);
        // --level binds first; its out-of-range element stops binding before its list validator runs.
        CauseListValidator.Calls = 0;
        (human, json) = await Fail(2, ["cause-validated", "--level", "1", "--level", "0"]);
        using (json) AssertEx.Equal("RCLI2002: --level requires a number between 1 and 3.\n", human);
        AssertEx.Equal(0, CauseCountValidator.Seen.Count, string.Join(",", CauseCountValidator.Seen));
        AssertEx.Equal(0, CauseListValidator.Calls);

        (human, json) = await Fail(2, ["cause-validated", "--count", "7"]);
        using (json)
        {
            AssertEx.Equal("RCLI2005: --count has a value that failed validation.\n", human);
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2005", "validation-failed", "--count has a value that failed validation.", "binding", "--count");
        }
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["cause-validated", "--count", "10", "--level", "3"]));
        AssertEx.Equal("10:3\n", console.StandardOutput);
        AssertEx.True(CauseCountValidator.Seen.All(static value => value is >= 1 and <= 10), string.Join(",", CauseCountValidator.Seen));
        AssertEx.True(CauseCountValidator.Seen.Contains(7) && CauseCountValidator.Seen.Contains(10));
        AssertEx.True(CauseListValidator.Calls > 0);
    }

    [Test, DisplayName("causes/argument-type-errors-name-the-argument")]
    public async ValueTask ArgumentTypeErrors()
    {
        (string human, JsonDocument json) = await Fail(2, ["cause-argument", "many"]);
        using (json)
        {
            AssertEx.Equal("RCLI2005: <COUNT> requires a whole number.\n", human);
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI2005", "invalid-integer", "<COUNT> requires a whole number.", "binding", "<COUNT>");
        }
    }

    [Test, DisplayName("causes/invalid-choices-list-non-sensitive-choices")]
    public async ValueTask Choices()
    {
        (string human, JsonDocument json) = await Fail(2, ["cause-values", "--encoding", "yaml"]);
        using (json)
        {
            AssertEx.Equal("RCLI1015: --encoding must be one of: json, csv.\n", human);
            AssertFault(json, "RCLI1015", "--encoding must be one of: json, csv.");
            AssertDiagnostic(json.RootElement.GetProperty("diagnostics")[0], "RCLI1015", "invalid-choice", "--encoding must be one of: json, csv.", "parse", "--encoding", "json, csv");
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

    [Test, DisplayName("causes/invalid-output-mode-is-specific")]
    public async ValueTask OutputMode()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["cause-values", "--format", "yaml"]));
        AssertEx.Equal("RCLI1010: --format must be human or json.\n", console.StandardError);
        ParseOutcome parsed = PortableCommandSyntaxAdapter.Instance.Parse(GeneratedCommandCatalog.Create(), ["cause-values", "--format=yaml"], ParseSettings.Default);
        AssertEx.Equal("invalid-output-mode", parsed.Diagnostics.Single().Kind);
        AssertEx.Equal("diagnostics.invalid-output-mode", parsed.Diagnostics.Single().MessageKey);
        AssertEx.SequenceEqual(["--format"], parsed.Diagnostics.Single().Arguments);
        parsed = PortableCommandSyntaxAdapter.Instance.Parse(GeneratedCommandCatalog.Create(), ["cause-values"], new ParseSettings("yaml"));
        AssertEx.Equal("RUNIC_COMMANDLINE_OUTPUT must be human or json.", parsed.Diagnostics.Single().Message);
        AssertEx.SequenceEqual(["RUNIC_COMMANDLINE_OUTPUT"], parsed.Diagnostics.Single().Arguments);
    }

    [Test, DisplayName("causes/environment-values-name-their-variable")]
    public async ValueTask EnvironmentSource()
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

    [Test, DisplayName("causes/command-typos-are-suggested-with-a-default-command")]
    public async ValueTask DefaultCommandTypo()
    {
        var builder = new CommandCatalogBuilder();
        foreach (string name in new[] { "list", "add" })
            builder.Command<TestOptions, TestHandler, TestResult>(name, command => command.BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec()));
        CommandCatalog catalog = builder.DefaultCommand("list").Build();
        CommandDiagnostic diagnostic = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["lst"], ParseSettings.Default).Diagnostics.Single();
        AssertEx.Equal("RCLI1002", diagnostic.Code);
        AssertEx.Equal("unknown-command", diagnostic.Kind);
        AssertEx.True(diagnostic.Message.EndsWith("Did you mean 'list'?", StringComparison.Ordinal), diagnostic.Message);
        AssertEx.SequenceEqual(["lst", "list"], diagnostic.Arguments);
        AssertEx.Equal(0, diagnostic.TokenIndex);
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await new CommandApp(catalog) { Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default }.RunAsync(["lst", "--format=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("RCLI1002", frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
            AssertEx.Equal("diagnostics.unknown-command-suggestion", frame.RootElement.GetProperty("diagnostics")[0].GetProperty("messageKey").GetString());
        }
        // A token that resembles no command is still an unexpected argument of the default command.
        AssertEx.Equal("RCLI1006", PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["zzzzzz"], ParseSettings.Default).Diagnostics.Single().Code);
        AssertEx.Equal(ParseOutcomeKind.Invocation, PortableCommandSyntaxAdapter.Instance.Parse(catalog, [], ParseSettings.Default).Kind);
    }

    [Test, DisplayName("causes/handler-exceptions-hint-and-print-in-development")]
    public async ValueTask HandlerExceptions()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(3, await App(console).RunAsync(["cause-throw"]));
        AssertEx.Equal("RCLI5000: The command could not be completed. Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr.\n", console.StandardError);
        console = new();
        AssertEx.Equal(3, await App(console).RunAsync(["cause-throw", "--format=json"]));
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
            AssertEx.Equal(3, await App(console, environment).RunAsync(["cause-throw"]));
            AssertEx.True(console.StandardError.Contains("System.ArgumentException: handler-detail", StringComparison.Ordinal), console.StandardError);
            AssertEx.True(console.StandardError.EndsWith("\nRCLI5000: The command could not be completed.\n", StringComparison.Ordinal), console.StandardError);
            console = new();
            AssertEx.Equal(3, await App(console, environment).RunAsync(["cause-throw", "--format=json"]));
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
            Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default, ExceptionObserver = exception => observed = exception,
        };
        AssertEx.Equal(3, await app.RunAsync(["cause-throw"]));
        AssertEx.Equal("RCLI5000: The command could not be completed.\n", console.StandardError);
        AssertEx.True(observed is ArgumentException);
    }

    [Test, DisplayName("causes/unknown-description-keys-are-reported")]
    public async ValueTask DescriptionKeys()
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
            Console = console, HandleTerminationSignals = false, TextResolver = resolver, Culture = CultureInfo.GetCultureInfo("de"),
            ParseSettings = new ParseSettings { GetEnvironmentVariable = static name => name == "RUNIC_COMMANDLINE_DEBUG" ? "1" : null },
        };
        AssertEx.Equal(0, await app.RunAsync(["cause-keys", "--help"]));
        AssertEx.True(console.StandardError.Contains("warning RCLI0023 at 'cause-keys': " + issue.Message + "\n", StringComparison.Ordinal), console.StandardError);
        console = new();
        AssertEx.Equal(0, await App(console, resolver: resolver).RunAsync(["cause-keys", "--help"]));
        AssertEx.Equal(string.Empty, console.StandardError);
    }

    [Test, DisplayName("causes/new-messages-are-localizable")]
    public async ValueTask Localizable()
    {
        var resolver = new KeyResolver(new Dictionary<string, string>
        {
            ["diagnostics.out-of-range"] = "{0} erwartet eine Zahl von {1} bis {2}.",
            ["diagnostics.invalid-choice"] = "{0} muss eines von {1} sein.",
            [HintKey] = "Mit RUNIC_COMMANDLINE_DEBUG=1 erscheint die Ausnahme auf stderr.",
        });
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console, resolver: resolver).RunAsync(["cause-values", "--limit", "99", "--format=json"]));
        using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("--limit erwartet eine Zahl von 1 bis 50.", frame.RootElement.GetProperty("fault").GetProperty("message").GetString());
            AssertEx.Equal("diagnostics.out-of-range", frame.RootElement.GetProperty("diagnostics")[0].GetProperty("messageKey").GetString());
        }
        console = new();
        AssertEx.Equal(2, await App(console, resolver: resolver).RunAsync(["cause-values", "--encoding", "yaml"]));
        AssertEx.Equal("RCLI1015: --encoding muss eines von json, csv sein.\n", console.StandardError);
        console = new();
        AssertEx.Equal(3, await App(console, resolver: resolver).RunAsync(["cause-throw"]));
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
        [Option("--encoding", Choices = ["json", "csv"])] string encoding = "json",
        [Option("--token", Choices = ["alpha", "beta"], Sensitive = true)] string? token = null) => "ok";

    [Command("cause-validated", Hidden = true)]
    internal static string Validated(
        [Option("--level", Minimum = 1, Maximum = 3), ValidateWith(typeof(CauseListValidator))] IReadOnlyList<int> levels,
        [Option("--count", Minimum = 1, Maximum = 10), ValidateWith(typeof(CauseCountValidator))] int count = 1) =>
        count.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", levels);

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

// Records every value it sees, so a test can prove declared bounds were applied first.
internal sealed class CauseCountValidator : ICommandValueValidator<int>
{
    internal static readonly List<int> Seen = [];
    public static bool IsValid(int value)
    {
        lock (Seen) Seen.Add(value);
        return value != 7;
    }
}

internal sealed class CauseListValidator : ICommandValueValidator<IReadOnlyList<int>>
{
    internal static int Calls;
    public static bool IsValid(IReadOnlyList<int> value)
    {
        Interlocked.Increment(ref Calls);
        return value.All(static level => level is >= 1 and <= 3);
    }
}
