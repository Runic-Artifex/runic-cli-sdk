# Command Line hosting, version 1

This document specifies what a host process does around one invocation: the exit code it returns, how it handles termination signals, how a handler's cancellation ends, what debug output it writes, and how it captures the environment that [environment fallbacks](grammar.md#environment-fallbacks) read. The grammar ([grammar.md](grammar.md)) and the machine protocol ([protocol/README.md](protocol/README.md)) say what an invocation means and what it writes; this document says how the process runs it.

The rules come from the shipped .NET library, `Runic.CommandLine`. It has two hosts:

- the **standalone runner** (`CommandApp`), which owns the process: it reads the environment, handles signals and returns the exit code from `Main`;
- the **embedding host** (`CommandLineHostingAdapter`), which an application with its own host uses. That application owns signals and the environment, and passes in a cancellation token and a captured launch input.

A rule applies to both unless it names one. Behaviour marked **implementation-defined** is how .NET behaves because of its runtime or platform, not a contract; another implementation may differ there and still conform.

## Exit codes

### Categories

Every outcome has a semantic category, and the process exit code comes from it. The category, not the code, decides `success` in the [envelope](protocol/README.md#envelope), the fault code and what observers see.

| Category | Default code | Typical cause |
|---|---|---|
| Success | 0 | The command succeeded; help, version and a completion script also exit 0. |
| Usage | 2 | A parse or binding error (`RCLI1xxx`, `RCLI2xxx`), an invalid `RUNIC_COMMANDLINE_OUTPUT`, or a prompt that cannot ask (`RCLI4001`). |
| Validation | 2 | A handler rejected the shape of well-formed input, for example two options that exclude each other. |
| Command failure | 1 | An expected domain failure on well-formed input: a refused operation, a failed check, a conflict. |
| Unavailable | 1 | Something the command needs is missing or unreachable, including "not found". |
| Host failure | 3 | An unexpected exception or infrastructure failure (`RCLI5000`). |
| Cancelled | 130 | The invocation was cancelled (`RCLI4000`): a [termination signal](#termination-signals), the embedding host's token, or the user cancelled a prompt. |

Exit code 2 means the command line itself is wrong, so it fails the same way whatever state the command works on; exit code 1 means a well-formed command failed on that state. The defaults are the ones every Runic tool reports: 1 for failures and 2 for usage, as the `dotnet` CLI does, and 130 for an interrupted command, as a shell reports one (128 plus SIGINT).

If writing help, version, a completion script or a usage error throws, the invocation is a host failure (3 by default). If it is cancelled, the invocation is cancelled.

### Exit-code policy

An application may install an exit-code policy that maps each category to a code, for example a distinct code per category. It applies to every command of the application. A policy must map success to 0 and every other category to a nonzero code; .NET rejects any other policy when it creates the executor. Nothing else is checked: the policy is responsible for the codes it chooses.

One outcome may select its own code instead of the policy's, for that invocation only, for example 2, 3 and 4 for three documented failures of one command:

- A **failure** code must not be 0 and must not be a multiple of 256. Linux and macOS keep only the low 8 bits of an exit status, so 256 reads as 0, which is success. .NET refuses such a code when the outcome selects it. A policy is not checked this way, so a policy should also avoid multiples of 256.
- A **success** may use 0 or an informational nonzero code that its callers document, such as Windows Installer's 3010 ("success, restart required"). Other callers read any nonzero code as failure. Linux and macOS report 3010 as 194, so a tool that runs on them uses codes from 1 to 255.

The outcomes the framework creates itself (usage errors, cancellation and host failures) always take the policy's code. The process returns the selected code, if any, and otherwise the policy's code. The envelope's `exitCode` is that code ([Envelope](protocol/README.md#envelope)), not the status a parent process observes.

The forced-exit codes 130, 131 and 143 under [Termination signals](#termination-signals) are not categories and do not go through the policy.

### Empty command line

The standalone runner shows root help, exit code 0, when it is started without arguments and the catalog has no default command. This differs from the grammar, where empty input is `unknown-command`. The embedding host lets the application choose: by default it parses the empty input (`unknown-command`, exit code 2), or it hands the launch to the application's own user interface without parsing.

## Termination signals

### Standalone runner

The standalone runner handles three signals by default: SIGINT (Ctrl+C), SIGQUIT and SIGTERM. An application can turn this off; the runtime's default handling then applies. The handlers are registered when an invocation starts, before parsing, and removed when it returns its exit code. Outside that time, and for every other signal, the runtime's default handling applies.

1. **The first signal** does not end the process. It cancels the invocation's [cancellation token](#cancellation-and-async-handlers), and the invocation ends however its handler responds. A handler that stops because of the token ends cancelled, exit code 130 by default, whichever of the three signals it was: a graceful stop after SIGTERM also exits 130, not 143.
2. **A repeated signal** while the invocation still runs ends the process at once. The exit code is 128 plus the number of the signal that ended it: 130 for SIGINT, 131 for SIGQUIT and 143 for SIGTERM. So SIGTERM followed by Ctrl+C exits 130. The invocation does not finish: no outcome is written. .NET ends the process with `Environment.Exit`, which still runs the runtime's process-exit handlers; that part is implementation-defined.
3. **No timeout.** Nothing forces an exit after the first signal. A handler that ignores its token runs until it finishes or another signal arrives.

**Shutdown protection.** A handler can protect cleanup that must finish, such as stopping a child process tree or removing what it created, by holding a protection scope. While at least one scope of the invocation is alive, a repeated signal is ignored, and the invocation stays cancelled. After the last scope is released, the next signal ends the process as in step 2. A scope has no effect when the standalone runner does not handle signals: when the application turned handling off, under the embedding host, or when the executor is used directly.

**SIGHUP is not handled.** When the terminal closes, the runtime ends the process at once, without process-exit handlers. A Unix child in a process group of its own can outlive it. Other signals keep their default actions.

### Platforms

- **Linux.** As above. The .NET test suite sends real signals on Linux only (`signals/*` cases).
- **macOS.** The same three POSIX signals through the same runtime registration, with the same exit codes. No test exercises them.
- **Windows.** The .NET runtime maps the console control events to the same three signals: Ctrl+C to SIGINT, Ctrl+Break to SIGQUIT, and the system shutdown event to SIGTERM. The forced-exit codes are the same values, 130, 131 and 143. Closing the console window raises the close event. .NET maps that event to SIGHUP, so the runner does not handle it, and Windows ends the process. Windows may also end the process as soon as the shutdown handler returns, so cleanup after SIGTERM is not guaranteed to finish there. These mappings and the timing of Windows termination are implementation-defined.
- **Elsewhere.** Where the runtime cannot register a signal handler, the runner registers none and the runtime's default applies (implementation-defined).

### Embedding host

The embedding host never subscribes to process signals. The application passes it a cancellation token. Cancelling that token is the same as the first signal: the invocation is cancelled and ends as its handler decides. There is no forced exit, and shutdown protection has no effect. The standalone runner also accepts a caller's token; cancelling it behaves like the first signal, without forcing an exit.

## Cancellation and async handlers

Each invocation has one cancellation token. The standalone runner links it to the caller's token and to the signals above. The embedding host uses the application's token. The binder and the handler receive the token, and so do prompts.

- **Cancelled before the handler.** If the token is cancelled before binding starts, neither the binder nor the handler runs, and the invocation is cancelled.
- **The handler stops.** The handler throws the runtime's cancellation exception (`OperationCanceledException` in .NET) while the token is cancelled. The outcome is cancelled, with the fault `RCLI4000` (English message "The command was cancelled.") and no diagnostics. The same exception while the token is *not* cancelled is an unexpected exception: a host failure, `RCLI5000`.
- **The user cancels a prompt.** Escape or the end of input during a prompt also cancels the invocation with `RCLI4000`.
- **The handler completes anyway.** A handler may return an outcome after a graceful stop, including success. That outcome stands, and its code is the exit code.
- **The outcome is still written.** When cancellation was requested or the outcome is cancelled, the framework writes the outcome with a token that is not cancelled, so the cancelled outcome's human text or JSON envelope still reaches the output. If cancellation arrives while an outcome is being written and stops the write, the invocation is cancelled.
- **Exceptions.** Any other exception from the binder, the handler, scope disposal or the outcome writer is a host failure, `RCLI5000`. The exception itself is not written, except as [debug output](#debug-output). Runtime-fatal exceptions, such as running out of memory, are not caught; they end the process (implementation-defined).

## Debug output

Debug output writes the exceptions that the framework caught during an invocation to standard error, for developers. Only the standalone runner has it.

**Enabling.** Any one of these turns it on:

- `RUNIC_COMMANDLINE_DEBUG` is exactly `1`;
- `DOTNET_ENVIRONMENT` is `Development`, compared ignoring ASCII case (.NET's development switch; implementation-defined);
- a debugger is attached to the process (implementation-defined).

The two variables are read through the same environment reader as option fallbacks (see [Environment capture](#environment-capture)). An application that gives the runner explicit parse settings without an environment reader therefore leaves only the debugger switch.

**What it writes.** For each caught exception, in the order caught, it writes a header line `Runic.CommandLine observed an exception (RUNIC_COMMANDLINE_DEBUG/Development output):`, then the exception's full text (in .NET, `Exception.ToString()`: type, message, stack trace and inner exceptions), then a line break. The text is cleaned so that it cannot drive a terminal. It keeps LF and tab; it removes CR and every other C0 and C1 control character, and the formatting characters that reorder or hide text ([Unicode control characters](protocol/README.md#unicode-control-characters) without the line break and tab). It is **not** redacted, so it can contain paths and secrets. Keep both variables unset where standard error is collected, such as in CI. The wording and the exception text are presentation, not a contract.

**Where and when.** It always goes to standard error, never to standard output, so it never mixes with the JSON frame. It is written even when the application asked for a quiet standard error. Exceptions caught so far are written just before the outcome, so the trace comes before the `RCLI5000` line. Any caught later are written when the invocation ends. For a help, version or completion request, they are written after that output. Writing debug output never changes the outcome or the exit code. If writing fails, the failure is ignored.

**Help.** With debug output on, a help request whose application translates its texts also writes a warning line to standard error for each description key the translation does not know: `warning <code> at '<location>': <message>`.

**The hint.** When debug output is off and the application observes no exceptions itself, the human output of a host failure ends with the hint "Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr." (text key `hints.RCLI5000`). It also does this for payload-shaped JSON output, which has no envelope fault for the hint to follow.

## Environment capture

An invocation reads four kinds of environment input:

- `RUNIC_COMMANDLINE_OUTPUT`, for [output classification](grammar.md#output-classification);
- the variables that options name for [environment fallbacks](grammar.md#environment-fallbacks);
- `RUNIC_COMMANDLINE_NO_INPUT`, which disables prompts;
- the [debug output](#debug-output) variables.

**Standalone runner.** By default the runner reads `RUNIC_COMMANDLINE_OUTPUT` once, when the invocation starts. It reads the other variables by name from the process environment when it needs them during that invocation: the debug variables at the start, a fallback variable while it checks options (step 6.1 of [Parse order](grammar.md#parse-order), only for options the command line did not give), and `RUNIC_COMMANDLINE_NO_INPUT` before it runs the handler. It never writes to the process environment. An application may instead give explicit parse settings: a captured output value and an optional reader. Without a reader, no variable counts as set for fallbacks, prompts or debug output.

**Embedding host.** The application passes a launch input that holds the arguments, the captured `RUNIC_COMMANDLINE_OUTPUT` value and a snapshot of named variables. The host reads fallbacks and `RUNIC_COMMANDLINE_NO_INPUT` only from that snapshot: a variable missing from it is unset, and the process environment is never read. It has no debug output.

**Values.**

- A missing variable (null) is unset.
- For fallbacks an empty value counts as set. For `RUNIC_COMMANDLINE_OUTPUT` an empty value counts as absent ([Output classification](grammar.md#output-classification)).
- Names in a snapshot are compared ordinally, so they are case-sensitive. A reader of the process environment follows the platform: Windows variable names are case-insensitive (implementation-defined).
- `RUNIC_COMMANDLINE_NO_INPUT` disables prompts when its value, with surrounding white space removed, is `1`, `true` or `yes`, comparing the words ignoring ASCII case. A prompt that would ask then fails with `RCLI4001` (usage, exit code 2), unless it has a default answer.

The corpora replay this capture: a corpus row's `environment` is the snapshot, and a variable it does not list is unset ([Normalized results](grammar.md#normalized-results)).
