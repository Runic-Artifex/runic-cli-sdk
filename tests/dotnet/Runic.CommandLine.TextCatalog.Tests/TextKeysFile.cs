using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Runic.CommandLine.TextCatalog.Tests;

/// <summary>One entry of the generated text-keys.json.</summary>
internal sealed record TextKeyEntry(string Key, IReadOnlyList<string> Arguments, string? Message);

/// <summary>The text-keys.json that Runic.CommandLine writes from <see cref="CommandTextKeys.All"/> and packs.</summary>
internal sealed class TextKeysFile
{
    private TextKeysFile(JsonElement root, IReadOnlyList<TextKeyEntry> keys)
    {
        Root = root;
        Keys = keys;
    }

    public JsonElement Root { get; }

    public IReadOnlyList<TextKeyEntry> Keys { get; }

    /// <summary>The keys the built-in catalog translates, by message name.</summary>
    public IReadOnlyDictionary<string, TextKeyEntry> ByMessage =>
        Keys.Where(static entry => entry.Message is not null).ToDictionary(static entry => entry.Message!, StringComparer.Ordinal);

    public static TextKeysFile Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "catalog", "text-keys.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement.Clone();
        TextKeyEntry[] keys = root.GetProperty("keys").EnumerateArray().Select(static item => new TextKeyEntry(
            item.GetProperty("key").GetString()!,
            item.GetProperty("arguments").EnumerateArray().Select(static argument => argument.GetString()!).ToArray(),
            item.GetProperty("message").ValueKind == JsonValueKind.Null ? null : item.GetProperty("message").GetString())).ToArray();
        return new TextKeysFile(root, keys);
    }
}
