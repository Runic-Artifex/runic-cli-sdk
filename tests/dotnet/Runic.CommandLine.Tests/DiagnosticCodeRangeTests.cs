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
        new("diagnostics/every-shipped-code-has-a-catalog-entry", EveryShippedCodeHasCatalogEntry),
    ];

    // "RCLI0001 through RCLI9999" bounds the code range in CommandDiagnostic's validation message; neither is reported.
    private static readonly HashSet<string> RangeBounds = new(StringComparer.Ordinal) { "RCLI0001", "RCLI9999" };

    /// <summary>
    /// The diagnostics catalog at the release tag of the assembly that links to it, ending in <c>#</c>:
    /// eng/build/release-links.targets names the tag v$(PackageVersion), never main.
    /// </summary>
    internal static string CatalogUrl(Type type)
    {
        string version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        return $"https://github.com/Runic-Artifex/runic-cli-sdk/blob/v{version}/docs/guides/command-line/diagnostics.md#";
    }

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

    private static string Catalog() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "guides", "command-line", "diagnostics.md")).ReplaceLineEndings("\n");

    [GeneratedRegex(@"RCLI[0-9]{4}")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^### (RCLI[0-9]{4})$", RegexOptions.Multiline)]
    private static partial Regex CatalogEntryPattern();

    private static SortedSet<string> ShippedCodes()
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
        return codes;
    }

    private static ValueTask LibraryNeverEmitsApplicationRange()
    {
        string[] application = ShippedCodes().Where(static code => code.StartsWith("RCLI8", StringComparison.Ordinal)).ToArray();
        AssertEx.True(application.Length == 0, "Library code uses the application range: " + string.Join(", ", application));
        return ValueTask.CompletedTask;
    }

    // Help links and catalog validation messages open the code's catalog entry at the release tag, so each code the
    // packages can report needs one, and an entry for a code that no longer ships is stale.
    private static ValueTask EveryShippedCodeHasCatalogEntry()
    {
        SortedSet<string> shipped = ShippedCodes();
        shipped.ExceptWith(RangeBounds);
        var entries = new SortedSet<string>(CatalogEntryPattern().Matches(Catalog()).Select(static match => match.Groups[1].Value), StringComparer.Ordinal);
        string[] missing = shipped.Except(entries).ToArray();
        AssertEx.True(missing.Length == 0, "docs/guides/command-line/diagnostics.md has no '### <code>' entry for " + string.Join(", ", missing));
        string[] stale = entries.Except(shipped).ToArray();
        AssertEx.True(stale.Length == 0, "docs/guides/command-line/diagnostics.md describes codes that no shipped assembly reports: " + string.Join(", ", stale));
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
        string document = Catalog();
        // Help links name the release tag of the generator, never the main branch.
        string prefix = CatalogUrl(typeof(CommandLineGenerator));
        foreach (DiagnosticDescriptor descriptor in descriptors)
        {
            AssertEx.Equal(prefix + descriptor.Id.ToLowerInvariant(), descriptor.HelpLinkUri);
            AssertEx.True(document.Contains("\n### " + descriptor.Id + "\n", StringComparison.Ordinal), descriptor.Id + " has no documented section.");
            AssertEx.True(!descriptor.Id.StartsWith("RCLI8", StringComparison.Ordinal), descriptor.Id + " is in the application range.");
        }
        return ValueTask.CompletedTask;
    }
}
