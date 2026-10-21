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
        DetachFromTerminal = true,
    };

    private static readonly string[] SplitLines = ["one", "two", "münze", "last"];

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
        var splitter = new LineSplitter(ProcessOutputChannel.StandardOutput, new UTF8Encoding(false));
        var lines = new List<ProcessOutputLine>();
        byte[] bytes = Encoding.UTF8.GetBytes("one\r\ntwo\nmünze\r\nlast");
        // Every chunk boundary, including one inside the two-byte "ü" and one between \r and \n.
        for (int index = 0; index < bytes.Length; index++)
        {
            splitter.Decode(bytes.AsSpan(index, 1), lines, flush: false);
        }

        TestAssert.SequenceEqual(SplitLines[..3], Texts(lines));
        splitter.Decode(ReadOnlySpan<byte>.Empty, lines, flush: true);
        TestAssert.SequenceEqual(SplitLines, Texts(lines));
        TestAssert.False(lines.Any(line => line.IsPartial), "A short line was marked partial.");
    }

    [Test, DisplayName("output/long-lines-arrive-in-parts-marked-partial")]
    public void LongLinesArriveInMarkedParts()
    {
        const int Maximum = LineSplitter.MaximumLineLength;
        string[] Split(string text, Encoding encoding, int chunkSize)
        {
            var splitter = new LineSplitter(ProcessOutputChannel.StandardError, encoding);
            var lines = new List<ProcessOutputLine>();
            byte[] bytes = encoding.GetBytes(text);
            for (int index = 0; index < bytes.Length; index += chunkSize)
            {
                splitter.Decode(bytes.AsSpan(index, Math.Min(chunkSize, bytes.Length - index)), lines, flush: false);
            }

            splitter.Decode(ReadOnlySpan<byte>.Empty, lines, flush: true);
            TestAssert.True(lines.All(line => line.Channel == ProcessOutputChannel.StandardError));
            TestAssert.True(lines.All(line => line.Text.Length <= Maximum), "A part is longer than the maximum.");
            return lines.Select(line => (line.IsPartial ? "+" : "=") + line.Text.Length.ToString(CultureInfo.InvariantCulture))
                .ToArray();
        }

        // Each part but the last of a long line is partial, in one chunk and across many.
        string twoAndAHalf = new string('a', (2 * Maximum) + 5) + "\nnext\n";
        string[] parts = ["+65536", "+65536", "=5", "=4"];
        TestAssert.SequenceEqual(parts, Split(twoAndAHalf, Encoding.ASCII, int.MaxValue));
        TestAssert.SequenceEqual(parts, Split(twoAndAHalf, Encoding.ASCII, 4096));
        // A line of exactly the maximum fits, also with \r\n split from it, and is not followed by an empty part.
        string[] exact = ["=65536", "=1"];
        TestAssert.SequenceEqual(exact, Split(new string('a', Maximum) + "\r\nb", Encoding.ASCII, int.MaxValue));
        TestAssert.SequenceEqual(exact, Split(new string('a', Maximum) + "\r\nb", Encoding.ASCII, Maximum + 1));
        TestAssert.SequenceEqual(exact, Split(new string('a', Maximum) + "\nb", Encoding.ASCII, Maximum));
        // A \r one past the maximum that is not part of a terminator is text of the next part.
        TestAssert.SequenceEqual(["+65536", "=2"], Split(new string('a', Maximum) + "\rx\n", Encoding.ASCII, Maximum + 1));
        TestAssert.SequenceEqual(["+65536", "=1"], Split(new string('a', Maximum) + "\r", Encoding.ASCII, int.MaxValue));

        // A part never ends between the halves of a surrogate pair.
        var splitter = new LineSplitter(ProcessOutputChannel.StandardOutput, new UTF8Encoding(false));
        var lines = new List<ProcessOutputLine>();
        splitter.Decode(Encoding.UTF8.GetBytes(new string('a', Maximum - 1) + "\U0001F600b\n"), lines, flush: false);
        TestAssert.Equal(2, lines.Count);
        TestAssert.True(lines[0].IsPartial);
        TestAssert.Equal(new string('a', Maximum - 1), lines[0].Text);
        TestAssert.False(lines[1].IsPartial);
        TestAssert.Equal("\U0001F600b", lines[1].Text);
    }

    [Test, DisplayName("output/the-runner-marks-the-parts-of-a-long-line")]
    public async Task RunnerMarksLongLinePartsAsync()
    {
        var handler = new PartRecordingHandler();
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy())
            .RunAsync(Request(Unattended, "pressure", "150000", "3", "0"), handler).ConfigureAwait(false);

        AssertExited(result);
        TestAssert.SequenceEqual(
            ["StandardError=3", "StandardOutput+65536", "StandardOutput+65536", "StandardOutput=18928"],
            handler.Parts.OrderBy(part => part, StringComparer.Ordinal).ToArray());
        TestAssert.Equal(new string('O', 150000), handler.Text(ProcessOutputChannel.StandardOutput));
    }

    [Test, DisplayName("output/a-stream-handler-writes-one-channel-and-forwards-all-output")]
    public async Task StreamHandlerWritesChannelAsync()
    {
        using var file = new CountingStream();
        var lines = new RecordingHandler();
        var handler = new ProcessOutputStreamHandler(ProcessOutputChannel.StandardOutput, file, lines);
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy())
            .RunAsync(Request(new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(60), standardOutputLimitBytes: 0)
            {
                StandardInput = ProcessStandardInput.Closed,
            }, "interleave", "3", "100"), handler).ConfigureAwait(false);

        AssertExited(result);
        TestAssert.Equal("out-0\nout-1\nout-2\n", Encoding.UTF8.GetString(file.ToArray()));
        TestAssert.Equal(string.Empty, result.StandardOutput.Text);
        TestAssert.Equal(file.Length, result.StandardOutput.ObservedByteCount);
        // Every write was flushed before the next chunk, and the caller's stream stays open.
        TestAssert.True(file.Writes > 0);
        TestAssert.Equal(file.Writes, file.FlushesAfterWrite);
        TestAssert.True(file.CanWrite, "The handler disposed the caller's stream.");
        // The next handler still sees the chunks and lines of both channels.
        TestAssert.SequenceEqual(
            ["StandardOutput:out-0", "StandardError:err-0", "StandardOutput:out-1", "StandardError:err-1",
                "StandardOutput:out-2", "StandardError:err-2"],
            lines.Lines);
        TestAssert.Equal("out-0\nout-1\nout-2\n", lines.ChunkText(ProcessOutputChannel.StandardOutput));
        TestAssert.Equal("err-0\nerr-1\nerr-2\n", lines.ChunkText(ProcessOutputChannel.StandardError));

        // Both channels can share one stream through a second handler, also behind a test double's runner.
        using var shared = new MemoryStream();
        var both = new ProcessOutputStreamHandler(ProcessOutputChannel.StandardError, shared,
            new ProcessOutputStreamHandler(ProcessOutputChannel.StandardOutput, shared));
        await ((IProcessRunner)new CapturedRunner()).RunAsync(new ProcessRequest("tool"), both).ConfigureAwait(false);
        TestAssert.Equal("a\nbe\n", Encoding.UTF8.GetString(shared.ToArray()));

        TestAssert.Throws<ArgumentNullException>(() => _ = new ProcessOutputStreamHandler(ProcessOutputChannel.StandardOutput, null!));
        using var readOnly = new MemoryStream([], writable: false);
        TestAssert.Throws<ArgumentException>(() => _ = new ProcessOutputStreamHandler(ProcessOutputChannel.StandardOutput, readOnly));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => _ = new ProcessOutputStreamHandler((ProcessOutputChannel)2, shared));
    }

    [Test, DisplayName("output/a-slow-stream-applies-backpressure")]
    public async Task SlowStreamAppliesBackpressureAsync()
    {
        const int TotalBytes = 4 * 1024 * 1024;
        var observer = new IdentityObserver();
        using var file = new CountingStream { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        Task<ProcessResult> run = new ProcessRunner(new LocalExecutablePolicy(), observer)
            .RunAsync(Request(Unattended, "pressure", TotalBytes.ToString(CultureInfo.InvariantCulture), "0", "0"),
                new ProcessOutputStreamHandler(ProcessOutputChannel.StandardOutput, file))
            .AsTask();

        await file.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await Task.Delay(1000).ConfigureAwait(false);
        TestAssert.False(run.IsCompleted, "The run completed while its stream was blocked.");
        TestAssert.False(HasExited(observer.Identity!.ProcessId), "The writer finished while its stream was blocked.");
        TestAssert.True(file.Length < 1024 * 1024, $"The stream received {file.Length} bytes ahead.");
        file.Gate.TrySetResult();

        AssertExited(await run.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false));
        TestAssert.Equal(TotalBytes, file.Length);
    }

    [Test, DisplayName("output/a-failing-stream-fails-the-handler")]
    public async Task FailingStreamFailsHandlerAsync()
    {
        var observer = new IdentityObserver();
        using var file = new CountingStream { FailWrites = true };
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy(), observer)
            .RunAsync(Request(Unattended, "interleave", "100", "100"),
                new ProcessOutputStreamHandler(ProcessOutputChannel.StandardOutput, file))
            .ConfigureAwait(false);

        TestAssert.Equal(ProcessState.ExecutionFailed, result.State);
        TestAssert.Equal(ProcessFaultCodes.OutputHandlerFailed, result.Fault!.Code);
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(10), "The failed stream did not end the run.");
        TestAssert.True(await WaitForExitAsync(observer.Identity!.ProcessId).ConfigureAwait(false));
    }

    private static string[] Texts(List<ProcessOutputLine> lines) => lines.Select(line => line.Text).ToArray();

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

    [Test, DisplayName("output/handler-failure-after-a-short-child-exited-is-reported")]
    public async Task HandlerFailureAfterExitIsReportedAsync()
    {
        var observer = new IdentityObserver();
        var handler = new FailAfterExitHandler(observer);
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy(), observer)
            .RunAsync(Request(Unattended, "pressure", "5", "0", "0"), handler).ConfigureAwait(false);

        TestAssert.True(handler.ChildHadExited, "The child had not exited when the handler failed.");
        TestAssert.Equal(ProcessState.ExecutionFailed, result.State);
        TestAssert.Equal(ProcessFaultCodes.OutputHandlerFailed, result.Fault!.Code);
        TestAssert.Null(result.ExitCode);
        TestAssert.Equal("OOOOO", result.StandardOutput.Text);
        TestAssert.Throws<ProcessException>(() => result.EnsureCompleted());
    }

    [Test, DisplayName("output/a-handler-call-longer-than-the-grace-period-keeps-the-output")]
    public async Task LongHandlerCallKeepsOutputAsync()
    {
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(60), drainGracePeriod: TimeSpan.FromMilliseconds(300))
        {
            StandardInput = ProcessStandardInput.Closed,
            DetachFromTerminal = true,
        };
        var handler = new SlowLineHandler(TimeSpan.FromMilliseconds(1500));
        ProcessResult result = await new ProcessRunner(new LocalExecutablePolicy())
            .RunAsync(Request(options, "interleave", "2", "0"), handler).ConfigureAwait(false);

        AssertExited(result);
        TestAssert.False(result.StandardOutput.DrainTimedOut, "A running handler call was taken for a held pipe.");
        TestAssert.False(result.StandardError.DrainTimedOut, "A running handler call was taken for a held pipe.");
        TestAssert.Equal(4, handler.Lines.Count);
        TestAssert.Equal("out-0\nout-1\n", result.StandardOutput.Text);
    }

    [Test, DisplayName("termination/a-non-interactive-child-cannot-open-the-callers-terminal")]
    public async Task NonInteractiveChildHasNoTerminalAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("SKIP pseudo-terminal case outside Linux: it runs the runner under util-linux script.");
            return;
        }

        // The runner runs in a pseudo-terminal session. Detached, a child that reads /dev/tty fails at once instead
        // of being stopped by SIGTTIN; attached, it shares the caller's group and terminal.
        string detached = await RunInTerminalAsync("run-probe", "detach", "read-tty", "read").ConfigureAwait(false);
        TestAssert.True(detached.Contains("state=Exited code=0 group=own out=no-tty", StringComparison.Ordinal), detached);
        string attached = await RunInTerminalAsync("run-probe", "attach", "read-tty").ConfigureAwait(false);
        TestAssert.True(attached.Contains("state=Exited code=0 group=none out=tty", StringComparison.Ordinal), attached);
    }

    [Test, DisplayName("termination/an-ignored-sigchld-never-reports-exited-without-a-code")]
    public async Task IgnoredChildSignalKeepsExitCodeAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP SIGCHLD on Windows.");
            return;
        }

        // sh starts the runner with SIGCHLD ignored, so the kernel would reap its children itself.
        string command = "trap '' CHLD; exec " + ShellQuote(ChildProcessFixture.ExecutablePath) +
            " --process-test-child run-probe detach pressure 1 1 7";
        string output = await RunCapturedAsync("sh", ["-c", command]).ConfigureAwait(false);
        // Whether the kernel reaps the child depends on whether the runtime has replaced the inherited SIG_IGN with
        // its own handler yet. Either the exit code survives, or the run ends at exit as not observed; never Exited
        // without a code, and never a wait for the timeout.
        TestAssert.True(
            output.Contains("state=Exited code=7 group=own out=O", StringComparison.Ordinal) ||
            output.Contains("state=ExecutionFailed code=none group=own out=O", StringComparison.Ordinal),
            output);
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
                DetachFromTerminal = true,
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
                DetachFromTerminal = true,
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
                DetachFromTerminal = true,
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
        ProcessResult cancellation = await runner.RunAsync(Request(Unattended, "sleep", "1"), cancellationToken: cancelled.Token)
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

    private static Task<string> RunInTerminalAsync(params string[] arguments)
    {
        string command = ShellQuote(ChildProcessFixture.ExecutablePath) + " --process-test-child " +
            string.Join(' ', arguments.Select(ShellQuote));
        return RunCapturedAsync("script", ["-qec", command, "/dev/null"]);
    }

    private static async Task<string> RunCapturedAsync(string fileName, string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{fileName} did not finish: a child hung on the terminal.");
        }

        return await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

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

    // The runner keeps an exited Unix child as an unreaped zombie until the run ends, which Process.HasExited
    // reports as running. On Linux, read the state from /proc; elsewhere, wait for the child to finish exiting.
    private static bool HasEnded(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            string stat = Path.Combine("/proc", processId.ToString(CultureInfo.InvariantCulture), "stat");
            try
            {
                string text = File.ReadAllText(stat);
                return text[(text.LastIndexOf(')') + 2)..].StartsWith('Z');
            }
            catch (IOException)
            {
                return true;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return HasExited(processId);
        }

        Thread.Sleep(1000);
        return true;
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

    private sealed class FailAfterExitHandler(IdentityObserver observer) : IProcessOutputHandler
    {
        public bool ChildHadExited { get; private set; }

        public async ValueTask OnChunkAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (!HasEnded(observer.Identity!.ProcessId) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            ChildHadExited = HasEnded(observer.Identity!.ProcessId);
            throw new InvalidOperationException("handler failure after exit");
        }
    }

    private sealed class SlowLineHandler(TimeSpan delay) : IProcessOutputHandler
    {
        public List<string> Lines { get; } = [];

        public async ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            if (Lines.Count == 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            Lines.Add(line.Text);
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

    /// <summary>A test double that passes its fixed capture to the handler as one chunk per channel.</summary>
    private sealed class CapturedRunner : IProcessRunner
    {
        public async ValueTask<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputHandler? outputHandler = null, CancellationToken cancellationToken = default)
        {
            ProcessResult result = ProcessResult.Exited(0, new ProcessOutput("a\nb"), new ProcessOutput("e\n"));
            if (outputHandler is not null)
            {
                await outputHandler.OnChunkAsync(new ProcessOutputChunk(ProcessOutputChannel.StandardOutput,
                    Encoding.UTF8.GetBytes(result.StandardOutput.Text)), cancellationToken).ConfigureAwait(false);
                await outputHandler.OnChunkAsync(new ProcessOutputChunk(ProcessOutputChannel.StandardError,
                    Encoding.UTF8.GetBytes(result.StandardError.Text)), cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    private sealed class PartRecordingHandler : IProcessOutputHandler
    {
        private readonly StringBuilder standardOutput = new();

        public List<string> Parts { get; } = [];

        public string Text(ProcessOutputChannel channel) =>
            channel == ProcessOutputChannel.StandardOutput ? standardOutput.ToString() : string.Empty;

        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            Parts.Add(line.Channel + (line.IsPartial ? "+" : "=") +
                line.Text.Length.ToString(CultureInfo.InvariantCulture));
            if (line.Channel == ProcessOutputChannel.StandardOutput)
            {
                standardOutput.Append(line.Text);
            }

            return ValueTask.CompletedTask;
        }
    }

    // A writable stream that records its writes and flushes, and can block or fail them.
    private sealed class CountingStream : MemoryStream
    {
        private bool unflushed;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; init; }

        public bool FailWrites { get; init; }

        public int Writes { get; private set; }

        public int FlushesAfterWrite { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (FailWrites)
            {
                throw new IOException("disk full");
            }

            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            TestAssert.False(unflushed, "A chunk was written before the previous one was flushed.");
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Writes++;
            unflushed = true;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (unflushed)
            {
                FlushesAfterWrite++;
                unflushed = false;
            }

            return base.FlushAsync(cancellationToken);
        }
    }
}
