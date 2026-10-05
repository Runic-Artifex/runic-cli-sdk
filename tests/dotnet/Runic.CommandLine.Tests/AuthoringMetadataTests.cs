using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static class AuthoringMetadataTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generator/converter-validator-metadata-errors-at-attributes", InvalidMetadata),
        new("generator/closed-explicit-and-inherited-conversion-compiles-and-executes", ValidMetadata),
    ];

    private static ValueTask InvalidMetadata()
    {
        (string Attribute, string Type, string Binding, string Declarations, string Code)[] cases =
        [
            ("ConvertWith(typeof(string))", "int", "Option(\"--value\")", "", "RCLI9030"),
            ("ValidateWith(typeof(string))", "int", "Argument", "", "RCLI9031"),
            ("ConvertWith(typeof(Wrong))", "int", "Argument", "internal class Wrong : ICommandValueConverter<string> { public static string Parse(string value) => value; }", "RCLI9030"),
            ("ValidateWith(typeof(Wrong))", "int", "Argument", "internal class Wrong : ICommandValueValidator<string> { public static bool IsValid(string value) => true; }", "RCLI9031"),
            ("ConvertWith(typeof(NullableText))", "string", "Argument", "internal struct NullableText : ICommandValueConverter<string?> { public static string? Parse(string value) => value; }", "RCLI9030"),
            ("ValidateWith(typeof(NullableText))", "string", "Argument", "internal struct NullableText : ICommandValueValidator<string?> { public static bool IsValid(string? value) => true; }", "RCLI9031"),
            ("ConvertWith(typeof(Open<>))", "int", "Argument", "internal class Open<T> : ICommandValueConverter<T> { public static T Parse(string value) => default!; }", "RCLI9030"),
            ("ValidateWith(typeof(Open<>))", "int", "Argument", "internal class Open<T> : ICommandValueValidator<T> { public static bool IsValid(T value) => true; }", "RCLI9031"),
            ("ConvertWith(typeof(Abstract))", "int", "Argument", "internal abstract class Abstract : ICommandValueConverter<int> { public static int Parse(string value) => 0; }", "RCLI9030"),
            ("ConvertWith(typeof(Local))", "int", "Argument", "file class Local : ICommandValueConverter<int> { public static int Parse(string value) => 0; }", "RCLI9030"),
            ("ConvertWith(typeof(Hidden))", "int", "Argument", "", "RCLI9030"),
            ("ValidateWith(typeof(Hidden))", "int", "Argument", "", "RCLI9031"),
            ("ConvertWith(typeof(Flag))", "bool", "Option(\"--value\")", "internal struct Flag : ICommandValueConverter<bool> { public static bool Parse(string value) => true; }", "RCLI9032"),
            ("ConvertWith(typeof(string))", "int[]", "Option(\"--value\")", "", "RCLI9034"),
            ("ValidateWith(typeof(string))", "ICommandConsole", "", "", "RCLI9033"),
        ];
        foreach (var item in cases)
        {
            string binding = item.Binding.Length == 0 ? "" : "[" + item.Binding + "]";
            string source = $$"""
                using Runic.CommandLine;
                {{item.Declarations}}
                internal static class Commands
                {
                    private class Hidden : ICommandValueConverter<int>, ICommandValueValidator<int>
                    { public static int Parse(string value) => 0; public static bool IsValid(int value) => true; }
                    [Command("probe")]
                    public static string Probe({{binding}} [{{item.Attribute}}] {{item.Type}} value) => "ok";
                }
                """;
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(CreateCompilation(source));
            Diagnostic diagnostic = driver.GetRunResult().Results.Single().Diagnostics.Single();
            AssertEx.Equal(item.Code, diagnostic.Id, diagnostic.ToString());
            AssertEx.Equal(item.Attribute, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
            AssertEx.Equal(0, driver.GetRunResult().Results.Single().GeneratedSources.Length);
        }
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ValidMetadata()
    {
        const string source = """
            using System;
            using Runic.CommandLine;
            internal class Base : ICommandValueConverter<int>
            { public static int Parse(string value) => int.Parse(value) + 1; }
            internal sealed class Inherited : Base { }
            internal struct Explicit<T> : ICommandValueConverter<T> where T : IParsable<T>
            { static T ICommandValueConverter<T>.Parse(string value) => T.Parse(value, null); }
            internal struct Positive : ICommandValueValidator<int>
            { static bool ICommandValueValidator<int>.IsValid(int value) => value > 0; }
            internal struct FlagValidator : ICommandValueValidator<bool>
            { public static bool IsValid(bool value) => !value; }
            internal struct NullableText : ICommandValueConverter<string?>, ICommandValueValidator<string?>
            { public static string? Parse(string value) => value == "null" ? null : value; public static bool IsValid(string? value) => value is null or "ok"; }
            internal static class Commands
            {
                [Command("nullable")]
                public static string Nullable([Option("--text"), ConvertWith(typeof(NullableText)), ValidateWith(typeof(NullableText))] string? text = null) => text ?? "none";
                [Command("probe", DescriptionKey = "commands.probe", Description = "Probe")]
                public static string Probe(
                    [Argument(DescriptionKey = "arguments.value", Description = "Value"), ConvertWith(typeof(Explicit<int>)), ValidateWith(typeof(Positive))] int value,
                    [Option("--offset", DescriptionKey = "options.offset"), ConvertWith(typeof(Inherited))] int offset,
                    [Option("--flag"), ValidateWith(typeof(FlagValidator))] bool flag) => $"{value}:{offset}";
            }
            """;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out Compilation compiled, out var generatorDiagnostics);
        AssertEx.Equal(0, generatorDiagnostics.Length, string.Join("\n", generatorDiagnostics));
        Diagnostic[] errors = compiled.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error || d.Id is "CS8631" or "CS8620").ToArray();
        AssertEx.Equal(0, errors.Length, string.Join("\n", errors.Select(d => d.ToString())));
        using var bytes = new MemoryStream();
        var emitted = compiled.Emit(bytes);
        AssertEx.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var loader = new AssemblyLoadContext("authoring-probe", isCollectible: true);
        try
        {
            Assembly assembly = loader.LoadFromStream(bytes);
            var catalog = (CommandCatalog)assembly.GetType("Runic.CommandLine.Generated.GeneratedCommandCatalog")!.GetMethod("Create")!.Invoke(null, [null])!;
            AssertEx.Equal("commands.probe", catalog.Commands.Single(c => c.Name == "probe").DescriptionKey);
            AssertEx.Equal("arguments.value", catalog.Commands.Single(c => c.Name == "probe").Arguments.Single().DescriptionKey);
            AssertEx.Equal("options.offset", catalog.Commands.Single(c => c.Name == "probe").Options[0].DescriptionKey);
            var console = new TestCommandConsole();
            var app = new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false };
            AssertEx.Equal(0, await app.RunAsync(["probe", "2", "--offset", "3"]));
            AssertEx.Equal("2:4\n", console.StandardOutput);
            AssertEx.Equal(0, await app.RunAsync(["nullable"]));
            AssertEx.Equal(0, await app.RunAsync(["nullable", "--text", "ok"]));
            AssertEx.Equal(0, await app.RunAsync(["nullable", "--text", "null"]));
            AssertEx.Equal(2, await app.RunAsync(["nullable", "--text", "invalid"]));
            AssertEx.Equal(2, await app.RunAsync(["probe", "-2", "--offset", "3"]));
            AssertEx.Equal(2, await app.RunAsync(["probe", "2", "--offset", "3", "--flag"]));
        }
        finally { loader.Unload(); }
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platform.Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Append(typeof(CommandAttribute).Assembly.Location).Select(p => MetadataReference.CreateFromFile(p));
        return CSharpCompilation.Create("AuthoringProbe" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
