namespace Runic.CommandLine.Tests;

internal sealed class CatalogTests
{
    private static readonly string[] MissingRegistrationCodes =
        ["RCLI0010", "RCLI0011", "RCLI0012"];

    [Test, DisplayName("catalog/aliases-resolve-to-canonical-descriptors")]
    public ValueTask AliasesResolve()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        AssertEx.True(catalog.TryGetCommand("stat", out CommandDescriptor? status), "Root alias was not resolved.");
        AssertEx.Equal("status", status!.Name);
        AssertEx.True(catalog.TryResolve(["c", "purge"], out CommandDescriptor? clear, out int consumed),
            "Nested aliases were not resolved.");
        AssertEx.Equal("clear", clear!.Name);
        AssertEx.Equal(2, consumed);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/combine-preserves-order-and-rejects-duplicates")]
    public ValueTask CombineCatalogs()
    {
        var firstBuilder = new CommandCatalogBuilder();
        ValidCommand(firstBuilder, "first");
        var secondBuilder = new CommandCatalogBuilder();
        ValidCommand(secondBuilder, "second");
        CommandCatalog combined = CommandCatalog.Combine(firstBuilder.Build(), secondBuilder.Build());
        AssertEx.SequenceEqual(["first", "second"], combined.Commands.Select(static command => command.Name));

        var duplicateBuilder = new CommandCatalogBuilder();
        ValidCommand(duplicateBuilder, "first");
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() =>
            CommandCatalog.Combine(combined, duplicateBuilder.Build()));
        AssertEx.Equal("RCLI0004", exception.Issues[0].Code);

        CommandCatalog firstDefault = firstBuilder.DefaultCommand("first").Build();
        CommandCatalog secondDefault = secondBuilder.DefaultCommand("second").Build();
        exception = AssertEx.Throws<CommandCatalogValidationException>(() => CommandCatalog.Combine(firstDefault, secondDefault));
        AssertEx.Equal("RCLI0019", exception.Issues[0].Code);

        var thirdBuilder = new CommandCatalogBuilder();
        ValidCommand(thirdBuilder, "third");
        CommandCatalog withDefault = CommandCatalog.Combine(firstDefault, thirdBuilder.Build());
        AssertEx.Equal("first", withDefault.DefaultCommand!.Name);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/global-option-conflict-names-the-option-and-both-alias-sets")]
    public ValueTask GlobalOptionConflictMessage()
    {
        var builder = new CommandCatalogBuilder().GlobalOption("force", "--force", CommandArity.Zero, aliases: ["-F"]);
        ValidCommand(builder, "c").Option("force", "--force", CommandArity.Zero, aliases: ["-f"]);
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() => builder.Build());
        CommandCatalogIssue issue = exception.Issues[0];
        AssertEx.Equal("RCLI0019", issue.Code);
        AssertEx.Equal("c", issue.Location);
        AssertEx.Equal("Global option 'force' conflicts with the command-local option of the same ID: the global option is --force with aliases [-F], " +
            "the command-local option is --force with aliases [-f]. Use the same name, arity and aliases, or give the options different IDs.", issue.Message);
        AssertEx.True(exception.Message.Contains("RCLI0019 at 'c': Global option 'force'", StringComparison.Ordinal), exception.Message);
        AssertEx.True(exception.Message.Contains("diagnostics.md#rcli0019", StringComparison.Ordinal), exception.Message);

