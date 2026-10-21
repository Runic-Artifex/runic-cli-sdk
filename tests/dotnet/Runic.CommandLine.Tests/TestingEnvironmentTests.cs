using System.Globalization;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal sealed class TestingEnvironmentTests
{
    [Test, DisplayName("testing/capture-reads-the-output-variable")]
    public ValueTask CaptureReadsOutputVariable()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        ParseSettings json = CommandTestEnvironment.Capture(new Dictionary<string, string?> { [CommandOutputClassifier.EnvironmentVariableName] = "json" });
        AssertEx.Equal("json", json.OutputEnvironmentValue);
        ParsedInvocation invocation = CommandTestInvocation.Parse(catalog, json, "status");
        AssertEx.Equal(CommandOutputMode.Json, invocation.OutputClassification.Mode);
        AssertEx.Equal(CommandOutputModeSource.Environment, invocation.OutputClassification.Source);

        // The explicit selector still wins.
        invocation = CommandTestInvocation.Parse(catalog, json, "status", "--format", "human");
        AssertEx.Equal(CommandOutputMode.Human, invocation.OutputClassification.Mode);

        ParseSettings none = CommandTestEnvironment.Capture();
        AssertEx.Equal<string?>(null, none.OutputEnvironmentValue);
        AssertEx.Equal(CommandOutputMode.Human, none.DefaultOutputMode);
        AssertEx.Equal(CommandOutputModeSource.Default, CommandTestInvocation.Parse(catalog, none, "status").OutputClassification.Source);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("testing/capture-snapshots-the-variables-and-never-reads-the-process")]
    public ValueTask CaptureSnapshotsVariables()
    {
        // PATH is set in the test process; captured settings must not see it.
        AssertEx.True(Environment.GetEnvironmentVariable("PATH") is not null, "The test process has no PATH.");
        ParseSettings empty = CommandTestEnvironment.Capture();
        AssertEx.True(empty.GetEnvironmentVariable is not null);
        AssertEx.Equal<string?>(null, empty.GetEnvironmentVariable!("PATH"));

        var variables = new Dictionary<string, string?> { ["APP_VALUE"] = "captured", ["PATH"] = null };
        ParseSettings settings = CommandTestEnvironment.Capture(variables);
        variables["APP_VALUE"] = "mutated";
        variables[CommandOutputClassifier.EnvironmentVariableName] = "json";
        AssertEx.Equal("captured", settings.GetEnvironmentVariable!("APP_VALUE"));
        AssertEx.Equal<string?>(null, settings.GetEnvironmentVariable("PATH"));
        AssertEx.Equal<string?>(null, settings.OutputEnvironmentValue);

        // Option environment fallbacks read the captured variables.
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "run")
            .Option("value", "--value", CommandArity.ExactlyOne)
            .ParameterHelp("value", new CommandHelp(environmentVariable: "APP_VALUE"));
        ParsedInvocation invocation = CommandTestInvocation.Parse(builder.Build(), settings, "run");
        AssertEx.SequenceEqual(["captured"], invocation.Options.Single().Values);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("testing/capture-honours-no-input")]
    public async ValueTask CaptureHonoursNoInput()
    {
        var disabled = new TestCommandConsole { IsInteractive = true };
        AssertEx.Equal(CommandExitCodes.Usage, await App(disabled, CommandTestEnvironment.Capture(
            new Dictionary<string, string?> { ["RUNIC_COMMANDLINE_NO_INPUT"] = "1" })).RunAsync(["ask"]));
        AssertEx.True(disabled.StandardError.StartsWith("RCLI4001: Name needs an answer, but prompts are disabled.", StringComparison.Ordinal), disabled.StandardError);

        var enabled = new TestCommandConsole { IsInteractive = true };
        enabled.QueueInput("ada");
        AssertEx.Equal(0, await App(enabled, CommandTestEnvironment.Capture()).RunAsync(["ask"]));
        AssertEx.Equal("0:ada\n", enabled.StandardOutput);
    }

    private static CommandApp App(ICommandConsole console, ParseSettings settings)
    {
        var handler = new TestHandler(async (_, context, cancellationToken) =>
            CommandOutcome.Success(new TestResult(0, await new CommandPrompts(context).TextAsync("Name", cancellationToken: cancellationToken))));
        CommandCatalog catalog = new CommandCatalogBuilder().Command<TestOptions, TestHandler, TestResult>("ask", command => command
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory(_ => handler)).Produces(new TestCodec())).Build();
        return new CommandApp(catalog)
        {
            Name = "prompts", Console = console, ParseSettings = settings, HandleTerminationSignals = false, Culture = CultureInfo.InvariantCulture,
        };
    }
}
