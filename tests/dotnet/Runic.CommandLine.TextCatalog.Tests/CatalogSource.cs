using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Runic.CommandLine.TextCatalog.Tests;

/// <summary>One message of a catalog source file: its declared inputs, the variables its text uses, and the text.</summary>
internal sealed record SourceMessage(string Name, int Line, IReadOnlyList<string> Inputs, IReadOnlyList<string> Variables, string Text);

/// <summary>
/// Reads one locale's source file of the runic-command-line catalog, which uses a small RMF2 subset: nested groups,
/// one-line messages, and messages with <c>.input</c> declarations and a quoted pattern. The compiler checks the syntax
/// at build time; this reader gives the tests each locale's own inputs and text, which the compiled catalog merges.
/// </summary>
internal static partial class CatalogSource
{
    public static IReadOnlyDictionary<string, SourceMessage> Load(string locale)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "catalog", locale + ".rmf2");
        string[] lines = File.ReadAllLines(file);
        var messages = new Dictionary<string, SourceMessage>(StringComparer.Ordinal);
        var groups = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            int number = index + 1;
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "}")
            {
                if (groups.Count == 0) throw new FormatException($"{locale}.rmf2:{number}: unexpected '}}'.");
                groups.RemoveAt(groups.Count - 1);
                continue;
            }

            if (Group().Match(line) is { Success: true } group)
            {
                groups.Add(group.Groups[1].Value);
                continue;
            }

            Match message = Message().Match(line);
            if (!message.Success) throw new FormatException($"{locale}.rmf2:{number}: '{line}' is outside the subset this catalog uses.");
            string name = string.Join('.', [.. groups, message.Groups[1].Value]);
            var inputs = new List<string>();
            string text = message.Groups[2].Value;
            if (text.Length == 0)
            {
                while (++index < lines.Length && Input().Match(lines[index].Trim()) is { Success: true } input) inputs.Add(input.Groups[1].Value);
                string pattern = index < lines.Length ? lines[index].Trim() : "";
                if (!pattern.StartsWith("{{", StringComparison.Ordinal) || !pattern.EndsWith("}}", StringComparison.Ordinal))
                    throw new FormatException($"{locale}.rmf2:{number}: {name} needs a {{{{...}}}} pattern after its .input lines.");
                text = pattern[2..^2];
            }

            var variables = new List<string>();
            foreach (Match variable in Variable().Matches(text))
                if (!variables.Contains(variable.Groups[1].Value)) variables.Add(variable.Groups[1].Value);
            if (!messages.TryAdd(name, new SourceMessage(name, number, inputs, variables, text)))
                throw new FormatException($"{locale}.rmf2:{number}: {name} is defined twice.");
        }

        if (groups.Count > 0) throw new FormatException($"{locale}.rmf2: group '{groups[^1]}' is not closed.");
        return messages;
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]*) \{$")]
    private static partial Regex Group();

    [GeneratedRegex(@"^([A-Za-z0-9]+) =\s*(.*)$")]
    private static partial Regex Message();

    [GeneratedRegex(@"^\.input \{\$([A-Za-z][A-Za-z0-9]*) :string\}$")]
    private static partial Regex Input();

    [GeneratedRegex(@"\{\$([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Variable();
}
