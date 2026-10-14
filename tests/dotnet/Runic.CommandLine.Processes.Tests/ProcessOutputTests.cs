using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine.Processes.Internal;
using TUnit.Core;

namespace Runic.CommandLine.Processes.Tests;

/// <summary>Live output, process-group and job-object termination, and the non-interactive helpers.</summary>
internal sealed partial class ProcessOutputTests
{
    private static readonly ProcessExecutionOptions Unattended = new(timeout: TimeSpan.FromSeconds(60))
    {
        StandardInput = ProcessStandardInput.Closed,
    };

    private static readonly string[] SplitLines = ["one", "two", "münze", "last"];

    private static readonly string[] ReplayedEvents =
    [
        "chunk:StandardOutput", "StandardOutput:a", "StandardOutput:b", "chunk:StandardError", "StandardError:e",
    ];

    [Test, DisplayName("output/interleaved-lines-arrive-in-write-order")]
    public async Task InterleavedLinesArriveInWriteOrderAsync()
    {
        var handler = new RecordingHandler();
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy())
            .RunAsync(Request(Unattended, "interleave", "5", "100"), handler).ConfigureAwait(false);

        AssertExited(result);
        string[] expected = Enumerable.Range(0, 5)
            .SelectMany(index => new[] { $"StandardOutput:out-{index}", $"StandardError:err-{index}" })
            .ToArray();
        TestAssert.SequenceEqual(expected, handler.Lines);
        // Every chunk precedes the lines it completes, and the chunks hold exactly the captured bytes.
        TestAssert.Equal("out-0\nout-1\nout-2\nout-3\nout-4\n", result.StandardOutput.Text);
        TestAssert.Equal(result.StandardOutput.Text, handler.ChunkText(ProcessOutputChannel.StandardOutput));
        TestAssert.Equal(result.StandardError.Text, handler.ChunkText(ProcessOutputChannel.StandardError));
        TestAssert.Equal("chunk:StandardOutput", handler.Events[0]);
    }

    [Test, DisplayName("output/slow-handler-applies-backpressure-and-capture-reports-truncation")]
    public async Task SlowHandlerAppliesBackpressureAsync()
    {
        const int LineCount = 64 * 1024;
        const long TotalBytes = LineCount * 64L;
        var observer = new IdentityObserver();
        var handler = new BlockingHandler();
        Task<ProcessResult> run = new ProcessRunner(new LocalExecutablePolicy(), observer)
            .RunAsync(Request(Unattended, "flood-lines", LineCount.ToString(CultureInfo.InvariantCulture)), handler)
            .AsTask();

        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        // The runner reads no further while the handler is busy, so the 4 MiB writer is still blocked on the pipe.
        TestAssert.False(run.IsCompleted, "The run completed while its handler was blocked.");
        TestAssert.False(HasExited(observer.Identity!.ProcessId), "The writer finished while its reader was blocked.");
        TestAssert.True(handler.ChunkBytes < 1024 * 1024, $"The runner read {handler.ChunkBytes} bytes ahead.");
        handler.Release();

        ProcessResult result = await run.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        AssertExited(result);
        TestAssert.Equal(TotalBytes, handler.ChunkBytes);
        TestAssert.Equal(LineCount, handler.LineCount);
        TestAssert.True(handler.LinesInOrder, "Lines arrived out of order.");
        TestAssert.Equal(TotalBytes, result.StandardOutput.ObservedByteCount);
        TestAssert.True(result.StandardOutput.IsTruncated);
        TestAssert.Equal(ProcessExecutionOptions.DefaultOutputLimitBytes, result.StandardOutput.Text.Length);
        ProcessException incomplete = TestAssert.Throws<ProcessException>(() => result.EnsureCompleted());
        TestAssert.Equal(ProcessFaultCodes.OutputIncomplete, incomplete.Fault.Code);
        TestAssert.Equal("truncated", incomplete.Fault.Details["reason"]);
        TestAssert.Equal("standardOutput", incomplete.Fault.Details["stream"]);
    }

    [Test, DisplayName("output/lines-split-on-lf-and-crlf-across-chunks")]
    public void LinesSplitAcrossChunks()
    {
        var splitter = new LineSplitter(new UTF8Encoding(false));
        var lines = new List<string>();
        byte[] bytes = Encoding.UTF8.GetBytes("one\r\ntwo\nmünze\r\nlast");
        // Every chunk boundary, including one inside the two-byte "ü" and one between \r and \n.
        for (int index = 0; index < bytes.Length; index++)
        {
            splitter.Decode(bytes.AsSpan(index, 1), lines, flush: false);
        }

        TestAssert.SequenceEqual(SplitLines[..3], lines);
        splitter.Decode(ReadOnlySpan<byte>.Empty, lines, flush: true);
        TestAssert.SequenceEqual(SplitLines, lines);

        lines.Clear();
        var longLines = new LineSplitter(Encoding.ASCII);
        longLines.Decode(Encoding.ASCII.GetBytes(new string('a', LineSplitter.MaximumLineLength + 5) + "\n"), lines, false);
        TestAssert.SequenceEqual(new[] { new string('a', LineSplitter.MaximumLineLength), "aaaaa" }, lines);
    }

    [Test, DisplayName("output/failing-handler-terminates-the-child")]
    public async Task FailingHandlerTerminatesChildAsync()
    {
        var observer = new IdentityObserver();
        var handler = new ThrowingHandler();
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy(), observer)
            .RunAsync(Request(Unattended, "interleave", "100", "100"), handler).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.ExecutionFailed, result.State);
        TestAssert.Equal(ProcessFaultCodes.OutputHandlerFailed, result.Fault!.Code);
        TestAssert.Equal(1, handler.Calls);
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(10), "The failed handler did not end the run.");
        TestAssert.True(await WaitForExitAsync(observer.Identity!.ProcessId).ConfigureAwait(false));
        TestAssert.Equal(ProcessFaultCodes.OutputHandlerFailed,
            TestAssert.Throws<ProcessException>(() => result.EnsureCompleted()).Fault.Code);
    }

    [Test, DisplayName("output/runners-that-cannot-stream-replay-the-capture")]
    public async Task DefaultImplementationReplaysCaptureAsync()
    {
        IProcessRunner runner = new CapturedRunner();
        var handler = new RecordingHandler();
        ProcessResult result = await runner.RunAsync(new ProcessRequest("tool"), handler).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.Exited, result.State);
        TestAssert.SequenceEqual(ReplayedEvents, handler.Events);
    }

    [Test, DisplayName("termination/cancellation-ends-reparented-descendants")]
    public async Task CancellationEndsReparentedDescendantsAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"rcli-orphan-{Guid.NewGuid():N}.txt");
        var observer = new IdentityObserver();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handler = new ReadyHandler(cancellation);
        int? leaf = null;
        try
        {
            var options = new ProcessExecutionOptions(drainGracePeriod: TimeSpan.FromSeconds(2))
            {
                StandardInput = ProcessStandardInput.Closed,
            };
            ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy(), observer)
                .RunAsync(Request(options, "orphan-tree", markerPath), handler, cancellation.Token)
                .ConfigureAwait(false);

            leaf = handler.ReadyValue is null ? null : int.Parse(handler.ReadyValue, CultureInfo.InvariantCulture);
            TestAssert.Equal(ProcessState.Cancelled, result.State);
            TestAssert.NotNull(leaf);
            ProcessIdentity identity = observer.Identity!;
            // The leaf's parent exited before the cancellation, so only the process group or job reaches it.
            TestAssert.True(await WaitForExitAsync(leaf!.Value).ConfigureAwait(false), "The reparented leaf survived.");
            TestAssert.True(await WaitForExitAsync(identity.ProcessId).ConfigureAwait(false), "The child survived.");
            if (!OperatingSystem.IsWindows())
            {
                TestAssert.Equal<int?>(identity.ProcessId, identity.ProcessGroupId);
                TestAssert.True(GroupIsEmpty(identity.ProcessGroupId!.Value), "A process is left in the group.");
            }
        }
        finally
        {
            if (leaf is not null)
            {
                TryKill(leaf.Value);
            }

            File.Delete(markerPath);
        }
    }

    [Test, DisplayName("termination/sigterm-lets-the-group-exit-within-the-grace-period")]
    public async Task TerminationRequestPrecedesKillAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP SIGTERM on Windows: the job object is terminated at once.");
            return;
        }

        string markerPath = Path.Combine(Path.GetTempPath(), $"rcli-term-{Guid.NewGuid():N}.txt");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = new ProcessExecutionOptions(drainGracePeriod: TimeSpan.FromSeconds(20))
            {
                StandardInput = ProcessStandardInput.Closed,
            };
            ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy())
                .RunAsync(Request(options, "term-aware", markerPath, "exit"), new ReadyHandler(cancellation),
                    cancellation.Token)
                .ConfigureAwait(false);

            TestAssert.Equal(ProcessState.Cancelled, result.State);
            TestAssert.Equal("SIGTERM", await File.ReadAllTextAsync(markerPath).ConfigureAwait(false));
            // It exited on SIGTERM rather than at the SIGKILL after the 20 s grace period.
            TestAssert.True(result.Duration < TimeSpan.FromSeconds(15), $"Took {result.Duration}.");
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Test, DisplayName("termination/sigkill-follows-an-ignored-sigterm-after-the-grace-period")]
    public async Task IgnoredTerminationRequestIsKilledAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP SIGTERM on Windows: the job object is terminated at once.");
            return;
        }

        string markerPath = Path.Combine(Path.GetTempPath(), $"rcli-term-{Guid.NewGuid():N}.txt");
        var observer = new IdentityObserver();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = new ProcessExecutionOptions(drainGracePeriod: TimeSpan.FromMilliseconds(500))
            {
                StandardInput = ProcessStandardInput.Closed,
            };
            ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy(), observer)
                .RunAsync(Request(options, "term-aware", markerPath, "ignore"), new ReadyHandler(cancellation),
                    cancellation.Token)
                .ConfigureAwait(false);

            TestAssert.Equal(ProcessState.Cancelled, result.State);
            TestAssert.Equal("SIGTERM", await File.ReadAllTextAsync(markerPath).ConfigureAwait(false));
            TestAssert.True(await WaitForExitAsync(observer.Identity!.ProcessId).ConfigureAwait(false));
            TestAssert.True(GroupIsEmpty(observer.Identity.ProcessGroupId!.Value), "A process is left in the group.");
            TestAssert.True(result.Duration < TimeSpan.FromSeconds(10), $"Took {result.Duration}.");
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Test, DisplayName("observer/started-reports-the-process-and-its-group")]
    public async Task ObserverReceivesIdentityAsync()
    {
        var observer = new IdentityObserver();
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy(), observer)
            .RunAsync(Request(Unattended, "sleep", "1")).ConfigureAwait(false);

        AssertExited(result);
        TestAssert.True(observer.Identity!.ProcessId > 0);
        TestAssert.Equal(result.StartedAt, observer.StartedAt);
        TestAssert.Equal<int?>(OperatingSystem.IsWindows() ? null : observer.Identity.ProcessId,
            observer.Identity.ProcessGroupId);
    }

    [Test, DisplayName("result/ensure-completed-maps-terminal-states")]
    public async Task EnsureCompletedMapsStatesAsync()
    {
        var runner = new ProcessRunner(new LocalExecutablePolicy());
        ProcessResult exited = await runner.RunAsync(Request(Unattended, "pressure", "3", "2", "7"))
            .ConfigureAwait(false);
        TestAssert.True(ReferenceEquals(exited, exited.EnsureCompleted()), "A complete result was not returned.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        ProcessResult cancellation = await runner.RunAsync(Request(Unattended, "sleep", "1"), cancelled.Token)
            .ConfigureAwait(false);
        OperationCanceledException canceledException =
            TestAssert.Throws<OperationCanceledException>(() => cancellation.EnsureCompleted(cancelled.Token));
        TestAssert.Equal(cancelled.Token, canceledException.CancellationToken);

        ProcessResult timedOut = await runner.RunAsync(Request(
            new ProcessExecutionOptions(timeout: TimeSpan.FromMilliseconds(100)) { StandardInput = ProcessStandardInput.Closed },
            "sleep", "30000")).ConfigureAwait(false);
        ProcessException timeout = TestAssert.Throws<ProcessException>(() => timedOut.EnsureCompleted());
        TestAssert.Equal(ProcessFaultCodes.TimedOut, timeout.Fault.Code);
        TestAssert.True(ReferenceEquals(timedOut, timeout.Result), "The exception does not carry the result.");

        ProcessResult missing = await runner.RunAsync(new ProcessRequest(
            Path.Combine(Path.GetTempPath(), $"rcli-missing-{Guid.NewGuid():N}"))).ConfigureAwait(false);
        TestAssert.Equal(ProcessFaultCodes.StartFailed,
            TestAssert.Throws<ProcessException>(() => missing.EnsureCompleted()).Fault.Code);
    }

    [Test, DisplayName("options/non-interactive-closes-standard-input")]
    public async Task NonInteractiveClosesInputAsync()
    {
        TestAssert.Equal(ProcessStandardInputMode.Closed, ProcessExecutionOptions.NonInteractive.StandardInput.Mode);
        TestAssert.Equal(ProcessExecutionOptions.DefaultOutputLimitBytes,
            ProcessExecutionOptions.NonInteractive.StandardOutputLimitBytes);
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy())
            .RunAsync(Request(ProcessExecutionOptions.NonInteractive, "input-echo")).ConfigureAwait(false);
        AssertExited(result);
        TestAssert.Equal(string.Empty, result.StandardOutput.Text);
    }

    private static ProcessRequest Request(ProcessExecutionOptions options, string mode, params string[] arguments) =>
        new(ChildProcessFixture.ExecutablePath, ChildProcessFixture.CreateArguments(mode, arguments), options: options);

    private static void AssertExited(ProcessResult result)
    {
        TestAssert.Equal(ProcessState.Exited, result.State);
        TestAssert.Equal<int?>(0, result.ExitCode);
        TestAssert.Null(result.Fault);
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<bool> WaitForExitAsync(int processId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (HasExited(processId))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
        }
    }

    // kill(-group, 0) fails with ESRCH once no process is left in the group.
    private static bool GroupIsEmpty(int processGroupId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Kill(-processGroupId, 0) == -1 && Marshal.GetLastPInvokeError() == 3)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    private sealed class IdentityObserver : IProcessObserver
    {
        public ProcessIdentity? Identity { get; private set; }

        public DateTimeOffset StartedAt { get; private set; }

        public void OnStarted(ProcessIdentity process, DateTimeOffset startedAt)
        {
            Identity = process;
            StartedAt = startedAt;
        }

        public void OnCompleted(ProcessState state, TimeSpan duration)
        {
        }
    }

    private sealed class RecordingHandler : IProcessOutputHandler
    {
        private readonly List<byte> standardOutput = [];
        private readonly List<byte> standardError = [];

        public List<string> Events { get; } = [];

        public string[] Lines => Events.Where(item => !item.StartsWith("chunk:", StringComparison.Ordinal)).ToArray();

        public string ChunkText(ProcessOutputChannel channel) => Encoding.UTF8.GetString(
            (channel == ProcessOutputChannel.StandardOutput ? standardOutput : standardError).ToArray());

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            Events.Add($"{line.Channel}:{line.Text}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            Events.Add($"chunk:{chunk.Channel}");
            (chunk.Channel == ProcessOutputChannel.StandardOutput ? standardOutput : standardError)
                .AddRange(chunk.Bytes.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingHandler : IProcessOutputHandler
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public long ChunkBytes => Interlocked.Read(ref chunkBytes);

        public int LineCount { get; private set; }

        public bool LinesInOrder { get; private set; } = true;

        private long chunkBytes;

        public void Release() => release.TrySetResult();

        public async ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref chunkBytes, chunk.Bytes.Length);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            if (!line.Text.StartsWith(LineCount.ToString("D8", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                line.Text.Length != 63)
            {
                LinesInOrder = false;
            }

            LineCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IProcessOutputHandler
    {
        public int Calls { get; private set; }

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("handler failure");
        }
    }

    // Cancels the request at the first line that starts with READY, and keeps the rest of that line.
    private sealed class ReadyHandler(CancellationTokenSource cancellation) : IProcessOutputHandler
    {
        public string? ReadyValue { get; private set; }

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            if (line.Channel == ProcessOutputChannel.StandardOutput &&
                line.Text.StartsWith("READY", StringComparison.Ordinal))
            {
                ReadyValue = line.Text.Length > 6 ? line.Text[6..] : string.Empty;
                cancellation.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturedRunner : IProcessRunner
    {
        public ValueTask<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ProcessResult(
                ProcessState.Exited,
                0,
                new ProcessOutput("a\nb", 3, false, false),
                new ProcessOutput("e\n", 2, false, false),
                null,
                null,
                now,
                now));
        }
    }
}
