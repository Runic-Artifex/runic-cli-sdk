using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal sealed class GeneratorTests
{
    private const string Commands = """
        using Runic.CommandLine;

        namespace Probe;

        internal static class Commands
        {
            [Command("greet")]
            public static string Greet([Argument("name")] string name, [Option("--loud")] bool loud) => loud ? name.ToUpperInvariant() : name;

            [Command("list")]
            public static string List([Argument("items", AllowMultipleValues = true)] string[] items) => string.Join(",", items);
        }
        """;

    [Test, DisplayName("generator/unchanged-commands-are-cached")]
    public ValueTask UnchangedCommandsAreCached()
    {
        CSharpCompilation compilation = CreateCompilation(Commands);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new CommandLineGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        GeneratorRunResult first = driver.GetRunResult().Results.Single();
        AssertEx.Equal(0, first.Diagnostics.Length, string.Join("\n", first.Diagnostics));
        AssertEx.Equal(1, first.GeneratedSources.Length);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("internal static class Unrelated { }", (CSharpParseOptions)compilation.SyntaxTrees[0].Options)));
        GeneratorRunResult second = driver.GetRunResult().Results.Single();
        foreach (string step in new[] { "CommandModels", "CommandCatalog" })
        {
            ImmutableArray<IncrementalGeneratorRunStep> runs = second.TrackedSteps[step];
            AssertEx.True(runs.Length > 0, step + " was not tracked.");
            AssertEx.True(
                runs.SelectMany(static run => run.Outputs).All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
                step + " recomputed after an unrelated edit.");
        }

        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("generator/catalog-is-internal-generated-code")]
    public ValueTask CatalogIsInternalGeneratedCode()
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(Commands), out Compilation compiled, out var diagnostics);
        AssertEx.Equal(0, diagnostics.Length, string.Join("\n", diagnostics));
        string generated = string.Concat(driver.GetRunResult().Results.Single().GeneratedSources.Select(static item => item.SourceText.ToString()));
        string version = typeof(CommandLineGenerator).Assembly.GetName().Version!.ToString();
        AssertEx.True(generated.Contains(
            "[global::System.CodeDom.Compiler.GeneratedCode(\"Runic.CommandLine.Generators\", \"" + version + "\")]\ninternal static class GeneratedCommandCatalog",
            StringComparison.Ordinal) || generated.Contains(
            "[global::System.CodeDom.Compiler.GeneratedCode(\"Runic.CommandLine.Generators\", \"" + version + "\")]\r\ninternal static class GeneratedCommandCatalog",
            StringComparison.Ordinal), generated);
        AssertEx.True(!generated.Contains("public static class GeneratedCommandCatalog", StringComparison.Ordinal), generated);
        AssertEx.True(generated.Contains("global::Runic.CommandLine.Generated.GeneratedCommandBinding.", StringComparison.Ordinal), generated);

        INamedTypeSymbol catalog = compiled.GetTypeByMetadataName("Runic.CommandLine.Generated.GeneratedCommandCatalog")!;
        AssertEx.Equal(Accessibility.Internal, catalog.DeclaredAccessibility);
        AttributeData attribute = catalog.GetAttributes().Single();
        AssertEx.Equal("System.CodeDom.Compiler.GeneratedCodeAttribute", attribute.AttributeClass!.ToDisplayString());
        AssertEx.Equal("Runic.CommandLine.Generators", (string?)attribute.ConstructorArguments[0].Value);
        AssertEx.Equal(version, (string?)attribute.ConstructorArguments[1].Value);

        using var bytes = new MemoryStream();
        var emitted = compiled.Emit(bytes);
        AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var loader = new AssemblyLoadContext("internal-catalog", isCollectible: true);
        try
        {
            Type type = loader.LoadFromStream(bytes).GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!;
            AssertEx.True(type.IsNotPublic, "The generated catalog is public.");
            AssertEx.True(type.GetCustomAttributes(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), false).Length == 1);
        }
        finally { loader.Unload(); }
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("generator/root-command-is-RCLI9001")]
    public ValueTask RootCommandIsReserved()
    {
        foreach (string name in new[] { "root", "tools root" })
        {
            CSharpCompilation compilation = CreateCompilation($$"""
                using Runic.CommandLine;

                internal static class Commands
                {
                    [Command("{{name}}")]
                    public static string Root() => "";

                    [Command("roots")]
                    public static string Roots() => "";
                }
                """);
            ImmutableArray<Diagnostic> diagnostics = CSharpGeneratorDriver.Create(new CommandLineGenerator())
                .RunGenerators(compilation).GetRunResult().Results.Single().Diagnostics;
            AssertEx.SequenceEqual(["RCLI9001"], diagnostics.Select(static diagnostic => diagnostic.Id).ToArray(), string.Join("\n", diagnostics));
            AssertEx.True(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains(name, StringComparison.Ordinal),
                diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        }

        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("generator/parameter-errors-use-distinct-diagnostics")]
    public ValueTask ParameterErrorsUseDistinctDiagnostics()
    {
        CSharpCompilation compilation = CreateCompilation("""
            using Runic.CommandLine;

            internal static class Commands
            {
                [Command("by-ref")]
                public static string ByRef([Argument("value")] ref string value) => value;

                [Command("flag")]
                public static string Flag([Option("--on")] bool on = true) => "";

                [Command("required")]
                public static string Required([Option("--name", Required = true)] string name = "x") => name;

                [Command("first")]
                [DefaultCommand]
                public static string First() => "";

                [Command("second")]
                [DefaultCommand]
                public static string Second() => "";

                // A defaulted injected token is accepted so it can follow defaulted options (CA1068).
                [Command("token")]
                public static string Token([Option("--overwrite")] bool overwrite = false, System.Threading.CancellationToken cancellationToken = default) => "";

                // Other unbound defaults remain RCLI9022: the default would never apply.
                [Command("service")]
                public static string Service([FromServices] string? text = null) => "";
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(compilation);
        ImmutableArray<Diagnostic> diagnostics = driver.GetRunResult().Results.Single().Diagnostics;
        AssertEx.SequenceEqual(
            ["RCLI9021", "RCLI9024", "RCLI9023", "RCLI9022", "RCLI9029", "RCLI9029"],
            diagnostics.Select(static diagnostic => diagnostic.Id).ToArray(),
            string.Join("\n", diagnostics));
        AssertEx.True(
            diagnostics.All(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0),
            "Generator diagnostics lost their source locations.");
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("generator/custom-result-contexts-compile-and-execute")]
    public async ValueTask CustomResultContexts()
    {
        foreach (bool hasDefault in new[] { false, true })
        {
            string source = $$"""
                using System;
                using System.Text.Json;
                using System.Text.Json.Serialization;
                using System.Text.Json.Serialization.Metadata;
                using Runic.CommandLine;

                [JsonSerializable(typeof(int))]
                internal sealed class CustomContext : JsonSerializerContext
                {
                    {{(hasDefault ? "public static CustomContext Default { get; } = new(new JsonSerializerOptions { NumberHandling = JsonNumberHandling.WriteAsString });" : "")}}
                    public CustomContext() : this(new JsonSerializerOptions()) { }
                    public CustomContext(JsonSerializerOptions options) : base(options) { }
                    protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                    public override JsonTypeInfo? GetTypeInfo(Type type) => type == typeof(int)
                        ? JsonMetadataServices.CreateValueInfo<int>(Options, JsonMetadataServices.Int32Converter)
                        : null;
                }

                internal static class Commands
                {
                    [Command("custom"), CommandResult("sample.custom-context/1", typeof(CustomContext))]
                    public static int Custom() => 42;
                }
                """;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator());
            driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out Compilation compiled, out var diagnostics);
            AssertEx.Equal(0, diagnostics.Length, string.Join("\n", diagnostics));
            using var bytes = new MemoryStream();
            var emitted = compiled.Emit(bytes);
            AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            bytes.Position = 0;
            var loader = new AssemblyLoadContext("custom-result-context", isCollectible: true);
            try
            {
                Assembly assembly = loader.LoadFromStream(bytes);
                var catalog = (CommandCatalog)assembly.GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!.GetMethod("Create")!.Invoke(null, [null])!;
                var console = new TestCommandConsole();
                var app = new CommandApp(catalog) { Console = console, HandleTerminationSignals = false };
                AssertEx.Equal(0, await app.RunAsync(["custom", "--format=json"]));
                using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
                JsonElement payload = frame.RootElement.GetProperty("payload");
                if (hasDefault) AssertEx.Equal("42", payload.GetString());
                else AssertEx.Equal(42, payload.GetInt32());
            }
            finally { loader.Unload(); }
        }
    }

    [Test, DisplayName("generator/assembly-output-selector-declares-the-catalog-selector")]
    public async ValueTask AssemblyOutputSelectorDeclaresTheSelector()
    {
        const string source = """
            using Runic.CommandLine;

            [assembly: OutputSelector("--output-format", "--output")]

            internal static class Commands
            {
                [Command("bind")]
                public static string Bind([Option("--format")] string format = "epub") => format;
            }
            """;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator());
        driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out Compilation compiled, out var diagnostics);
        AssertEx.Equal(0, diagnostics.Length, string.Join("\n", diagnostics));
        using var bytes = new MemoryStream();
        var emitted = compiled.Emit(bytes);
        AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var loader = new AssemblyLoadContext("assembly-output-selector", isCollectible: true);
        try
        {
            MethodInfo create = loader.LoadFromStream(bytes).GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!.GetMethod("Create")!;
            var catalog = (CommandCatalog)create.Invoke(null, [null])!;
            AssertEx.Equal("--output-format", catalog.OutputSelector);
            AssertEx.SequenceEqual(["--output"], catalog.OutputSelectorAliases);

            // The command keeps --format; the declared selector and its alias select the output.
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await new CommandApp(catalog) { Console = console, HandleTerminationSignals = false }.RunAsync(["bind", "--format", "pdf", "--output", "json"]));
            using (var frame = CommandTestEnvelope.Parse(console.StandardOutput))
                AssertEx.Equal("pdf", frame.RootElement.GetProperty("payload").GetString());

            // A selector declared in configure still replaces it.
            var configured = (CommandCatalog)create.Invoke(null, [new Action<CommandCatalogBuilder>(static builder => builder.OutputSelector("--emit"))])!;
            AssertEx.Equal("--emit", configured.OutputSelector);
        }
        finally { loader.Unload(); }
    }

    [Test, DisplayName("generator/command-unit-results-behave-like-void")]
    public async ValueTask CommandUnitResultsBehaveLikeVoid()
    {
        const string source = """
            using System.Threading.Tasks;
            using Runic.CommandLine;

            internal static class Commands
            {
                [Command("void")]
                public static void Void() { }

                [Command("unit")]
                public static CommandUnit Unit() => default;

                [Command("task-unit")]
                public static Task<CommandUnit> TaskUnit() => Task.FromResult(default(CommandUnit));

                [Command("outcome")]
                public static CommandOutcome<CommandUnit> Outcome() => CommandOutcome.Success(default(CommandUnit));

                [Command("task-outcome")]
                public static Task<CommandOutcome<CommandUnit>> TaskOutcome() => Task.FromResult(CommandOutcome.Success(default(CommandUnit)));

                [Command("value-task-outcome")]
                public static ValueTask<CommandOutcome<CommandUnit>> ValueTaskOutcome() => ValueTask.FromResult(CommandOutcome.Success(default(CommandUnit)));
            }
            """;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator());
        driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out Compilation compiled, out var diagnostics);
        AssertEx.Equal(0, diagnostics.Length, string.Join("\n", diagnostics));
        using var bytes = new MemoryStream();
        var emitted = compiled.Emit(bytes);
        AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var loader = new AssemblyLoadContext("command-unit-results", isCollectible: true);
        try
        {
            MethodInfo create = loader.LoadFromStream(bytes).GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!.GetMethod("Create")!;
            var catalog = (CommandCatalog)create.Invoke(null, [(Action<CommandCatalogBuilder>)(builder => builder.JsonOutputAlias("--json", JsonOutputShape.Payload))])!;

            async Task<(int ExitCode, string Human, string HumanError, string PayloadType, string Payload, string Alias, string AliasError)> RunAsync(string command)
            {
                var human = new TestCommandConsole();
                int exitCode = await new CommandApp(catalog) { Console = human, HandleTerminationSignals = false }.RunAsync([command]);
                var json = new TestCommandConsole();
                AssertEx.Equal(0, await new CommandApp(catalog) { Console = json, HandleTerminationSignals = false }.RunAsync([command, "--format=json"]));
                using JsonDocument envelope = CommandTestEnvelope.Parse(json.StandardOutput);
                var alias = new TestCommandConsole();
                AssertEx.Equal(0, await new CommandApp(catalog) { Console = alias, HandleTerminationSignals = false }.RunAsync([command, "--json"]));
                return (exitCode, human.StandardOutput, human.StandardError, envelope.RootElement.GetProperty("payloadType").GetString()!,
                    envelope.RootElement.GetProperty("payload").GetRawText(), alias.StandardOutput, alias.StandardError);
            }

            var expected = await RunAsync("void");
            AssertEx.Equal(0, expected.ExitCode);
            AssertEx.Equal("runic.unit/1", expected.PayloadType);
            AssertEx.Equal(string.Empty, expected.Human + expected.HumanError + expected.Alias + expected.AliasError);
            foreach (string command in new[] { "unit", "task-unit", "outcome", "task-outcome", "value-task-outcome" })
            {
                AssertEx.Equal(expected, await RunAsync(command), command);
            }
        }
        finally { loader.Unload(); }
    }

    [Test, DisplayName("generator/command-unit-results-accept-explicit-result-metadata")]
    public ValueTask CommandUnitResultsAcceptExplicitResultMetadata()
    {
        // [CommandResult] still overrides the built-in identity and codec, as it does for a string result.
        const string source = """
            using System.Text.Json.Serialization;
            using Runic.CommandLine;

            [JsonSerializable(typeof(CommandUnit))]
            internal sealed partial class UnitContext : JsonSerializerContext;

            internal static class Commands
            {
                [Command("unit"), CommandResult("sample.unit/1", typeof(UnitContext))]
                public static CommandOutcome<CommandUnit> Unit() => CommandOutcome.Success(default(CommandUnit));
            }
            """;
        GeneratorRunResult result = CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGenerators(CreateCompilation(source)).GetRunResult().Results.Single();
        AssertEx.Equal(0, result.Diagnostics.Length, string.Join("\n", result.Diagnostics));
        string generated = string.Concat(result.GeneratedSources.Select(static item => item.SourceText.ToString()));
        AssertEx.True(generated.Contains("\"sample.unit/1\"", StringComparison.Ordinal) && generated.Contains("global::UnitContext.Default.GetTypeInfo", StringComparison.Ordinal), generated);
        AssertEx.True(!generated.Contains("CommandResultCodecs.Unit", StringComparison.Ordinal), generated);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("generator/partial-result-contexts-use-default-only-when-json-generation-completes-them")]
    public ValueTask PartialResultContexts()
    {
        // System.Text.Json's generator is not run here, so only the expression
        // chosen for each context is observable. A non-partial containing type keeps
        // System.Text.Json from completing NestedContext.
        const string source = """
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using System.Text.Json.Serialization.Metadata;
            using Runic.CommandLine;

            [JsonSerializable(typeof(int))]
            internal sealed partial class GeneratedContext : JsonSerializerContext;

            internal static partial class PartialOuter
            {
                [JsonSerializable(typeof(int))]
                internal sealed partial class NestedGeneratedContext : JsonSerializerContext;
            }

            internal static class PlainOuter
            {
                [JsonSerializable(typeof(int))]
                internal sealed partial class NestedContext : JsonSerializerContext;
            }

            [JsonSerializable(typeof(int))]
            internal sealed partial class ImplementedContext : JsonSerializerContext
            {
                public ImplementedContext() : base(null) { }
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
            }

            internal sealed partial class ImplementedContext
            {
                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }

            internal static class Commands
            {
                [Command("generated"), CommandResult("sample.generated/1", typeof(GeneratedContext))]
                public static int Generated() => 1;

                [Command("nested-generated"), CommandResult("sample.nested-generated/1", typeof(PartialOuter.NestedGeneratedContext))]
                public static int NestedGenerated() => 2;

                [Command("nested"), CommandResult("sample.nested/1", typeof(PlainOuter.NestedContext))]
                public static int Nested() => 3;

                [Command("implemented"), CommandResult("sample.implemented/1", typeof(ImplementedContext))]
                public static int Implemented() => 4;
            }
            """;
        GeneratorRunResult result = CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGenerators(CreateCompilation(source)).GetRunResult().Results.Single();
        AssertEx.Equal(0, result.Diagnostics.Length, string.Join("\n", result.Diagnostics));
        string generated = string.Concat(result.GeneratedSources.Select(static item => item.SourceText.ToString()));
        foreach (string expected in new[]
        {
            "global::GeneratedContext.Default.GetTypeInfo",
            "global::PartialOuter.NestedGeneratedContext.Default.GetTypeInfo",
            "new global::PlainOuter.NestedContext().GetTypeInfo",
            "new global::ImplementedContext().GetTypeInfo",
        })
        {
            AssertEx.True(generated.Contains(expected, StringComparison.Ordinal), expected + " was not generated.\n" + generated);
        }
        AssertEx.True(!generated.Contains("NestedContext.Default", StringComparison.Ordinal) && !generated.Contains("ImplementedContext.Default", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("generator/result-contexts-without-camel-case-warn")]
    public ValueTask ResultContextsWithoutCamelCaseWarn()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Runic.CommandLine;

            internal sealed record Report(string Status, int Count);

            [JsonSerializable(typeof(Report))]
            internal sealed partial class PlainContext : JsonSerializerContext;

            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
            [JsonSerializable(typeof(Report))]
            [JsonSerializable(typeof(IReadOnlyList<Report>))]
            internal sealed partial class SnakeContext : JsonSerializerContext;

            [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
            [JsonSerializable(typeof(Report))]
            internal sealed partial class CamelContext : JsonSerializerContext;

            [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
            [JsonSerializable(typeof(Report))]
            internal sealed partial class WebContext : JsonSerializerContext;

            [JsonSerializable(typeof(int))]
            [JsonSerializable(typeof(IReadOnlyList<string>))]
            [JsonSerializable(typeof(System.Guid))]
            internal sealed partial class ScalarContext : JsonSerializerContext;

            internal static class Commands
            {
                [Command("plain"), CommandResult("sample.plain/1", typeof(PlainContext))]
                public static Report Plain() => new("Ready", 1);

                [Command("snake"), CommandResult("sample.snake/1", typeof(SnakeContext))]
                public static IReadOnlyList<Report> Snake() => [];

                [Command("camel"), CommandResult("sample.camel/1", typeof(CamelContext))]
                public static Report Camel() => new("Ready", 1);

                [Command("web"), CommandResult("sample.web/1", typeof(WebContext))]
                public static Report Web() => new("Ready", 1);

                [Command("number"), CommandResult("sample.number/1", typeof(ScalarContext))]
                public static int Number() => 1;

                [Command("names"), CommandResult("sample.names/1", typeof(ScalarContext))]
                public static IReadOnlyList<string> Names() => [];

                [Command("id"), CommandResult("sample.id/1", typeof(ScalarContext))]
                public static System.Guid Id() => default;
            }
            """;
        GeneratorRunResult result = CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGenerators(CreateCompilation(source)).GetRunResult().Results.Single();
        Diagnostic[] warnings = result.Diagnostics.Where(static diagnostic => diagnostic.Id == "RCLI9044").ToArray();
        AssertEx.Equal(2, warnings.Length, string.Join("\n", result.Diagnostics));
        AssertEx.Equal(2, result.Diagnostics.Length, string.Join("\n", result.Diagnostics));
        AssertEx.True(warnings.All(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning));
        AssertEx.Equal(
            "Command 'plain' returns 'Report' through JSON context 'PlainContext', which does not use camelCase: payload properties would be serialized PascalCase, "
            + "while Runic payloads are camelCase; add [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)] to the context",
            warnings[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        AssertEx.True(warnings[1].GetMessage(System.Globalization.CultureInfo.InvariantCulture).StartsWith("Command 'snake' returns", StringComparison.Ordinal));
        // The warning points at the [CommandResult] attribute, where a #pragma can suppress it, and the commands are still generated.
        string[] lines = source.Split('\n');
        foreach (Diagnostic warning in warnings)
        {
            LinePosition start = warning.Location.GetLineSpan().StartLinePosition;
            AssertEx.True(lines[start.Line][start.Character..].StartsWith("CommandResult(", StringComparison.Ordinal), warning.ToString());
        }
        string generated = string.Concat(result.GeneratedSources.Select(static item => item.SourceText.ToString()));
        AssertEx.True(generated.Contains("\"sample.plain/1\"", StringComparison.Ordinal) && generated.Contains("\"sample.snake/1\"", StringComparison.Ordinal), generated);
        return ValueTask.CompletedTask;
    }

    // A compiler refuses a generator built against a newer Microsoft.CodeAnalysis than its own (CS9057). The lowest
    // supported .NET 10 SDK feature band, 10.0.1xx, ships Roslyn 5.0.0; Directory.Packages.props pins that version.
    [Test, DisplayName("generator/references-the-lowest-supported-compiler")]
    public ValueTask ReferencesLowestSupportedCompiler()
    {
        AssemblyName[] references = typeof(CommandLineGenerator).Assembly.GetReferencedAssemblies()
            .Where(static reference => reference.Name is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp")
            .ToArray();
        AssertEx.Equal(2, references.Length, string.Join(", ", references.Select(static reference => reference.FullName)));
        foreach (AssemblyName reference in references)
            AssertEx.Equal(new Version(5, 0, 0, 0), reference.Version, reference.FullName);
        return ValueTask.CompletedTask;
    }

    internal static CSharpCompilation CreateCompilation(string source)
    {
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<MetadataReference> references = platform
            .Where(static path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll")
            .Append(typeof(CommandAttribute).Assembly.Location)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "GeneratorProbe",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
