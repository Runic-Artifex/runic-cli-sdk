using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>
/// Selects how JSON output treats outcomes without a document, standard error and non-ASCII text. Set it on
/// <see cref="CommandApp.JsonOutput"/>; the default writes what earlier releases wrote.
/// </summary>
/// <remarks>
/// The options apply to every invocation in JSON mode, whether <c>--format json</c>, <c>RUNIC_COMMANDLINE_OUTPUT=json</c>
/// or a JSON output alias selected it. Human output is unchanged.
/// </remarks>
public sealed class CommandJsonOutputOptions
{
    /// <summary>Gets the options that keep the behaviour of earlier releases.</summary>
    public static CommandJsonOutputOptions Default { get; } = new();

    /// <summary>
    /// Gets whether a JSON output alias of payload shape (<see cref="JsonOutputShape.Payload"/>) writes one JSON
    /// document to standard output for every outcome. The default is false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The envelope shape already does: usage, validation and domain failures, cancellation and host failures each
    /// write one <c>runic.commandline/1</c> envelope. The payload shape writes the bare success payload and the
    /// declared failure data, and nothing otherwise. When this is true, a payload-shape invocation that would write
    /// no document writes one instead:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A usage error (also one the parser reports for <c>tool --bogus --json</c>), a binding or
    /// validation failure, a domain failure without <see cref="CommandOutcome{T}.FailureData"/>, cancellation and a
    /// host failure write the <c>runic.commandline/1</c> envelope, with the fault code and message, the diagnostics and
    /// the exit code. A caller tells it from the command's own documents by its <c>protocol</c> member.</description></item>
    /// <item><description>Help and version write their text as a JSON string, as a command with a string result does.</description></item>
    /// <item><description>A command whose result is <see cref="CommandUnit"/> writes its payload, <c>{}</c>.</description></item>
    /// </list>
    /// <para>
    /// Declared failure data is still written as the bare document. Standard error is unchanged; combine this
    /// with <see cref="QuietStandardError"/> so that standard error stays empty.
    /// </para>
    /// </remarks>
    public bool DocumentEveryOutcome { get; init; }

    /// <summary>
    /// Gets whether a JSON invocation writes nothing to standard error, so the document on standard output is the
    /// only output. The default is false.
    /// </summary>
    /// <remarks>
    /// When true, JSON mode drops the human text it otherwise writes to standard error: the diagnostics, fault line,
    /// failure text (<see cref="CommandOutcome{T}.HumanOutput"/>), usage line and hints of the payload shape, and the text
    /// a command writes to its console, which JSON mode moves from standard output to standard error. Diagnostics and
    /// the fault remain in the envelope; the payload shape has no place for them, so use it with
    /// <see cref="DocumentEveryOutcome"/> or declared failure data. Developer output that
    /// <c>RUNIC_COMMANDLINE_DEBUG</c> turns on is still written.
    /// </remarks>
    public bool QuietStandardError { get; init; }

    /// <summary>
    /// Gets how JSON output writes characters outside ASCII. The default, <see cref="CommandJsonEscaping.Unicode"/>,
    /// writes them as UTF-8.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public CommandJsonEscaping Escaping
    {
        get => _escaping;
        init => _escaping = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    private readonly CommandJsonEscaping _escaping;

    internal bool QuietFor(CommandOutputMode mode) => QuietStandardError && mode == CommandOutputMode.Json;
}

/// <summary>Selects how JSON output writes characters outside ASCII.</summary>
/// <remarks>Open: a later minor release may add values, so code that switches over them must handle an unknown value.</remarks>
public enum CommandJsonEscaping
{
    /// <summary>
    /// Letters and symbols from every script are written as UTF-8, so <c>Räume</c> stays readable. Control,
    /// HTML-sensitive, bidirectional and zero-width formatting characters are escaped.
    /// </summary>
    Unicode = 0,

    /// <summary>
    /// Every character outside ASCII is written as a <c>\uXXXX</c> escape, so <c>Räume</c> is <c>R\u00E4ume</c> and
    /// standard output is pure ASCII. Every JSON reader decodes the same text, and a caller that decodes standard
    /// output with a legacy code page, such as Windows PowerShell 5.1 with its default
    /// <c>[Console]::OutputEncoding</c>, reads it correctly.
    /// </summary>
    Ascii = 1,
}

/// <summary>Rewrites a UTF-8 JSON document as pure ASCII.</summary>
internal static class CommandJsonAscii
{
    /// <summary>
    /// Replaces every character outside ASCII with its <c>\uXXXX</c> escape (a surrogate pair beyond the Basic
    /// Multilingual Plane). Valid JSON holds such characters only inside strings, where the escape means the same
    /// character, so the document keeps its meaning.
    /// </summary>
    internal static byte[] Escape(byte[] utf8)
    {
        ReadOnlySpan<byte> rest = utf8;
        int next = rest.IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
        if (next < 0) return utf8;

        var output = new ArrayBufferWriter<byte>(utf8.Length + 64);
        Span<char> units = stackalloc char[2];
        Span<byte> escape = stackalloc byte[6];
        while (next >= 0)
        {
            output.Write(rest[..next]);
            rest = rest[next..];
            if (Rune.DecodeFromUtf8(rest, out Rune rune, out int consumed) != OperationStatus.Done)
                throw new InvalidOperationException("A JSON document contained invalid UTF-8.");

            int count = rune.EncodeToUtf16(units);
            for (int index = 0; index < count; index++)
            {
                escape[0] = (byte)'\\';
                escape[1] = (byte)'u';
                ((int)units[index]).TryFormat(escape[2..], out _, "X4", CultureInfo.InvariantCulture);
                output.Write(escape);
            }

            rest = rest[consumed..];
            next = rest.IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
        }

        output.Write(rest);
        return output.WrittenSpan.ToArray();
    }
}

// A JSON invocation under CommandJsonOutputOptions.QuietStandardError: standard output is unchanged, and
// standard error receives nothing.
internal sealed class StandardErrorDiscardingConsole(ICommandConsole inner) : ICommandConsole
{
    public bool IsInteractive => inner.IsInteractive;
    public bool IsInputRedirected => inner.IsInputRedirected;
    public bool IsOutputRedirected => inner.IsOutputRedirected;
    public bool IsErrorRedirected => inner.IsErrorRedirected;
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => inner.ReadLineAsync(cancellationToken);
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteOutAsync(value, cancellationToken);
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.WriteOutBytesAsync(value, cancellationToken);
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
