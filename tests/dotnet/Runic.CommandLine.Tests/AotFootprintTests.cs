using System.Reflection;
using System.Text.Json;
using Runic.CommandLine.Hosting;

namespace Runic.CommandLine.Tests;

// The framework validates its own enums with explicit range checks and writes CommandUnit with a value converter, so a
// NativeAOT executable carries no enum-metadata lookups or object metadata for them. These cases keep the behaviour.
internal sealed class AotFootprintTests
{
    [Test, DisplayName("aot-footprint/enum-range-checks-match-the-declared-members")]
    public ValueTask EnumRangeChecksMatchDeclaredMembers()
    {
        Type ranges = typeof(CommandApp).Assembly.GetType("Runic.CommandLine.CommandEnums", throwOnError: true)!;
        MethodInfo[] checks = ranges.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(static method => method.Name == "IsDefined")
            .ToArray();
        Type[] expected =
        [
            typeof(CommandOutputMode), typeof(JsonOutputShape), typeof(CommandDiagnosticPhase), typeof(CommandDiagnosticSeverity),
            typeof(CommandExitCategory), typeof(CommandPathKind), typeof(CommandOptionRepeatPolicy), typeof(CommandJsonEscaping),
            typeof(CommandSyntaxDialect), typeof(CommandHelpOrder), typeof(EmptyInputFallback), typeof(HostedCommandLineDecisionKind),
        ];
        AssertEx.SequenceEqual(
            expected.Select(static type => type.FullName!).Order(StringComparer.Ordinal),
            checks.Select(static method => method.GetParameters()[0].ParameterType.FullName!).Order(StringComparer.Ordinal));

        foreach (MethodInfo check in checks)
        {
            Type type = check.GetParameters()[0].ParameterType;
            int[] values = Enum.GetValues(type).Cast<object>().Select(static value => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
            // A range check equals Enum.IsDefined only for members numbered 0..n-1.
            AssertEx.SequenceEqual(Enumerable.Range(0, values.Length), values, type.Name);
            foreach (int value in values)
                AssertEx.True((bool)check.Invoke(null, [Enum.ToObject(type, value)])!, $"{type.Name} {value}");
            AssertEx.True(!(bool)check.Invoke(null, [Enum.ToObject(type, -1)])!, $"{type.Name} -1");
            AssertEx.True(!(bool)check.Invoke(null, [Enum.ToObject(type, values.Length)])!, $"{type.Name} {values.Length}");
        }

        AssertEx.Equal(Enum.GetValues<CommandExitCategory>().Max(), (CommandExitCategory)ranges.GetField("LastExitCategory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("aot-footprint/public-validation-rejects-undefined-enum-values")]
    public ValueTask PublicValidationRejectsUndefinedValues()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new CommandJsonOutputOptions { Escaping = (CommandJsonEscaping)2 });
        AssertEx.Equal(CommandJsonEscaping.Ascii, new CommandJsonOutputOptions { Escaping = CommandJsonEscaping.Ascii }.Escaping);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => new CommandCatalogBuilder().Syntax((CommandSyntaxDialect)2));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => new CommandCatalogBuilder().HelpOrder((CommandHelpOrder)(-1)));
        _ = new CommandCatalogBuilder().Syntax(CommandSyntaxDialect.WindowsSlash).HelpOrder(CommandHelpOrder.Declaration);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new HostedCommandLineLaunchInput([], emptyInputFallback: (EmptyInputFallback)2));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new HostedCommandLineLaunchInput([], defaultOutputMode: (CommandOutputMode)2));
        _ = new HostedCommandLineLaunchInput([], emptyInputFallback: EmptyInputFallback.UserInterface, defaultOutputMode: CommandOutputMode.Json);
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CommandOutcome.Failure<string>((CommandExitCategory)7, new CommandFault("X", "x")));
        AssertEx.Equal(CommandExitCategory.HostFailure, CommandOutcome.Failure<string>(CommandExitCategory.HostFailure, new CommandFault("X", "x")).ExitCategory);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("aot-footprint/exit-policy-is-checked-for-every-category")]
    public ValueTask ExitPolicyCheckedForEveryCategory()
    {
        // The last category is checked too: a policy that maps only HostFailure to zero is rejected.
        AssertEx.Throws<ArgumentException>(() => _ = new CommandExecutor(new TrackingScopeFactory(), new ZeroForPolicy(CommandExitCategory.HostFailure)));
        AssertEx.Throws<ArgumentException>(() => _ = new CommandExecutor(new TrackingScopeFactory(), new ZeroForPolicy(CommandExitCategory.Usage)));
        _ = new CommandExecutor(new TrackingScopeFactory(), new ZeroForPolicy(CommandExitCategory.Success));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("aot-footprint/unit-payload-json-is-an-empty-object")]
    public ValueTask UnitPayloadJson()
    {
        var typeInfo = CommandResultCodecs.Unit.TypeInfo;
        AssertEx.True(ReferenceEquals(typeInfo, CommandResultCodecs.Unit.TypeInfo), "The unit metadata is cached.");
        AssertEx.Equal("{}", JsonSerializer.Serialize(default(CommandUnit), typeInfo));
        AssertEx.Equal(default, JsonSerializer.Deserialize("{}", typeInfo));
        // As with object metadata, unknown members are ignored and anything but an object is rejected.
        AssertEx.Equal(default, JsonSerializer.Deserialize("""{"extra":[1,{"a":null}]}""", typeInfo));
        foreach (string json in new[] { "null", "1", "[]", "\"\"", "true" })
            AssertEx.Throws<JsonException>(() => JsonSerializer.Deserialize(json, typeInfo));
        return ValueTask.CompletedTask;
    }

    private sealed class ZeroForPolicy(CommandExitCategory zero) : IExitCodePolicy
    {
        public int GetExitCode(CommandExitCategory category) =>
            category == zero ? 0 : category == CommandExitCategory.Success ? 0 : 1;
    }
}
