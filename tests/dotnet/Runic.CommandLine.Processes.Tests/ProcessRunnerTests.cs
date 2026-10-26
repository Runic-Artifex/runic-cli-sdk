using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine.Processes;
using TUnit.Core;

namespace Runic.CommandLine.Processes.Tests;

internal sealed class ProcessRunnerTests
{
    private static readonly string[] OriginalArgument = ["original"];
    private static readonly string[] RejectedCompletion = ["completed:Rejected"];
    private static readonly string[] StartFailedCompletion = ["completed:StartFailed"];
    private static readonly string[] CancelledCompletion = ["completed:Cancelled"];

    private static readonly string[] BatchMetacharacterArguments =
        ["x&calc", "%PATH%", "!x!", "a^b", "a|b", "a<b", "a>b", "a\"b", "(a)", "a\nb", "a\rb"];

    [Test, DisplayName("argument tokens are preserved without shell interpretation")]
    public async Task ArgumentTokensArePreservedAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"wut-cli-shell-{Guid.NewGuid():N}.txt");
        string[] payload =
        [
            "contains spaces",
            "embedded\"quote",
            string.Empty,
            "Grüße-東京-🙂",
            "-leading-hyphen",
            $">{markerPath}",
            $"$(touch {markerPath})",
            $"& echo shell > {markerPath}",
        ];

        ProcessResult result = await RunChildAsync("echo-arguments", payload).ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.SequenceEqual(payload, DecodeArgumentEcho(result.StandardOutput.Text));
        TestAssert.False(File.Exists(markerPath), "A shell metacharacter argument created a marker file.");
        TestAssert.Equal(string.Empty, result.StandardError.Text);
    }

    [Test, DisplayName("request defensively copies argument tokens")]
    public async Task RequestDefensivelyCopiesArgumentsAsync()
    {
        string[] mutable = ChildProcessFixture.CreateArguments("echo-arguments", "original");
        var request = new ProcessRequest(ChildProcessFixture.ExecutablePath, mutable);
        mutable[^1] = "mutated";

        ProcessResult result = await CreateRunner().RunAsync(request).ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.SequenceEqual(OriginalArgument, DecodeArgumentEcho(result.StandardOutput.Text));
    }

    [Test, DisplayName("hostile policy rejection is normalized and prevents process start")]
    public async Task PolicyRejectionPreventsStartAsync()
    {
        var policy = new RecordingRejectPolicy();
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(policy, observer);

        ProcessResult result = await runner.RunAsync(CreateChildRequest("sleep", "1")).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.Rejected, result.State);
        TestAssert.Null(result.ExitCode);
        TestAssert.Equal(1, policy.EvaluationCount);
        TestAssert.NotNull(result.Fault);
        TestAssert.Equal(ProcessFaultCodes.ExecutableRejected, result.Fault!.Code);
        TestAssert.Equal("The process request is not permitted by the configured policy.", result.Fault.Message);
        TestAssert.False(result.Fault.Code.Contains("secret", StringComparison.OrdinalIgnoreCase));
        TestAssert.False(result.Fault.Message.Contains("secret", StringComparison.Ordinal));
        TestAssert.Equal(0, result.Fault.Details.Count);
        TestAssert.SequenceEqual(RejectedCompletion, observer.Events);
    }

    [Test, DisplayName("missing executable is a sanitized start failure")]
    public async Task MissingExecutableIsStartFailureAsync()
    {
        var observer = new RecordingObserver();
        string missing = Path.Combine(Path.GetTempPath(), $"wut-cli-missing-{Guid.NewGuid():N}");

        ProcessResult result = await CreateRunner(observer).RunAsync(new ProcessRequest(missing)).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.StartFailed, result.State);
        TestAssert.Null(result.ExitCode);
        TestAssert.Equal(ProcessStartFailureCategory.NotFound, result.StartFailureCategory);
        TestAssert.NotNull(result.Fault);
        TestAssert.Equal(ProcessFaultCodes.StartFailed, result.Fault!.Code);
        TestAssert.SequenceEqual(StartFailedCompletion, observer.Events);
    }

    [Test, DisplayName("success exit is reported")]
    public async Task SuccessExitIsReportedAsync()
    {
        ProcessResult result = await RunChildAsync("pressure", "5", "7", "0").ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.Equal("OOOOO", result.StandardOutput.Text);
        TestAssert.Equal("EEEEEEE", result.StandardError.Text);
    }

    [Test, DisplayName("nonzero exit is reported without reclassification")]
    public async Task NonzeroExitIsReportedAsync()
    {
        ProcessResult result = await RunChildAsync("pressure", "0", "0", "23").ConfigureAwait(false);

        AssertExited(result, 23);
        TestAssert.Null(result.Fault);
    }

    [Test, DisplayName("simultaneous stdout and stderr pressure is drained")]
    public async Task SimultaneousPressureIsDrainedAsync()
    {
        const int byteCount = 512 * 1024;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            standardOutputLimitBytes: byteCount,
            standardErrorLimitBytes: byteCount);

        ProcessResult result = await RunChildAsync(
            options,
            "pressure",
            byteCount.ToString(CultureInfo.InvariantCulture),
            byteCount.ToString(CultureInfo.InvariantCulture),
            "0",
            "1024").ConfigureAwait(false);

        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, byteCount, byteCount, 'O', truncated: false);
        AssertOutput(result.StandardError, byteCount, byteCount, 'E', truncated: false);
    }

    [Test, DisplayName("both output caps retain bounded bytes and count all observed bytes")]
    public async Task BothOutputCapsAreEnforcedAsync()
    {
        const int observedOutput = 300_123;
        const int observedError = 400_321;
        const int retainedOutput = 1_003;
        const int retainedError = 2_007;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            standardOutputLimitBytes: retainedOutput,
            standardErrorLimitBytes: retainedError);

        ProcessResult result = await RunChildAsync(
            options,
            "pressure",
            observedOutput.ToString(CultureInfo.InvariantCulture),
            observedError.ToString(CultureInfo.InvariantCulture),
            "0").ConfigureAwait(false);

        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, observedOutput, retainedOutput, 'O', truncated: true);
        AssertOutput(result.StandardError, observedError, retainedError, 'E', truncated: true);
    }

    [Test, DisplayName("zero output caps still drain both channels")]
    public async Task ZeroOutputCapsStillDrainAsync()
    {
        const int observed = 200_000;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            standardOutputLimitBytes: 0,
            standardErrorLimitBytes: 0);

        ProcessResult result = await RunChildAsync(
            options,
            "pressure",
            observed.ToString(CultureInfo.InvariantCulture),
            observed.ToString(CultureInfo.InvariantCulture),
            "0").ConfigureAwait(false);

        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, observed, 0, 'O', truncated: true);
        AssertOutput(result.StandardError, observed, 0, 'E', truncated: true);
    }

    [Test, DisplayName("output cap configuration rejects invalid bounds")]
    public Task OutputCapConfigurationRejectsInvalidBoundsAsync()
    {
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(standardOutputLimitBytes: -1));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(standardErrorLimitBytes: -1));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(
                standardOutputLimitBytes: ProcessExecutionOptions.MaximumOutputLimitBytes + 1));
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => _ = new ProcessExecutionOptions(
                standardErrorLimitBytes: ProcessExecutionOptions.MaximumOutputLimitBytes + 1));
        return Task.CompletedTask;
    }

    [Test, DisplayName("input configuration enforces bounds and unchanged defaults")]
    public Task InputConfigurationEnforcesBoundsAsync()
    {
        var defaults = new ProcessExecutionOptions();
        TestAssert.Equal(ProcessStandardInputMode.Inherit, defaults.StandardInput.Mode);
        TestAssert.True(defaults.InheritEnvironment);
        TestAssert.Throws<ArgumentNullException>(() => _ = new ProcessExecutionOptions { StandardInput = null! });
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ProcessStandardInput.FromBytes(new byte[1], 0));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ProcessStandardInput.FromBytes(ReadOnlyMemory<byte>.Empty, -1));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ProcessStandardInput.FromBytes(
            ReadOnlyMemory<byte>.Empty, ProcessStandardInput.MaximumInputLimitBytes + 1));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ProcessStandardInput.FromBytes(
            new byte[ProcessStandardInput.DefaultInputLimitBytes + 1]));
        TestAssert.Equal(ProcessStandardInput.MaximumInputLimitBytes,
            ProcessStandardInput.FromBytes(new byte[ProcessStandardInput.MaximumInputLimitBytes],
                ProcessStandardInput.MaximumInputLimitBytes).ByteCount);
        return Task.CompletedTask;
    }

    [Test, DisplayName("bounded input is copied and reaches EOF without text conversion")]
    public async Task BoundedInputIsCopiedAsync()
    {
        byte[] payload = [0, 1, 127, 128, 255, 13, 10];
        string expected = Convert.ToBase64String(payload);
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10))
        {
            StandardInput = ProcessStandardInput.FromBytes(payload, payload.Length),
        };
        Array.Fill(payload, (byte)42);
        ProcessResult result = await RunChildAsync(options, "input-echo").ConfigureAwait(false);
        AssertExited(result, 0);
        TestAssert.Equal(expected, result.StandardOutput.Text);
        TestAssert.Equal(payload.Length, options.StandardInput.ByteCount);
        TestAssert.Equal(ProcessStandardInputMode.BoundedBytes, options.StandardInput.Mode);
    }

    [Test, DisplayName("closed and empty input send EOF")]
    public async Task ClosedInputSendsEofAsync()
    {
        foreach (ProcessStandardInput input in new[] { ProcessStandardInput.Closed,
            ProcessStandardInput.FromBytes(ReadOnlyMemory<byte>.Empty, 0) })
        {
            ProcessResult result = await RunChildAsync(
                new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10)) { StandardInput = input },
                "input-echo").ConfigureAwait(false);
            AssertExited(result, 0);
            TestAssert.Equal(string.Empty, result.StandardOutput.Text);
        }
    }

    [Test, DisplayName("default stdin is inherited by nested child")]
    public async Task DefaultInputIsInheritedAsync()
    {
        byte[] payload = Encoding.UTF8.GetBytes("inherited input\n");
        ProcessResult result = await RunChildAsync(
            new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10))
            {
                StandardInput = ProcessStandardInput.FromBytes(payload),
            }, "input-inherit").ConfigureAwait(false);
        AssertExited(result, 0);
        TestAssert.Equal(Convert.ToBase64String(payload), result.StandardOutput.Text);
    }

    [Test, DisplayName("large duplex input and output pressure cannot deadlock")]
    public async Task DuplexInputIsDrainedAsync()
    {
        const int count = 2 * 1024 * 1024;
        byte[] payload = new byte[count];
        Array.Fill(payload, (byte)'I');
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(15),
            standardOutputLimitBytes: 128, standardErrorLimitBytes: 256)
        {
            StandardInput = ProcessStandardInput.FromBytes(payload, count),
        };
        ProcessResult result = await RunChildAsync(options, "input-duplex",
            count.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        AssertExited(result, 0);
        AssertOutput(result.StandardOutput, 2L * count, 128, 'O', truncated: true);
        AssertOutput(result.StandardError, count, 256, 'E', truncated: true);
    }

    [Test, DisplayName("blocked stdin and silent child remain timeout and cancellation safe")]
    public async Task BlockedInputIsCancellableAsync()
    {
        ProcessStandardInput input = ProcessStandardInput.FromBytes(
            new byte[2 * 1024 * 1024], 2 * 1024 * 1024);
        foreach (bool cancel in new[] { false, true })
        {
            var options = new ProcessExecutionOptions(
                timeout: cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(250),
                drainGracePeriod: TimeSpan.FromSeconds(1)) { StandardInput = input };
            using var cancellation = new CancellationTokenSource();
            if (cancel)
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
            }

            var elapsed = Stopwatch.StartNew();
            ProcessResult result = await CreateRunner().RunAsync(
                CreateChildRequest(options, "sleep", "30000"), cancellationToken: cancellation.Token).ConfigureAwait(false);
            AssertTerminated(result, cancel ? ProcessState.Cancelled : ProcessState.TimedOut);
            TestAssert.Equal(0L, result.StandardOutput.ObservedByteCount);
            TestAssert.Equal(0L, result.StandardError.ObservedByteCount);
            TestAssert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Blocked stdin delayed cancellation.");
        }
    }

    [Test, DisplayName("descendant-held stdin does not block parent exit")]
    public async Task DescendantHeldInputDoesNotBlockAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"rcli-input-holder-{Guid.NewGuid():N}.txt");
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10),
            drainGracePeriod: TimeSpan.FromMilliseconds(300))
        {
            StandardInput = ProcessStandardInput.FromBytes(new byte[2 * 1024 * 1024], 2 * 1024 * 1024),
        };
        try
        {
            ProcessResult result = await RunChildAsync(options, "orphan-input-holder", markerPath).ConfigureAwait(false);
            AssertExited(result, 0);
            // runic-cli-sdk#30: the output read before a drain timeout is kept.
            TestAssert.Equal("READY", result.StandardOutput.Text.Trim());
            // The descendant redirects its own output, but on Windows it inherits every inheritable handle of the
            // child, including the child's stdout and stderr, and holds them; elsewhere it gets only stdin.
            TestAssert.Equal(OperatingSystem.IsWindows(), result.StandardOutput.DrainTimedOut);
            TestAssert.Equal(OperatingSystem.IsWindows(), result.StandardError.DrainTimedOut);
            TestAssert.True(result.Duration < TimeSpan.FromSeconds(5), "Descendant-held stdin delayed parent exit.");
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                TryKillProcess(int.Parse(await File.ReadAllTextAsync(markerPath).ConfigureAwait(false),
                    CultureInfo.InvariantCulture));
                File.Delete(markerPath);
            }
        }
    }

    [Test, DisplayName("early child exit tolerates a broken input pipe")]
    public async Task EarlyExitToleratesBrokenInputAsync()
    {
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10))
        {
            StandardInput = ProcessStandardInput.FromBytes(new byte[2 * 1024 * 1024], 2 * 1024 * 1024),
        };
        ProcessResult result = await RunChildAsync(options, "pressure", "5", "7", "23").ConfigureAwait(false);
        AssertExited(result, 23);
        TestAssert.Equal("OOOOO", result.StandardOutput.Text);
        TestAssert.Equal("EEEEEEE", result.StandardError.Text);
        TestAssert.Null(result.Fault);
    }

    [Test, DisplayName("environment inheritance isolation overrides and removal are explicit")]
    public async Task EnvironmentInheritanceIsExplicitAsync()
    {
        string inheritedName = $"RCLI_PROCESS_INHERITED_{Guid.NewGuid():N}";
        string removedName = $"RCLI_PROCESS_REMOVED_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(inheritedName, "parent-secret");
        Environment.SetEnvironmentVariable(removedName, "remove-me");
        try
        {
            foreach (bool inherit in new[] { true, false })
            {
                var environment = RuntimeEnvironment();
                environment[removedName] = null;
                environment["RCLI_PROCESS_EXPLICIT"] = "explicit-value";
                var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10))
                {
                    InheritEnvironment = inherit,
                    StandardInput = ProcessStandardInput.Closed,
                };
                ProcessResult result = await CreateRunner().RunAsync(new ProcessRequest(
                    ChildProcessFixture.ExecutablePath,
                    ChildProcessFixture.CreateArguments("environment", inheritedName, removedName,
                        "RCLI_PROCESS_EXPLICIT"), environment: environment, options: options)).ConfigureAwait(false);
                AssertExited(result, 0);
                TestAssert.SequenceEqual(new[] { inherit ? "parent-secret" : "<missing>", "<missing>", "explicit-value" },
                    result.StandardOutput.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            }

            var overridden = RuntimeEnvironment();
            overridden[inheritedName] = "child-value";
            ProcessResult overriddenResult = await CreateRunner().RunAsync(new ProcessRequest(
                ChildProcessFixture.ExecutablePath, ChildProcessFixture.CreateArguments("environment", inheritedName),
                environment: overridden)).ConfigureAwait(false);
            AssertExited(overriddenResult, 0);
            TestAssert.Equal("child-value", overriddenResult.StandardOutput.Text.Trim());
            TestAssert.Equal("parent-secret", Environment.GetEnvironmentVariable(inheritedName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedName, null);
            Environment.SetEnvironmentVariable(removedName, null);
        }
    }

    private static Dictionary<string, string?> RuntimeEnvironment()
    {
        var result = new Dictionary<string, string?>();
        foreach (string name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                result[name] = value;
            }
        }

        return result;
    }

    [Test, DisplayName("isolated executable resolution uses only supplied PATH")]
    public async Task IsolatedPathResolutionIsExplicitAsync()
    {
        var platform = new ProcessPlatform(false, _ => true, _ => "/inherited-tools");
        var policy = new CountingAllowPolicy();
        var runner = new ProcessRunner(policy, null, null, platform);
        var options = new ProcessExecutionOptions { InheritEnvironment = false };
        ProcessResult unresolved = await runner.RunAsync(new ProcessRequest("rcli-nonexistent-tool", options: options))
            .ConfigureAwait(false);
        TestAssert.Equal(ProcessState.StartFailed, unresolved.State);
        TestAssert.Equal(ProcessStartFailureCategory.NotFound, unresolved.StartFailureCategory);
        TestAssert.Equal(0, policy.EvaluationCount);
        await runner.RunAsync(new ProcessRequest("rcli-nonexistent-tool", environment:
            new Dictionary<string, string?> { ["PATH"] = "/explicit-tools" }, options: options)).ConfigureAwait(false);
        TestAssert.Equal(Path.GetFullPath("/explicit-tools/rcli-nonexistent-tool"), policy.LastFileName);

        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string name = Path.GetFileName(ChildProcessFixture.ExecutablePath);
            string directory = Path.GetDirectoryName(ChildProcessFixture.ExecutablePath)!;
            Environment.SetEnvironmentVariable("PATH", directory);
            ProcessResult isolated = await CreateRunner().RunAsync(new ProcessRequest(name,
                ChildProcessFixture.CreateArguments("pressure", "1", "1", "0"),
                environment: RuntimeEnvironment(), options: options)).ConfigureAwait(false);
            TestAssert.Equal(ProcessState.StartFailed, isolated.State);
            TestAssert.Equal(ProcessStartFailureCategory.NotFound, isolated.StartFailureCategory);
            var environment = RuntimeEnvironment();
            environment["PATH"] = directory;
            ProcessResult supplied = await CreateRunner().RunAsync(new ProcessRequest(name,
                ChildProcessFixture.CreateArguments("pressure", "1", "1", "0"),
                environment: environment, options: options)).ConfigureAwait(false);
            AssertExited(supplied, 0);
            ProcessResult inherited = await CreateRunner().RunAsync(new ProcessRequest(name,
                ChildProcessFixture.CreateArguments("pressure", "1", "1", "0"))).ConfigureAwait(false);
            AssertExited(inherited, 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    // SEC-38: the fixture executable lies in the current directory, which is also the application directory. A bare
    // name that no absolute PATH entry provides is not found there, with or without the inherited environment, and
    // a "." entry in PATH does not bring the current directory back. An explicit relative path still runs.
    [Test, DisplayName("bare names never run from the current or application directory")]
    public async Task BareNameDoesNotRunFromCurrentDirectoryAsync()
    {
        string name = Path.GetFileName(ChildProcessFixture.ExecutablePath);
        string directory = Path.GetDirectoryName(ChildProcessFixture.ExecutablePath)!;
        string emptyDirectory = Path.Combine(Path.GetTempPath(), "runic-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDirectory);
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            var observer = new RecordingObserver();
            var runner = new ProcessRunner(new LocalExecutablePolicy(), observer);
            string[] arguments = ChildProcessFixture.CreateArguments("pressure", "1", "1", "0");
            foreach (string path in new[] { emptyDirectory, ".", string.Empty })
            {
                Environment.SetEnvironmentVariable("PATH", path.Length == 0 ? null : path);
                foreach (bool inherit in new[] { true, false })
                {
                    var environment = RuntimeEnvironment();
                    environment["PATH"] = path.Length == 0 ? null : path;
                    var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(30)) { InheritEnvironment = inherit };
                    ProcessResult result = await runner.RunAsync(new ProcessRequest(
                        name, arguments, environment: inherit ? null : environment, options: options)).ConfigureAwait(false);
                    TestAssert.Equal(ProcessState.StartFailed, result.State);
                    TestAssert.Equal(ProcessStartFailureCategory.NotFound, result.StartFailureCategory);
                    TestAssert.Equal(ProcessFaultCodes.StartFailed, result.Fault!.Code);
                }
            }

            TestAssert.Equal(0, observer.StartedCount);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            AssertExited(await runner.RunAsync(new ProcessRequest("./" + name, arguments)).ConfigureAwait(false), 0);
            Environment.CurrentDirectory = Path.GetDirectoryName(directory)!;
            AssertExited(await runner.RunAsync(new ProcessRequest(
                Path.GetFileName(directory) + "/" + name, arguments)).ConfigureAwait(false), 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(emptyDirectory);
        }
    }

    // SEC-38 on Windows: CreateProcess would find "tool" as tool.exe in the application or current directory before
    // PATH. The runner resolves the name through PATH and PATHEXT itself and passes CreateProcess a full path.
    [Test, DisplayName("windows bare name present only in the current directory is not found")]
    public async Task WindowsBareNameInCurrentDirectoryIsNotFoundAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.GetDirectoryName(ChildProcessFixture.ExecutablePath)!;
        string name = Path.GetFileNameWithoutExtension(ChildProcessFixture.ExecutablePath);
        string[] arguments = ChildProcessFixture.CreateArguments("pressure", "1", "1", "0");
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            var observer = new RecordingObserver();
            var runner = new ProcessRunner(new LocalExecutablePolicy(), observer);
            var systemOnly = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = Environment.SystemDirectory,
                ["PATHEXT"] = ".COM;.EXE",
            };
            ProcessResult result = await runner.RunAsync(new ProcessRequest(name, arguments, environment: systemOnly))
                .ConfigureAwait(false);
            TestAssert.Equal(ProcessState.StartFailed, result.State);
            TestAssert.Equal(ProcessStartFailureCategory.NotFound, result.StartFailureCategory);
            TestAssert.Equal(0, observer.StartedCount);

            AssertExited(await runner.RunAsync(new ProcessRequest(@".\" + name + ".exe", arguments)).ConfigureAwait(false), 0);
            var withDirectory = new Dictionary<string, string?>(systemOnly, StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = Environment.SystemDirectory + ";" + directory,
            };
            AssertExited(await runner.RunAsync(new ProcessRequest(name, arguments, environment: withDirectory))
                .ConfigureAwait(false), 0);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Test, DisplayName("unresolved bare names are not found before the policy runs")]
    public async Task UnresolvedBareNameIsNotFoundAsync()
    {
        var policy = new CountingAllowPolicy();
        var observer = new RecordingObserver();
        var platform = new ProcessPlatform(false, static _ => true, static _ => ".:bin");
        var runner = new ProcessRunner(policy, observer, null, platform);

        ProcessResult result = await runner.RunAsync(new ProcessRequest("gh")).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.StartFailed, result.State);
        TestAssert.Equal(ProcessStartFailureCategory.NotFound, result.StartFailureCategory);
        TestAssert.Equal(ProcessFaultCodes.StartFailed, result.Fault!.Code);
        TestAssert.Equal(0, policy.EvaluationCount);
        TestAssert.SequenceEqual(StartFailedCompletion, observer.Events);

        await runner.RunAsync(new ProcessRequest("./gh")).ConfigureAwait(false);
        TestAssert.Equal(Path.GetFullPath("./gh"), policy.LastFileName);
    }

    [Test, DisplayName("timeout terminates the child and is distinct")]
    public async Task TimeoutIsDistinctAsync()
    {
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromMilliseconds(150),
            drainGracePeriod: TimeSpan.FromSeconds(2));

        ProcessResult result = await RunChildAsync(options, "sleep", "5000").ConfigureAwait(false);

        AssertTerminated(result, ProcessState.TimedOut);
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(4), "Timeout did not bound execution.");
    }

    [Test, DisplayName("external cancellation terminates the child and is distinct")]
    public async Task ExternalCancellationIsDistinctAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        ProcessResult result = await CreateRunner()
            .RunAsync(CreateChildRequest("sleep", "5000"), cancellationToken: cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.Cancelled);
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(4), "Cancellation did not bound execution.");
    }

    [Test, DisplayName("pre-cancellation does not start a child")]
    public async Task PreCancellationDoesNotStartAsync()
    {
        var observer = new RecordingObserver();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        ProcessResult result = await CreateRunner(observer)
            .RunAsync(CreateChildRequest("sleep", "1"), cancellationToken: cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.Cancelled);
        TestAssert.SequenceEqual(CancelledCompletion, observer.Events);
    }

    [Test, DisplayName("cancellation during policy evaluation prevents start")]
    public async Task CancellationDuringPolicyPreventsStartAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var policy = new BlockingAllowPolicy();
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(policy, observer);

        Task<ProcessResult> execution = Task.Run(async () =>
            await runner.RunAsync(CreateChildRequest("sleep", "5"), cancellationToken: cancellation.Token).ConfigureAwait(false));
        TestAssert.True(policy.WaitUntilEntered(TimeSpan.FromSeconds(10)), "Policy evaluation did not begin.");
        cancellation.Cancel();
        policy.Release();

        ProcessResult result = await execution.ConfigureAwait(false);
        TestAssert.Equal(ProcessState.Cancelled, result.State);
        AssertObserverExactlyOnce(observer, ProcessState.Cancelled, expectsStart: false);
    }

    [Test, DisplayName("cancellation and process-exit races have one terminal result")]
    public async Task CancellationExitRaceIsStableAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var observer = new RecordingObserver();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            ProcessResult result = await CreateRunner(observer)
                .RunAsync(CreateChildRequest("sleep", "80", "7"), cancellationToken: cancellation.Token)
                .ConfigureAwait(false);

            TestAssert.True(
                result.State is ProcessState.Exited or ProcessState.Cancelled,
                $"Unexpected race state {result.State}.");
            AssertExitCodeMatchesState(result, 7);
            AssertObserverExactlyOnce(observer, result.State, expectsStart: true);
        }
    }

    [Test, DisplayName("timeout and process-exit races have one terminal result")]
    public async Task TimeoutExitRaceIsStableAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var observer = new RecordingObserver();
            var options = new ProcessExecutionOptions(timeout: TimeSpan.FromMilliseconds(80));
            ProcessResult result = await CreateRunner(observer)
                .RunAsync(CreateChildRequest(options, "sleep", "80", "9"))
                .ConfigureAwait(false);

            TestAssert.True(
                result.State is ProcessState.Exited or ProcessState.TimedOut,
                $"Unexpected race state {result.State}.");
            AssertExitCodeMatchesState(result, 9);
            AssertObserverExactlyOnce(observer, result.State, expectsStart: true);
        }
    }

    [Test, DisplayName("caller cancellation wins over a later timeout")]
    public async Task CallerCancellationWinsAsync()
    {
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        ProcessResult result = await CreateRunner()
            .RunAsync(CreateChildRequest(options, "sleep", "5000"), cancellationToken: cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.Cancelled);
    }

    [Test, DisplayName("timeout wins over later caller cancellation")]
    public async Task TimeoutWinsAsync()
    {
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        ProcessResult result = await CreateRunner()
            .RunAsync(CreateChildRequest(options, "sleep", "5000"), cancellationToken: cancellation.Token)
            .ConfigureAwait(false);

        AssertTerminated(result, ProcessState.TimedOut);
    }

    [Test, DisplayName("process-tree timeout terminates descendants")]
    public async Task ProcessTreeIsTerminatedAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"wut-cli-tree-{Guid.NewGuid():N}.txt");
        string startedMarkerPath = string.Concat(markerPath, ".started");
        int? descendantProcessId = null;
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(10),
            drainGracePeriod: TimeSpan.FromSeconds(2));

        try
        {
            ProcessResult result = await RunChildAsync(
                options,
                "tree-parent",
                markerPath,
                "30000").ConfigureAwait(false);

            AssertTerminated(result, ProcessState.TimedOut);
            TestAssert.True(File.Exists(startedMarkerPath), "The descendant was not started before timeout.");
            descendantProcessId = int.Parse(
                await File.ReadAllTextAsync(startedMarkerPath).ConfigureAwait(false),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            TestAssert.True(
                await WaitForProcessExitAsync(descendantProcessId.Value, TimeSpan.FromSeconds(3)).ConfigureAwait(false),
                "A descendant remained alive after process-tree termination.");
            TestAssert.False(File.Exists(markerPath), "A descendant survived process-tree termination.");
        }
        finally
        {
            if (descendantProcessId is not null)
            {
                TryKillProcess(descendantProcessId.Value);
            }

            File.Delete(markerPath);
            File.Delete(startedMarkerPath);
        }
    }

    [Test, DisplayName("timestamps and duration bracket execution")]
    public async Task TimestampsBracketExecutionAsync()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        ProcessResult result = await RunChildAsync("sleep", "100").ConfigureAwait(false);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        AssertExited(result, 0);
        TestAssert.True(result.StartedAt >= before);
        TestAssert.True(result.EndedAt <= after);
        TestAssert.True(result.EndedAt >= result.StartedAt);
        TestAssert.Equal(result.EndedAt - result.StartedAt, result.Duration);
        TestAssert.True(result.Duration >= TimeSpan.FromMilliseconds(50));
    }

    [Test, DisplayName("observer notifications are ordered and exactly once")]
    public async Task ObserverIsOrderedAndExactlyOnceAsync()
    {
        var observer = new RecordingObserver();
        ProcessResult result = await CreateRunner(observer)
            .RunAsync(CreateChildRequest("sleep", "20"))
            .ConfigureAwait(false);

        AssertExited(result, 0);
        AssertObserverExactlyOnce(observer, ProcessState.Exited, expectsStart: true);
        TestAssert.Equal(result.StartedAt, observer.StartedAt);
        TestAssert.Equal(result.Duration, observer.CompletedDuration);
    }

    [Test, DisplayName("terminal-only paths notify completion exactly once")]
    public async Task TerminalOnlyPathsNotifyExactlyOnceAsync()
    {
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(new RecordingRejectPolicy(), observer);

        ProcessResult result = await runner.RunAsync(CreateChildRequest("sleep", "1")).ConfigureAwait(false);

        TestAssert.Equal(ProcessState.Rejected, result.State);
        AssertObserverExactlyOnce(observer, ProcessState.Rejected, expectsStart: false);
    }

    [Test, DisplayName("observer exceptions do not corrupt process completion")]
    public async Task ObserverExceptionsAreContainedAsync()
    {
        var observer = new ThrowingObserver();
        ProcessResult result = await CreateRunner(observer)
            .RunAsync(CreateChildRequest("sleep", "1"))
            .ConfigureAwait(false);

        AssertExited(result, 0);
        TestAssert.Equal(1, observer.StartedCount);
        TestAssert.Equal(1, observer.CompletedCount);
    }

    [Test, DisplayName("windows batch files resolved through PATHEXT are rejected")]
    public async Task WindowsBatchFilesAreRejectedAsync()
    {
        var policy = new CountingAllowPolicy();
        var observer = new RecordingObserver();
        var runner = new ProcessRunner(policy, observer, null, CreateWindowsPlatform("npm.cmd"));

        ProcessResult resolved = await runner.RunAsync(
            new ProcessRequest("npm", ["view", "x&calc"], environment: FakePath)).ConfigureAwait(false);
        ProcessResult explicitPath = await runner.RunAsync(
            new ProcessRequest(Path.Combine(FakeBinDirectory, "build.BAT. "))).ConfigureAwait(false);

        foreach (ProcessResult result in new[] { resolved, explicitPath })
        {
            TestAssert.Equal(ProcessState.Rejected, result.State);
            TestAssert.Equal(ProcessFaultCodes.BatchFileRejected, result.Fault!.Code);
        }

        TestAssert.Equal(0, policy.EvaluationCount);
        TestAssert.Equal(0, observer.StartedCount);
    }

    [Test, DisplayName("windows batch opt-in rejects cmd.exe metacharacters")]
    public async Task WindowsBatchOptInRejectsMetacharactersAsync()
    {
        var runner = new ProcessRunner(new CountingAllowPolicy(), null, null, CreateWindowsPlatform("npm.cmd"));
        var options = new ProcessExecutionOptions(allowWindowsBatchFiles: true);

        foreach (string argument in BatchMetacharacterArguments)
        {
            ProcessResult result = await runner.RunAsync(
                new ProcessRequest("npm", ["view", argument], environment: FakePath, options: options))
                .ConfigureAwait(false);

            TestAssert.Equal(ProcessState.Rejected, result.State);
            TestAssert.Equal(ProcessFaultCodes.BatchFileArgumentRejected, result.Fault!.Code);
            TestAssert.Equal("1", result.Fault.Details["argumentIndex"]);
        }

        ProcessResult path = await runner.RunAsync(
            new ProcessRequest(Path.Combine(FakeBinDirectory, "100%", "tool.cmd"), options: options))
            .ConfigureAwait(false);
        TestAssert.Equal(ProcessFaultCodes.BatchFileArgumentRejected, path.Fault!.Code);
    }

    [Test, DisplayName("windows batch opt-in admits plain arguments")]
    public async Task WindowsBatchOptInAdmitsPlainArgumentsAsync()
    {
        var policy = new CountingAllowPolicy();
        var runner = new ProcessRunner(policy, null, null, CreateWindowsPlatform("npm.cmd"));
        var options = new ProcessExecutionOptions(allowWindowsBatchFiles: true);

        ProcessResult result = await runner.RunAsync(
            new ProcessRequest("npm", ["view", "left pad", "@scope/name@1.0.0"], environment: FakePath, options: options))
            .ConfigureAwait(false);

        // The fake resolved path does not exist, so admission ends in a real start failure.
        TestAssert.Equal(1, policy.EvaluationCount);
        TestAssert.Equal(ProcessState.StartFailed, result.State);
        TestAssert.Equal(Path.GetFullPath(Path.Combine(FakeBinDirectory, "npm.CMD")), policy.LastFileName);
    }

    // SEC-44: Windows names a file's unnamed data stream "tool.cmd::$DATA" and ignores trailing dots and spaces.
    [Test, DisplayName("windows batch detection sees through stream suffixes and trailing dots and spaces")]
    public async Task WindowsBatchStreamSuffixesAreRejectedAsync()
    {
        var policy = new CountingAllowPolicy();
        var runner = new ProcessRunner(policy, null, null, CreateWindowsPlatform("npm.cmd"));
        foreach (string name in new[] { "build.cmd::$DATA", "build.CMD .::$DATA", "build.bat:stream", "build.Bat:stream:$DATA", "build.cmd. . " })
        {
            ProcessResult result = await runner.RunAsync(new ProcessRequest(Path.Combine(FakeBinDirectory, name))).ConfigureAwait(false);
            TestAssert.Equal(ProcessState.Rejected, result.State);
            TestAssert.Equal(ProcessFaultCodes.BatchFileRejected, result.Fault!.Code);
        }

        TestAssert.Equal(0, policy.EvaluationCount);
        foreach (string name in new[] { "build.exe", "build.cmd.exe", "build.exe::$DATA", "cmd", "bat" })
        {
            TestAssert.False(ProcessRunner.IsBatchFile(Path.Combine(FakeBinDirectory, name)), name);
        }
    }

    // SEC-44: a real batch file through cmd.exe. Without the opt-in it is rejected, also through PATHEXT and a stream
    // suffix; with it, cmd.exe metacharacters are rejected and plain arguments reach the script, also from a
    // directory whose name contains & and ^.
    [Test, DisplayName("windows batch file runs through cmd.exe only with the opt-in")]
    public async Task RealWindowsBatchFileAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "runic-batch-" + Guid.NewGuid().ToString("N"), "a&b^c");
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "echo-args.cmd");
        string marker = Path.Combine(directory, "injected.txt");
        await File.WriteAllTextAsync(script, "@echo off\r\necho RUNIC-BATCH-RAN\r\necho [%*]\r\n", Encoding.ASCII).ConfigureAwait(false);
        try
        {
            var observer = new RecordingObserver();
            var runner = new ProcessRunner(new LocalExecutablePolicy(), observer);
            var path = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = directory, ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD" };
            foreach (ProcessRequest request in new[]
            {
                new ProcessRequest(script, ["plain"]),
                new ProcessRequest("echo-args", ["plain"], environment: path),
                new ProcessRequest(script + "::$DATA", ["plain"]),
            })
            {
                ProcessResult rejected = await runner.RunAsync(request).ConfigureAwait(false);
                TestAssert.Equal(ProcessState.Rejected, rejected.State);
                TestAssert.Equal(ProcessFaultCodes.BatchFileRejected, rejected.Fault!.Code);
            }

            TestAssert.Equal(0, observer.StartedCount);
            var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(30), allowWindowsBatchFiles: true);
            foreach (string argument in BatchMetacharacterArguments.Append("x & echo injected > \"" + marker + "\""))
            {
                ProcessResult result = await runner.RunAsync(new ProcessRequest(script, ["ok", argument], options: options))
                    .ConfigureAwait(false);
                TestAssert.Equal(ProcessState.Rejected, result.State);
                TestAssert.Equal(ProcessFaultCodes.BatchFileArgumentRejected, result.Fault!.Code);
                TestAssert.Equal("1", result.Fault.Details["argumentIndex"]);
            }

            TestAssert.Equal(0, observer.StartedCount);
            ProcessResult ran = await runner.RunAsync(
                new ProcessRequest(script, ["left pad", @"C:\trailing\", "two  spaces", "@scope/name@1.0.0"], options: options))
                .ConfigureAwait(false);
            AssertExited(ran, 0);
            string output = ran.StandardOutput.Text;
            TestAssert.True(output.Contains("RUNIC-BATCH-RAN", StringComparison.Ordinal), output);
            TestAssert.True(output.Contains("left pad", StringComparison.Ordinal), output);
            TestAssert.True(output.Contains("two  spaces", StringComparison.Ordinal), output);
            TestAssert.True(output.Contains("@scope/name@1.0.0", StringComparison.Ordinal), output);
            TestAssert.False(File.Exists(marker), "A batch argument was interpreted by cmd.exe.");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    // SEC-39: the process exit handler kills the children of runs that have not returned.
    [Test, DisplayName("exit handler kills the children of running requests")]
    public async Task ExitHandlerKillsLiveChildrenAsync()
    {
        var observer = new RecordingObserver();
        var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(30), drainGracePeriod: TimeSpan.FromSeconds(2));
        ValueTask<ProcessResult> pending = CreateRunner(observer).RunAsync(CreateChildRequest(options, "sleep", "30000"));
        Task<ProcessResult> run = pending.AsTask();
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (Runic.CommandLine.Processes.Internal.LiveChildren.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        TestAssert.Equal(1, Runic.CommandLine.Processes.Internal.LiveChildren.Count);
        Runic.CommandLine.Processes.Internal.LiveChildren.KillAll();
        ProcessResult result = await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        TestAssert.Equal(ProcessState.Exited, result.State);
        TestAssert.True(result.ExitCode is not null and not 0, "The killed child reported success.");
        TestAssert.True(result.Duration < TimeSpan.FromSeconds(10), "The child was not killed.");
        TestAssert.Equal(0, Runic.CommandLine.Processes.Internal.LiveChildren.Count);
    }

    // SEC-39: a forced exit while the runner still waits for its child, as CommandApp's exit on a repeated signal,
    // kills the child. On Windows the job also ends the child when the parent ends without exit handlers.
    [Test, DisplayName("forced parent exit does not leave the child running")]
    public async Task ForcedExitKillsChildAsync()
    {
        string[] modes = OperatingSystem.IsWindows() ? ["exit", "terminate"] : ["exit"];
        foreach (string mode in modes)
        {
            string identityPath = Path.Combine(Path.GetTempPath(), $"wut-cli-exit-{Guid.NewGuid():N}.txt");
            int? childProcessId = null;
            try
            {
                using var parent = Process.Start(new ProcessStartInfo(ChildProcessFixture.ExecutablePath,
                    ChildProcessFixture.CreateArguments("exit-during-run", mode, identityPath))
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                })!;
                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                TestAssert.True(File.Exists(identityPath), "The parent did not start its child (exit code " +
                    parent.ExitCode.ToString(CultureInfo.InvariantCulture) + ").");
                childProcessId = int.Parse(
                    await File.ReadAllTextAsync(identityPath).ConfigureAwait(false), NumberStyles.None, CultureInfo.InvariantCulture);
                TestAssert.True(
                    await WaitForProcessExitAsync(childProcessId.Value, TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                    "The child outlived the parent's forced exit (" + mode + ").");
            }
            finally
            {
                if (childProcessId is not null)
                {
                    TryKillProcess(childProcessId.Value);
                }

                File.Delete(identityPath);
            }
        }
    }

    [Test, DisplayName("batch extensions are ordinary files outside windows")]
    public async Task BatchExtensionsAreOrdinaryOutsideWindowsAsync()
    {
        var policy = new CountingAllowPolicy();
        var platform = new ProcessPlatform(false, static _ => false, static _ => null);
        var runner = new ProcessRunner(policy, null, null, platform);

        ProcessResult result = await runner.RunAsync(
            new ProcessRequest(Path.Combine(FakeBinDirectory, "tool.cmd"), ["x&calc"])).ConfigureAwait(false);

        TestAssert.Equal(1, policy.EvaluationCount);
        TestAssert.Equal(ProcessState.StartFailed, result.State);
    }

    [Test, DisplayName("descendant-held pipes report a drain timeout")]
    public async Task DescendantHeldPipeReportsDrainTimeoutAsync()
    {
        string markerPath = Path.Combine(Path.GetTempPath(), $"wut-cli-holder-{Guid.NewGuid():N}.txt");
        var options = new ProcessExecutionOptions(
            timeout: TimeSpan.FromSeconds(20),
            drainGracePeriod: TimeSpan.FromMilliseconds(300));
        try
        {
            ProcessResult result = await RunChildAsync(options, "orphan-pipe-holder", markerPath).ConfigureAwait(false);

            AssertExited(result, 0);
            TestAssert.True(result.StandardOutput.Text.StartsWith("READY", StringComparison.Ordinal));
            TestAssert.True(result.StandardOutput.DrainTimedOut, "A descendant-held stdout did not report a drain timeout.");
            TestAssert.True(result.StandardError.DrainTimedOut, "A descendant-held stderr did not report a drain timeout.");
            TestAssert.True(result.Duration < TimeSpan.FromSeconds(10), "Drain grace did not bound completion.");

            ProcessResult normal = await RunChildAsync("pressure", "1", "1", "0").ConfigureAwait(false);
            TestAssert.False(normal.StandardOutput.DrainTimedOut);
            TestAssert.False(normal.StandardError.DrainTimedOut);
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                TryKillProcess(int.Parse(
                    await File.ReadAllTextAsync(markerPath).ConfigureAwait(false),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture));
                File.Delete(markerPath);
            }
        }
    }

    [Test, DisplayName("timeout uses the injected time provider")]
    public async Task TimeoutUsesInjectedTimeProviderAsync()
    {
        TimeSpan timeout = TimeSpan.FromHours(1);
        var time = new ManualTimeProvider();
        var runner = new ProcessRunner(new LocalExecutablePolicy(), null, time);
        Task<ProcessResult> run = runner.RunAsync(
            CreateChildRequest(new ProcessExecutionOptions(timeout: timeout), "sleep", "30000")).AsTask();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!time.Fire(timeout))
        {
            TestAssert.True(DateTimeOffset.UtcNow < deadline, "The runner did not create its timeout timer.");
            await Task.Delay(10).ConfigureAwait(false);
        }

        ProcessResult result = await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        AssertTerminated(result, ProcessState.TimedOut);
    }

    private const string FakeBinDirectory = "/runic-fake-bin";

    private static readonly Dictionary<string, string?> FakePath = new(StringComparer.Ordinal)
    {
        ["PATH"] = FakeBinDirectory,
        ["PATHEXT"] = null,
    };

    private static ProcessPlatform CreateWindowsPlatform(string existingFileName) =>
        new(
            true,
            path => path.EndsWith(Path.DirectorySeparatorChar + existingFileName, StringComparison.OrdinalIgnoreCase),
            static _ => null);

    private static ProcessRunner CreateRunner(IProcessObserver? observer = null) =>
        new(new LocalExecutablePolicy(), observer);

    private static ProcessRequest CreateChildRequest(string mode, params string[] arguments) =>
        new(ChildProcessFixture.ExecutablePath, ChildProcessFixture.CreateArguments(mode, arguments));

    private static ProcessRequest CreateChildRequest(
        ProcessExecutionOptions options,
        string mode,
        params string[] arguments) =>
        new(
            ChildProcessFixture.ExecutablePath,
            ChildProcessFixture.CreateArguments(mode, arguments),
            options: options);

    private static async Task<ProcessResult> RunChildAsync(string mode, params string[] arguments) =>
        await CreateRunner().RunAsync(CreateChildRequest(mode, arguments)).ConfigureAwait(false);

    private static async Task<ProcessResult> RunChildAsync(
        ProcessExecutionOptions options,
        string mode,
        params string[] arguments) =>
        await CreateRunner().RunAsync(CreateChildRequest(options, mode, arguments)).ConfigureAwait(false);

    private static string[] DecodeArgumentEcho(string output)
    {
        using var reader = new StringReader(output);
        int count = int.Parse(reader.ReadLine()!, NumberStyles.None, CultureInfo.InvariantCulture);
        var arguments = new string[count];

        for (var index = 0; index < arguments.Length; index++)
        {
            string encoded = reader.ReadLine()
                ?? throw new InvalidOperationException("The argument echo ended early.");
            arguments[index] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }

        TestAssert.Null(reader.ReadLine());
        return arguments;
    }

    private static void AssertExited(ProcessResult result, int expectedExitCode)
    {
        TestAssert.Equal(ProcessState.Exited, result.State);
        TestAssert.Equal<int?>(expectedExitCode, result.ExitCode);
        TestAssert.Null(result.StartFailureCategory);
        TestAssert.Null(result.Fault);
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void AssertTerminated(ProcessResult result, ProcessState expectedState)
    {
        TestAssert.Equal(expectedState, result.State);
        TestAssert.Null(result.ExitCode);
        TestAssert.Null(result.StartFailureCategory);
    }

    private static void AssertOutput(
        ProcessOutput output,
        long expectedObservedBytes,
        int expectedRetainedBytes,
        char expectedCharacter,
        bool truncated)
    {
        TestAssert.Equal(expectedObservedBytes, output.ObservedByteCount);
        TestAssert.Equal(expectedRetainedBytes, Encoding.UTF8.GetByteCount(output.Text));
        TestAssert.True(output.Text.All(character => character == expectedCharacter));
        TestAssert.Equal(truncated, output.IsTruncated);
    }

    private static void AssertExitCodeMatchesState(ProcessResult result, int expectedExitCode)
    {
        if (result.State == ProcessState.Exited)
        {
            TestAssert.Equal<int?>(expectedExitCode, result.ExitCode);
        }
        else
        {
            TestAssert.Null(result.ExitCode);
        }
    }

    private static void AssertObserverExactlyOnce(
        RecordingObserver observer,
        ProcessState state,
        bool expectsStart)
    {
        string[] expected = expectsStart
            ? ["started", $"completed:{state}"]
            : [$"completed:{state}"];
        TestAssert.SequenceEqual(expected, observer.Events);
        TestAssert.Equal(expectsStart ? 1 : 0, observer.StartedCount);
        TestAssert.Equal(1, observer.CompletedCount);
    }

    private sealed class RecordingRejectPolicy : IExecutablePolicy
    {
        public int EvaluationCount { get; private set; }

        public ExecutablePolicyDecision Evaluate(ProcessRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            EvaluationCount++;
            return ExecutablePolicyDecision.Reject(
                "secret-policy-code",
                "secret\u001b[31m\r\npolicy detail");
        }
    }

    private sealed class CountingAllowPolicy : IExecutablePolicy
    {
        public int EvaluationCount { get; private set; }

        public string? LastFileName { get; private set; }

        public ExecutablePolicyDecision Evaluate(ProcessRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            EvaluationCount++;
            LastFileName = request.FileName;
            return ExecutablePolicyDecision.Allow();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (timers)
            {
                timers.Add(timer);
            }

            return timer;
        }

        public bool Fire(TimeSpan dueTime)
        {
            ManualTimer[] due;
            lock (timers)
            {
                due = timers.Where(timer => timer.DueTime == dueTime).ToArray();
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }

            return due.Length != 0;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private bool disposed;

        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Fire()
        {
            if (!disposed)
            {
                callback(state);
            }
        }

        public void Dispose() => disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingAllowPolicy : IExecutablePolicy, IDisposable
    {
        private readonly ManualResetEventSlim entered = new(false);
        private readonly ManualResetEventSlim release = new(false);

        public ExecutablePolicyDecision Evaluate(ProcessRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            entered.Set();
            release.Wait();
            return ExecutablePolicyDecision.Allow();
        }

        public bool WaitUntilEntered(TimeSpan timeout) => entered.Wait(timeout);

        public void Release() => release.Set();

        public void Dispose()
        {
            entered.Dispose();
            release.Dispose();
        }
    }

    private sealed class RecordingObserver : IProcessObserver
    {
        private readonly List<string> events = [];

        public IReadOnlyList<string> Events => events;

        public int StartedCount { get; private set; }

        public int CompletedCount { get; private set; }

        public DateTimeOffset StartedAt { get; private set; }

        public TimeSpan CompletedDuration { get; private set; }

        public void OnStarted(ProcessIdentity process, DateTimeOffset startedAt)
        {
            StartedCount++;
            StartedAt = startedAt;
            events.Add("started");
        }

        public void OnCompleted(ProcessState state, TimeSpan duration)
        {
            CompletedCount++;
            CompletedDuration = duration;
            events.Add($"completed:{state}");
        }
    }

    private sealed class ThrowingObserver : IProcessObserver
    {
        public int StartedCount { get; private set; }

        public int CompletedCount { get; private set; }

        public void OnStarted(ProcessIdentity process, DateTimeOffset startedAt)
        {
            StartedCount++;
            throw new InvalidOperationException("observer start failure");
        }

        public void OnCompleted(ProcessState state, TimeSpan duration)
        {
            CompletedCount++;
            throw new InvalidOperationException("observer completion failure");
        }
    }
}
