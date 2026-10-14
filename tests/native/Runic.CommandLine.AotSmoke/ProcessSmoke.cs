using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine.Processes;

namespace Runic.CommandLine.AotSmoke;

// Runs this executable as its own child: live output through a handler, and a cancellation that terminates the
// child's process group on Unix or its job object on Windows.
internal static class ProcessSmoke
{
    private const string ChildSwitch = "--process-child";

    internal static bool IsChild(string[] args) => args.Length == 2 && args[0] == ChildSwitch;

    internal static async Task<int> RunChildAsync(string[] args)
    {
        if (args[1] == "sleep")
        {
            Console.Out.WriteLine("READY");
            await Task.Delay(30000).ConfigureAwait(false);
            return 0;
        }

        Console.Out.WriteLine("out-1");
        Console.Out.Flush();
        await Task.Delay(50).ConfigureAwait(false);
        Console.Error.WriteLine("err-1");
        return 0;
    }

    internal static async Task<int> RunAsync()
    {
        string executable = Environment.ProcessPath ?? string.Empty;
        var observer = new Observer();
        var runner = new ProcessRunner(new LocalExecutablePolicy(), observer);
        var lines = new Lines();
        ProcessResult output = await runner.RunAsync(
            new ProcessRequest(executable, [ChildSwitch, "lines"], options: ProcessExecutionOptions.NonInteractive),
            lines).ConfigureAwait(false);
        output.EnsureCompleted();
        if (output.ExitCode != 0 || string.Join("|", lines.Received) != "StandardOutput:out-1|StandardError:err-1") return 90;
        if (observer.Identity is null || observer.Identity.ProcessGroupId != (OperatingSystem.IsWindows() ? null : observer.Identity.ProcessId)) return 91;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = new CancelOnReady(cancellation);
        ProcessResult cancelled = await runner.RunAsync(
            new ProcessRequest(executable, [ChildSwitch, "sleep"], options: ProcessExecutionOptions.NonInteractive),
            ready, cancellation.Token).ConfigureAwait(false);
        if (cancelled.State != ProcessState.Cancelled || !ready.Seen || cancelled.Duration > TimeSpan.FromSeconds(15)) return 92;
        return 0;
    }

    private sealed class Observer : IProcessObserver
    {
        public ProcessIdentity? Identity { get; private set; }
        public void OnStarted(ProcessIdentity process, DateTimeOffset startedAt) => Identity = process;
        public void OnCompleted(ProcessState state, TimeSpan duration) { }
    }

    private sealed class Lines : IProcessOutputHandler
    {
        public List<string> Received { get; } = [];
        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            Received.Add($"{line.Channel}:{line.Text}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelOnReady(CancellationTokenSource cancellation) : IProcessOutputHandler
    {
        public bool Seen { get; private set; }
        public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
        {
            Seen |= line.Text == "READY";
            if (Seen) cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
