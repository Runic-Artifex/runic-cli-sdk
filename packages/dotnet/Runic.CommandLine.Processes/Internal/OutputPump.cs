using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes.Internal;

/// <summary>The bounded capture of one output pipe; readable at any time, also while its pump still runs.</summary>
internal sealed class OutputCapture
{
    private readonly object gate = new();
    private readonly byte[] retained;
    private readonly int limit;
    private readonly Encoding encoding;
    private int retainedCount;
    private long observed;

    internal OutputCapture(int limit, Encoding encoding)
    {
        this.limit = limit;
        this.encoding = encoding;
        retained = limit == 0 ? Array.Empty<byte>() : new byte[limit];
    }

    internal long ObservedByteCount
    {
        get
        {
            lock (gate)
            {
                return observed;
            }
        }
    }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            observed = observed > long.MaxValue - bytes.Length ? long.MaxValue : observed + bytes.Length;
            int count = Math.Min(bytes.Length, retained.Length - retainedCount);
            if (count > 0)
            {
                bytes[..count].CopyTo(retained.AsSpan(retainedCount));
                retainedCount += count;
            }
        }
    }

    internal ProcessOutput ToOutput(bool drainTimedOut)
    {
        lock (gate)
        {
            string text;
            try
            {
                text = encoding.GetString(retained, 0, retainedCount);
            }
            catch (Exception exception) when (!OutputPump.IsFatal(exception))
            {
                text = string.Empty;
            }

            return new ProcessOutput(text, observed, observed > limit, drainTimedOut);
        }
    }
}

/// <summary>Splits decoded output into lines; a line longer than the maximum is passed on in parts.</summary>
internal sealed class LineSplitter
{
    internal const int MaximumLineLength = 64 * 1024;

    private readonly Decoder decoder;
    private readonly StringBuilder pending = new();
    private char[] characters = Array.Empty<char>();

    internal LineSplitter(Encoding encoding)
    {
        decoder = encoding.GetDecoder();
    }

    internal void Decode(ReadOnlySpan<byte> bytes, List<string> lines, bool flush)
    {
        int count = decoder.GetCharCount(bytes, flush);
        if (characters.Length < count)
        {
            characters = new char[Math.Max(count, 1024)];
        }

        int decoded = decoder.GetChars(bytes, characters, flush);
        ReadOnlySpan<char> text = characters.AsSpan(0, decoded);
        while (!text.IsEmpty)
        {
            int newline = text.IndexOf('\n');
            if (newline < 0)
            {
                Append(text, lines);
                break;
            }

            Append(text[..newline], lines);
            if (pending.Length > 0 && pending[^1] == '\r')
            {
                pending.Length--;
            }

            lines.Add(pending.ToString());
            pending.Clear();
            text = text[(newline + 1)..];
        }

        if (flush && pending.Length > 0)
        {
            lines.Add(pending.ToString());
            pending.Clear();
        }
    }

    private void Append(ReadOnlySpan<char> text, List<string> lines)
    {
        while (pending.Length + text.Length > MaximumLineLength)
        {
            int count = MaximumLineLength - pending.Length;
            pending.Append(text[..count]);
            lines.Add(pending.ToString());
            pending.Clear();
            text = text[count..];
        }

        pending.Append(text);
    }
}

/// <summary>Passes output to one handler, one call at a time, until the handler fails or the run ends.</summary>
[SuppressMessage("Design", "CA1001", Justification = "A pump can outlive the request; the semaphore has no wait handle to release.")]
internal sealed class OutputDelivery
{
    private readonly IProcessOutputHandler handler;
    private readonly CancellationToken cancellationToken;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopped;
    private long progress;
    private int inFlight;

    internal OutputDelivery(IProcessOutputHandler handler, CancellationToken cancellationToken)
    {
        this.handler = handler;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>Completes when the handler throws while the request is neither cancelled nor timed out.</summary>
    internal Task Failure => failed.Task;

    /// <summary>Counts completed handler calls, so a drain can tell a busy handler from a held pipe.</summary>
    internal long Progress => Interlocked.Read(ref progress);

    /// <summary>Gets a value indicating whether a handler call is running.</summary>
    internal bool IsBusy => Volatile.Read(ref inFlight) != 0;

    /// <summary>Stops all later calls; a call in progress is not interrupted.</summary>
    internal void Stop() => stopped = true;

    internal async Task DeliverAsync(
        ProcessOutputChannel stream,
        ReadOnlyMemory<byte> bytes,
        LineSplitter lines,
        List<string> buffer,
        bool final)
    {
        if (stopped)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        bool entered = false;
        try
        {
            if (stopped)
            {
                return;
            }

            entered = true;
            Interlocked.Increment(ref inFlight);
            if (!bytes.IsEmpty)
            {
                await handler.OnChunkAsync(new ProcessOutputChunk(stream, bytes), cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref progress);
            }

            buffer.Clear();
            lines.Decode(bytes.Span, buffer, final);
            foreach (string line in buffer)
            {
                if (stopped)
                {
                    return;
                }

                await handler.OnLineAsync(new ProcessOutputLine(stream, line), cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref progress);
            }
        }
        catch (Exception exception) when (!OutputPump.IsFatal(exception))
        {
            stopped = true;
            if (!cancellationToken.IsCancellationRequested)
            {
                failed.TrySetResult();
            }
        }
        finally
        {
            if (entered)
            {
                Interlocked.Decrement(ref inFlight);
            }

            gate.Release();
        }
    }
}

internal static class OutputPump
{
    private const int BufferSize = 81920;

    /// <summary>Reads a pipe to its end into the capture, and through the delivery when there is one.</summary>
    internal static async Task PumpAsync(
        Stream stream,
        OutputCapture capture,
        ProcessOutputChannel kind,
        OutputDelivery? delivery,
        Encoding encoding)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        LineSplitter? lines = delivery is null ? null : new LineSplitter(encoding);
        var decoded = new List<string>();
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                capture.Append(buffer.AsSpan(0, read));
                if (delivery is not null)
                {
                    await delivery.DeliverAsync(kind, buffer.AsMemory(0, read), lines!, decoded, final: false)
                        .ConfigureAwait(false);
                }
            }

            if (delivery is not null)
            {
                await delivery.DeliverAsync(kind, ReadOnlyMemory<byte>.Empty, lines!, decoded, final: true)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Closing a held pipe after the drain grace period ends the read. The capture keeps what was read.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

/// <summary>Passes a finished result's captured output to a handler, for runners that cannot stream.</summary>
internal static class OutputReplay
{
    internal static async ValueTask ReplayAsync(
        ProcessRequest request,
        ProcessResult result,
        IProcessOutputHandler handler,
        CancellationToken cancellationToken)
    {
        await ReplayAsync(ProcessOutputChannel.StandardOutput, result.StandardOutput.Text,
            request.Options.StandardOutputEncoding, handler, cancellationToken).ConfigureAwait(false);
        await ReplayAsync(ProcessOutputChannel.StandardError, result.StandardError.Text,
            request.Options.StandardErrorEncoding, handler, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReplayAsync(
        ProcessOutputChannel stream,
        string text,
        Encoding encoding,
        IProcessOutputHandler handler,
        CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        byte[] bytes = encoding.GetBytes(text);
        await handler.OnChunkAsync(new ProcessOutputChunk(stream, bytes), cancellationToken).ConfigureAwait(false);
        var lines = new List<string>();
        new LineSplitter(encoding).Decode(bytes, lines, flush: true);
        foreach (string line in lines)
        {
            await handler.OnLineAsync(new ProcessOutputLine(stream, line), cancellationToken).ConfigureAwait(false);
        }
    }
}
