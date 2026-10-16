using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes;

/// <summary>
/// Writes the raw bytes of one output channel to a stream as the child writes them, for example to a file, and passes
/// all output on to another handler.
/// </summary>
/// <remarks>
/// <para>
/// Each chunk of the channel is written and flushed before the runner reads the next one, so a slow stream slows the
/// child instead of growing memory. When the child exited and its pipes closed, the stream holds the whole channel by
/// the time <see cref="ProcessRunner"/> returns its result; on cancellation or timeout, the write in progress receives
/// the cancelled token. The bytes are written unchanged, without decoding or line splitting,
/// so the stream receives long lines and invalid text exactly as the child wrote them. The capture in
/// <see cref="ProcessResult"/> is still recorded within its own limit; set the limit to <c>0</c> to keep only the
/// count.
/// </para>
/// <para>
/// A failing write or flush fails the handler: the runner terminates the child and returns
/// <see cref="ProcessState.ExecutionFailed"/> with <see cref="ProcessFaultCodes.OutputHandlerFailed"/>. The stream
/// belongs to the caller, which disposes it after the run; the handler never disposes it. To write both channels,
/// pass a second handler for the other channel as <c>next</c>. Calls are never concurrent, so both handlers may
/// share one stream.
/// </para>
/// </remarks>
public sealed class ProcessOutputStreamHandler : IProcessOutputHandler
{
    private readonly ProcessOutputChannel channel;
    private readonly Stream stream;
    private readonly IProcessOutputHandler? next;

    /// <summary>Initializes a handler that writes one channel to a stream.</summary>
    /// <param name="channel">The channel whose bytes are written.</param>
    /// <param name="stream">The writable stream that receives the bytes; the caller owns and disposes it.</param>
    /// <param name="next">
    /// An optional handler that receives every chunk, after it was written, and every line of both channels.
    /// </param>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentException">The stream is not writable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The channel is not defined.</exception>
    public ProcessOutputStreamHandler(ProcessOutputChannel channel, Stream stream, IProcessOutputHandler? next = null)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "The channel is not defined.");
        }

        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(stream));
        }

        this.channel = channel;
        this.stream = stream;
        this.next = next;
    }

    /// <inheritdoc />
    public async ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
    {
        if (chunk.Channel == channel)
        {
            await stream.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (next is not null)
        {
            await next.OnChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken) =>
        next?.OnLineAsync(line, cancellationToken) ?? ValueTask.CompletedTask;
}
