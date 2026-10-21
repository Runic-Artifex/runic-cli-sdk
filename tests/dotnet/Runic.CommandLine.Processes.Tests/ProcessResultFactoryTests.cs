using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.CommandLine.Processes.Tests;

/// <summary>Results and captures built through the public factories, as test doubles of the runner build them.</summary>
internal sealed class ProcessResultFactoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Test, DisplayName("fakes/a-fake-runner-built-from-public-api-drives-a-consumer")]
    public async Task FakeRunnerDrivesConsumerAsync()
    {
        var runner = new FakeRunner(
            ProcessResult.Exited(0, new ProcessOutput("{\"ok\":true}\n")),
            ProcessResult.Exited(0, new ProcessOutput("partial", observedByteCount: 1000, isTruncated: true)),
            ProcessResult.StartFailed(ProcessStartFailureCategory.NotFound),
            ProcessResult.TimedOut(),
            ProcessResult.Cancelled());

        TestAssert.Equal("{\"ok\":true}\n", await ReadToolOutputAsync(runner).ConfigureAwait(false));
        TestAssert.Equal(ProcessFaultCodes.OutputIncomplete, await ReadFaultAsync(runner).ConfigureAwait(false));
        TestAssert.Equal(ProcessFaultCodes.StartFailed, await ReadFaultAsync(runner).ConfigureAwait(false));
        TestAssert.Equal(ProcessFaultCodes.TimedOut, await ReadFaultAsync(runner).ConfigureAwait(false));
        await TestAssert.ThrowsAsync<OperationCanceledException>(() => ReadToolOutputAsync(runner)).ConfigureAwait(false);
        TestAssert.Equal(5, runner.Requests.Count);
        TestAssert.Equal("tool", runner.Requests[0].FileName);

        // The default streaming overload replays the fake's capture to a handler.
        var lines = new List<string>();
        await ((IProcessRunner)new FakeRunner(ProcessResult.Exited(3, new ProcessOutput("a\nb\n"), new ProcessOutput("e"))))
            .RunAsync(new ProcessRequest("tool"), new LineList(lines)).ConfigureAwait(false);
        TestAssert.SequenceEqual(["StandardOutput:a", "StandardOutput:b", "StandardError:e"], lines);
    }

    [Test, DisplayName("fakes/factories-enforce-the-terminal-state-invariants")]
    public void FactoriesEnforceInvariants()
    {
        var output = new ProcessOutput("out");
        var error = new ProcessOutput("err");

        ProcessResult exited = ProcessResult.Exited(7, output, error, Start, TimeSpan.FromSeconds(2));
        AssertResult(exited, ProcessState.Exited, 7, null, null);
        TestAssert.True(ReferenceEquals(output, exited.StandardOutput));
        TestAssert.True(ReferenceEquals(error, exited.StandardError));
        TestAssert.Equal(Start, exited.StartedAt);
        TestAssert.Equal(Start.AddSeconds(2), exited.EndedAt);
        TestAssert.Equal(TimeSpan.FromSeconds(2), exited.Duration);
        TestAssert.True(ReferenceEquals(exited, exited.EnsureCompleted()));

        ProcessResult timedOut = ProcessResult.TimedOut(output, error);
        AssertResult(timedOut, ProcessState.TimedOut, null, null, null);
        TestAssert.Equal("out", timedOut.StandardOutput.Text);
        TestAssert.Equal(ProcessFaultCodes.TimedOut,
            TestAssert.Throws<ProcessException>(() => timedOut.EnsureCompleted()).Fault.Code);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ProcessResult cancelled = ProcessResult.Cancelled(standardError: error);
        AssertResult(cancelled, ProcessState.Cancelled, null, null, null);
        TestAssert.Equal(cancellation.Token, TestAssert.Throws<OperationCanceledException>(
            () => cancelled.EnsureCompleted(cancellation.Token)).CancellationToken);

        ProcessResult rejected = ProcessResult.Rejected();
        AssertResult(rejected, ProcessState.Rejected, null, ProcessFaultCodes.ExecutableRejected, null);
        AssertEmpty(rejected);
        var batch = new CommandFault(ProcessFaultCodes.BatchFileRejected, "Batch files are not allowed.");
        TestAssert.True(ReferenceEquals(batch, ProcessResult.Rejected(batch).Fault));
        TestAssert.True(ReferenceEquals(batch,
            TestAssert.Throws<ProcessException>(() => ProcessResult.Rejected(batch).EnsureCompleted()).Fault));

        ProcessResult startFailed = ProcessResult.StartFailed(ProcessStartFailureCategory.AccessDenied, Start);
        AssertResult(startFailed, ProcessState.StartFailed, null, ProcessFaultCodes.StartFailed,
            ProcessStartFailureCategory.AccessDenied);
        TestAssert.Equal("AccessDenied", startFailed.Fault!.Details["category"]);
        TestAssert.Equal(TimeSpan.Zero, startFailed.Duration);
        AssertEmpty(startFailed);

        ProcessResult failed = ProcessResult.ExecutionFailed(standardOutput: output);
        AssertResult(failed, ProcessState.ExecutionFailed, null, ProcessFaultCodes.ExecutionFailed, null);
        TestAssert.Equal("The process lifecycle could not be observed safely.", failed.Fault!.Message);
        var handler = new CommandFault(ProcessFaultCodes.OutputHandlerFailed, "The output handler failed.");
        TestAssert.Equal(ProcessFaultCodes.OutputHandlerFailed, TestAssert.Throws<ProcessException>(
            () => ProcessResult.ExecutionFailed(handler).EnsureCompleted()).Fault.Code);

        TestAssert.Throws<ArgumentOutOfRangeException>(() => ProcessResult.Exited(0, duration: TimeSpan.FromTicks(-1)));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => ProcessResult.Cancelled(startedAt: DateTimeOffset.MaxValue, duration: TimeSpan.FromTicks(1)));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ProcessResult.StartFailed((ProcessStartFailureCategory)99));
    }

    [Test, DisplayName("fakes/process-output-counts-utf8-bytes-by-default")]
    public void ProcessOutputCountsBytes()
    {
        var output = new ProcessOutput("münze\n");
        TestAssert.Equal(7L, output.ObservedByteCount);
        TestAssert.False(output.IsTruncated);
        TestAssert.False(output.DrainTimedOut);

        var held = new ProcessOutput(string.Empty, 0, drainTimedOut: true);
        TestAssert.Equal(0L, held.ObservedByteCount);
        TestAssert.True(held.DrainTimedOut);
        TestAssert.Equal("drainTimedOut", TestAssert.Throws<ProcessException>(
            () => ProcessResult.Exited(0, standardError: held).EnsureCompleted()).Fault.Details["reason"]);

        TestAssert.Throws<ArgumentNullException>(() => _ = new ProcessOutput(null!));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => _ = new ProcessOutput("x", -1));
    }

    [Test, DisplayName("fakes/factory-faults-match-the-runner")]
    public async Task FactoryFaultsMatchRunnerAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"rcli-factory-{Guid.NewGuid():N}");
        var runner = new ProcessRunner(new LocalExecutablePolicy(executableRoots: [root]));
        ProcessResult rejected = await runner.RunAsync(new ProcessRequest(ChildProcessFixture.ExecutablePath))
            .ConfigureAwait(false);
        AssertSameOutcome(rejected, ProcessResult.Rejected());

        ProcessResult missing = await new ProcessRunner(new LocalExecutablePolicy()).RunAsync(
            new ProcessRequest(Path.Combine(root, "missing-tool"))).ConfigureAwait(false);
        AssertSameOutcome(missing, ProcessResult.StartFailed(missing.StartFailureCategory!.Value));
        TestAssert.Equal(ProcessStartFailureCategory.NotFound, missing.StartFailureCategory);
    }

    private static void AssertSameOutcome(ProcessResult expected, ProcessResult actual)
    {
        TestAssert.Equal(expected.State, actual.State);
        TestAssert.Equal(expected.ExitCode, actual.ExitCode);
        TestAssert.Equal(expected.StartFailureCategory, actual.StartFailureCategory);
        TestAssert.Equal(expected.Fault!.Code, actual.Fault!.Code);
        TestAssert.Equal(expected.Fault.Message, actual.Fault.Message);
        TestAssert.SequenceEqual(
            [.. expected.Fault.Details],
            new List<KeyValuePair<string, string>>(actual.Fault.Details));
        AssertEmpty(expected);
        AssertEmpty(actual);
    }

    private static void AssertResult(
        ProcessResult result,
        ProcessState state,
        int? exitCode,
        string? faultCode,
        ProcessStartFailureCategory? category)
    {
        TestAssert.Equal(state, result.State);
        TestAssert.Equal(exitCode, result.ExitCode);
        TestAssert.Equal(faultCode, result.Fault?.Code);
        TestAssert.Equal(category, result.StartFailureCategory);
        TestAssert.NotNull(result.StandardOutput);
        TestAssert.NotNull(result.StandardError);
    }

    private static void AssertEmpty(ProcessResult result)
    {
        foreach (ProcessOutput output in new[] { result.StandardOutput, result.StandardError })
        {
            TestAssert.Equal(string.Empty, output.Text);
            TestAssert.Equal(0L, output.ObservedByteCount);
            TestAssert.False(output.IsTruncated || output.DrainTimedOut);
        }
    }

    // Application code under test: runs a tool and returns its complete output.
    private static async Task<string> ReadToolOutputAsync(IProcessRunner runner)
    {
        ProcessResult result = await runner.RunAsync(new ProcessRequest("tool", ["--json"])).ConfigureAwait(false);
        return result.EnsureCompleted().StandardOutput.Text;
    }

    private static async Task<string> ReadFaultAsync(IProcessRunner runner)
    {
        ProcessException exception = await TestAssert.ThrowsAsync<ProcessException>(() => ReadToolOutputAsync(runner))
            .ConfigureAwait(false);
        return exception.Fault.Code;
    }

    // A test double of IProcessRunner that uses only public API.
    private sealed class FakeRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> results = new(results);

        public List<ProcessRequest> Requests { get; } = [];

        public async ValueTask<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputHandler? outputHandler = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            ProcessResult result = results.Dequeue();
            if (outputHandler is not null)
            {
                await PassAsync(ProcessOutputChannel.StandardOutput, result.StandardOutput.Text).ConfigureAwait(false);
                await PassAsync(ProcessOutputChannel.StandardError, result.StandardError.Text).ConfigureAwait(false);
            }

            return result;

            async ValueTask PassAsync(ProcessOutputChannel channel, string text)
            {
                foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    await outputHandler.OnLineAsync(new ProcessOutputLine(channel, line), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class LineList(List<string> lines) : IProcessOutputHandler
    {
        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            lines.Add($"{line.Channel}:{line.Text}");
            return ValueTask.CompletedTask;
        }
    }
}
