using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Runic.CommandLine;

/// <summary>The string escaping shared by every JSON frame the framework writes.</summary>
internal static class CommandJsonEncoding
{
    /// <summary>
    /// Writes letters and symbols from every script as UTF-8 so localized messages and payloads stay readable
    /// (<c>gültige</c>, not <c>g\u00FCltige</c>). Unlike <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c>, it
    /// still escapes the HTML-sensitive characters <c>&lt; &gt; &amp; ' " + `</c>, control characters, U+2028/U+2029
    /// and unassigned code points, so a frame stays valid and safe to embed in HTML or script. The bidirectional
    /// and zero-width formatting characters are escaped too, as with the default encoder, so a terminal or
    /// viewer cannot reorder or hide text in a frame.
    /// </summary>
    internal static JavaScriptEncoder Encoder { get; } = Create();

    private static JavaScriptEncoder Create()
    {
        var settings = new TextEncoderSettings(UnicodeRanges.All);
        settings.ForbidCharacters('\u061C', '\u200B', '\u200C', '\u200D', '\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
            '\u2060', '\u2066', '\u2067', '\u2068', '\u2069', '\uFEFF');
        return JavaScriptEncoder.Create(settings);
    }
}
