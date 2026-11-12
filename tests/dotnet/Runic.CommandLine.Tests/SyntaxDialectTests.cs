using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// The Windows slash dialect, case-insensitive matching, display spellings and help order: runic-cli-sdk#87.
internal sealed class SyntaxDialectTests
{
    // Declared out of name order, so declaration order differs from the generated registration order.
    private const string Commands = """
        using Runic.CommandLine;

        internal static class Commands
        {
            [Command("policy link", Description = "Link a policy.")]
            public static string Link([Argument] string name, [Argument] string path, [Option("--force")] bool force = false) =>
                "link:" + name + ":" + path + ":" + force;

            [Command("policy reset-all", Spelling = "resetAll", Description = "Reset every policy.")]
            public static string ResetAll() => "reset-all";

            [Command("alias", Description = "Rename an alias.")]
            public static string Alias([Option("-R")] string? rename = null) => "alias:" + rename;

            [Command("selftest", Description = "Run the self test.")]
            public static string Selftest([Option("--count", Minimum = 1)] int count = 1,
                [Option("--token", Choices = ["alpha", "beta"], Sensitive = true)] string? token = null) => "selftest:" + count;
        }
        """;

    private static void Slash(CommandCatalogBuilder builder) => builder
        .Syntax(CommandSyntaxDialect.WindowsSlash)
        .CaseInsensitive()
        .JsonOutputAlias("/json", JsonOutputShape.Payload)
        .HelpOrder(CommandHelpOrder.Declaration);

    private static CommandApp App(CommandCatalog catalog, ICommandConsole console) =>
        new(catalog) { Name = "tool", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false };

