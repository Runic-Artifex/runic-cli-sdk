# Runic.CommandLine.Processes

`Runic.CommandLine.Processes` runs local tools for command applications without
building a shell command string. It preserves argument tokens, applies an
executable policy before launch, bounds retained stdout/stderr, and returns one
result for success, rejection, timeout, cancellation, and start failure. The
result's fault is normalized so that it does not leak the request's input; the
captured and streamed child output is the child's, unchanged, and can contain
terminal escape sequences (see [security and failure behavior](#security-and-failure-behavior)).

## Install

```bash
dotnet add package Runic.CommandLine.Processes
```

The package targets `net10.0` and brings `Runic.CommandLine` transitively. Until
1.0 its version is `0.y.z`: a new minor may contain breaking changes, a patch
only fixes or adds, and only the latest minor gets fixes. Use it for trusted
application-controlled automation, not as a browser-facing execution endpoint or
operating-system sandbox.

The runner runs a child to completion; it does not supervise long-running
children. It does not forward signals to a child that keeps running, restart
it, or stop a process tree gracefully on request; an application that hosts a
development server or another long-lived child manages that process itself.

## Run an allowlisted tool

Constrain executable and working-directory roots, pass one argument per token,
give the tool only the environment it needs, and inspect the resulting state:

```csharp
var policy = new LocalExecutablePolicy(
    executableRoots: new[] { applicationToolsDirectory },
    workingDirectoryRoots: new[] { applicationDataDirectory });
var runner = new ProcessRunner(policy);
var request = new ProcessRequest(
    toolPath,
    new[] { "export", "--output", outputPath },
    workingDirectory: applicationDataDirectory,
    // Without this, the tool inherits every variable of the parent, such as GITHUB_TOKEN in a CI job.
    environment: new Dictionary<string, string?> { ["LANG"] = "C.UTF-8" },
    options: new ProcessExecutionOptions(
        timeout: TimeSpan.FromSeconds(30),
        standardOutputLimitBytes: 64 * 1024,
        standardErrorLimitBytes: 64 * 1024)
    {
        InheritEnvironment = false,
    });

ProcessResult result = await runner.RunAsync(request, cancellationToken: cancellationToken);

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
incomplete even though the state is `Exited`. The text read before that point is
kept. On Windows, a descendant started with handle inheritance, as
`Process.Start` does, inherits the child's stdout and stderr even when it
redirects its own output. Timeouts and drain grace periods use the `TimeProvider` passed to
`ProcessRunner`.

## Live output

Pass an `IProcessOutputHandler` to see output while the child runs, for example
`nix build` progress in a log view. Implement `OnLineAsync` for decoded lines,
`OnChunkAsync` for the raw bytes, or both:

```csharp
sealed class LogLines(ObservableCollection<string> log) : IProcessOutputHandler
{
    public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken)
    {
        log.Add(line.Channel == ProcessOutputChannel.StandardError ? "! " + line.Text : line.Text);
        return ValueTask.CompletedTask;
    }
}

ProcessResult result = await runner.RunAsync(request, new LogLines(log), cancellationToken);
```

- **Lines.** A line is split at `\n`, and a trailing `\r` is removed. A line
  longer than 64 Ki characters is passed on in parts of at most 64 Ki
  characters, never between the halves of a surrogate pair: every part but the
  last has `IsPartial` set, so concatenate the parts up to the next line that is
  not partial to rebuild the line. The last line is passed on when the stream
  ends without a terminator. To keep output exactly as the child wrote it, such
  as a large `nix eval --json` document, use the raw chunks or write the channel
  to a stream.
- **Order.** The handler is never called concurrently. Within one channel,
  chunks and lines arrive in the order the child wrote them, each chunk before
  the lines it completes. Standard output and standard error are separate pipes,
  so between them the order is the order in which the runner read them: lines
  written with a pause in between keep their order, lines written at the same
  instant or held in the child's own stdout buffer may not.
- **Backpressure.** The runner reads the next chunk of a channel only after the
  handler's task completes. A slow handler fills the pipe and then slows the
  child instead of growing memory. After the child exits, the runner keeps
  draining while a handler call runs or the handler makes progress, until it is
  idle for `DrainGracePeriod`, or the request is cancelled or times out. A
  handler call that never returns therefore keeps a request without a timeout
  open until it is cancelled. Only an idle pipe counts as `DrainTimedOut`.
- **Capture.** The result still holds the bounded capture of both channels, with
  `IsTruncated` and `ObservedByteCount`. Set the limits to `0` to keep nothing
  but the counts.
- **Failure.** If the handler throws while the request is neither cancelled nor
  timed out, the runner terminates the child as on cancellation and returns
  `ExecutionFailed` with `RCLI6009`, also when the child had already exited. No
  call starts after the result is returned.

In a command, a `CommandTaskLog` from `CommandPrompts` in `Runic.CommandLine`
shows the last lines under a spinner, clears them on success and shows them on
failure; without a terminal, and with JSON output, it writes plain lines to
standard error:

```csharp
sealed class TaskLogLines(CommandTaskLog log) : IProcessOutputHandler
{
    public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken) =>
        log.WriteLineAsync(line.Text, cancellationToken);
}

