using System.Text.Json;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// Usage errors name the argument that is missing and, in human output, read as a sentence followed by the usage line
// and the help command (runic-cli-sdk#82). JSON output keeps its codes, kinds and arguments.
internal sealed class UsageErrorTests
{
    private static CommandCatalog Catalog(CommandSyntaxDialect syntax = CommandSyntaxDialect.Portable, bool valueNames = false)
    {
        var builder = new CommandCatalogBuilder().Syntax(syntax);
        CommandBuilder<TestOptions, TestHandler, TestResult> bind = CatalogTests.ValidCommand(builder, "bind")
            .Argument("world", "world", CommandArity.ExactlyOne)
            .Argument("binding", "binding", CommandArity.ExactlyOne);
        if (valueNames) bind.ParameterHelp("binding", new CommandHelp(valueName: "Verknüpfung"));
        // A bounded range before a required argument: the range takes only the tokens the later minimum leaves.
        CatalogTests.ValidCommand(builder, "copy")
            .Argument("sources", "sources", new CommandArity(1, 2))
            .Argument("target", "target", CommandArity.ExactlyOne);
        // Optional arguments follow the required ones (RCLI0015).
        CatalogTests.ValidCommand(builder, "tag")
            .Argument("source", "source", CommandArity.ExactlyOne)
            .Argument("label", "label", CommandArity.ZeroOrOne);
        CatalogTests.ValidCommand(builder, "variadic")
            .Argument("program", "program", CommandArity.ExactlyOne)
            .Argument("files", "files", CommandArity.OneOrMore);
        CatalogTests.ValidCommand(builder, "pair")
            .Argument("points", "points", new CommandArity(2, null));
        CatalogTests.ValidCommand(builder, "forward")
            .Argument("program", "program", CommandArity.ExactlyOne)
            .Argument("rest", "rest", CommandArity.ZeroOrMore)
            .TrailingArguments("native", "native");
        return builder.Build();
    }

    private static ParseOutcome Parse(params string[] args) =>
        PortableCommandSyntaxAdapter.Instance.Parse(Catalog(), args, ParseSettings.Default);

    private static void AssertMissing(string argument, params string[] args)
    {
        ParseOutcome outcome = Parse(args);
        string line = string.Join(' ', args);
        AssertEx.Equal(ParseOutcomeKind.Error, outcome.Kind, line);
        CommandDiagnostic diagnostic = outcome.Diagnostics.Single();
        AssertEx.Equal("RCLI1005", diagnostic.Code, line);
        AssertEx.Equal("missing-argument", diagnostic.Kind, line);
        AssertEx.Equal(args.Length, diagnostic.TokenIndex, line);
        // The argument is named as the usage line shows it.
        AssertEx.SequenceEqual(["<" + argument + ">"], diagnostic.Arguments, line);
        AssertEx.Equal("The <" + argument + "> argument is missing.", diagnostic.Message, line);
        // Programs read the argument's ID; in this catalog the IDs are the names.
        AssertEx.Equal(argument, diagnostic.Binding, line);
    }

