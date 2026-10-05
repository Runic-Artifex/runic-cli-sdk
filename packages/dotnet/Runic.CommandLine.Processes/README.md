# Runic.CommandLine.Processes

`Runic.CommandLine.Processes` runs local tools for command applications without
building a shell command string. It preserves argument tokens, applies an
executable policy before launch, bounds retained stdout/stderr, and returns a
sanitized result for success, rejection, timeout, cancellation, and start
failure.

## Install

```bash
dotnet add package Runic.CommandLine.Processes --prerelease
```

The package targets `net10.0` and brings `Runic.CommandLine` transitively. It is
a preview package. Use it for trusted application-controlled
automation, not as a browser-facing execution endpoint or operating-system
sandbox.

## Run an allowlisted tool

Constrain executable and working-directory roots, pass one argument per token,
and inspect the resulting state:

```csharp
var policy = new LocalExecutablePolicy(
    executableRoots: new[] { applicationToolsDirectory },
    workingDirectoryRoots: new[] { applicationDataDirectory });
var runner = new ProcessRunner(policy);
var request = new ProcessRequest(
    toolPath,
    new[] { "export", "--output", outputPath },
    workingDirectory: applicationDataDirectory,
    options: new ProcessExecutionOptions(
        timeout: TimeSpan.FromSeconds(30),
        standardOutputLimitBytes: 64 * 1024,
        standardErrorLimitBytes: 64 * 1024));

ProcessResult result = await runner.RunAsync(request, cancellationToken);

if (result.State == ProcessState.Exited && result.ExitCode == 0)
{
    return result.StandardOutput.Text;
}

throw new InvalidOperationException(
    $"Tool ended in {result.State} ({result.Fault?.Code ?? "no fault code"}).");
```

`ProcessStartInfo.ArgumentList` receives each argument separately—never join or
quote them into a shell command. Both redirected streams are drained
concurrently even after their retention caps; `IsTruncated` and
`ObservedByteCount` distinguish retained text from the total observed output.
After the child exits, the runner waits at most `DrainGracePeriod` for both
pipes to close. A descendant that inherited a pipe can hold it open; the runner
then closes the pipe and sets `DrainTimedOut`, so the captured text may be
incomplete even though the state is `Exited`. Timeouts and drain grace periods
use the `TimeProvider` passed to `ProcessRunner`.

## Standard input and environment

Existing requests inherit stdin and the parent environment. For unattended tools,
close stdin so a read receives EOF, or supply a bounded immutable byte payload:

```csharp
var options = new ProcessExecutionOptions(timeout: TimeSpan.FromSeconds(30))
{
    StandardInput = ProcessStandardInput.FromBytes(
        Encoding.UTF8.GetBytes(document), limitBytes: 64 * 1024),
    InheritEnvironment = false,
};
var request = new ProcessRequest(
    absoluteToolPath,
    new[] { "transform" },
    environment: new Dictionary<string, string?> { ["LANG"] = "C.UTF-8" },
    options: options);
ProcessResult result = await runner.RunAsync(request, cancellationToken);
```

Use `ProcessStandardInput.Closed` for immediate EOF and
`ProcessStandardInput.Inherit` to retain interactive input. `FromBytes` validates
the payload before copying it: the default limit is 1 MiB, and the hard maximum
is 16 MiB. Bytes are written without text conversion or a BOM and stdin is closed
after the payload, including an empty payload. Writing and both output drains
run concurrently; cancellation and timeout also stop a blocked input write.
A child may close stdin or exit before consuming the payload; its exit state and
code remain authoritative rather than turning a broken pipe into an execution
failure. A successful exit therefore does not prove that every input byte was
consumed.

With `InheritEnvironment = false`, the child starts with an empty environment
before the request's explicit overrides are applied. Include variables required
by your tool or runtime, such as `DOTNET_ROOT` for an apphost on a custom .NET
installation. Prefer absolute executable paths. Bare executable names use the
request's explicit `PATH`; without a match they resolve relative to the caller's
current directory instead of falling back to the parent's `PATH`. Environment
removal entries (`null`) and child overrides never change the parent environment.

The [ProcessInput tool-chain example](../../../examples/command-line/ProcessInput)
passes bounded output between two tools and closes stdin for a health check,
with an explicitly isolated child environment.

## Windows batch files

Windows starts `.bat` and `.cmd` files through `cmd.exe`, which parses the
command line again and interprets `&`, `|`, `%` and other characters even inside
quoted arguments. Because bare names such as `npm` resolve through `PATHEXT`
to `npm.cmd`, the runner rejects batch files with `RCLI6007` after resolution.
Set `allowWindowsBatchFiles: true` on `ProcessExecutionOptions` only for
trusted scripts; arguments or paths that contain `% ! ^ & | < > " ( )` or line
breaks are still rejected with `RCLI6008` rather than escaped. Prefer the
underlying executable (for example `node` with the package's script path) when
arguments come from users.

## Security and failure behavior

Every request is evaluated by `IExecutablePolicy`. `LocalExecutablePolicy`
checks configured roots and the runner launches the path it evaluated, but this
does not eliminate operating-system races: a permitted file or symlink can be
replaced between evaluation and start. When inputs cross a trust boundary, use
an absolute allowlisted executable and a minimal child environment—inheritance
can expose credentials.

Use `ProcessState` to handle terminal behavior and `ProcessFaultCodes` for
stable diagnostics such as `RCLI6001` (executable rejected), `RCLI6002`
(working directory rejected), `RCLI6005` (start failure), `RCLI6007` (batch
file without opt-in), and `RCLI6008` (batch file argument). Policy messages
and details are normalized before they reach a result to avoid leaking input.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
see [process examples](https://github.com/Runic-Artifex/runic-cli-sdk/tree/main/tests/dotnet/Runic.CommandLine.Processes.Tests),
or [report an issue](https://github.com/Runic-Artifex/runic-cli-sdk/issues).
Runic.CommandLine.Processes is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/LICENSE).