    [Test, DisplayName("dialect/run-runasync-and-hosting-run-slash-command-lines")]
    public async ValueTask RunnersAgree()
    {
        await WithCatalog(Slash, async catalog =>
        {
            string[] args = ["/POLICY", "/Link", "a", "b", "/json"];
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await App(catalog, console).RunAsync(args), console.StandardError);
            AssertEx.Equal("\"link:a:b:False\"\n", console.StandardOutput);

            console = new TestCommandConsole();
            AssertEx.Equal(0, App(catalog, console).Run(args), console.StandardError);
            AssertEx.Equal("\"link:a:b:False\"\n", console.StandardOutput);

            console = new TestCommandConsole();
            var hosting = new CommandLineHostingAdapter(App(catalog, console));
            HostedCommandLineDecision decision = hosting.Classify(new HostedCommandLineLaunchInput(args));
            AssertEx.Equal(HostedCommandLineDecisionKind.Invocation, decision.Kind);
            AssertEx.True(decision.IsCommandLineRequest);
            AssertEx.Equal("policy link", decision.Path!.ToString());
            AssertEx.Equal("policy link", decision.MatchedPath!.ToString());
            AssertEx.Equal(0, await hosting.RunAsync(decision));
            AssertEx.Equal("\"link:a:b:False\"\n", console.StandardOutput);

            // Help is a command-line request too, and so is a usage error of a known command.
            AssertEx.True(hosting.Classify(new HostedCommandLineLaunchInput(["/policy", "/?"])).IsCommandLineRequest);
            HostedCommandLineDecision invalid = hosting.Classify(new HostedCommandLineLaunchInput(["/policy", "/link", "a"]));
            AssertEx.Equal(HostedCommandLineDecisionKind.Invalid, invalid.Kind);
            AssertEx.True(invalid.IsCommandLineRequest);
        });
    }

    [Test, DisplayName("dialect/words-and-options-match-in-any-case-and-spelling")]
    public async ValueTask Matching()
    {
        await WithCatalog(Slash, async catalog =>
        {
            AssertEx.Equal("selftest:1", await ArgumentTests.Run(catalog, 0, "Selftest"));
            AssertEx.Equal("selftest:1", await ArgumentTests.Run(catalog, 0, "/SELFTEST"));
            AssertEx.Equal("selftest:3", await ArgumentTests.Run(catalog, 0, "/selftest", "/COUNT=3"));
            AssertEx.Equal("selftest:3", await ArgumentTests.Run(catalog, 0, "/selftest", "--count", "3"));
            AssertEx.Equal("alias:OLDNAME", await ArgumentTests.Run(catalog, 0, "/alias", "/R", "OLDNAME"));
            AssertEx.Equal("alias:OLDNAME", await ArgumentTests.Run(catalog, 0, "/alias", "/r", "OLDNAME"));
            AssertEx.Equal("alias:OLDNAME", await ArgumentTests.Run(catalog, 0, "/alias", "-R", "OLDNAME"));
            AssertEx.Equal("reset-all", await ArgumentTests.Run(catalog, 0, "/policy", "/resetAll"));
            AssertEx.Equal("reset-all", await ArgumentTests.Run(catalog, 0, "/policy", "/RESETALL"));
            AssertEx.Equal("reset-all", await ArgumentTests.Run(catalog, 0, "policy", "reset-all"));
            AssertEx.Equal("link:a:b:True", await ArgumentTests.Run(catalog, 0, "/policy", "/link", "/force", "a", "b"));
            AssertEx.Equal("1.2.3", (await RunApp(catalog, 0, "/version")).Out.TrimEnd('\n'));
            // The output selector has a slash spelling too, and its envelope keeps the canonical path.
            string json = (await RunApp(catalog, 0, "/policy", "/resetall", "/FORMAT", "json")).Out;
            AssertEx.True(json.Contains("\"command\":\"policy reset-all\"", StringComparison.Ordinal), json);
        });
    }

    [Test, DisplayName("dialect/data-that-starts-with-a-slash-reaches-the-handler")]
    public async ValueTask SlashData()
    {
        await WithCatalog(Slash, async catalog =>
        {
            AssertEx.Equal("link:/weird-name:/tmp/file:False", await ArgumentTests.Run(catalog, 0, "/policy", "/link", "/weird-name", "/tmp/file"));
            AssertEx.Equal("link:CN=x,DC=y:/:False", await ArgumentTests.Run(catalog, 0, "/policy", "/link", "CN=x,DC=y", "/"));
            // After -- a token that spells an option, or help, is data.
            AssertEx.Equal("link:/force:/?:False", await ArgumentTests.Run(catalog, 0, "/policy", "/link", "--", "/force", "/?"));

            ParsedInvocation invocation = CommandTestInvocation.Parse(catalog, "/Policy", "/LINK", "/weird-name", "/x", "/FORCE");
            AssertEx.Equal("policy link", invocation.Path.ToString());
            AssertEx.SequenceEqual(["/weird-name"], invocation.Arguments[0].Values);
            AssertEx.SequenceEqual(["/x"], invocation.Arguments[1].Values);
            AssertEx.Equal("force", invocation.Options.Single().Id);
        });
    }

    [Test, DisplayName("dialect/help-uses-the-dialect-spelling-and-declaration-order")]
    public async ValueTask Help()
    {
        await WithCatalog(Slash, async catalog =>
        {
            string root = (await RunApp(catalog, 0, "/?")).Out;
            AssertEx.True(root.IndexOf("/policy", StringComparison.Ordinal) < root.IndexOf("/alias", StringComparison.Ordinal) &&
                root.IndexOf("/alias", StringComparison.Ordinal) < root.IndexOf("/selftest", StringComparison.Ordinal), root);
            AssertEx.True(root.Contains("/?, /help", StringComparison.Ordinal) && root.Contains("/version", StringComparison.Ordinal) &&
                root.Contains("/format <human|json>", StringComparison.Ordinal) && root.Contains("/json", StringComparison.Ordinal), root);
            AssertEx.True(!root.Contains("--help", StringComparison.Ordinal), root);

            string group = (await RunApp(catalog, 0, "/policy", "/?")).Out;
            AssertEx.True(group.StartsWith("Usage: tool /policy [options]\n", StringComparison.Ordinal), group);
            AssertEx.True(group.IndexOf("/link", StringComparison.Ordinal) < group.IndexOf("/resetAll", StringComparison.Ordinal), group);
            AssertEx.True(!group.Contains("reset-all", StringComparison.Ordinal), group);

            string command = (await RunApp(catalog, 0, "/policy", "/link", "/help")).Out;
            AssertEx.True(command.StartsWith("Usage: tool /policy /link <name> <path> [options]\n", StringComparison.Ordinal), command);
            AssertEx.True(command.Contains("  /force", StringComparison.Ordinal), command);
            AssertEx.Equal(command, (await RunApp(catalog, 0, "HELP", "POLICY", "LINK")).Out);

            AssertEx.True((await RunApp(catalog, 0, "/alias", "/?")).Out.Contains("  /R <rename>", StringComparison.Ordinal));
        });
    }

    [Test, DisplayName("dialect/diagnostics-hints-and-suggestions-use-the-dialect-spelling")]
    public async ValueTask Diagnostics()
    {
        await WithCatalog(Slash, async catalog =>
        {
            string missing = (await RunApp(catalog, CommandExitCodes.Usage, "/policy", "/link", "a")).Error;
            AssertEx.True(missing.Contains("Usage: tool /policy /link <name> <path> [options]", StringComparison.Ordinal) &&
                missing.Contains("Run \"tool /policy /link /?\" for more information.", StringComparison.Ordinal), missing);

            string value = (await RunApp(catalog, CommandExitCodes.Usage, "/alias", "/R")).Error;
            AssertEx.True(value.Contains("The /R option requires a value.", StringComparison.Ordinal), value);

            string range = (await RunApp(catalog, CommandExitCodes.Usage, "/selftest", "/count", "0")).Error;
            AssertEx.True(range.Contains("/count requires a number of at least 1.", StringComparison.Ordinal), range);

            string unknown = (await RunApp(catalog, CommandExitCodes.Usage, "/polcy")).Error;
            AssertEx.True(unknown.Contains("The command '/polcy' is not recognized. Did you mean '/policy'?", StringComparison.Ordinal), unknown);
            AssertEx.True(unknown.Contains("Run \"tool /?\" for more information.", StringComparison.Ordinal), unknown);

            ParseOutcome sub = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["/policy", "/resetal"], ParseSettings.Default);
            CommandDiagnostic diagnostic = sub.Diagnostics.Single();
            AssertEx.Equal("RCLI1002", diagnostic.Code);
            AssertEx.Equal(1, diagnostic.TokenIndex);
            AssertEx.SequenceEqual(["/resetal", "/resetAll"], diagnostic.Arguments);
            AssertEx.Equal(CommandTextKeys.DiagnosticUnknownCommandSuggestion, diagnostic.MessageKey);

            ParseOutcome conflict = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["/selftest", "/json", "/format", "human"], ParseSettings.Default);
            AssertEx.SequenceEqual(["/format", "/json"], conflict.Diagnostics.Single().Arguments);

            // Choices that are not listed are named by the dialect's help request.
            string hidden = (await RunApp(catalog, CommandExitCodes.Usage, "/selftest", "/token", "nope")).Error;
            AssertEx.True(hidden.StartsWith("/token must be one of the choices shown in /?.\n", StringComparison.Ordinal), hidden);
            CommandDiagnostic choice = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["/selftest", "/token=nope"], ParseSettings.Default).Diagnostics.Single();
            AssertEx.SequenceEqual(["/token", "/?"], choice.Arguments);
            AssertEx.Equal(CommandTextKeys.DiagnosticInvalidChoiceHidden, choice.MessageKey);

            // -h is help, which the dialect spells /?.
            CommandDiagnostic option = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["/selftest", "-j"], ParseSettings.Default).Diagnostics.Single();
            AssertEx.SequenceEqual(["-j", "/?"], option.Arguments);
            AssertEx.Equal("The option '-j' is not recognized. Did you mean '/?'?", option.Message);
        });
    }

    [Test, DisplayName("dialect/testing-helpers-accept-dialect-catalogs")]
    public async ValueTask Testing()
    {
        await WithCatalog(Slash, async catalog =>
        {
            AssertEx.Equal("policy reset-all", CommandTestInvocation.Parse(catalog, "/POLICY", "/resetall").Path.ToString());
            var tester = new CommandAppTester(console => App(catalog, console));
            CommandAppTestResult result = await tester.RunAsync(["/policy", "/link", "a", "b"]);
            AssertEx.Equal(0, result.ExitCode, result.StandardError);
            AssertEx.Equal("link:a:b:False\n", result.StandardOutput);
            AssertEx.Equal(0, tester.Run(["/selftest", "/json"]).ExitCode);
        });
    }

    [Test, DisplayName("dialect/portable-default-is-unchanged")]
    public async ValueTask PortableDefault()
    {
        await WithCatalog(null, async catalog =>
        {
            AssertEx.Equal(CommandSyntaxDialect.Portable, catalog.Syntax);
            AssertEx.True(!catalog.IsCaseInsensitive);
            AssertEx.Equal(CommandHelpOrder.Registration, catalog.HelpOrder);
            await ArgumentTests.Run(catalog, CommandExitCodes.Usage, "/policy", "/link", "a", "b");
            await ArgumentTests.Run(catalog, CommandExitCodes.Usage, "POLICY", "LINK", "a", "b");
            AssertEx.Equal("link:/weird-name:b:False", await ArgumentTests.Run(catalog, 0, "policy", "link", "/weird-name", "b"));
            string help = (await RunApp(catalog, 0, "--help")).Out;
            AssertEx.True(help.IndexOf("  alias", StringComparison.Ordinal) < help.IndexOf("  policy", StringComparison.Ordinal), help);
            AssertEx.True(help.Contains("-h, --help", StringComparison.Ordinal), help);
            // A display spelling is shown and matched under the portable grammar as well, case-sensitively.
            AssertEx.True((await RunApp(catalog, 0, "policy", "--help")).Out.Contains("  resetAll", StringComparison.Ordinal));
            AssertEx.Equal("reset-all", await ArgumentTests.Run(catalog, 0, "policy", "resetAll"));
            await ArgumentTests.Run(catalog, CommandExitCodes.Usage, "policy", "resetall");
            CommandDiagnostic choice = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["selftest", "--token", "nope"], ParseSettings.Default).Diagnostics.Single();
            AssertEx.SequenceEqual(["--token", "--help"], choice.Arguments);
            AssertEx.Equal("--token must be one of the choices shown in --help.", choice.Message);
        });

        // Case-insensitive matching without the slash dialect.
        await WithCatalog(static builder => builder.CaseInsensitive(), async catalog =>
        {
            AssertEx.Equal("link:A:B:True", await ArgumentTests.Run(catalog, 0, "POLICY", "Link", "A", "B", "--FORCE"));
            AssertEx.True((await RunApp(catalog, 0, "POLICY", "--HELP")).Out.StartsWith("Usage: tool policy [options]", StringComparison.Ordinal));
            await ArgumentTests.Run(catalog, CommandExitCodes.Usage, "/policy", "/link", "a", "b");
        });
    }

    [Test, DisplayName("dialect/catalog-validation-checks-spellings-under-the-dialect")]
    public ValueTask Validation()
    {
        // The fixture corpus declares --verbose with the alias /verbose, which the slash dialect reads alike.
        AssertEx.Equal("RCLI0008", AssertEx.Throws<CommandCatalogValidationException>(
            () => FixtureCatalog.Create(static builder => builder.Syntax(CommandSyntaxDialect.WindowsSlash))).Issues[0].Code);

        static string Issue(Action<CommandCatalogBuilder> configure)
        {
            var builder = new CommandCatalogBuilder().JsonOutputAlias("--json");
            builder.Command<TestOptions, TestHandler, TestResult>("status", command => Fixture(command));
            configure(builder);
            return AssertEx.Throws<CommandCatalogValidationException>(() => builder.Build()).Issues[0].Code;
        }

        // Under the slash dialect an option /json reads like the JSON output alias --json.
        AssertEx.Equal("RCLI0024", Issue(static builder => builder.Syntax(CommandSyntaxDialect.WindowsSlash)
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Option("json", "/json", CommandArity.Zero))));
        AssertEx.Equal("RCLI0008", Issue(static builder => builder.Syntax(CommandSyntaxDialect.WindowsSlash)
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Option("a", "--n", CommandArity.Zero).Option("b", "-n", CommandArity.Zero))));
        AssertEx.Equal("RCLI0008", Issue(static builder => builder.CaseInsensitive()
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Option("a", "-n", CommandArity.Zero).Option("b", "-N", CommandArity.Zero))));
        AssertEx.Equal("RCLI0007", Issue(static builder => builder.Syntax(CommandSyntaxDialect.WindowsSlash)
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Option("a", "/help", CommandArity.Zero))));
        AssertEx.Equal("RCLI0007", Issue(static builder => builder.CaseInsensitive()
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Option("a", "-H", CommandArity.Zero))));
        AssertEx.Equal("RCLI0002", Issue(static builder =>
            builder.Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Spelling("re set"))));
        AssertEx.Equal("RCLI0002", Issue(static builder =>
            builder.Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Spelling("HELP"))));
        AssertEx.Equal("RCLI0004", Issue(static builder => builder.CaseInsensitive()
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Spelling("STATUS"))));
        // Without case-insensitive matching the spelling is distinct.
        AssertEx.Equal("STATUS", new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("status", command => Fixture(command))
            .Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command).Spelling("STATUS")).Build().Commands[1].Spelling);

        // A slash JSON alias needs the slash dialect.
        AssertEx.Equal("RCLI0024", AssertEx.Throws<CommandCatalogValidationException>(() => new CommandCatalogBuilder()
            .JsonOutputAlias("/json").Command<TestOptions, TestHandler, TestResult>("extra", command => Fixture(command)).Build()).Issues.Single().Code);
        AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().JsonOutputAlias("/js/on"));
        AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().JsonOutputAlias("/help"));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => new CommandCatalogBuilder().Syntax((CommandSyntaxDialect)7));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => new CommandCatalogBuilder().HelpOrder((CommandHelpOrder)7));

        // Combined catalogs must match the same way.
        CommandCatalog slash = new CommandCatalogBuilder().Syntax(CommandSyntaxDialect.WindowsSlash)
            .Command<TestOptions, TestHandler, TestResult>("one", command => Fixture(command)).Build();
        CommandCatalog portable = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("two", command => Fixture(command)).Build();
        AssertEx.Equal("RCLI0027", AssertEx.Throws<CommandCatalogValidationException>(() => CommandCatalog.Combine(slash, portable)).Issues.Single().Code);
        CommandCatalog other = new CommandCatalogBuilder().Syntax(CommandSyntaxDialect.WindowsSlash)
            .Command<TestOptions, TestHandler, TestResult>("two", command => Fixture(command).Spelling("Two")).Build();
        CommandCatalog combined = CommandCatalog.Combine(slash, other);
        AssertEx.Equal(CommandSyntaxDialect.WindowsSlash, combined.Syntax);
        AssertEx.True(combined.TryGetCommand("Two", out _));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("dialect/declaration-order-applies-to-help-only")]
    public ValueTask DeclarationOrder()
    {
        CommandCatalog catalog = new CommandCatalogBuilder()
            .HelpOrder(CommandHelpOrder.Declaration)
            .CommandPath<TestOptions, TestHandler, TestResult>("b first", command => Fixture(command))
            .CommandPath<TestOptions, TestHandler, TestResult>("a second", command => Fixture(command))
            .CommandPath<TestOptions, TestHandler, TestResult>("b third", command => Fixture(command))
            .DeclarationOrder("b third", "a second")
            .Build();
        AssertEx.SequenceEqual(["b", "a"], catalog.Commands.Select(static command => command.Name));
        string help = CommandHelpFormatter.Format(catalog, "tool", CommandPath.Root);
        AssertEx.True(help.IndexOf("  b", StringComparison.Ordinal) < help.IndexOf("  a", StringComparison.Ordinal), help);
        string group = CommandHelpFormatter.Format(catalog, "tool", new CommandPath(["b"]));
        AssertEx.True(group.IndexOf("  third", StringComparison.Ordinal) < group.IndexOf("  first", StringComparison.Ordinal), group);
        AssertEx.SequenceEqual(["first", "third"], catalog.Commands[0].Subcommands.Select(static command => command.Name));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("dialect/generator-checks-spellings-and-records-declaration-order")]
    public ValueTask Generator()
    {
        GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGenerators(GeneratorTests.CreateCompilation(Commands));
        string generated = driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString();
        AssertEx.True(generated.Contains(".Spelling(\"resetAll\")", StringComparison.Ordinal), generated);
        AssertEx.True(generated.Contains(".DeclarationOrder(\"policy link\", \"policy reset-all\", \"alias\", \"selftest\")", StringComparison.Ordinal), generated);

        // Name order needs no declaration order.
        driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGenerators(GeneratorTests.CreateCompilation(Commands.Replace("\"policy ", "\"a1 ", StringComparison.Ordinal).Replace("\"alias\"", "\"b1\"", StringComparison.Ordinal)
                .Replace("\"selftest\"", "\"c1\"", StringComparison.Ordinal)));
        AssertEx.True(!driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString().Contains(".DeclarationOrder(", StringComparison.Ordinal));

        ImmutableArray<Diagnostic> diagnostics = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGenerators(GeneratorTests.CreateCompilation(Commands.Replace("Spelling = \"resetAll\"", "Spelling = \"reset all\"", StringComparison.Ordinal)))
            .GetRunResult().Results.Single().Diagnostics;
        AssertEx.Equal("RCLI9005", diagnostics.Single().Id);
        AssertEx.True(diagnostics.Single().GetMessage(CultureInfo.InvariantCulture).Contains("command spelling", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    private static CommandBuilder<TestOptions, TestHandler, TestResult> Fixture(CommandBuilder<TestOptions, TestHandler, TestResult> command) =>
        command.BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec());

    private static async ValueTask<(string Out, string Error)> RunApp(CommandCatalog catalog, int exitCode, params string[] args)
    {
        var console = new TestCommandConsole();
        int actual = await new CommandApp(catalog)
        {
            Name = "tool", Version = "1.2.3", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false,
        }.RunAsync(args);
        AssertEx.Equal(exitCode, actual, string.Join(' ', args) + ": " + console.StandardOutput + console.StandardError);
        return (console.StandardOutput, console.StandardError);
    }

    // Compiles the commands above into a collectible assembly and builds their catalog with the given configuration.
    private static async ValueTask WithCatalog(Action<CommandCatalogBuilder>? configure, Func<CommandCatalog, ValueTask> test)
    {
        GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new CommandLineGenerator());
        driver.RunGeneratorsAndUpdateCompilation(GeneratorTests.CreateCompilation(Commands), out Compilation compiled, out ImmutableArray<Diagnostic> diagnostics);
        AssertEx.Equal(0, diagnostics.Length, string.Join("\n", diagnostics));
        using var bytes = new MemoryStream();
        var emitted = compiled.Emit(bytes);
        AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var loader = new AssemblyLoadContext("syntax-dialect-tests", isCollectible: true);
        try
        {
            Assembly assembly = loader.LoadFromStream(bytes);
            await test((CommandCatalog)assembly.GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!.GetMethod("Create")!.Invoke(null, [configure])!);
        }
        finally { loader.Unload(); }
    }
}