        var arity = new CommandCatalogBuilder().GlobalOption("level", "--level", CommandArity.ExactlyOne);
        ValidCommand(arity, "c").Option("level", "--level", CommandArity.Zero);
        AssertEx.Equal("Global option 'level' conflicts with the command-local option of the same ID: the global option is --level (one value) with aliases [], " +
            "the command-local option is --level (a flag) with aliases []. Use the same name, arity and aliases, or give the options different IDs.",
            AssertEx.Throws<CommandCatalogValidationException>(() => arity.Build()).Issues[0].Message);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/default-command-receives-unprefixed-tokens")]
    public ValueTask DefaultCommand()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "pack").Argument("path", "path", CommandArity.ExactlyOne);
        CommandCatalog catalog = builder.DefaultCommand("pack").Build();
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["artifact.bin"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        AssertEx.Equal("pack", outcome.Invocation!.Command.Name);
        AssertEx.Equal("artifact.bin", outcome.Invocation.Arguments[0].Values[0]);

        // `help` is reserved at the root; `--` passes it to the default command as data.
        AssertEx.Equal(ParseOutcomeKind.Help, PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["help"], ParseSettings.Default).Kind);
        ParseOutcome escaped = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["--", "help"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, escaped.Kind);
        AssertEx.Equal("help", escaped.Invocation!.Arguments[0].Values[0]);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/environment-flags-accept-common-spellings")]
    public ValueTask EnvironmentFlagSpellings()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "build").Option("verbose", "--verbose", CommandArity.Zero)
            .ParameterHelp("verbose", new CommandHelp(environmentVariable: "APP_VERBOSE"));
        CommandCatalog catalog = builder.Build();
        foreach ((string value, bool present) in new[] { ("true", true), ("TRUE", true), ("1", true), ("yes", true), ("Yes", true), ("false", false), ("0", false), ("no", false), ("NO", false) })
        {
            ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["build"], new ParseSettings { GetEnvironmentVariable = _ => value });
            AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind, value);
            AssertEx.Equal(present, outcome.Invocation!.Options.Any(static option => option.Id == "verbose"), value);
        }

        ParseOutcome invalid = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["build"], new ParseSettings { GetEnvironmentVariable = _ => "maybe" });
        AssertEx.Equal(ParseOutcomeKind.Error, invalid.Kind);
        AssertEx.Equal("RCLI1014", invalid.Diagnostics[0].Code);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/choices-bind-the-declared-spelling")]
    public ValueTask ChoicesUseDeclaredSpelling()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "build")
            .Option("configuration", "--configuration", CommandArity.ExactlyOne)
            .Argument("target", "target", CommandArity.ZeroOrMore)
            .ParameterHelp("configuration", new CommandHelp(choices: ["Debug", "Release"]))
            .ParameterHelp("target", new CommandHelp(choices: ["linux-x64", "Win-X64", "win-x64"]));
        CommandCatalog catalog = builder.Build();

        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["build", "--configuration", "release", "LINUX-X64", "win-x64"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        AssertEx.Equal("Release", outcome.Invocation!.Options.Single().Values[0]);
        AssertEx.SequenceEqual(["linux-x64", "win-x64"], outcome.Invocation.Arguments.Single().Values);
        AssertEx.Equal(ParseOutcomeKind.Error, PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["build", "--configuration", "retail"], ParseSettings.Default).Kind);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/reserved-command-names-rejected")]
    public ValueTask ReservedCommandNames()
    {
        foreach (string name in new[] { "help", "version", "output", "root" })
        {
            var builder = new CommandCatalogBuilder();
            ValidCommand(builder, name);
            CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() =>
                builder.Build());
            AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0002"), name);
        }

        return ValueTask.CompletedTask;
    }

    // "root" is the envelope's command value for root-level responses, so no command, subcommand or alias may use it.
    [Test, DisplayName("catalog/root-is-reserved-for-subcommands-and-aliases")]
    public ValueTask RootIsReserved()
    {
        var nested = new CommandCatalogBuilder();
        ValidCommand(nested, "tools").Subcommand<TestOptions, TestHandler, TestResult>("root", root => root
            .BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec()));
        CommandCatalogIssue subcommand = AssertEx.Throws<CommandCatalogValidationException>(nested.Build).Issues.Single();
        AssertEx.Equal("RCLI0002", subcommand.Code);
        AssertEx.True(subcommand.Message.Contains("'root'", StringComparison.Ordinal), subcommand.Message);

        var aliased = new CommandCatalogBuilder();
        ValidCommand(aliased, "status").Alias("root");
        CommandCatalogIssue alias = AssertEx.Throws<CommandCatalogValidationException>(aliased.Build).Issues.Single();
        AssertEx.Equal("RCLI0002", alias.Code);

        // Only the exact segment is reserved.
        var similar = new CommandCatalogBuilder();
        ValidCommand(similar, "roots");
        ValidCommand(similar, "root-cause");
        AssertEx.Equal(2, similar.Build().Commands.Count);
        return ValueTask.CompletedTask;
    }

    // The positional bool? isRequired overload is gone; isRequired is passed by name and may follow the other options.
    [Test, DisplayName("catalog/option-is-required-by-name")]
    public ValueTask OptionIsRequiredByName()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "deploy")
            .Option("target", "--target", CommandArity.ExactlyOne, isRequired: true, aliases: ["-t"])
            .Option("token", "--token", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Error, isSensitive: true,
                descriptionKey: "deploy.token", isRequired: true)
            .Option("tag", "--tag", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Append, aliases: ["-g"]);
        CommandCatalog catalog = builder.Build();
        CommandDescriptor deploy = catalog.Commands.Single();
        CommandOptionDescriptor target = deploy.Options.Single(static option => option.Id == "target");
        AssertEx.True(target.IsRequired);
        AssertEx.True(!target.IsSensitive);
        AssertEx.SequenceEqual(["-t"], target.Aliases);
        CommandOptionDescriptor token = deploy.Options.Single(static option => option.Id == "token");
        AssertEx.True(token.IsRequired);
        AssertEx.True(token.IsSensitive);
        AssertEx.Equal("deploy.token", token.DescriptionKey);
        CommandOptionDescriptor tag = deploy.Options.Single(static option => option.Id == "tag");
        AssertEx.True(!tag.IsRequired);
        AssertEx.SequenceEqual(["-g"], tag.Aliases);

        ParseOutcome missing = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["deploy", "-t", "prod"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Error, missing.Kind);
        AssertEx.Equal("RCLI1012", missing.Diagnostics.Single().Code);
        ParseOutcome complete = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog, ["deploy", "-t", "prod", "--token", "secret"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, complete.Kind);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/reserved-option-names-rejected")]
    public ValueTask ReservedOptionNames()
    {
        foreach (string name in new[] { "--help", "-h", "--version" })
        {
            var builder = new CommandCatalogBuilder();
            ValidCommand(builder, "status").Option("reserved", name, CommandArity.Zero);
            CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
            AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0007"), name);
        }

        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/application-output-and-configured-transport-coexist")]
    public ValueTask ConfiguredTransportOutput()
    {
        var builder = new CommandCatalogBuilder().OutputSelector("--runic-output");
        ValidCommand(builder, "status").Option("output", "--output", CommandArity.ExactlyOne);
        CommandCatalog catalog = builder.Build();

        ParseOutcome success = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            ["status", "--output", "application", "--runic-output=json"],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, success.Kind);
        AssertEx.Equal(CommandOutputMode.Json, success.Invocation!.OutputClassification.Mode);
        AssertEx.Equal("application", success.Invocation.Options.Single().Values.Single());

        // Under the default selector, --output is an ordinary application option.
        var plain = new CommandCatalogBuilder();
        ValidCommand(plain, "status").Option("output", "--output", CommandArity.ExactlyOne);
        ParseOutcome defaultSelector = PortableCommandSyntaxAdapter.Instance.Parse(
            plain.Build(),
            ["status", "--output", "application", "--format=json"],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, defaultSelector.Kind);
        AssertEx.Equal(CommandOutputMode.Json, defaultSelector.Invocation!.OutputClassification.Mode);
        AssertEx.Equal("application", defaultSelector.Invocation.Options.Single().Values.Single());

        // An option spelled like the selector is rejected when the catalog is built, before any parse could report RCLI1011.
        var colliding = new CommandCatalogBuilder();
        ValidCommand(colliding, "status").Option("format", "--format", CommandArity.ExactlyOne);
        CommandCatalogValidationException collision = AssertEx.Throws<CommandCatalogValidationException>(colliding.Build);
        AssertEx.Equal("RCLI0026", collision.Issues.Single().Code);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/duplicate-spellings-reported")]
    public ValueTask DuplicateSpellings()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "one").Alias("shared");
        ValidCommand(builder, "two").Alias("shared");
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0004"));

        builder = new CommandCatalogBuilder();
        ValidCommand(builder, "one")
            .Option("first", "--first", CommandArity.Zero, aliases: "-x")
            .Option("second", "--second", CommandArity.Zero, aliases: "-x");
        exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0008"));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/contradictory-argument-arity-reported")]
    public ValueTask ContradictoryArgumentArity()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "copy")
            .Argument("optional", "optional", CommandArity.ZeroOrOne)
            .Argument("required", "required", CommandArity.ExactlyOne);
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0015"));

        builder = new CommandCatalogBuilder();
        ValidCommand(builder, "copy")
            .Argument("many", "many", CommandArity.ZeroOrMore)
            .Argument("tail", "tail", CommandArity.ZeroOrOne);
        exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0016"));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/missing-closed-registration-reported-in-order")]
    public ValueTask MissingRegistration()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("status");
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.SequenceEqual(
            MissingRegistrationCodes,
            exception.Issues.Select(static issue => issue.Code));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/invalid-payload-identities-fail-at-freeze")]
    public ValueTask InvalidPayloadIdentities()
    {
        foreach (string payloadType in new[]
        {
            string.Empty,
            "Upper/1",
            "name",
            "name/0",
            "name/x",
            "name/",
            "bad name/1",
            "bad_name/1",
            "bad/name/1",
            $"{new string('a', 127)}/1",
            $"a{new string('\u00e9', 63)}/1",
        })
        {
            var builder = new CommandCatalogBuilder();
            builder.Command<TestOptions, TestHandler, TestResult>("status")
                .BindWith(new TestBinder())
                .CreateHandlerWith(new TestHandlerFactory())
                .Produces(new InvalidCodec(payloadType));
            CommandCatalogValidationException exception =
                AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
            AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0017"), payloadType);
        }

        AssertInvalidCodec(new NullTypeInfoCodec());
        AssertInvalidCodec(new ThrowingCodec(throwPayloadType: true));
        AssertInvalidCodec(new ThrowingCodec(throwPayloadType: false));

        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/payload-identity-is-frozen")]
    public async ValueTask PayloadIdentityIsFrozen()
    {
        var initialContext = new TestJsonContext(new System.Text.Json.JsonSerializerOptions());
        var mutatedContext = new TestJsonContext(new System.Text.Json.JsonSerializerOptions());
        var handlerContext = new TestJsonContext(new System.Text.Json.JsonSerializerOptions());
        var codec = new MutableCodec("initial/1", initialContext.TestResult);
        var factory = new TestHandlerFactory(_ => new TestHandler((_, _, _) =>
        {
            codec.PayloadType = "handler-mutated/3";
            codec.TypeInfo = handlerContext.TestResult;
            return ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok")));
        }));
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("status")
            .BindWith(new TestBinder())
            .CreateHandlerWith(factory)
            .Produces(codec);
        CommandCatalog catalog = builder.Build();
        codec.PayloadType = "mutated/2";
        codec.TypeInfo = mutatedContext.TestResult;

        ParsedInvocation invocation = FixtureCatalog.ParseInvocation(catalog, "status");
        var sink = new CapturingSink();
        var request = new CommandExecutionRequest(
            invocation,
            new MemoryCommandConsole(),
            System.Globalization.CultureInfo.InvariantCulture,
            "catalog-freeze");
        await new CommandExecutor(new TrackingScopeFactory()).ExecuteAsync(request, sink);

        AssertEx.Equal("initial/1", sink.PayloadType);
        AssertEx.True(ReferenceEquals(initialContext.TestResult, sink.TypeInfo));
    }

    private static void AssertInvalidCodec(ICommandResultCodec<TestResult> codec)
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("status")
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(codec);
        CommandCatalogValidationException exception =
            AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0017"));
    }

    [Test, DisplayName("catalog/frozen-descriptors-isolate-source-arrays")]
    public ValueTask FrozenDescriptors()
    {
        string[] aliases = ["stat"];
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "status").Alias(aliases);
        CommandCatalog catalog = builder.Build();
        aliases[0] = "changed";
        AssertEx.True(catalog.TryGetCommand("stat", out _));
        AssertEx.True(!catalog.TryGetCommand("changed", out _));
        AssertEx.Throws<NotSupportedException>(() =>
            ((IList<string>)catalog.Commands[0].Aliases).Add("mutate"));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("catalog/diagnostic-identities-RCLI0002-through-RCLI0017-are-frozen")]
    public ValueTask FrozenDiagnosticIdentities()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Capture(builder => ValidCommand(builder, "Bad"), seen);
        Capture(builder => ValidCommand(builder, "ok").Describe(""), seen);
        Capture(builder => { ValidCommand(builder, "one").Alias("same"); ValidCommand(builder, "two").Alias("same"); }, seen);
        Capture(builder => ValidCommand(builder, "ok").Option("Bad", "--good", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("same", "--one", CommandArity.Zero).Option("same", "--two", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("one", "--same", CommandArity.Zero).Option("two", "--same", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("one", "--help", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("one", "--one", CommandArity.Zero, (CommandOptionRepeatPolicy)999), seen);
        Capture(builder => builder.Command<TestOptions, TestHandler, TestResult>("ok"), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("Bad", "Bad", CommandArity.ZeroOrOne), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("same", "one", CommandArity.ZeroOrOne).Argument("same", "two", CommandArity.ZeroOrOne), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("optional", "optional", CommandArity.ZeroOrOne).Argument("required", "required", CommandArity.ExactlyOne), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("many", "many", CommandArity.ZeroOrMore).Argument("tail", "tail", CommandArity.ZeroOrOne), seen);

        var invalidCodecBuilder = new CommandCatalogBuilder();
        invalidCodecBuilder.Command<TestOptions, TestHandler, TestResult>("ok")
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new InvalidCodec(string.Empty));
        Capture(invalidCodecBuilder, seen);

        AssertEx.SequenceEqual(
            Enumerable.Range(2, 16).Select(static number => $"RCLI{number:0000}"),
            seen.OrderBy(static code => code, StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }

    private static void Capture(
        Action<CommandCatalogBuilder> configure,
        HashSet<string> seen)
    {
        var builder = new CommandCatalogBuilder();
        configure(builder);
        Capture(builder, seen);
    }

    private static void Capture(CommandCatalogBuilder builder, HashSet<string> seen)
    {
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        foreach (CommandCatalogIssue issue in exception.Issues)
        {
            seen.Add(issue.Code);
        }
    }

    private sealed class InvalidCodec : ICommandResultCodec<TestResult>
    {
        public InvalidCodec(string payloadType) => PayloadType = payloadType;

        public string PayloadType { get; }

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo => TestJsonContext.Default.TestResult;

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class MutableCodec : ICommandResultCodec<TestResult>
    {
        public MutableCodec(
            string payloadType,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> typeInfo)
        {
            PayloadType = payloadType;
            TypeInfo = typeInfo;
        }

        public string PayloadType { get; set; }

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo { get; set; }

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NullTypeInfoCodec : ICommandResultCodec<TestResult>
    {
        public string PayloadType => "valid/1";

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo => null!;

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingCodec : ICommandResultCodec<TestResult>
    {
        private readonly bool _throwPayloadType;

        public ThrowingCodec(bool throwPayloadType) => _throwPayloadType = throwPayloadType;

        public string PayloadType => _throwPayloadType
            ? throw new InvalidOperationException("payload getter")
            : "valid/1";

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo => !_throwPayloadType
            ? throw new InvalidOperationException("metadata getter")
            : TestJsonContext.Default.TestResult;

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    internal static CommandBuilder<TestOptions, TestHandler, TestResult> ValidCommand(
        CommandCatalogBuilder builder,
        string name,
        TestBinder? binder = null,
        TestHandlerFactory? handlerFactory = null,
        TestCodec? codec = null) =>
        builder.Command<TestOptions, TestHandler, TestResult>(name)
            .BindWith(binder ?? new TestBinder())
            .CreateHandlerWith(handlerFactory ?? new TestHandlerFactory())
            .Produces(codec ?? new TestCodec());
}
