using System;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Runic.CommandLine.Testing;

/// <summary>Provides a deterministic in-memory console for command tests.</summary>
/// <remarks>
/// Script prompt answers with <see cref="QueueInput"/> on a console with <see cref="IsInteractive"/> set: each plain
/// prompt reads one line. To exercise redrawn prompts set <see cref="SupportsAnsi"/> too and queue keys with
/// <see cref="QueueKey(ConsoleKey)"/> and <see cref="QueueText"/>; their frames, ANSI sequences included, are captured in
/// <see cref="StandardError"/>.
/// </remarks>
public sealed class TestCommandConsole : ICommandConsole, ICommandTerminal
{
    private readonly Queue<string?> _input = new();
    private readonly Channel<ConsoleKeyInfo> _keys = Channel.CreateUnbounded<ConsoleKeyInfo>();

    /// <summary>Queues a line or EOF for an interactive test.</summary>
    public void QueueInput(string? line) => _input.Enqueue(line);

    /// <summary>Queues a key press for a redrawn prompt.</summary>
    public void QueueKey(ConsoleKeyInfo key) => _keys.Writer.TryWrite(key);

    /// <summary>Queues a key press without a character, such as <see cref="ConsoleKey.DownArrow"/>, <see cref="ConsoleKey.Enter"/> or <see cref="ConsoleKey.Escape"/>.</summary>
    public void QueueKey(ConsoleKey key) => QueueKey(new ConsoleKeyInfo(key switch
    {
        ConsoleKey.Enter => '\r',
        ConsoleKey.Spacebar => ' ',
        ConsoleKey.Escape => '\u001b',
        ConsoleKey.Backspace => '\b',
        ConsoleKey.Tab => '\t',
        _ => '\0',
    }, key, false, false, false));

    /// <summary>Queues one key press per character of <paramref name="text"/>; a line feed is Enter.</summary>
    public void QueueText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char character in text)
        {
            if (character == '\n') QueueKey(ConsoleKey.Enter);
            else
            {
                ConsoleKey key = char.IsAsciiLetter(character) ? ConsoleKey.A + (char.ToUpperInvariant(character) - 'A')
                    : char.IsAsciiDigit(character) ? ConsoleKey.D0 + (character - '0')
                    : character == ' ' ? ConsoleKey.Spacebar : ConsoleKey.NoName;
                QueueKey(new ConsoleKeyInfo(character, key, char.IsAsciiLetterUpper(character), false, false));
            }
        }
    }

    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();

    /// <summary>Gets captured standard output.</summary>
    public string StandardOutput => _standardOutput.ToString();

    /// <summary>Gets captured standard error.</summary>
    public string StandardError => _standardError.ToString();

    /// <inheritdoc />
    public bool IsInteractive { get; init; }
    /// <inheritdoc />
    public bool IsInputRedirected { get; init; } = true;
    /// <inheritdoc />
    public bool IsOutputRedirected { get; init; } = true;
    /// <inheritdoc />
    public bool IsErrorRedirected { get; init; } = true;

    /// <summary>Gets or sets whether prompts redraw with ANSI and read keys rather than lines. False by default.</summary>
    public bool SupportsAnsi { get; init; }
    /// <inheritdoc />
    public bool SupportsUnicode { get; init; } = true;
    /// <inheritdoc />
    public int Width { get; init; } = 80;

    /// <summary>Returns the next queued key, waiting until one is queued or <paramref name="cancellationToken"/> is cancelled.</summary>
    public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken) => _keys.Reader.ReadAsync(cancellationToken);

    /// <summary>Appends characters to <see cref="StandardError"/>.</summary>
    public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => WriteErrorAsync(value, cancellationToken);

    /// <inheritdoc />
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
    }

    /// <inheritdoc />
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _standardOutput.Append(value.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _standardOutput.Append(Encoding.UTF8.GetString(value.Span));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _standardError.Append(value.Span);
        return ValueTask.CompletedTask;
    }
}