await using CommandTaskLog log = await prompts.StartTaskLogAsync("nix build", visibleLines: 6, cancellationToken);
ProcessResult result = await runner.RunAsync(request, new TaskLogLines(log), cancellationToken);
if (result.State == ProcessState.Exited && result.ExitCode == 0) await log.SucceedAsync(cancellationToken: cancellationToken);
else await log.FailAsync($"nix build ended in {result.State}", cancellationToken: cancellationToken);
```

`IProcessRunner` has one member,
`RunAsync(request, outputHandler = null, cancellationToken = default)`. Pass the
token by name when you pass no handler. `ProcessRunner` streams the output to the
handler while the child runs; a runner that cannot stream, such as a test double,
implements the same member and passes its capture to the handler itself.

### Write a channel to a file

`ProcessOutputStreamHandler` writes the raw bytes of one channel to a stream,
such as a file, while the child runs, and passes all output on to an optional
next handler, so you can still show the lines:

```csharp
await using FileStream file = File.Create(outputPath);
var handler = new ProcessOutputStreamHandler(
    ProcessOutputChannel.StandardOutput, file, next: new LogLines(log));
var request = new ProcessRequest(
    "nix", ["eval", "--json", attribute],
    options: new ProcessExecutionOptions(standardOutputLimitBytes: 0)
    {
        StandardInput = ProcessStandardInput.Closed,
        DetachFromTerminal = true,
    });
