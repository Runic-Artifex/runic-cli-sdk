using System.Diagnostics;
using System.Text;

namespace Runic.CommandLine.Tests;

// The old-name guards of the Runic Artifex rename (D079.11). buildTransitive/Runic.CommandLine.targets imports
// Runic.CommandLine.RenamedNames.targets, which eng/rename/old-names.mjs generates from eng/rename/old-names.json; 0.15
// ships its lists empty. OldNameGuards/ holds the same file generated from a test-only list (eng/rename/old-names.test.mjs
// checks that both files are current), so these cases need no JavaScript runtime.
internal sealed class OldNameGuardsTests
{
    private static readonly string[] QuietBuilds = ["-p:DesignTimeBuild=true", "-p:_TargetAssemblyProjectName=App"];

    // Old names of every kind: a property, an item, a direct and (as NuGet resolves it) a transitive package, and the
    // environment variable RUNIC_EXAMPLE_SET. RunicExampleKept is set beside its new name.
    private const string OldNames = """
          <PropertyGroup>
            <RunicExampleProperty>old</RunicExampleProperty>
            <RunicExampleKept>old</RunicExampleKept>
            <RaxExampleKept>new</RaxExampleKept>
          </PropertyGroup>
          <ItemGroup>
            <RunicExampleItem Include="one;two" />
            <PackageReference Include="runic.example.direct" Version="1.0.0" />
            <PackageDependencies Include="Runic.Example.Transitive" />
          </ItemGroup>
        """;

    [Test, DisplayName("old-name-guards/shipped-targets-report-nothing")]
    public ValueTask ShippedTargetsReportNothing()
    {
        using var directory = new FixtureDirectory();
        string targets = Path.Combine(RepositoryRoot(), "packages", "dotnet", "Runic.CommandLine", "buildTransitive", "Runic.CommandLine.targets");
        (int exitCode, string output) = Build(directory.Project($"""<Import Project="{targets}" />"""));
        AssertEx.Equal(0, exitCode, output);
        AssertEx.True(!output.Contains("RCLI7", StringComparison.Ordinal), output);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("old-name-guards/listed-names-are-reported-once-with-the-new-name")]
    public ValueTask ListedNamesAreReportedOnce()
    {
        using var directory = new FixtureDirectory();
        (int exitCode, string output) = Build(directory.Project(directory.TestOnlyImports()));
        AssertEx.True(exitCode != 0, output);
        string[] errors = output.Split('\n').Select(static line => line.Trim()).Where(static line => line.Contains(": error ", StringComparison.Ordinal)).ToArray();
        // The file is imported from two copies, as two packages could ship it; the import guard keeps one.
        AssertEx.Equal(5, errors.Length, output);
        foreach (string expected in new[]
        {
            "error RCLI7002: The Runic Command Line build property RunicExampleProperty is renamed to RaxExampleProperty. The old name no longer applies; rename it in App.proj, its imports or the command line.",
            "error RCLI7002: The Runic Command Line build item RunicExampleItem is renamed to RaxExampleItem.",
            "error RCLI7003: The environment variable RUNIC_EXAMPLE_SET is renamed to RAX_EXAMPLE_SET. The old name no longer applies; set RAX_EXAMPLE_SET instead",
            "error RCLI7001: App references the package Runic.Example.Direct, directly or through another package. It is renamed to RunicArtifex.Example.Direct, and packages of the old and the new names do not work together. Run rax migrate",
            "error RCLI7001: App references the package Runic.Example.Transitive, directly or through another package. It is renamed to RunicArtifex.Example.Transitive,",
        })
            AssertEx.Equal(1, errors.Count(line => line.Contains(expected, StringComparison.Ordinal)), expected + "\n" + output);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("old-name-guards/temporary-and-design-time-builds-report-nothing")]
    public ValueTask TemporaryAndDesignTimeBuildsReportNothing()
    {
        using var directory = new FixtureDirectory();
        string project = directory.Project(directory.TestOnlyImports());
        foreach (string argument in QuietBuilds)
        {
            (int exitCode, string output) = Build(project, argument);
            AssertEx.Equal(0, exitCode, argument + "\n" + output);
        }

        return ValueTask.CompletedTask;
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Runic.CommandLine.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found.");
        return root;
    }

    private static (int ExitCode, string Output) Build(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "msbuild", project, "-t:PrepareForBuild;ResolvePackageAssets", "-nologo", "-noAutoResponse", "-nodeReuse:false", "-v:m", "-clp:NoSummary" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["RUNIC_EXAMPLE_SET"] = "1";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet msbuild did not finish.");
        }

        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }

    private sealed class FixtureDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "runic-old-name-guards-" + Guid.NewGuid().ToString("N"));

        public FixtureDirectory() => Directory.CreateDirectory(_path);

        // Two copies of the test-only guard file and their imports.
        public string TestOnlyImports()
        {
            string generated = Path.Combine(RepositoryRoot(), "tests", "dotnet", "Runic.CommandLine.Tests", "OldNameGuards", "Runic.CommandLine.RenamedNames.targets");
            var imports = new StringBuilder();
            foreach (string copy in new[] { "first", "second" })
            {
                Directory.CreateDirectory(Path.Combine(_path, copy));
                File.Copy(generated, Path.Combine(_path, copy, "Runic.CommandLine.RenamedNames.targets"));
                imports.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"""  <Import Project="{copy}/Runic.CommandLine.RenamedNames.targets" Condition="'$(_RunicCommandLineRenamedNamesImported)' != 'true'" />""").Append('\n');
            }

            return imports.ToString();
        }

        // A project that sets OldNames, then imports, and stands in for the SDK targets the guards run before and after.
        public string Project(string imports)
        {
            string project = Path.Combine(_path, "App.proj");
            File.WriteAllText(project, $"""
                <Project>
                {OldNames}
                {imports}
                  <Target Name="PrepareForBuild" />
                  <Target Name="ResolvePackageAssets" />
                </Project>
                """, new UTF8Encoding(false));
            return project;
        }

        public void Dispose()
        {
            try { Directory.Delete(_path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
