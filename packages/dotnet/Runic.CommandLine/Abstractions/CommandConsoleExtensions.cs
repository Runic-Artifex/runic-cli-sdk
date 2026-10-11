using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>String and line conveniences over <see cref="ICommandConsole"/>.</summary>
/// <remarks>Lines end with LF on every platform, matching the framework's own output.</remarks>
public static class CommandConsoleExtensions
{
    /// <summary>Writes text to standard output without adding a terminator.</summary>
    public static ValueTask WriteOutAsync(this ICommandConsole console, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(value);
        return console.WriteOutAsync(value.AsMemory(), cancellationToken);
    }

    /// <summary>Writes text followed by LF to standard output.</summary>
    public static ValueTask WriteOutLineAsync(this ICommandConsole console, string value = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(value);
        return console.WriteOutAsync((value + "\n").AsMemory(), cancellationToken);
    }

    /// <summary>Writes text to standard error without adding a terminator.</summary>
    public static ValueTask WriteErrorAsync(this ICommandConsole console, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(value);
        return console.WriteErrorAsync(value.AsMemory(), cancellationToken);
    }

    /// <summary>Writes text followed by LF to standard error.</summary>
    public static ValueTask WriteErrorLineAsync(this ICommandConsole console, string value = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(value);
        return console.WriteErrorAsync((value + "\n").AsMemory(), cancellationToken);
    }
}
