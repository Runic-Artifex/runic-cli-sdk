using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Runic.CommandLine;

/// <summary>
/// Expands <c>@file</c> arguments for a catalog that declares <see cref="CommandCatalogBuilder.ResponseFiles"/>,
/// before the grammar sees the arguments.
/// </summary>
/// <remarks>
/// A response file is strict UTF-8 with an optional byte order mark. Lines whose first non-blank character is
/// <c>#</c> are comments. Tokens are separated by white space and quoted with the MSVCRT rules: a double quote
/// toggles quoting, backslashes are literal unless they precede a quote, 2n backslashes and a quote yield n
/// backslashes and a delimiter, 2n+1 yield n backslashes and a literal quote. A token that starts with <c>@</c> is
/// itself a response file, resolved against the directory of the file that names it; <c>@@</c> stands for a
/// literal leading <c>@</c> and a lone <c>@</c> is literal. Files nest at most 16 levels and may not include
/// themselves. Expansion is bounded: a file holds at most 1 MiB, and all files together at most 4 MiB and 65,536
/// arguments, so that files naming each other several times cannot grow the arguments exponentially. A file is
/// read in chunks until a bound is exceeded, so a pipe such as <c>@/dev/stdin</c> works and <c>/dev/zero</c> fails
/// fast. A file that breaks a bound is unreadable (<c>RCLI1017</c>).
/// </remarks>
internal static class CommandResponseFiles
{
    internal const int MaximumDepth = 16;
    internal const int MaximumFileBytes = 1 << 20;
    internal const int MaximumTotalBytes = 4 << 20;
    internal const int MaximumTokens = 65_536;
    internal const string UnreadableCode = "RCLI1017";
    internal const string InvalidUtf8Code = "RCLI1018";
    internal const string UnterminatedQuoteCode = "RCLI1019";
    internal const string TooDeepCode = "RCLI1020";
    internal const string CycleCode = "RCLI1021";
    internal const string UnreadableKind = "response-file-unreadable";
    internal const string InvalidUtf8Kind = "response-file-invalid-utf8";
    internal const string UnterminatedQuoteKind = "response-file-unterminated-quote";
    internal const string TooDeepKind = "response-file-too-deep";
    internal const string CycleKind = "response-file-cycle";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>Whether any argument would be expanded or unescaped; otherwise the arguments are used as given.</summary>
    internal static bool AnyReference(IReadOnlyList<string> arguments)
    {
        foreach (string argument in arguments)
            if (argument is { Length: > 1 } && argument[0] == '@') return true;
        return false;
    }

    /// <summary>
    /// Expands the arguments, resolving top-level references against <paramref name="baseDirectory"/>. Returns
    /// null and the failure when a response file cannot be used.
    /// </summary>
    internal static string[]? Expand(IReadOnlyList<string> arguments, string baseDirectory, out ResponseFileFailure? failure)
    {
        failure = null;
        var result = new List<string>(arguments.Count);
        var activeFiles = new HashSet<string>(PathComparer);
        var budget = new Budget();
        for (int index = 0; index < arguments.Count; index++)
        {
            ArgumentNullException.ThrowIfNull(arguments[index], nameof(arguments));
            failure = ExpandArgument(arguments[index], baseDirectory, 0, activeFiles, budget, result, index);
            if (failure is not null) return null;
        }

        return result.ToArray();
    }