ProcessResult result = await runner.RunAsync(request, handler, cancellationToken);
```

The bytes are written unchanged, without decoding or line splitting. Each chunk
is written and flushed before the runner reads the next one, so a slow stream
applies backpressure like a slow handler, and the stream holds the whole channel
when the child exited and the result is returned. A failing write behaves like
a failing handler: the result is `ExecutionFailed` with `RCLI6009`. The stream
belongs to the caller; the handler never disposes it. For both channels, pass a
second `ProcessOutputStreamHandler` for the other channel as `next`; the two may
share one stream. Set the capture limit to `0`, as above, when the stream is the
only copy you need.

## Test doubles

Code that takes an `IProcessRunner` can be tested without starting processes.
`ProcessResult` has a factory for each terminal state, and `ProcessOutput` a
public constructor for a capture:

```csharp
sealed class FakeRunner(ProcessResult result) : IProcessRunner
{
    public async ValueTask<ProcessResult> RunAsync(
        ProcessRequest request, IProcessOutputHandler? outputHandler = null, CancellationToken cancellationToken = default)
    {
        if (outputHandler is not null)
        {
            foreach (string line in result.StandardOutput.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await outputHandler.OnLineAsync(new ProcessOutputLine(ProcessOutputChannel.StandardOutput, line), cancellationToken);
        }

        return result;
    }
}

var runner = new FakeRunner(ProcessResult.Exited(0, new ProcessOutput("{\"ok\":true}\n")));
var missing = new FakeRunner(ProcessResult.StartFailed(ProcessStartFailureCategory.NotFound));
```

The factories produce what `ProcessRunner` produces, so `EnsureCompleted`
behaves the same:

- `Exited(exitCode, ...)` has an exit code and no fault.
- `TimedOut(...)` and `Cancelled(...)` have neither.
- `Rejected(fault)` and `StartFailed(category)` have empty output. The default
  rejection fault is the runner's `RCLI6001`; `StartFailed` has the runner's
  `RCLI6005` fault with the category in its `category` detail.
- `ExecutionFailed(fault, ...)` defaults to `RCLI6006`; pass a `RCLI6009` fault
  for a failed output handler.

Each factory takes optional `startedAt` and `duration` values.
`new ProcessOutput(text)` counts the text's UTF-8 bytes as observed; pass
`observedByteCount`, `isTruncated` or `drainTimedOut` to fake a truncated or
cut-off capture.

## Cancellation, timeout and descendants

On cancellation or timeout the runner stops the child and everything it
started:

- **Unix, own process group.** The child leads a new process group. The runner
  sends `SIGTERM` to the group, waits up to `DrainGracePeriod` for the child to
  exit and the pipes to close, and then sends `SIGKILL` to whatever is left of
  the group, also a descendant whose parent already exited. A tool such as
  `nix` can release its locks in that grace period. The runner reaps the child
  only after its last signal, so the group ID cannot be reused by another group
  meanwhile. A descendant that calls `setsid` or `setpgid` leaves the group and
  is not reached.
- **Unix, caller's process group.** The child gets `SIGTERM` and, after the
  grace period, the runner kills its process tree.
- **Windows.** The child joins a job object right after it starts, and the
  runner terminates the job and the process tree at once. A descendant the child
  started before it joined the job is reached through the process tree only.

The runner returns only after its child has exited, so these steps assume that
the caller's process lives until then. When the process exits while a run is
still in progress, the runner cannot wait out the grace period:

- **A forced exit**, such as `Environment.Exit` in `CommandApp` on a repeated
  Ctrl+C or SIGTERM, or `Main` returning while a run is pending, runs the
  runtime's exit handlers. The runner's handler kills the child of every run
  that has not returned, with `SIGKILL` to its process group or by terminating
  its job and process tree, without the `SIGTERM` step. A child that needs its
  grace period, such as a tool releasing locks, gets it only when the command
  holds `CommandExecutionContext.ProtectShutdown()` around the whole
  `RunAsync` call; see the
  [Runic.CommandLine README](../Runic.CommandLine/README.md#presentation-and-testing).
- **An exit without handlers**, such as a crash, `SIGKILL`, or `SIGHUP` when
  the terminal closes (`CommandApp` does not handle `SIGHUP`), skips the
  runner. On Windows the job is set to kill its processes when its last handle
  closes, so the child still ends with the parent, also when the console window
  is closed. On Unix a child in a process group of its own keeps running; a
  child in the caller's group also receives the terminal's `SIGHUP`.

After a run returns, the job no longer kills on close: a descendant that
outlives a normally exited child is left running, as before.

Which group a Unix child gets depends on the caller's terminal:

- **A caller with a controlling terminal**, such as a command-line application,
  keeps the child in its own process group. A background group would be stopped
  (`SIGTTIN`, `SIGTTOU`) when the child prompts on `/dev/tty`, as ssh, sudo, git
  credentials and gpg do, and would no longer receive the terminal's Ctrl+C or
  `SIGHUP`.
- **A caller without one**, such as a service, a CI job or a desktop application
  started from the desktop, starts the child in a new process group.
- **`DetachFromTerminal = true`** (part of `ProcessExecutionOptions.NonInteractive`)
  starts the child in a new session without a controlling terminal, in either
  case. Opening `/dev/tty` then fails, so a tool that would prompt fails at once
  instead of waiting, and cancellation reaches the whole group.

Inherited ignored signals, such as `SIGHUP` under `nohup`, stay ignored in the
child; only `SIGPIPE`, which the .NET runtime ignores, is reset. When the caller
is process 1, for example in a container, the runtime reaps every child itself,
so the child stays in the caller's group. When the caller ignores `SIGCHLD`, the
kernel reaps the child and its exit code is lost: the result is then
`ExecutionFailed` (`RCLI6006`), never `Exited` without a code. The runner
resolves the C library from the running process, which covers glibc, musl
(Alpine) and macOS. If it cannot, children run in the caller's group, the runner
writes a `Trace` warning, and `ProcessIdentity.ProcessGroupId` is null.

`IProcessObserver.OnStarted(ProcessIdentity, DateTimeOffset)` receives the
child's process ID and, on Unix, its process group ID, for logs or a list of
running tools. It is the only start notification; the former
`OnStarted(DateTimeOffset)` is gone.

## Desktop applications and services

Callers without a terminal, such as a GUI view model, should not let a tool
prompt on whatever terminal started them, and should not take truncated output
for complete output:

```csharp
var request = new ProcessRequest(
    "nix", ["path-info", "--recursive", "--size", path],
    options: new ProcessExecutionOptions(
        timeout: TimeSpan.FromMinutes(5), standardOutputLimitBytes: 16 * 1024 * 1024)
    {
        StandardInput = ProcessStandardInput.Closed,
        DetachFromTerminal = true,
    });
ProcessResult result = (await runner.RunAsync(request, cancellationToken: cancellationToken)).EnsureCompleted(cancellationToken);
```

`ProcessExecutionOptions.NonInteractive` is the default options with closed
stdin and `DetachFromTerminal`. `EnsureCompleted` returns the result when the child exited and both
captures are complete, and throws `OperationCanceledException` for a cancelled
request and `ProcessException` otherwise: `RCLI6010` for a timeout, `RCLI6011`
when a capture was truncated or cut off after the drain grace period, and the
result's own fault for a rejection or start failure. It does not check the exit
code.

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
ProcessResult result = await runner.RunAsync(request, cancellationToken: cancellationToken);
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
It recognizes the extension as Windows does, ignoring case, trailing dots and
spaces, and a stream suffix (`tool.cmd::$DATA`).
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

Only the result's fault is normalized. The captured output
(`ProcessResult.StandardOutput.Text`, `StandardError.Text`) and the data passed
to `OnLineAsync` and `OnChunkAsync` are the child's own bytes: an untrusted tool
can write terminal escape sequences or bidirectional overrides
into them. Show such output through a `CommandTaskLog`, which removes them,
rather than writing it to the terminal or a command's human output directly.

Use `ProcessState` to handle terminal behavior and `ProcessFaultCodes` for
stable diagnostics such as `RCLI6001` (executable rejected), `RCLI6002`
(working directory rejected), `RCLI6005` (start failure), `RCLI6007` (batch
file without opt-in), `RCLI6008` (batch file argument) and `RCLI6009` (output
handler failed). Policy messages
and details are normalized before they reach a result to avoid leaking input.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
see [process examples](https://github.com/Runic-Artifex/runic-cli-sdk/tree/main/tests/dotnet/Runic.CommandLine.Processes.Tests),
or [report an issue](https://github.com/Runic-Artifex/runic-cli-sdk/issues).
Runic.CommandLine.Processes is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/LICENSE).
