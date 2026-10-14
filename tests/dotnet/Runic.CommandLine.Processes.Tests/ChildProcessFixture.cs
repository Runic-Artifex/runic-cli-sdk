using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes.Tests;

internal static class ChildProcessFixture
{
    private const string ChildSwitch = "--process-test-child";

    public static bool IsChildInvocation(string[] args)
    {
        return args.Length >= 2 && string.Equals(args[0], ChildSwitch, StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        return args[1] switch
        {
            "echo-arguments" => EchoArguments(args.AsSpan(2)),
            "input-echo" => await EchoInputAsync().ConfigureAwait(false),
            "input-duplex" => await DuplexAsync(args).ConfigureAwait(false),
            "input-inherit" => await InheritInputAsync().ConfigureAwait(false),
            "environment" => EchoEnvironment(args),
            "pressure" => await WritePressureAsync(args).ConfigureAwait(false),
            "sleep" => await SleepAsync(args).ConfigureAwait(false),
            "tree-parent" => await RunTreeParentAsync(args).ConfigureAwait(false),
            "tree-leaf" => await RunTreeLeafAsync(args).ConfigureAwait(false),
            "orphan-input-holder" => await StartPipeHolderAsync(args, isolateOutputs: true).ConfigureAwait(false),
            "orphan-pipe-holder" => await StartPipeHolderAsync(args).ConfigureAwait(false),
            "interleave" => await InterleaveAsync(args).ConfigureAwait(false),
            "flood-lines" => await FloodLinesAsync(args).ConfigureAwait(false),
            "orphan-tree" => await RunOrphanTreeAsync(args).ConfigureAwait(false),
            "orphan-middle" => StartOrphanLeaf(args),
            "term-aware" => await RunTermAwareAsync(args).ConfigureAwait(false),
            _ => 97,
        };
    }

    public static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("The executable path is unavailable.");

    public static string[] CreateArguments(string mode, params string[] arguments)
    {
        var result = new string[arguments.Length + 2];
        result[0] = ChildSwitch;
        result[1] = mode;
        arguments.CopyTo(result, 2);
        return result;
    }

    private static int EchoArguments(ReadOnlySpan<string> arguments)
    {
        Console.WriteLine(arguments.Length.ToString(CultureInfo.InvariantCulture));

        foreach (string argument in arguments)
        {
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)));
        }

        return 0;
    }

    private static async Task<int> EchoInputAsync()
    {
        using var input = new MemoryStream();
        await Console.OpenStandardInput().CopyToAsync(input).ConfigureAwait(false);
        Console.Write(Convert.ToBase64String(input.ToArray()));
        return 0;
    }

    private static async Task<int> DuplexAsync(string[] args)
    {
        int count = ParseNonNegativeInt32(args, 2);
        // Fill both output pipes before reading input: a runner that awaits its input write first deadlocks.
        await Task.WhenAll(
            WriteBytesAsync(Console.OpenStandardOutput(), (byte)'O', count, 4096),
            WriteBytesAsync(Console.OpenStandardError(), (byte)'E', count, 4096)).ConfigureAwait(false);
        await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput()).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> InheritInputAsync()
    {
        var runner = new ProcessRunner(new LocalExecutablePolicy());
        ProcessResult result = await runner.RunAsync(new ProcessRequest(
            ExecutablePath,
            CreateArguments("input-echo"),
            options: new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(10)))).ConfigureAwait(false);
        Console.Write(result.StandardOutput.Text);
        return result.State == ProcessState.Exited ? result.ExitCode ?? 96 : 95;
    }

    private static int EchoEnvironment(string[] args)
    {
        for (int index = 2; index < args.Length; index++)
        {
            Console.WriteLine(Environment.GetEnvironmentVariable(args[index]) ?? "<missing>");
        }

        return 0;
    }

    private static async Task<int> WritePressureAsync(string[] args)
    {
        int standardOutputBytes = ParseNonNegativeInt32(args, 2);
        int standardErrorBytes = ParseNonNegativeInt32(args, 3);
        int exitCode = ParseNonNegativeInt32(args, 4);
        int chunkSize = args.Length > 5 ? ParsePositiveInt32(args, 5) : 4096;

        Task output = WriteBytesAsync(Console.OpenStandardOutput(), (byte)'O', standardOutputBytes, chunkSize);
        Task error = WriteBytesAsync(Console.OpenStandardError(), (byte)'E', standardErrorBytes, chunkSize);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> SleepAsync(string[] args)
    {
        int delayMilliseconds = ParseNonNegativeInt32(args, 2);
        int exitCode = args.Length > 3 ? ParseNonNegativeInt32(args, 3) : 0;
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> RunTreeParentAsync(string[] args)
    {
        string markerPath = args[2];
        string delayMilliseconds = args[3];
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add(ChildSwitch);
        process.StartInfo.ArgumentList.Add("tree-leaf");
        process.StartInfo.ArgumentList.Add(markerPath);
        process.StartInfo.ArgumentList.Add(delayMilliseconds);

        if (!process.Start())
        {
            return 96;
        }

        await File.WriteAllTextAsync(
            string.Concat(markerPath, ".started"),
            process.Id.ToString(CultureInfo.InvariantCulture),
            Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine("READY");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    // Starts a descendant that inherits stdout/stderr and exits while the descendant keeps them open.
    private static async Task<int> StartPipeHolderAsync(string[] args, bool isolateOutputs = false)
    {
        string markerPath = args[2];
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add(ChildSwitch);
        process.StartInfo.RedirectStandardOutput = isolateOutputs;
        process.StartInfo.RedirectStandardError = isolateOutputs;
        process.StartInfo.ArgumentList.Add("sleep");
        process.StartInfo.ArgumentList.Add("30000");

        if (!process.Start())
        {
            return 96;
        }

        await File.WriteAllTextAsync(
            markerPath,
            process.Id.ToString(CultureInfo.InvariantCulture),
            Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine("READY");
        return 0;
    }

    // Writes "out-N" and "err-N" lines alternately, with a pause after each, flushing every line.
    private static async Task<int> InterleaveAsync(string[] args)
    {
        int count = ParseNonNegativeInt32(args, 2);
        int pauseMilliseconds = ParseNonNegativeInt32(args, 3);
        using Stream output = Console.OpenStandardOutput();
        using Stream error = Console.OpenStandardError();
        for (int index = 0; index < count; index++)
        {
            await WriteLineAsync(output, $"out-{index}").ConfigureAwait(false);
            await Task.Delay(pauseMilliseconds).ConfigureAwait(false);
            await WriteLineAsync(error, $"err-{index}").ConfigureAwait(false);
            await Task.Delay(pauseMilliseconds).ConfigureAwait(false);
        }

        return 0;
    }

    // Writes numbered 64-byte lines to stdout as fast as the pipe accepts them.
    private static async Task<int> FloodLinesAsync(string[] args)
    {
        int count = ParseNonNegativeInt32(args, 2);
        using Stream output = Console.OpenStandardOutput();
        byte[] line = new byte[64];
        for (int index = 0; index < count; index++)
        {
            Encoding.ASCII.GetBytes(index.ToString("D8", CultureInfo.InvariantCulture)).CopyTo(line, 0);
            line.AsSpan(8, 55).Fill((byte)'x');
            line[63] = (byte)'\n';
            await output.WriteAsync(line).ConfigureAwait(false);
        }

        await output.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    // Starts a middle process that starts a sleeping leaf and exits at once, so the leaf is reparented and a walk
    // of the process tree from this process no longer finds it. Prints READY with the leaf's PID, then sleeps.
    private static async Task<int> RunOrphanTreeAsync(string[] args)
    {
        string markerPath = args[2];
        using var middle = new Process();
        middle.StartInfo = new ProcessStartInfo { FileName = ExecutablePath, UseShellExecute = false };
        middle.StartInfo.ArgumentList.Add(ChildSwitch);
        middle.StartInfo.ArgumentList.Add("orphan-middle");
        middle.StartInfo.ArgumentList.Add(markerPath);
        if (!middle.Start())
        {
            return 96;
        }

        await middle.WaitForExitAsync().ConfigureAwait(false);
        Console.WriteLine($"READY {await File.ReadAllTextAsync(markerPath).ConfigureAwait(false)}");
        await Task.Delay(30000).ConfigureAwait(false);
        return 0;
    }

    private static int StartOrphanLeaf(string[] args)
    {
        using var leaf = new Process();
        leaf.StartInfo = new ProcessStartInfo { FileName = ExecutablePath, UseShellExecute = false };
        leaf.StartInfo.ArgumentList.Add(ChildSwitch);
        leaf.StartInfo.ArgumentList.Add("sleep");
        leaf.StartInfo.ArgumentList.Add("30000");
        if (!leaf.Start())
        {
            return 96;
        }

        File.WriteAllText(args[2], leaf.Id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        return 0;
    }

    // Handles SIGTERM: "exit" records it in the marker file and exits, "ignore" records it and keeps running.
    private static async Task<int> RunTermAwareAsync(string[] args)
    {
        string markerPath = args[2];
        bool ignore = string.Equals(args[3], "ignore", StringComparison.Ordinal);
        using var termination = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context =>
            {
                File.WriteAllText(markerPath, "SIGTERM", Encoding.UTF8);
                context.Cancel = ignore;
                if (!ignore)
                {
                    Environment.Exit(0);
                }
            });
        Console.WriteLine("READY");
        await Task.Delay(30000).ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<int> RunTreeLeafAsync(string[] args)
    {
        string markerPath = args[2];
        int delayMilliseconds = ParseNonNegativeInt32(args, 3);
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        await File.WriteAllTextAsync(markerPath, "survived", Encoding.UTF8).ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteBytesAsync(Stream stream, byte value, int count, int chunkSize)
    {
        byte[] buffer = new byte[Math.Min(chunkSize, Math.Max(count, 1))];
        Array.Fill(buffer, value);

        while (count > 0)
        {
            int bytesToWrite = Math.Min(count, buffer.Length);
            await stream.WriteAsync(buffer.AsMemory(0, bytesToWrite)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            count -= bytesToWrite;
        }
    }

    private static int ParseNonNegativeInt32(string[] args, int index)
    {
        int value = int.Parse(args[index], NumberStyles.None, CultureInfo.InvariantCulture);
        return value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(args));
    }

    private static int ParsePositiveInt32(string[] args, int index)
    {
        int value = ParseNonNegativeInt32(args, index);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(args));
    }
}
