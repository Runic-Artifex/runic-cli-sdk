using System;
using System.Globalization;
using System.Text;

namespace Runic.CommandLine;

/// <summary>Measures and cuts text in terminal columns, by text element, so redrawn lines never wrap.</summary>
/// <remarks>
/// The widths follow the help formatter's table (Unicode UAX #11 wide and fullwidth ranges, emoji with default emoji
/// presentation as two columns, combining marks and format characters as none). An element is as wide as its first
/// scalar, except that a variation selector 16 (emoji presentation) or a regional-indicator pair (a flag) makes it two.
/// </remarks>
internal static class TerminalCells
{
    internal static int Width(string text)
    {
        int width = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width += ElementWidth(elements.GetTextElement());
        return width;
    }

    /// <summary>The width of text that may contain the SGR sequences the prompts write.</summary>
    internal static int VisibleWidth(string line)
    {
        if (line.IndexOf('\u001b', StringComparison.Ordinal) < 0) return Width(line);
        var builder = new StringBuilder(line.Length);
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '\u001b' && index + 1 < line.Length && line[index + 1] == '[')
            {
                index += 2;
                while (index < line.Length && line[index] is < '@' or > '~') index++;
                continue;
            }

            builder.Append(line[index]);
        }

        return Width(builder.ToString());
    }

    /// <summary>Returns the longest prefix of whole text elements that fits in <paramref name="columns"/>.</summary>
    internal static string Cut(string text, int columns, out int width)
    {
        width = 0;
        int end = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int next = ElementWidth(element);
            if (width + next > columns) break;
            width += next;
            end = elements.ElementIndex + element.Length;
        }

        return text[..end];
    }

    internal static int ElementWidth(string element)
    {
        if (element.Length == 0) return 0;
        if (Rune.DecodeFromUtf16(element, out Rune first, out int consumed) != System.Buffers.OperationStatus.Done) return 1;
        int width = CommandHelpFormatter.CellWidth(first);
        if (width == 0) return element.Length == consumed ? 0 : Width(element[consumed..]);
        if (width == 1 && (element.Contains('️', StringComparison.Ordinal) || (first.Value is >= 0x1F1E6 and <= 0x1F1FF && element.Length > consumed))) return 2;
        return width;
    }
}
