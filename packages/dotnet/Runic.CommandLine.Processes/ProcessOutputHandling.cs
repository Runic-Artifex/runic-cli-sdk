using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes;

/// <summary>Identifies one of a child's two output pipes.</summary>
public enum ProcessOutputChannel
{
    /// <summary>Standard output.</summary>
    StandardOutput = 0,

    /// <summary>Standard error.</summary>
    StandardError = 1,
}

/// <summary>One decoded line of live child output.</summary>
public readonly struct ProcessOutputLine
{
    private readonly string? text;

    /// <summary>Initializes a line.</summary>
    /// <param name="channel">The pipe the line was read from.</param>
    /// <param name="text">The line without its terminator.</param>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public ProcessOutputLine(ProcessOutputChannel channel, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Channel = channel;
        this.text = text;
    }

    /// <summary>Gets the pipe the line was read from.</summary>
    public ProcessOutputChannel Channel { get; }

    /// <summary>
    /// Gets the line, decoded with the channel's encoding, without its <c>\n</c> or <c>\r\n</c> terminator.
    /// </summary>
    public string Text => text ?? string.Empty;
}

/// <summary>One chunk of raw child output as it was read from the pipe.</summary>
public readonly struct ProcessOutputChunk
{
    /// <summary>Initializes a chunk.</summary>
    /// <param name="channel">The pipe the bytes were read from.</param>
    /// <param name="bytes">The bytes read.</param>
    public ProcessOutputChunk(ProcessOutputChannel channel, ReadOnlyMemory<byte> bytes)
    {
        Channel = channel;
        Bytes = bytes;
    }

    /// <summary>Gets the pipe the bytes were read from.</summary>
    public ProcessOutputChannel Channel { get; }

    /// <summary>
    /// Gets the bytes read. The memory is reused for the next read: copy what you keep before the returned task
    /// completes.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes { get; }
}

/// <summary>
/// Receives a child's output while it runs. Implement <see cref="OnLineAsync"/>, <see cref="OnChunkAsync"/> or both.
/// </summary>
/// <remarks>
/// <para>
/// Calls are never concurrent. Each chunk is delivered before the lines it completes. Within one channel, chunks and
/// lines arrive in the order the child wrote them. Standard output and standard error are separate pipes, so the
/// order between them is the order in which the runner read them: output written with a pause in between keeps its
/// order, but output written in the same instant, or held in the child's own buffer, may not.
/// </para>
/// <para>
/// The runner reads the next chunk of a channel only after the handler's task completes, so a slow handler slows the
/// child once the pipe buffer is full instead of growing memory. The capture in <see cref="ProcessResult"/> is
/// recorded independently, within its own limits.
/// </para>
/// </remarks>
public interface IProcessOutputHandler
{
    /// <summary>Called for each complete line, and for the final line when the channel ends without a terminator.</summary>
    /// <param name="line">The line.</param>
    /// <param name="cancellationToken">Cancelled when the request is cancelled or times out.</param>
    /// <returns>A task that completes when the runner may continue reading.</returns>
    ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Called for each chunk of bytes read from a pipe.</summary>
    /// <param name="chunk">The chunk; its memory is only valid until the returned task completes.</param>
    /// <param name="cancellationToken">Cancelled when the request is cancelled or times out.</param>
    /// <returns>A task that completes when the runner may continue reading.</returns>
    ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Identifies a started child process.</summary>
public sealed class ProcessIdentity
{
    internal ProcessIdentity(int processId, int? processGroupId)
    {
        ProcessId = processId;
        ProcessGroupId = processGroupId;
    }

    /// <summary>Gets the operating-system process ID.</summary>
    public int ProcessId { get; }

    /// <summary>
    /// Gets the Unix process group the child leads, which cancellation and timeout terminate as a whole; null on
    /// Windows, where the child runs in a job object instead, and for a child that shares the caller's terminal.
    /// </summary>
    public int? ProcessGroupId { get; }
}
