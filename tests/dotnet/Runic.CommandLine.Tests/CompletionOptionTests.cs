using System.Globalization;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// CommandCatalogBuilder.CompletionOption: runic-cli-sdk#44.
internal sealed class CompletionOptionTests
{
    private static readonly string[] Shells = ["bash", "fish", "powershell", "zsh"];

    [Test, DisplayName("completion-option/writes-the-script-for-both-spellings")]
    public async ValueTask WritesScript()
    {
        foreach (string[] args in new string[][] { ["--completions", "bash"], ["--completions=bash"] })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await App(console).RunAsync(args));
            AssertEx.True(console.StandardOutput.Contains("complete -F _runic_fixture 'fixture'", StringComparison.Ordinal), console.StandardOutput);
            AssertEx.Equal("", console.StandardError);
        }

        foreach (string[] args in new string[][] { ["--completions"], ["--completions", "tcsh"], ["--completions=bash", "extra"], ["--completions", "bash", "extra"], ["--completions="] })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(CommandExitCodes.Usage, await App(console).RunAsync(args));
            AssertEx.Equal("", console.StandardOutput);
            AssertEx.True(console.StandardError.Contains("Choose a completion shell", StringComparison.Ordinal), string.Join(' ', args) + ": " + console.StandardError);
        }
    }

    [Test, DisplayName("completion-option/replaces-the-completion-command")]
    public async ValueTask ReplacesCompletionCommand()
    {
        // The default command receives "completion" as an ordinary argument.
        var console = new TestCommandConsole();
        string[]? received = null;
        AssertEx.Equal(0, await App(console, words => received = words).RunAsync(["completion", "bash"]));
        AssertEx.SequenceEqual(["completion", "bash"], received!);
        AssertEx.True(!console.StandardOutput.Contains("complete -F", StringComparison.Ordinal), console.StandardOutput);

        // Without the option, the built-in command still intercepts the same line.
        console = new TestCommandConsole();
        received = null;
        AssertEx.Equal(0, await new CommandApp(Catalog(null, words => received = words)) { Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false }
            .RunAsync(["completion", "bash"]));
        AssertEx.True(received is null && console.StandardOutput.Contains("complete -F", StringComparison.Ordinal), console.StandardOutput);
    }

    [Test, DisplayName("completion-option/help-and-completions-list-the-option")]
    public ValueTask HelpAndCompletionsListOption()
    {
        CommandCatalog catalog = Catalog("--completions");
        AssertEx.Equal("--completions", catalog.CompletionOption);
        string help = CommandHelpFormatter.Format(catalog, "fixture", CommandPath.Root);
        AssertEx.True(help.Contains("--completions <shell>", StringComparison.Ordinal) && !help.Contains("completion <shell>", StringComparison.Ordinal), help);
        string commandHelp = CommandHelpFormatter.Format(catalog, "fixture", new CommandPath(["run"]));
        AssertEx.True(!commandHelp.Contains("--completions", StringComparison.Ordinal), commandHelp);

        AssertEx.SequenceEqual(["--completions"], CommandCompletion.Query(catalog, ["--comp"]).Candidates);
        AssertEx.SequenceEqual(Shells, CommandCompletion.Query(catalog, ["--completions", ""]).Candidates);
        AssertEx.SequenceEqual(["--completions=zsh"], CommandCompletion.Query(catalog, ["--completions=z"]).Candidates);
        AssertEx.True(!CommandCompletion.Query(catalog, [""]).Candidates.Contains("completion"));
        AssertEx.True(!CommandCompletion.Query(catalog, ["help", ""]).Candidates.Contains("completion"));
        foreach (string shell in Shells)
        {
            AssertEx.True(CommandCompletion.Generate(catalog, "fixture", shell).Contains("--completions", StringComparison.Ordinal), shell);
        }

        return ValueTask.CompletedTask;
    }

    // With an unnamed root command (runic-cli-sdk#43), root help is the command's help and lists the option.
    [Test, DisplayName("completion-option/works-with-an-unnamed-root-command")]
    public async ValueTask RootCommand()
    {
        var builder = new CommandCatalogBuilder().CompletionOption("--completions");
        string[]? received = null;
        builder.Command<TestOptions, TestHandler, TestResult>("run", command => command
            .Argument("words", "words", CommandArity.ZeroOrMore)
            .BindWith(new TestBinder((invocation, _) =>
            {
                received = invocation.Arguments.SelectMany(argument => argument.Values).ToArray();
                return ValueTask.FromResult(CommandOutcome.Success(new TestOptions()));
            }))
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        CommandCatalog catalog = builder.RootCommand("run").Build();
        string help = CommandHelpFormatter.Format(catalog, "fixture", CommandPath.Root);
        AssertEx.True(help.Contains("--completions <shell>", StringComparison.Ordinal) && help.Contains("[words...]", StringComparison.Ordinal) &&
            !help.Contains("completion <shell>", StringComparison.Ordinal), help);
        AssertEx.SequenceEqual(Shells, CommandCompletion.Query(catalog, ["--completions", ""]).Candidates);

        var console = new TestCommandConsole();
        CommandApp App() => new(catalog) { Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false };
        AssertEx.Equal(0, await App().RunAsync(["--completions", "zsh"]));
        AssertEx.True(console.StandardOutput.StartsWith("#compdef fixture", StringComparison.Ordinal), console.StandardOutput);
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App().RunAsync(["completion", "run"]));
        AssertEx.SequenceEqual(["completion", "run"], received!);
    }

    [Test, DisplayName("completion-option/hosting-adapter-classifies-and-presents-it")]
    public async ValueTask HostingAdapter()
    {
        var console = new TestCommandConsole();
        var adapter = new CommandLineHostingAdapter(App(console));
        HostedCommandLineDecision decision = adapter.Classify(new HostedCommandLineLaunchInput(["--completions=fish"]));
        AssertEx.Equal(HostedCommandLineDecisionKind.Completion, decision.Kind);
        AssertEx.True(decision.IsCommandLineRequest);
        AssertEx.Equal(0, await adapter.PresentAsync(decision, console, CultureInfo.InvariantCulture, "completion"));
        AssertEx.True(console.StandardOutput.Contains("complete -c 'fixture'", StringComparison.Ordinal), console.StandardOutput);

        console = new TestCommandConsole();
        decision = adapter.Classify(new HostedCommandLineLaunchInput(["--completions"]));
        AssertEx.Equal(HostedCommandLineDecisionKind.Completion, decision.Kind);
        AssertEx.Equal(CommandExitCodes.Usage, await adapter.PresentAsync(decision, console, CultureInfo.InvariantCulture, "completion"));
        AssertEx.True(console.StandardError.Contains("Choose a completion shell", StringComparison.Ordinal), console.StandardError);

        AssertEx.Equal(HostedCommandLineDecisionKind.Invocation, adapter.Classify(new HostedCommandLineLaunchInput(["completion", "bash"])).Kind);
    }

    [Test, DisplayName("completion-option/catalog-validates-the-option")]
    public ValueTask CatalogValidatesOption()
    {
        foreach (string invalid in new[] { "completions", "-c", "--Completions", "--help", "--version", "--" })
        {
            AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().CompletionOption(invalid));
        }

        var colliding = new CommandCatalogBuilder().CompletionOption("--completions");
        CatalogTests.ValidCommand(colliding, "report").Subcommand<TestOptions, TestHandler, TestResult>("list", list =>
            list.BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec())
                .Option("shell", "--shell", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Error, false, null, "--completions"));
        CommandCatalogIssue issue = AssertEx.Throws<CommandCatalogValidationException>(colliding.Build).Issues.Single();
        AssertEx.Equal("RCLI0025", issue.Code);
        AssertEx.Equal("report list", issue.Location);

        foreach (Action<CommandCatalogBuilder> declare in new Action<CommandCatalogBuilder>[]
        {
            builder => builder.CompletionOption("--format"),
            builder => builder.OutputSelector("--runic-output").CompletionOption("--runic-output"),
            builder => builder.OutputSelector("--format", "--output").CompletionOption("--output"),
            builder => builder.JsonOutputAlias("--json").CompletionOption("--json"),
        })
        {
            var builder = new CommandCatalogBuilder();
            declare(builder);
            CatalogTests.ValidCommand(builder, "report");
            AssertEx.Equal("RCLI0025", AssertEx.Throws<CommandCatalogValidationException>(builder.Build).Issues.Single().Code);
        }

        CommandCatalog first = Single("first", "--completions");
        AssertEx.Equal("--completions", CommandCatalog.Combine(first, Single("second", null)).CompletionOption);
        AssertEx.Equal("--completions", CommandCatalog.Combine(Single("second", null), first).CompletionOption);
        AssertEx.Equal("RCLI0025", AssertEx.Throws<CommandCatalogValidationException>(
            () => CommandCatalog.Combine(first, Single("third", "--complete"))).Issues.Single().Code);
        return ValueTask.CompletedTask;
    }

    private static CommandCatalog Single(string name, string? option)
    {
        var builder = new CommandCatalogBuilder();
        if (option is not null) builder.CompletionOption(option);
        CatalogTests.ValidCommand(builder, name);
        return builder.Build();
    }

    private static CommandCatalog Catalog(string? option, Action<string[]>? received = null)
    {
        var builder = new CommandCatalogBuilder();
        if (option is not null) builder.CompletionOption(option);
        builder.Command<TestOptions, TestHandler, TestResult>("run", command => command
            .Argument("words", "words", CommandArity.ZeroOrMore)
            .Option("force", "--force", CommandArity.Zero)
            .BindWith(new TestBinder((invocation, _) =>
            {
                received?.Invoke(invocation.Arguments.SelectMany(argument => argument.Values).ToArray());
                return ValueTask.FromResult(CommandOutcome.Success(new TestOptions()));
            }))
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        return builder.DefaultCommand("run").Build();
    }

    private static CommandApp App(ICommandConsole console, Action<string[]>? received = null) =>
        new(Catalog("--completions", received)) { Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false };
}
