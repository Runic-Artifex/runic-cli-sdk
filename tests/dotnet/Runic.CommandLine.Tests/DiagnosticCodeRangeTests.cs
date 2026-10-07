using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Runic.CommandLine.Generators;

namespace Runic.CommandLine.Tests;

internal static partial class DiagnosticCodeRangeTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("diagnostics/library-never-emits-application-range", LibraryNeverEmitsApplicationRange),
        new("diagnostics/generator-rules-link-to-documented-sections", GeneratorRulesLinkToDocumentation),
    ];

    // Every RCLI code the libraries and the generator can emit is a string literal in a shipped assembly. The packages
    // come from eng/build/shipping-projects.props, and a new package fails this test until this project references it; the
    // generator ships inside Runic.CommandLine's analyzers folder rather than as its own package.
    private static IEnumerable<string> ShippedAssemblyPaths()
    {
        var shipping = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot(), "eng", "build", "shipping-projects.props"));
        string[] packages = shipping.Descendants("RunicShippingProject").Select(static item => (string)item.Attribute("PackageId")!).ToArray();
        AssertEx.True(packages.Length > 0, "No shipping projects found.");
        foreach (string package in packages)
        {
            string path = Path.Combine(AppContext.BaseDirectory, package + ".dll");
            AssertEx.True(File.Exists(path), package + " is shipped but not referenced by the test project, so it cannot be scanned.");
            yield return path;
        }
        yield return typeof(CommandLineGenerator).Assembly.Location;
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Runic.CommandLine.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        return root;
    }

    [GeneratedRegex(@"RCLI[0-9]{4}")]
    private static partial Regex CodePattern();

    private static ValueTask LibraryNeverEmitsApplicationRange()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in ShippedAssemblyPaths())
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            MetadataReader reader = pe.GetMetadataReader();
            for (UserStringHandle handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = reader.GetNextHandle(handle))
                foreach (Match match in CodePattern().Matches(reader.GetUserString(handle)))
                    codes.Add(match.Value);
        }

        // Guard the scan itself: parser, process, generated-binding and generator codes must all be visible.
        foreach (string known in new[] { "RCLI1001", "RCLI2005", "RCLI5000", "RCLI6001", "RCLI9001", "RCLI9034" })
            AssertEx.True(codes.Contains(known), "The scan did not find " + known + ".");
        string[] application = codes.Where(static code => code.StartsWith("RCLI8", StringComparison.Ordinal)).ToArray();
        AssertEx.True(application.Length == 0, "Library code uses the application range: " + string.Join(", ", application));
        return ValueTask.CompletedTask;
    }

    private static ValueTask GeneratorRulesLinkToDocumentation()
    {
        DiagnosticDescriptor[] descriptors = typeof(CommandLineGenerator)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(DiagnosticDescriptor))
            .Select(static field => (DiagnosticDescriptor)field.GetValue(null)!)
            .ToArray();
        AssertEx.True(descriptors.Length >= 20, "Expected the generator's descriptors.");
        string document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "guides", "command-line", "diagnostics.md"));
        const string prefix = "https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/docs/guides/command-line/diagnostics.md#";
        foreach (DiagnosticDescriptor descriptor in descriptors)
        {
            AssertEx.Equal(prefix + descriptor.Id.ToLowerInvariant(), descriptor.HelpLinkUri);
            AssertEx.True(document.Contains("\n## " + descriptor.Id + "\n", StringComparison.Ordinal), descriptor.Id + " has no documented section.");
            AssertEx.True(!descriptor.Id.StartsWith("RCLI8", StringComparison.Ordinal), descriptor.Id + " is in the application range.");
        }
        return ValueTask.CompletedTask;
    }
}