    private static void AssertBound(string[] args, params (string Id, string[] Values)[] expected)
    {
        ParseOutcome outcome = Parse(args);
        string line = string.Join(' ', args);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind, line + ": " + string.Join(", ", outcome.Diagnostics.Select(static d => d.Message)));
        AssertEx.SequenceEqual(expected.Select(static binding => binding.Id), outcome.Invocation!.Arguments.Select(static binding => binding.Id), line);
        for (int index = 0; index < expected.Length; index++)
            AssertEx.SequenceEqual(expected[index].Values, outcome.Invocation.Arguments[index].Values, line);
    }

    [Test, DisplayName("usage-errors/missing-argument-names-the-first-argument-without-a-token")]
    public ValueTask MissingArgumentIsTheFirstUnbound()
    {
        // The given token binds to the first argument, so the second is the one missing.
        AssertMissing("binding", "bind", "w.storyline");
        AssertMissing("world", "bind");
        AssertBound(["bind", "w.storyline", "b"], ("world", ["w.storyline"]), ("binding", ["b"]));

        // A minimum of more than one token: missing until every token is there.
        AssertMissing("points", "pair", "1");
        AssertBound(["pair", "1", "2", "3"], ("points", ["1", "2", "3"]));

        // A required list after a required argument.
        AssertMissing("files", "variadic", "tool");
        AssertMissing("program", "variadic");
        AssertBound(["variadic", "tool", "a", "b"], ("program", ["tool"]), ("files", ["a", "b"]));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("usage-errors/ranges-and-optional-arguments-take-only-surplus-tokens")]
    public ValueTask RangesAndOptionalArguments()
    {
        // A bounded range before a required argument leaves the required argument its token.
        AssertMissing("target", "copy", "a");
        AssertMissing("sources", "copy");
        AssertBound(["copy", "a", "t"], ("sources", ["a"]), ("target", ["t"]));
        AssertBound(["copy", "a", "b", "t"], ("sources", ["a", "b"]), ("target", ["t"]));
        ParseOutcome surplus = Parse("copy", "a", "b", "c", "t");
        AssertEx.Equal("RCLI1006", surplus.Diagnostics.Single().Code);
        AssertEx.Equal(4, surplus.Diagnostics.Single().TokenIndex);
        AssertEx.Equal("Too many arguments were given.", surplus.Diagnostics.Single().Message);

        // An optional argument after a required one.
        AssertBound(["tag", "s"], ("source", ["s"]));
        AssertBound(["tag", "s", "l"], ("source", ["s"]), ("label", ["l"]));
        AssertMissing("source", "tag");

        // An unbounded optional argument and trailing arguments keep their tokens.
        AssertBound(["forward", "tool"], ("program", ["tool"]));
        AssertBound(["forward", "tool", "a", "b", "--", "-x"], ("program", ["tool"]), ("rest", ["a", "b"]), ("native", ["-x"]));
        AssertMissing("program", "forward", "--", "-x");
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("usage-errors/human-output-is-the-message-the-usage-line-and-the-help-command")]
    public async ValueTask HumanOutput()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console).RunAsync(["bind", "w.storyline"]));
        AssertEx.Equal("", console.StandardOutput);
        AssertEx.Equal(
            "The <binding> argument is missing.\n" +
            "Usage: storyline bind <world> <binding> [options]\n" +
            "Run \"storyline bind --help\" for more information.\n",
            console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console).RunAsync(["bnid", "w.storyline"]));
        AssertEx.Equal(
            "The command 'bnid' is not recognized. Did you mean 'bind'?\n" +
            "Usage: storyline <command> [options]\n" +
            "Run \"storyline --help\" for more information.\n",
            console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console).RunAsync(["tag", "--bogus", "s"]));
        AssertEx.Equal(
            "The option '--bogus' is not recognized.\n" +
            "Usage: storyline tag <source> [label] [options]\n" +
            "Run \"storyline tag --help\" for more information.\n",
            console.StandardError);
    }

    [Test, DisplayName("usage-errors/json-output-keeps-codes-and-adds-no-hint")]
    public async ValueTask JsonOutput()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console).RunAsync(["bind", "w.storyline", "--format", "json"]));
        AssertEx.Equal("", console.StandardError);
        using JsonDocument frame = CommandTestEnvelope.Parse(console.StandardOutput);
        JsonElement fault = frame.RootElement.GetProperty("fault");
        AssertEx.Equal("RCLI1005", fault.GetProperty("code").GetString());
        AssertEx.Equal("The <binding> argument is missing.", fault.GetProperty("message").GetString());
        JsonElement diagnostic = frame.RootElement.GetProperty("diagnostics").EnumerateArray().Single();
        AssertEx.Equal("RCLI1005", diagnostic.GetProperty("code").GetString());
        AssertEx.Equal("missing-argument", diagnostic.GetProperty("kind").GetString());
        AssertEx.Equal("diagnostics.missing-argument", diagnostic.GetProperty("messageKey").GetString());
        AssertEx.SequenceEqual(["<binding>"], diagnostic.GetProperty("arguments").EnumerateArray().Select(static item => item.GetString()!));
        AssertEx.Equal("binding", diagnostic.GetProperty("binding").GetString());
        AssertEx.True(!console.StandardOutput.Contains("--help", StringComparison.Ordinal), console.StandardOutput);
    }

    [Test, DisplayName("usage-errors/unknown-command-names-the-command-token")]
    public ValueTask UnknownCommandArgument()
    {
        CommandCatalog catalog = Catalog();
        ParseOutcome typed = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["--format", "json", "frob"], ParseSettings.Default);
        AssertEx.SequenceEqual(["frob"], typed.Diagnostics.Single().Arguments);
        AssertEx.Equal("The command 'frob' is not recognized.", typed.Diagnostics.Single().Message);

        ParseOutcome none = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["--format", "json"], ParseSettings.Default);
        AssertEx.Equal("RCLI1002", none.Diagnostics.Single().Code);
        AssertEx.Equal(0, none.Diagnostics.Single().Arguments.Count);
        AssertEx.Equal("No command was given.", none.Diagnostics.Single().Message);
        // The kind and code stay those of an unknown command; the text has its own key.
        AssertEx.Equal("unknown-command", none.Diagnostics.Single().Kind);
        AssertEx.Equal(CommandTextKeys.DiagnosticMissingCommand, none.Diagnostics.Single().MessageKey);
        AssertEx.Equal(CommandTextKeys.DiagnosticUnknownCommand, typed.Diagnostics.Single().MessageKey);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("usage-errors/the-hint-is-localizable")]
    public async ValueTask Localizable()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console, new HintResolver()).RunAsync(["bind", "w.storyline"]));
        AssertEx.Equal(
            "Das Argument <binding> fehlt.\n" +
            "Aufruf: storyline bind <world> <binding> [Optionen]\n" +
            "Hilfe: storyline bind --help\n",
            console.StandardError);
    }

    [Test, DisplayName("usage-errors/json-binding-is-the-argument-id-in-every-dialect-and-culture")]
    public async ValueTask JsonBinding()
    {
        foreach ((CommandSyntaxDialect syntax, string[] args) in new[]
        {
            (CommandSyntaxDialect.Portable, new[] { "bind", "w.storyline", "--format", "json" }),
            (CommandSyntaxDialect.WindowsSlash, new[] { "/bind", "w.storyline", "/format", "json" }),
        })
        foreach (bool german in new[] { false, true })
        {
            string label = syntax + (german ? " de" : " en");
            var console = new TestCommandConsole();
            var app = new CommandApp(Catalog(syntax, valueNames: true))
            {
                Name = "storyline", Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default,
                TextResolver = german ? new HintResolver() : null, Culture = System.Globalization.CultureInfo.GetCultureInfo(german ? "de" : "en"),
            };
            AssertEx.Equal(CommandExitCodes.Usage, await app.RunAsync(args), label + ": " + console.StandardError);
            using JsonDocument frame = CommandTestEnvelope.Parse(console.StandardOutput);
            JsonElement diagnostic = frame.RootElement.GetProperty("diagnostics").EnumerateArray().Single();
            AssertEx.Equal(german ? "Das Argument <Verknüpfung> fehlt." : "The <Verknüpfung> argument is missing.", diagnostic.GetProperty("message").GetString(), label);
            AssertEx.SequenceEqual(["<Verknüpfung>"], diagnostic.GetProperty("arguments").EnumerateArray().Select(static item => item.GetString()!), label);
            // The ID does not follow the value name, the dialect or the culture.
            AssertEx.Equal("binding", diagnostic.GetProperty("binding").GetString(), label);
            // The fault carries no arguments, so it has no binding either; its details stay empty.
            JsonElement fault = frame.RootElement.GetProperty("fault");
            AssertEx.True(!fault.TryGetProperty("binding", out _) && !fault.GetProperty("details").EnumerateObject().Any(), label);
            CommandResponse<ExportPayload> read = CommandJsonEnvelopeReader.Read(System.Text.Encoding.UTF8.GetBytes(console.StandardOutput), "sample.unused/1",
                CorpusJsonContext.Default.ExportPayload);
            AssertEx.Equal("binding", read.Diagnostics.Single().Binding, label);
        }

        // A diagnostic about no declared binding omits the member.
        var unknown = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(unknown).RunAsync(["tag", "--bogus", "s", "--format", "json"]));
        using JsonDocument unknownFrame = CommandTestEnvelope.Parse(unknown.StandardOutput);
        AssertEx.True(!unknownFrame.RootElement.GetProperty("diagnostics")[0].TryGetProperty("binding", out _), unknown.StandardOutput);
        AssertEx.Throws<ArgumentException>(() => new CommandDiagnostic("RCLI8001", "custom", "Custom.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Error) { Binding = "Not-An-Id" });
        // A reader rejects a binding that is not an ID.
        string invalid = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Corpus", "protocol", "examples", "missing-argument.json"))
            .Replace("\"binding\":\"destination\"", "\"binding\":\"Destination\"", StringComparison.Ordinal);
        CommandProtocolException rejected = AssertEx.Throws<CommandProtocolException>(() => CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(invalid), "sample.unused/1", CorpusJsonContext.Default.ExportPayload));
        AssertEx.Equal("invalid-diagnostic", rejected.Kind);
    }

    private static CommandApp App(TestCommandConsole console, ICommandTextResolver? resolver = null) => new(Catalog())
    {
        Name = "storyline", Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default, TextResolver = resolver,
    };

    private sealed class HintResolver : ICommandTextResolver
    {
        public string? Resolve(string key, System.Globalization.CultureInfo culture, IReadOnlyList<string> arguments) => key switch
        {
            CommandTextKeys.DiagnosticMissingArgument => "Das Argument " + arguments[0] + " fehlt.",
            CommandTextKeys.HelpUsage => "Aufruf",
            CommandTextKeys.HelpOptionsPlaceholder => "Optionen",
            CommandTextKeys.HintUsage => "Hilfe: " + arguments[0],
            _ => null,
        };
    }
}
