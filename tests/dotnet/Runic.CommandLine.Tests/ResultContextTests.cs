using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static class ResultContextTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("app/generated-result-preserves-json-context-options", JsonOptions),
        new("app/generated-human-result-preserves-json-context-options", HumanOptions),
    ];

    private static async ValueTask JsonOptions()
    {
        var console = new TestCommandConsole();
        var app = new CommandApp(GeneratedCommandCatalog.Create()) { Console = console, HandleCancelKeyPress = false };
        AssertEx.Equal(0, await app.RunAsync(["result-context", "--output=json"]));
        using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
        JsonElement payload = frame.RootElement.GetProperty("payload");
        JsonElement expected = JsonSerializer.SerializeToElement(ResultContextCommands.Result(), ResultOptionsContext.Default.ContextResult);
        AssertEx.Equal(expected.GetRawText(), payload.GetRawText());
        AssertEx.Equal("Ready", payload.GetProperty("displayName").GetString());
        AssertEx.True(!payload.TryGetProperty("optionalNote", out _));
        AssertEx.Equal(1, payload.EnumerateObject().Count());
    }

    private static async ValueTask HumanOptions()
    {
        var console = new TestCommandConsole();
        var app = new CommandApp(GeneratedCommandCatalog.Create()) { Console = console, HandleCancelKeyPress = false };
        AssertEx.Equal(0, await app.RunAsync(["result-context"]));
        AssertEx.Equal("displayName: Ready\n", console.StandardOutput);
    }
}

internal static class ResultContextCommands
{
    [Command("result-context"), CommandResult("sample.context-options/1", typeof(ResultOptionsContext))]
    internal static ContextResult Result() => new("Ready", null);
}

internal sealed record ContextResult(string DisplayName, string? OptionalNote);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ContextResult))]
internal sealed partial class ResultOptionsContext : JsonSerializerContext;
