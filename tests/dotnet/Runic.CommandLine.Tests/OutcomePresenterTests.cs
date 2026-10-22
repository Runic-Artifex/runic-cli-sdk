using System.Globalization;
using System.Text.Json;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal sealed class OutcomePresenterTests
{
    private const string Path = "pack";

    private static CommandCatalog Catalog(
        Func<TestOptions, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<TestResult>>> execute,
        Action<CommandCatalogBuilder>? configure = null)
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>(Path, command => command
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory(_ => new TestHandler(execute)))
            .Produces(new TestCodec()));
        configure?.Invoke(builder);
        return builder.Build();
    }

    private static CommandApp App(CommandCatalog catalog, ICommandConsole console, ParseSettings? settings = null) => new(catalog)
    {
        Name = "presenter", Console = console, ParseSettings = settings ?? ParseSettings.Default,
        HandleTerminationSignals = false, Culture = CultureInfo.InvariantCulture,
    };

    // The shape of Runic.Assets.Packer's former ICommandOutcomeSink: its own success line, and a failure message that
    // puts back the paths the fault keeps in its details, while JSON keeps the envelope with the redacted fault.
    private static ValueTask PresentPack(CommandOutcomePresentation<TestResult> presentation, CancellationToken cancellationToken)
    {
        ICommandConsole console = presentation.Context.Console;
        if (presentation.Outcome.Succeeded)
        {
            return console.WriteOutAsync(
                $"Packed a canonical Runic Assets archive into {presentation.Outcome.Value!.Value} bytes.\n".AsMemory(), cancellationToken);
        }

        CommandFault fault = presentation.Outcome.Fault!;
        string message = fault.Details.TryGetValue("sourceDirectory", out string? directory)
            ? $"{fault.Code}: The source directory '{directory}' does not exist."
            : $"{fault.Code}: {fault.Message}";
        if (fault.HelpUri is { } help) message += $"\nHelp for {fault.Code}: {help.AbsoluteUri}";
        return console.WriteErrorAsync((message + "\n").AsMemory(), cancellationToken);
    }

    private static readonly Uri PackHelp = new("https://runic.example/errors/RAS1001");

    private static ValueTask<CommandOutcome<TestResult>> MissingSource() => ValueTask.FromResult(CommandOutcome.Failure<TestResult>(
        CommandExitCategory.Validation,
        new CommandFault("RAS1001", "The source directory does not exist.",
            new Dictionary<string, string> { ["sourceDirectory"] = "/work/dist" }) { HelpUri = PackHelp }));

    [Test, DisplayName("outcome-presenter/packer-sink-as-presenter-writes-success-and-failure")]
    public async ValueTask PackerSinkAsPresenter()
    {
        CommandCatalog succeeding = Catalog((_, _, _) => ValueTask.FromResult(CommandOutcome.Success(new TestResult(1234, "ok"))),
            builder => builder.PresentOutcome<TestResult>(Path, PresentPack));
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(succeeding, console).RunAsync([Path]));
        AssertEx.Equal("Packed a canonical Runic Assets archive into 1234 bytes.\n", console.StandardOutput);
        AssertEx.Equal(string.Empty, console.StandardError);

        CommandCatalog failing = Catalog((_, _, _) => MissingSource(), builder => builder.PresentOutcome<TestResult>(Path, PresentPack));
        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.GetDefault(CommandExitCategory.Validation), await App(failing, console).RunAsync([Path]));
        AssertEx.Equal(string.Empty, console.StandardOutput);
        AssertEx.Equal(
            "RAS1001: The source directory '/work/dist' does not exist.\nHelp for RAS1001: https://runic.example/errors/RAS1001\n",
            console.StandardError);
    }

    [Test, DisplayName("outcome-presenter/json-output-keeps-the-envelope-and-skips-the-presenter")]
    public async ValueTask JsonSkipsPresenter()
    {
        int calls = 0;
        CommandCatalog catalog = Catalog((_, _, _) => MissingSource(), builder => builder.PresentOutcome<TestResult>(Path, (presentation, token) =>
        {
            calls++;
            return PresentPack(presentation, token);
        }));
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.GetDefault(CommandExitCategory.Validation),
            await App(catalog, console, new ParseSettings("json")).RunAsync([Path]));
        using JsonDocument frame = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("RAS1001", frame.RootElement.GetProperty("fault").GetProperty("code").GetString());
        AssertEx.Equal(0, calls);
    }

    [Test, DisplayName("outcome-presenter/receives-exit-code-diagnostics-and-writes-the-default")]
    public async ValueTask WriteDefault()
    {
        CommandOutcomePresentation<TestResult>? seen = null;
        CommandOutcomePresenter<TestResult> presenter = async (presentation, token) =>
        {
            seen = presentation;
            if (presentation.Outcome.Succeeded) await presentation.WriteDefaultAsync(token);
            else await presentation.Context.Console.WriteErrorAsync($"custom {presentation.ExitCode}\n".AsMemory(), token);
        };
        var warning = new CommandDiagnostic("RCLI8000", "app-note", "A note.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning);
        CommandCatalog succeeding = Catalog((_, _, _) => ValueTask.FromResult(CommandOutcome.Success(new TestResult(7, "seven"), [warning])),
            builder => builder.PresentOutcome(Path, presenter));
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(succeeding, console).RunAsync([Path]));
        AssertEx.Equal("7:seven\n", console.StandardOutput);
        AssertEx.True(console.StandardError.Contains("A note.", StringComparison.Ordinal), console.StandardError);
        AssertEx.Equal(Path, seen!.Command.Name);
        AssertEx.Equal(0, seen.ExitCode);
        AssertEx.Equal("RCLI8000", seen.Diagnostics.Single().Code);

        CommandCatalog failing = Catalog((_, _, _) => MissingSource(), builder => builder.PresentOutcome(Path, presenter));
        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.GetDefault(CommandExitCategory.Validation), await App(failing, console).RunAsync([Path]));
        AssertEx.Equal($"custom {CommandExitCodes.GetDefault(CommandExitCategory.Validation)}\n", console.StandardError);
    }

    [Test, DisplayName("outcome-presenter/replaces-the-application-sink-in-human-mode-only")]
    public async ValueTask ReplacesSink()
    {
        var sink = new CapturingSink();
        CommandCatalog catalog = Catalog((_, _, _) => ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok"))),
            builder => builder.PresentOutcome<TestResult>(Path, PresentPack));
        var console = new TestCommandConsole();
        var app = new CommandApp(catalog) { Console = console, OutcomeSink = sink, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false };
        AssertEx.Equal(0, await app.RunAsync([Path]));
        AssertEx.Equal(0, sink.WriteCount);
        AssertEx.Equal("Packed a canonical Runic Assets archive into 1 bytes.\n", console.StandardOutput);

        app = new CommandApp(catalog) { Console = console, OutcomeSink = sink, ParseSettings = new ParseSettings("json"), HandleTerminationSignals = false };
        AssertEx.Equal(0, await app.RunAsync([Path]));
        AssertEx.Equal(1, sink.WriteCount);
    }

    [Test, DisplayName("outcome-presenter/a-throwing-presenter-is-a-host-failure")]
    public async ValueTask ThrowingPresenter()
    {
        Exception? observed = null;
        CommandCatalog catalog = Catalog((_, _, _) => ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok"))),
            builder => builder.PresentOutcome<TestResult>(Path, (_, _) => throw new InvalidOperationException("presenter")));
        var app = new CommandApp(catalog)
        {
            Console = new TestCommandConsole(), ParseSettings = ParseSettings.Default, HandleTerminationSignals = false,
            ExceptionObserver = exception => observed = exception,
        };
        AssertEx.Equal(CommandExitCodes.GetDefault(CommandExitCategory.HostFailure), await app.RunAsync([Path]));
        AssertEx.Equal("presenter", observed?.Message);
    }

    [Test, DisplayName("outcome-presenter/type-mismatch-and-unknown-paths-are-rejected")]
    public void Validation()
    {
        var exception = AssertEx.Throws<CommandCatalogValidationException>(() =>
            Catalog((_, _, _) => MissingSource(), builder => builder.PresentOutcome<string>(Path, (_, _) => ValueTask.CompletedTask)));
        AssertEx.True(exception.Issues.Any(issue => issue.Code == "RCLI0020"), exception.Message);
        AssertEx.Throws<ArgumentException>(() =>
            Catalog((_, _, _) => MissingSource(), builder => builder.PresentOutcome<TestResult>("missing", (_, _) => ValueTask.CompletedTask)));
    }

    // I-164: the sink's context carries the handler's input state, text resolver and continuation instead of a
    // fresh context, both for an application sink and for an outcome presenter.
    [Test, DisplayName("outcome-sink/context-carries-the-handler-input-state")]
    public async ValueTask SinkContextCarriesHandlerState()
    {
        var resolver = new EmptyResolver();
        foreach (bool noInput in new[] { true, false })
        {
            var settings = new ParseSettings(null)
            {
                GetEnvironmentVariable = name => name == "RUNIC_COMMANDLINE_NO_INPUT" && noInput ? "1" : null,
            };

            var sink = new ContextSink();
            bool? handlerInteractive = null;
            CommandCatalog plain = Catalog((_, context, _) =>
            {
                handlerInteractive = new CommandPrompts(context).IsInteractive;
                return ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok")));
            });
            var app = new CommandApp(plain)
            {
                Console = new TestCommandConsole { IsInteractive = true }, OutcomeSink = sink, ParseSettings = settings,
                HandleTerminationSignals = false, TextResolver = resolver,
            };
            AssertEx.Equal(0, app.Run([Path]));
            AssertEx.Equal(!noInput, handlerInteractive);
            AssertEx.Equal(!noInput, sink.Interactive);
            AssertEx.True(ReferenceEquals(resolver, sink.Context!.TextResolver), "The sink context lacks the text resolver.");
            AssertEx.True(sink.Context.CanContinueOnCallingThread);

            bool? presenterInteractive = null;
            CommandExecutionContext? presenterContext = null;
            CommandCatalog presented = Catalog((_, _, _) => ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok"))),
                builder => builder.PresentOutcome<TestResult>(Path, (presentation, _) =>
                {
                    presenterContext = presentation.Context;
                    presenterInteractive = new CommandPrompts(presentation.Context).IsInteractive;
                    return ValueTask.CompletedTask;
                }));
            app = new CommandApp(presented)
            {
                Console = new TestCommandConsole { IsInteractive = true }, ParseSettings = settings,
                HandleTerminationSignals = false, TextResolver = resolver,
            };
            AssertEx.Equal(0, await app.RunAsync([Path]));
            AssertEx.Equal(!noInput, presenterInteractive);
            AssertEx.True(ReferenceEquals(resolver, presenterContext!.TextResolver), "The presenter context lacks the text resolver.");
            AssertEx.True(!presenterContext.CanContinueOnCallingThread, "RunAsync has no continuation.");
        }
    }

    private sealed class EmptyResolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) => null;
    }

    private sealed class ContextSink : ICommandOutcomeSink
    {
        public CommandExecutionContext? Context { get; private set; }

        public bool? Interactive { get; private set; }

        public ValueTask WriteAsync<TResult>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<TResult> outcome,
            ICommandResultCodec<TResult> codec, int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            Context = context;
            Interactive = new CommandPrompts(context).IsInteractive;
            return ValueTask.CompletedTask;
        }
    }
}
