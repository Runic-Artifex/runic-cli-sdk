using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// Consumer-trial findings: less boilerplate (W250-019) and output paper-cuts (W250-026).
internal static class DeveloperExperienceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("boilerplate/shared-services-resolve-and-are-never-disposed", SharedServices),
        new("boilerplate/per-invocation-scopes-are-disposed-once", PerInvocationScopes),
        new("boilerplate/hosted-adapter-reuses-app-identity-and-services", HostedAdapterReusesApp),
        new("boilerplate/hosted-decision-exposes-the-matched-command", HostedMatchInfo),
        new("boilerplate/console-string-and-line-overloads", ConsoleOverloads),
        new("papercuts/human-output-renders-collections-and-booleans", HumanCollections),
        new("papercuts/json-keeps-text-readable-and-escapes-html", JsonEscaping),
        new("papercuts/auto-created-groups-take-a-description", GroupDescriptions),
        new("papercuts/group-attribute-errors-are-build-diagnostics", GroupDiagnostics),
        new("papercuts/culture-defaults-to-current-ui-culture", CultureDefault),
    ];

    private static CommandApp App(ICommandConsole console, ICommandExecutionScopeFactory? scopes = null) => new(GeneratedCommandCatalog.Create())
    {
        Name = "sample", Version = "9.8.7", Console = console, HandleCancelKeyPress = false,
        ParseSettings = ParseSettings.Default, ScopeFactory = scopes,
    };

    private static async ValueTask SharedServices()
    {
        var service = new DxService("shared");
        CommandServices services = CommandServices.Empty.With(service);
        AssertEx.True(CommandServices.Empty.GetService(typeof(DxService)) is null, "With must not change the original provider.");
        AssertEx.True(ReferenceEquals(service, services.GetService(typeof(DxService))));
        AssertEx.True(services.GetService(typeof(object)) is null, "Lookup is by the registered type only.");
        CommandServices replaced = services.With(new DxService("replaced"));
        AssertEx.True(replaced.GetService(typeof(DxService)) is DxService { Name: "replaced" } && ReferenceEquals(service, services.GetService(typeof(DxService))), "With replaces a registration in a new provider only.");

        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, CommandScopes.FromServices(services)).RunAsync(["dx-service", "--loud"]));
        AssertEx.Equal(0, await App(console, CommandScopes.FromServices(services)).RunAsync(["dx-service"]));
        AssertEx.Equal("SHARED\nshared\n", console.StandardOutput);
        AssertEx.Equal(0, service.Disposals);
    }

    private static async ValueTask PerInvocationScopes()
    {
        var created = new List<DxScope>();
        var console = new TestCommandConsole();
        ICommandExecutionScopeFactory scopes = CommandScopes.Create(() => { var scope = new DxScope(); created.Add(scope); return scope; }, static scope => scope);
        AssertEx.Equal(0, await App(console, scopes).RunAsync(["dx-service"]));
        AssertEx.Equal(0, await App(console, scopes).RunAsync(["dx-service"]));
        AssertEx.Equal("scoped\nscoped\n", console.StandardOutput);
        AssertEx.Equal(2, created.Count);
        AssertEx.True(created.All(static scope => scope.Disposals == 1), "Each invocation scope is disposed exactly once.");

        // A value-type scope, such as Microsoft.Extensions.DependencyInjection's AsyncServiceScope, is disposed too.
        var counter = new DxCounter<int>();
        scopes = CommandScopes.Create(() => new DxStructScope(counter), static scope => scope.Services);
        AssertEx.Equal(0, await App(new TestCommandConsole(), scopes).RunAsync(["dx-service"]));
        AssertEx.Equal(1, counter.Value);
    }

    private static async ValueTask HostedAdapterReusesApp()
    {
        var console = new TestCommandConsole();
        var adapter = new CommandLineHostingAdapter(App(console, CommandScopes.FromServices(CommandServices.Empty.With(new DxService("hosted")))));

        AssertEx.Equal(0, await adapter.RunAsync(adapter.Classify(new HostedCommandLineLaunchInput(["--version"]))));
        AssertEx.Equal("9.8.7\n", console.StandardOutput);

        console = new TestCommandConsole();
        AssertEx.Equal(0, await adapter.RunAsync(adapter.Classify(new HostedCommandLineLaunchInput(["--help"])), console));
        AssertEx.True(console.StandardOutput.StartsWith("Usage: sample ", StringComparison.Ordinal), console.StandardOutput);

        console = new TestCommandConsole();
        AssertEx.Equal(0, await adapter.RunAsync(adapter.Classify(new HostedCommandLineLaunchInput(["dx-service", "--loud"])), console));
        AssertEx.Equal("HOSTED\n", console.StandardOutput);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await adapter.RunAsync(adapter.Classify(new HostedCommandLineLaunchInput(["dx-service", "--bogus"])), console));
        AssertEx.True(console.StandardError.Contains("RCLI1001", StringComparison.Ordinal), console.StandardError);

        HostedCommandLineDecision ui = adapter.Classify(new HostedCommandLineLaunchInput([], emptyInputFallback: EmptyInputFallback.UserInterface));
        await AssertEx.ThrowsAsync<InvalidOperationException>(async () => await adapter.RunAsync(ui, console));
    }

    private static ValueTask HostedMatchInfo()
    {
        var adapter = new CommandLineHostingAdapter(App(new TestCommandConsole()));
        HostedCommandLineDecision Classify(params string[] args) =>
            adapter.Classify(new HostedCommandLineLaunchInput(args, emptyInputFallback: EmptyInputFallback.UserInterface));

        HostedCommandLineDecision invalid = Classify("config", "show", "--bogus");
        AssertEx.Equal(HostedCommandLineDecisionKind.Invalid, invalid.Kind);
        AssertEx.Equal("config show", invalid.MatchedPath?.ToString());
        AssertEx.True(invalid.MatchesKnownCommand && invalid.IsCommandLineRequest, "A known command with bad arguments belongs to the command line.");

        AssertEx.Equal("config show", Classify("--output", "json", "config", "show").MatchedPath?.ToString());
        HostedCommandLineDecision group = Classify("config");
        AssertEx.True(group.Kind == HostedCommandLineDecisionKind.Help && group.MatchedPath?.ToString() == "config" && group.IsCommandLineRequest);

        // The default command takes one argument: a document path is an invocation, but names no command.
        HostedCommandLineDecision document = Classify("C:\\report.rpt");
        AssertEx.Equal(HostedCommandLineDecisionKind.Invocation, document.Kind);
        AssertEx.True(document.MatchedPath is null && !document.MatchesKnownCommand);

        HostedCommandLineDecision unknown = Classify("two", "words");
        AssertEx.Equal(HostedCommandLineDecisionKind.Invalid, unknown.Kind);
        AssertEx.True(unknown.MatchedPath is null && !unknown.IsCommandLineRequest, "Unknown words belong to the user interface.");
        AssertEx.True(!Classify().IsCommandLineRequest);
        AssertEx.True(Classify("--version").IsCommandLineRequest && Classify("completion", "bash").IsCommandLineRequest);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ConsoleOverloads()
    {
        var console = new TestCommandConsole();
        await console.WriteOutAsync("a");
        await console.WriteOutLineAsync("b");
        await console.WriteOutLineAsync();
        await console.WriteErrorAsync("c");
        await console.WriteErrorLineAsync("d");
        AssertEx.Equal("ab\n\n", console.StandardOutput);
        AssertEx.Equal("cd\n", console.StandardError);
    }

    private static async ValueTask HumanCollections()
    {
        var console = new TestCommandConsole();
        var value = new DxTask(7, "Buy milk\nand bread", false, ["home", "errands"], [], null,
            new DxOwner("Ada", true), [new DxOwner("Bob", false), new DxOwner("Cy", true)]);
        await CommandResultFormatter.WriteHumanAsync(value, DxJsonContext.Default.DxTask, console);
        AssertEx.Equal(
            "id: 7\n" +
            "title: Buy milk\n  and bread\n" +
            "done: false\n" +
            "tags: home, errands\n" +
            "empty:\n" +
            "note:\n" +
            "owner:\n  name: Ada\n  active: true\n" +
            "watchers:\n  - name: Bob\n    active: false\n  - name: Cy\n    active: true\n",
            console.StandardOutput);

        console = new TestCommandConsole();
        await CommandResultFormatter.WriteHumanAsync([new DxOwner("Bob", false), new DxOwner("Cy", true)], DxJsonContext.Default.DxOwnerArray, console);
        AssertEx.Equal("- name: Bob\n  active: false\n- name: Cy\n  active: true\n", console.StandardOutput);

        console = new TestCommandConsole();
        await CommandResultFormatter.WriteHumanAsync(["a", "b"], DxJsonContext.Default.StringArray, console);
        AssertEx.Equal("a\nb\n", console.StandardOutput);

        console = new TestCommandConsole();
        await CommandResultFormatter.WriteHumanAsync(true, DxJsonContext.Default.Boolean, console);
        AssertEx.Equal("true\n", console.StandardOutput);
    }

    private static async ValueTask JsonEscaping()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["dx-text", "--output", "json"]));
        string frame = console.StandardOutput;
        AssertEx.True(frame.Contains("Ungültige Eingabe – 日本語 ✓", StringComparison.Ordinal), frame);
        foreach (string escaped in new[] { "\\u003Cb\\u003E", "\\u0026", "\\u0027", "\\u0022", "\\u202E", "\\u200B", "\\u2028", "\\u001B" })
            AssertEx.True(frame.Contains(escaped, StringComparison.Ordinal), "Expected " + escaped + " in " + frame);
        using var document = CommandTestEnvelope.Parse(frame);
        AssertEx.Equal(DxCommands.Text, document.RootElement.GetProperty("payload").GetString());
    }

    private static async ValueTask GroupDescriptions()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["--help"]));
        AssertEx.True(console.StandardOutput.Contains("config", StringComparison.Ordinal) &&
            console.StandardOutput.Contains("Inspect sample configuration.", StringComparison.Ordinal), console.StandardOutput);

        console = new TestCommandConsole();
        var app = new CommandApp(GeneratedCommandCatalog.Create(builder => builder.Group("config", new CommandHelp("Overridden."))))
        {
            Name = "sample", Console = console, HandleCancelKeyPress = false, ParseSettings = ParseSettings.Default,
        };
        AssertEx.Equal(0, await app.RunAsync(["--help"]));
        AssertEx.True(console.StandardOutput.Contains("Overridden.", StringComparison.Ordinal), console.StandardOutput);
        AssertEx.Throws<ArgumentException>(() => GeneratedCommandCatalog.Create(builder => builder.Group("greet", new CommandHelp("No."))));

        // A group described before its commands exist is the same group they are added to.
        CommandCatalog manual = new CommandCatalogBuilder().Group("tools", new CommandHelp("Tools."), "groups.tools").Build();
        AssertEx.True(manual.TryGetCommand("tools", out CommandDescriptor? tools) && tools!.Help.Description == "Tools." && tools.DescriptionKey == "groups.tools");
    }

    private static ValueTask GroupDiagnostics()
    {
        CSharpCompilation compilation = GeneratorTests.CreateCompilation("""
            using Runic.CommandLine;

            [CommandGroup("config", Description = "Settings.")]
            [CommandGroup("confg", Description = "Typo.")]
            [CommandGroup("list", Description = "A command.")]
            [CommandGroup("config", Description = "Twice.")]
            internal static class Commands
            {
                [Command("config show")]
                public static string Show() => "";

                [Command("list")]
                public static string List() => "";
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out Compilation compiled, out _);
        GeneratorRunResult result = driver.GetRunResult().Results.Single();
        ImmutableArray<Diagnostic> diagnostics = result.Diagnostics;
        AssertEx.SequenceEqual(["RCLI9043", "RCLI9043", "RCLI9043"], diagnostics.Select(static diagnostic => diagnostic.Id).ToArray(), string.Join("\n", diagnostics));
        string messages = string.Join("\n", diagnostics.Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)));
        AssertEx.True(messages.Contains("'config' is described more than once", StringComparison.Ordinal) &&
            messages.Contains("'confg' does not begin", StringComparison.Ordinal) &&
            messages.Contains("'list' names a command", StringComparison.Ordinal), messages);
        AssertEx.True(diagnostics.All(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0), "Diagnostics point at the attribute.");
        AssertEx.True(result.GeneratedSources.Single().SourceText.ToString().Contains(".Group(\"config\"", StringComparison.Ordinal));
        AssertEx.Equal(0, compiled.GetDiagnostics().Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return ValueTask.CompletedTask;
    }

    private static async ValueTask CultureDefault()
    {
        CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await App(console).RunAsync(["dx-culture"]));
            AssertEx.Equal("de-DE\n", console.StandardOutput);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}

internal sealed class DxService(string name) : IAsyncDisposable
{
    public string Name { get; } = name;
    public int Disposals { get; private set; }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}

internal sealed class DxScope : IServiceProvider, IAsyncDisposable
{
    public int Disposals { get; private set; }
    public object? GetService(Type serviceType) => serviceType == typeof(DxService) ? new DxService("scoped") : null;
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}

internal sealed class DxCounter<T> { public T? Value { get; set; } }

internal readonly struct DxStructScope(DxCounter<int> disposals) : IAsyncDisposable
{
    public IServiceProvider Services => CommandServices.Empty.With(new DxService("struct" + disposals.Value));
    public ValueTask DisposeAsync() { disposals.Value++; return ValueTask.CompletedTask; }
}

[CommandGroup("config", Description = "Inspect sample configuration.")]
internal static class DxCommands
{
    internal const string Text = "Ungültige Eingabe – 日本語 ✓ <b> & ' \" \u202E \u200B \u2028 \u001B";

    // The injected token is defaulted and last, after a defaulted option (CA1068).
    [Command("dx-service", Hidden = true)]
    internal static string Service([FromServices] DxService service, [Option("--loud")] bool loud = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return loud ? service.Name.ToUpperInvariant() : service.Name;
    }

    [Command("dx-text", Hidden = true)]
    internal static string TextCommand() => Text;

    [Command("dx-culture", Hidden = true)]
    internal static string Culture(CommandExecutionContext context) => context.Culture.Name;
}

internal sealed record DxOwner(string Name, bool Active);
internal sealed record DxTask(int Id, string Title, bool Done, string[] Tags, string[] Empty, string? Note, DxOwner Owner, DxOwner[] Watchers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DxTask))]
[JsonSerializable(typeof(DxOwner[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
internal sealed partial class DxJsonContext : JsonSerializerContext;
