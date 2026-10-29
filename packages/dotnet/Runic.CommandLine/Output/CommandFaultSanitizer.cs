using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace Runic.CommandLine;

internal static class CommandFaultSanitizer
{
    private const int MaximumCodeLength = 64;
    private const int MaximumMessageLength = 4_096;
    private const int MaximumDetailCount = 32;
    private const int MaximumDetailKeyLength = 64;
    private const int MaximumDetailValueLength = 1_024;

    internal const string RedactedFaultMessage = "The command failed; details were redacted.";

    internal static CommandFault Sanitize(CommandFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        // A well-formed code is a stable identity, not presentation text, so it
        // survives an unsafe message and only the message is replaced. A code
        // that is not well-formed cannot be trusted and collapses to RCLI5000.
        string code = fault.Code;
        if (!IsSafeCode(code))
        {
            return SoftwareFailure();
        }

        string message = SanitizeScalar(fault.Message, MaximumMessageLength);
        if (string.IsNullOrWhiteSpace(message) || ContainsTechnicalContent(fault.Message))
        {
            message = RedactedFaultMessage;
        }

        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> detail in fault.Details)
        {
            if (details.Count == MaximumDetailCount)
            {
                break;
            }

            string key = SanitizeScalar(detail.Key, MaximumDetailKeyLength);
            // A key is an identifier, not a redactable value: a key with
            // technical content is dropped together with its value.
            if (string.IsNullOrWhiteSpace(key) || details.ContainsKey(key) ||
                ContainsTechnicalContent(detail.Key) || ContainsTechnicalContent(key))
            {
                continue;
            }

            string value = ContainsTechnicalContent(detail.Value)
                ? "[redacted]"
                : SanitizeScalar(detail.Value, MaximumDetailValueLength);
            details.Add(key, value);
        }

        // The help link was validated when it was set and is the author's fixed address,
        // not presentation text, so it survives a redacted message.
        return new CommandFault(code, message, details, fault.Retryable) { HelpUri = fault.HelpUri };
    }

    internal static string SanitizeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SanitizeScalar(value, MaximumMessageLength);
    }

    internal static string SanitizeRequiredText(string value)
    {
        string sanitized = SanitizeText(value);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "The diagnostic message was redacted."
            : sanitized;
    }

    // Human output keeps the line breaks of a message, so that a message the author wrote on several lines reads as
    // written: \n and \r\n become \n, and leading and trailing line breaks are dropped because the writer ends the
    // message with its own. Every other control character becomes a space, as in SanitizeText; JSON output and
    // CommandResponse.Fault keep the single-line form the protocol requires.
    internal static string SanitizeHumanText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SanitizeScalar(value, MaximumMessageLength, keepLineBreaks: true).Trim('\n');
    }

    internal static string SanitizeRequiredHumanText(string value)
    {
        string sanitized = SanitizeHumanText(value);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "The diagnostic message was redacted."
            : sanitized;
    }

    internal static string SanitizeArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SanitizeScalar(value, MaximumDetailValueLength);
    }

    // The strict reader's check: text a writer would change is unsafe. The protocol's "Unicode control characters"
    // include the formatting characters of IsInvisibleFormatCharacter, so the reader rejects them like C0 and C1
    // controls (SEC-40).
    internal static bool IsSafeText(string value, int maximumUtf8Bytes, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (allowEmpty || value.Length != 0) &&
            string.Equals(value, SanitizeScalar(value, maximumUtf8Bytes), StringComparison.Ordinal) &&
            !ContainsTechnicalContent(value);
    }

    internal static bool IsSafeCode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumCodeLength ||
            value[0] is < 'A' or > 'Z')
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (character is not ('.' or '_' or '-' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a code point can spoof text on a terminal or in a viewer, as in Trojan Source: the bidirectional
    /// embeddings, overrides and isolates (U+202A–U+202E, U+2066–U+2069), the invisible tag characters
    /// (U+E0000–U+E007F), and the zero-width space and no-break spaces that hide a break inside a word
    /// (U+200B, U+2060, U+FEFF). The protocol counts them among the Unicode control characters: writers replace
    /// them like control characters, and the strict reader and request ID validation reject them. ZWNJ, ZWJ,
    /// LRM, RLM and ALM (U+200C–U+200F, U+061C) are kept: Persian, Indic and right-to-left text and emoji
    /// sequences need them. The JSON encoder still escapes all of them (see <see cref="CommandJsonEncoding"/>).
    /// </summary>
    internal static bool IsInvisibleFormatCharacter(int value) =>
        value is 0x200B or (>= 0x202A and <= 0x202E) or 0x2060 or (>= 0x2066 and <= 0x2069) or 0xFEFF or
            (>= 0xE0000 and <= 0xE007F);

    private static string SanitizeScalar(string value, int maximumLength, bool keepLineBreaks = false)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        bool previousWasSpace = false;
        int utf8Bytes = 0;
        int index = 0;
        while (index < value.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                value.AsSpan(index),
                out Rune rune,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }

            if (keepLineBreaks && (rune.Value == '\n' || (rune.Value == '\r' && index + 1 < value.Length && value[index + 1] == '\n')))
            {
                if (utf8Bytes == maximumLength)
                {
                    break;
                }

                // A line does not end in the space written for a control character or the author's trailing space.
                if (previousWasSpace && builder.Length > 0 && builder[^1] == ' ')
                {
                    builder.Length--;
                    utf8Bytes--;
                }

                builder.Append('\n');
                previousWasSpace = false;
                utf8Bytes++;
                index += rune.Value == '\r' ? 2 : 1;
                continue;
            }

            bool replaceWithSpace = Rune.IsControl(rune) ||
                rune.Value is 0x1B or 0x2028 or 0x2029 ||
                IsInvisibleFormatCharacter(rune.Value);
            if (replaceWithSpace)
            {
                if (!previousWasSpace)
                {
                    if (utf8Bytes == maximumLength)
                    {
                        break;
                    }

                    builder.Append(' ');
                    previousWasSpace = true;
                    utf8Bytes++;
                }

                index += consumed;
                continue;
            }

            int runeBytes = rune.Utf8SequenceLength;
            if (utf8Bytes + runeBytes > maximumLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            previousWasSpace = rune.Value == ' ';
            utf8Bytes += runeBytes;
            index += consumed;
        }

        return builder.ToString();
    }

    private static CommandFault SoftwareFailure() =>
        new("RCLI5000", "The command failed unexpectedly.");

    internal static bool ContainsTechnicalContent(string value)
    {
        if (value.Contains("Exception", StringComparison.Ordinal) ||
            value.Contains("\\\\", StringComparison.Ordinal) ||
            value.Contains("/home/", StringComparison.Ordinal) ||
            value.Contains("/Users/", StringComparison.Ordinal) ||
            value.Contains("/root/", StringComparison.Ordinal) ||
            value.Contains("/tmp/", StringComparison.Ordinal))
        {
            return true;
        }

        for (int index = 0; index + 2 < value.Length; index++)
        {
            if (((value[index] is >= 'A' and <= 'Z') || (value[index] is >= 'a' and <= 'z')) &&
                value[index + 1] == ':' && value[index + 2] is '\\' or '/')
            {
                return true;
            }
        }

        return false;
    }

}
