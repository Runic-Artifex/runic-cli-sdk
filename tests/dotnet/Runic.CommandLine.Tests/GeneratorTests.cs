using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static class GeneratorTests
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

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generator/unchanged-commands-are-cached", UnchangedCommandsAreCached),
        new("generator/parameter-errors-use-distinct-diagnostics", ParameterErrorsUseDistinctDiagnostics),
        new("generator/custom-result-contexts-compile-and-execute", CustomResultContexts),
        new("generator/partial-result-contexts-use-default-only-when-json-generation-completes-them", PartialResultContexts),
    ];

    private static ValueTask UnchangedCommandsAreCached()
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

    private static ValueTask ParameterErrorsUseDistinctDiagnostics()
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
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(compilation);
        ImmutableArray<Diagnostic> diagnostics = driver.GetRunResult().Results.Single().Diagnostics;
        AssertEx.SequenceEqual(
            ["RCLI9021", "RCLI9024", "RCLI9023", "RCLI9029", "RCLI9029"],
            diagnostics.Select(static diagnostic => diagnostic.Id).ToArray(),
            string.Join("\n", diagnostics));
        AssertEx.True(
            diagnostics.All(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0),
            "Generator diagnostics lost their source locations.");
        return ValueTask.CompletedTask;
    }

    private static async ValueTask CustomResultContexts()
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
                var app = new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false };
                AssertEx.Equal(0, await app.RunAsync(["custom", "--output=json"]));
                using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
                JsonElement payload = frame.RootElement.GetProperty("payload");
                if (hasDefault) AssertEx.Equal("42", payload.GetString());
                else AssertEx.Equal(42, payload.GetInt32());
            }
            finally { loader.Unload(); }
        }
    }

    private static ValueTask PartialResultContexts()
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
