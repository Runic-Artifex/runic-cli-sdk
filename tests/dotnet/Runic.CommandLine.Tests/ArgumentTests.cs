using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// Trailing arguments (runic-cli-sdk#42) and required list arguments (runic-cli-sdk#48).
internal sealed class ArgumentTests
{
    private const string Commands = """
        using System.Collections.Generic;
        using Runic.CommandLine;

        internal static class Commands
        {
            [Command("build")]
            public static string Build(
                [Argument("words", AllowMultipleValues = true, Choices = ["home", "system"])] string[] words,
                [TrailingArguments(Description = "Arguments for nix", ValueName = "NIX-ARG")] IReadOnlyList<string> native,
                [Option("--dry-run")] bool dryRun = false) =>
                string.Join(",", words) + "|" + string.Join(",", native) + "|" + dryRun;

            [Command("add")]
            public static string Add([Argument("packages", AllowMultipleValues = true, Required = true)] string[] packages) => string.Join(",", packages);

            [Command("name")]
            public static string Name([Argument(Required = true)] string? name) => name ?? "<null>";
        }
        """;

    [Test, DisplayName("arguments/trailing-arguments-receive-the-tokens-after-the-terminator")]
    public async ValueTask TrailingArguments()
    {
        await WithCatalog(async catalog =>
        {
            AssertEx.Equal("home,system|--option,x,--,y|True", await Run(catalog, 0, "build", "home", "system", "--dry-run", "--", "--option", "x", "--", "y"));
            AssertEx.Equal("home||False", await Run(catalog, 0, "build", "home"));
            AssertEx.Equal("home||False", await Run(catalog, 0, "build", "home", "--"));
            AssertEx.Equal("|--help,home|False", await Run(catalog, 0, "build", "--", "--help", "home"));
            // Positional words before -- still fail their choices.
            await Run(catalog, CommandExitCodes.Usage, "build", "other");

            string help = CommandHelpFormatter.Format(catalog, "fixture", new CommandPath(["build"]));
            AssertEx.True(help.Contains("Usage: fixture build [words...] [-- NIX-ARG...] [options]", StringComparison.Ordinal), help);
            AssertEx.True(help.Contains("Arguments for nix", StringComparison.Ordinal), help);
            CommandArgumentDescriptor trailing = catalog.Commands.Single(command => command.Name == "build").Arguments[^1];
            AssertEx.True(trailing.IsTrailing && trailing.Id == "native" && trailing.Arity == CommandArity.ZeroOrMore);
            AssertEx.SequenceEqual(["home"], CommandCompletion.Query(catalog, ["build", "ho"]).Candidates);
            AssertEx.SequenceEqual(Array.Empty<string>(), CommandCompletion.Query(catalog, ["build", "--", "ho"]).Candidates);
        });
    }

    [Test, DisplayName("arguments/required-list-and-nullable-arguments")]
    public async ValueTask RequiredArguments()
    {
        await WithCatalog(async catalog =>
        {
            AssertEx.Equal("a,b", await Run(catalog, 0, "add", "a", "b"));
            string missing = await Run(catalog, CommandExitCodes.Usage, "add");
            AssertEx.True(missing.Contains("RCLI1007", StringComparison.Ordinal) || missing.Contains("packages", StringComparison.Ordinal), missing);
            AssertEx.Equal("ada", await Run(catalog, 0, "name", "ada"));
            await Run(catalog, CommandExitCodes.Usage, "name");

            CommandDescriptor add = catalog.Commands.Single(command => command.Name == "add");
            AssertEx.Equal(CommandArity.OneOrMore, add.Arguments.Single().Arity);
            AssertEx.Equal(CommandArity.ExactlyOne, catalog.Commands.Single(command => command.Name == "name").Arguments.Single().Arity);
            string help = CommandHelpFormatter.Format(catalog, "fixture", new CommandPath(["add"]));
            AssertEx.True(help.Contains("Usage: fixture add <packages...>", StringComparison.Ordinal) && help.Contains("[required]", StringComparison.Ordinal), help);
        });
    }

