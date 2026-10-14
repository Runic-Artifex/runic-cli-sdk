using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>
/// An optional capability of an <see cref="ICommandConsole"/>: single-key input and unstyled output on standard error,
/// which <see cref="CommandPrompts"/> uses to redraw prompts, spinners and task logs in place.
/// </summary>
/// <remarks>
/// Prompts redraw only when the console is interactive and <see cref="SupportsAnsi"/> is true. Otherwise they ask one
/// plain question per line through <see cref="ICommandConsole.ReadLineAsync"/>, which suits screen readers, logs and
/// <c>TERM=dumb</c>.
/// </remarks>
public interface ICommandTerminal
{
    /// <summary>
    /// Gets whether standard input and standard error are a terminal that accepts ANSI cursor movement and colour.
    /// False when either is redirected, when <c>NO_COLOR</c> is set or when <c>TERM</c> is <c>dumb</c>.
    /// </summary>
    bool SupportsAnsi { get; }

    /// <summary>Gets whether the terminal can show Unicode symbols; otherwise prompts use ASCII.</summary>
    bool SupportsUnicode { get; }

    /// <summary>Gets the terminal width in columns, read for every frame. Redrawn lines are cut to fit so they never wrap.</summary>
    int Width { get; }

    /// <summary>Gets the terminal height in rows, read for every frame. A redrawn block stays at least one row shorter.</summary>
    int Height { get; }

    /// <summary>Waits for one key press without echoing it.</summary>
    /// <param name="cancellationToken">Cancels the wait, for example after Ctrl+C.</param>
    ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes characters, including ANSI control sequences, to standard error without styling or wrapping them.
    /// Consoles implement this member explicitly, so it does not stand beside their standard-output writes.
    /// </summary>
    /// <param name="value">The characters to write.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken);
}