    private static ResponseFileFailure? ExpandArgument(
        string argument,
        string baseDirectory,
        int depth,
        HashSet<string> activeFiles,
        Budget budget,
        List<string> result,
        int tokenIndex)
    {
        if (argument.StartsWith("@@", StringComparison.Ordinal))
        {
            result.Add(argument[1..]);
            return null;
        }

        if (argument.Length < 2 || argument[0] != '@')
        {
            result.Add(argument);
            return null;
        }

        string reference = argument[1..];
        if (depth == MaximumDepth) return new(TooDeepCode, TooDeepKind, tokenIndex, reference);

        string path;
        try { path = Path.GetFullPath(reference, baseDirectory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(UnreadableCode, UnreadableKind, tokenIndex, reference);
        }

        if (!activeFiles.Add(path)) return new(CycleCode, CycleKind, tokenIndex, reference);
        try
        {
            byte[]? bytes;
            try { bytes = ReadBounded(path, Math.Min(MaximumFileBytes, MaximumTotalBytes - budget.Bytes)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                bytes = null;
            }

            if (bytes is null) return new(UnreadableCode, UnreadableKind, tokenIndex, reference);
            budget.Bytes += bytes.Length;
            string content;
            try { content = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { return new(InvalidUtf8Code, InvalidUtf8Kind, tokenIndex, reference); }

            List<string>? tokens = Tokenize(content);
            if (tokens is null) return new(UnterminatedQuoteCode, UnterminatedQuoteKind, tokenIndex, reference);
            budget.Tokens += tokens.Count;
            if (budget.Tokens > MaximumTokens) return new(UnreadableCode, UnreadableKind, tokenIndex, reference);
            string directory = Path.GetDirectoryName(path) ?? baseDirectory;
            foreach (string token in tokens)
            {
                ResponseFileFailure? failure = ExpandArgument(token, directory, depth + 1, activeFiles, budget, result, tokenIndex);
                if (failure is not null) return failure;
            }

            return null;
        }
        finally
        {
            activeFiles.Remove(path);
        }
    }

    // Reads in chunks and stops as soon as the file holds more than limit bytes, so /dev/zero or an endless pipe
    // ends with RCLI1017 instead of exhausting memory. A pipe or FIFO is read like a file, as cat reads it; a FIFO
    // without a writer blocks until one opens it.
    private static byte[]? ReadBounded(string path, int limit)
    {
        if (path.Contains('\0', StringComparison.Ordinal)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
        var buffer = new byte[Math.Min(limit, 64 * 1024) + 1];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (length > limit) return null;
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, limit + 1));
            }

            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0) break;
            length += read;
            if (length > limit) return null;
        }

        Array.Resize(ref buffer, length);
        return buffer;
    }

    private sealed class Budget
    {
        internal int Bytes;
        internal int Tokens;
    }

    // Returns null for an unterminated quote.
    internal static List<string>? Tokenize(string content)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        bool atLineStart = true;
        for (int index = content.Length > 0 && content[0] == '﻿' ? 1 : 0; index < content.Length; index++)
        {
            char value = content[index];
            if (!inQuotes && atLineStart && value == '#')
            {
                while (index < content.Length && content[index] is not '\r' and not '\n') index++;
                atLineStart = true;
                continue;
            }

            if (value == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                atLineStart = false;
                continue;
            }

            // MSVCRT rules: backslashes are literal unless they precede a quote, so UNC paths
            // (\\server\share) survive. 2n backslashes and a quote yield n backslashes and a
            // delimiter; 2n+1 yield n backslashes and a literal quote.
            if (value == '\\')
            {
                int count = 0;
                while (index + count < content.Length && content[index + count] == '\\') count++;
                bool beforeQuote = index + count < content.Length && content[index + count] == '"';
                token.Append('\\', beforeQuote ? count / 2 : count);
                index += count - 1;
                if (beforeQuote && count % 2 == 1)
                {
                    token.Append('"');
                    index++;
                }

                hasToken = true;
                atLineStart = false;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(value))
            {
                if (hasToken)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    hasToken = false;
                }

                if (value is '\r' or '\n') atLineStart = true;
                continue;
            }

            token.Append(value);
            hasToken = true;
            atLineStart = false;
        }

        if (inQuotes) return null;
        if (hasToken) tokens.Add(token.ToString());
        return tokens;
    }
}

/// <summary>Why a response file could not be used, at the top-level argument that named it.</summary>
internal sealed record ResponseFileFailure(string Code, string Kind, int TokenIndex, string Reference);
