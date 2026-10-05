using System;

namespace Runic.CommandLine.Processes;

/// <summary>Specifies how the child receives standard input.</summary>
public enum ProcessStandardInputMode
{
    /// <summary>Inherits the caller's standard-input handle.</summary>
    Inherit = 0,
    /// <summary>Receives immediate end-of-file from a closed pipe.</summary>
    Closed = 1,
    /// <summary>Receives a bounded immutable byte payload followed by end-of-file.</summary>
    BoundedBytes = 2,
}

/// <summary>Describes immutable, bounded standard input for a child process.</summary>
public sealed class ProcessStandardInput
{
    /// <summary>The default accepted input payload limit.</summary>
    public const int DefaultInputLimitBytes = 1024 * 1024;

    /// <summary>The hard accepted input payload ceiling.</summary>
    public const int MaximumInputLimitBytes = 16 * 1024 * 1024;

    private readonly byte[] bytes;

    private ProcessStandardInput(ProcessStandardInputMode mode, byte[] bytes)
    {
        Mode = mode;
        this.bytes = bytes;
    }

    /// <summary>Gets an input configuration that inherits the caller's stdin.</summary>
    public static ProcessStandardInput Inherit { get; } = new(ProcessStandardInputMode.Inherit, []);

    /// <summary>Gets an input configuration that sends immediate end-of-file.</summary>
    public static ProcessStandardInput Closed { get; } = new(ProcessStandardInputMode.Closed, []);

    /// <summary>Gets the input mode.</summary>
    public ProcessStandardInputMode Mode { get; }

    /// <summary>Gets the retained input payload size in bytes.</summary>
    public int ByteCount => bytes.Length;

    internal ReadOnlyMemory<byte> Bytes => bytes;

    /// <summary>Copies a byte payload within the supplied bound, to be written before closing stdin.</summary>
    /// <param name="bytes">The complete input payload. Later caller mutations cannot change it.</param>
    /// <param name="limitBytes">The accepted payload limit, between zero and the hard ceiling.</param>
    /// <returns>An immutable input configuration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The limit is invalid or the payload exceeds it.</exception>
    public static ProcessStandardInput FromBytes(
        ReadOnlyMemory<byte> bytes,
        int limitBytes = DefaultInputLimitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limitBytes, MaximumInputLimitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, limitBytes, nameof(bytes));
        return new ProcessStandardInput(ProcessStandardInputMode.BoundedBytes, bytes.ToArray());
    }
}