    [Test, DisplayName("arguments/generator-reports-invalid-required-and-trailing-arguments")]
    public ValueTask GeneratorDiagnostics()
    {
        CSharpCompilationDiagnostics("""
            using Runic.CommandLine;

            internal static class Commands
            {
                [Command("defaulted")]
                public static string Defaulted([Argument(Required = true)] string name = "x") => name;

                [Command("ordered")]
                public static string Ordered([Argument] string? first, [Argument(AllowMultipleValues = true, Required = true)] string[] rest) => "";

                [Command("scalar")]
                public static string Scalar([TrailingArguments] string native) => native;

                [Command("twice")]
                public static string Twice([TrailingArguments] string[] first, [TrailingArguments("second")] string[] second) => "";

                [Command("both")]
                public static string Both([Argument, TrailingArguments] string[] values) => "";
            }
            """, ["RCLI9023", "RCLI9035", "RCLI9003", "RCLI9005", "RCLI9002"]);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("arguments/catalog-trailing-arguments-follow-positional-arguments")]
    public ValueTask CatalogTrailingArguments()
    {
        var builder = new CommandCatalogBuilder();
        // Registered first, the trailing arguments still come last; a variadic positional argument may precede them.
        CommandBuilder<TestOptions, TestHandler, TestResult> run = CatalogTests.ValidCommand(builder, "run")
            .TrailingArguments("forwarded", "forwarded", isSensitive: true)
            .Argument("files", "files", CommandArity.ZeroOrMore);
        AssertEx.Throws<InvalidOperationException>(() => run.TrailingArguments("again", "again"));
        CommandCatalog catalog = builder.Build();
        AssertEx.SequenceEqual(["files", "forwarded"], catalog.Commands[0].Arguments.Select(static argument => argument.Id));
        AssertEx.SequenceEqual([false, true], catalog.Commands[0].Arguments.Select(static argument => argument.IsTrailing));

        ParseOutcome parsed = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["run", "a", "b", "--", "-3", "c"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, parsed.Kind);
        AssertEx.SequenceEqual(["a", "b"], parsed.Invocation!.Arguments.Single(binding => binding.Id == "files").Values);
        AssertEx.SequenceEqual(["-3", "c"], parsed.Invocation.Arguments.Single(binding => binding.Id == "forwarded").Values);
        parsed = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["run", "a"], ParseSettings.Default);
        AssertEx.True(parsed.Invocation!.Arguments.All(binding => binding.Id != "forwarded"));
        return ValueTask.CompletedTask;
    }

    internal static void CSharpCompilationDiagnostics(string source, string[] expected)
    {
        GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(GeneratorTests.CreateCompilation(source));
        ImmutableArray<Diagnostic> diagnostics = driver.GetRunResult().Results.Single().Diagnostics;
        AssertEx.SequenceEqual(expected.Order(StringComparer.Ordinal), diagnostics.Select(static diagnostic => diagnostic.Id).Order(StringComparer.Ordinal), string.Join("\n", diagnostics));
    }

    internal static async ValueTask<string> Run(CommandCatalog catalog, int exitCode, params string[] args)
    {
        var console = new TestCommandConsole();
        var app = new CommandApp(catalog) { Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleCancelKeyPress = false };
        AssertEx.Equal(exitCode, await app.RunAsync(args), string.Join(' ', args) + ": " + console.StandardOutput + console.StandardError);
        return exitCode == 0 ? console.StandardOutput.TrimEnd('\n') : console.StandardError;
    }

    private static ValueTask WithCatalog(Func<CommandCatalog, ValueTask> test) => WithCatalog(Commands, test);

    // Compiles generated commands into a collectible assembly and passes their catalog to the test.
    internal static async ValueTask WithCatalog(string source, Func<CommandCatalog, ValueTask> test)
    {
        GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new CommandLineGenerator());
        driver.RunGeneratorsAndUpdateCompilation(GeneratorTests.CreateCompilation(source), out Compilation compiled, out ImmutableArray<Diagnostic> diagnostics);
        AssertEx.Equal(0, diagnostics.Length, string.Join("\n", diagnostics));
        using var bytes = new MemoryStream();
        var emitted = compiled.Emit(bytes);
        AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var loader = new AssemblyLoadContext("argument-tests", isCollectible: true);
        try
        {
            Assembly assembly = loader.LoadFromStream(bytes);
            await test((CommandCatalog)assembly.GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!.GetMethod("Create")!.Invoke(null, [null])!);
        }
        finally { loader.Unload(); }
    }
}
