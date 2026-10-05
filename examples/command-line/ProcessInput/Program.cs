using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine.Processes;

namespace Runic.CommandLine.ProcessInput.Example;

internal static class Program
{
    private const int PayloadLimit = 16 * 1024;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--tool")
        {
            return await RunToolAsync(args[1]).ConfigureAwait(false);
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unavailable.");
        var runner = new ProcessRunner(new LocalExecutablePolicy(
            executableRoots: new[] { Path.GetDirectoryName(executable)! }));
        var environment = new Dictionary<string, string?> { ["RCLI_INPUT_LABEL"] = "example" };
        // A framework-dependent apphost may need its runtime location even in an isolated environment.
        foreach (string name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        byte[] payload = Encoding.UTF8.GetBytes(args.Length == 0 ? "stone and gold" : string.Join(' ', args));
        string normalized = await RunAsync("normalize", ProcessStandardInput.FromBytes(payload, PayloadLimit))
            .ConfigureAwait(false);
        string count = await RunAsync("count", ProcessStandardInput.FromBytes(
            Encoding.UTF8.GetBytes(normalized), PayloadLimit)).ConfigureAwait(false);
        string health = await RunAsync("health", ProcessStandardInput.Closed).ConfigureAwait(false);
        Console.WriteLine($"Normalized: {normalized}");
        Console.WriteLine($"Word count: {count}");
        Console.WriteLine($"Health: {health}");
        return 0;

        async Task<string> RunAsync(string tool, ProcessStandardInput input)
        {
            var options = new ProcessExecutionOptions(
                timeout: TimeSpan.FromSeconds(10),
                standardOutputLimitBytes: PayloadLimit,
                standardErrorLimitBytes: 4096)
            {
                StandardInput = input,
                InheritEnvironment = false,
            };
            ProcessResult result = await runner.RunAsync(new ProcessRequest(
                executable, new[] { "--tool", tool }, environment: environment, options: options),
                cancellation.Token).ConfigureAwait(false);
            if (result.State != ProcessState.Exited || result.ExitCode != 0 ||
                result.StandardOutput.IsTruncated || result.StandardOutput.DrainTimedOut)
            {
                throw new InvalidOperationException($"Tool {tool} ended in {result.State} ({result.ExitCode}).");
            }

            return result.StandardOutput.Text;
        }
    }

    private static async Task<int> RunToolAsync(string tool)
    {
        // The sample tools also cap accepted input when invoked directly.
        byte[] buffer = new byte[PayloadLimit + 1];
        int count = 0;
        using Stream input = Console.OpenStandardInput();
        while (count < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(count)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > PayloadLimit)
        {
            Console.Error.Write("Input exceeds 16 KiB.");
            return 2;
        }

        string text = Encoding.UTF8.GetString(buffer, 0, count);
        switch (tool)
        {
            case "normalize":
                Console.Write(text.ToUpperInvariant());
                return 0;
            case "count":
                Console.Write(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
                    .ToString(CultureInfo.InvariantCulture));
                return 0;
            case "health":
                Console.Write(count == 0 ? "ready" : "unexpected input");
                return count == 0 ? 0 : 2;
            default:
                return 2;
        }
    }
}
