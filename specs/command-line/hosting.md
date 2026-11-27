# Command Line hosting, version 1

This document specifies what a host process does around one invocation: the exit code it returns, how it handles termination signals, how a handler's cancellation ends, what it writes in human output, what debug output it writes, and how it captures the environment that [environment fallbacks](grammar.md#environment-fallbacks) read. The grammar ([grammar.md](grammar.md)) and the machine protocol ([protocol/README.md](protocol/README.md)) say what an invocation means and what it writes; this document says how the process runs it.

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

An application may install an exit-code policy that maps each category to a code, for example a distinct code per category. It applies to every command of the application. A policy must map success to 0, and only success: every other category must map to a nonzero code. .NET rejects any other policy when it creates the executor, before any command runs. Nothing else is checked. In particular, a policy that maps a failure to a multiple of 256 is accepted, and the policy is responsible for the codes it chooses.

One outcome may select its own code instead of the policy's, for that invocation only, for example 2, 3 and 4 for three documented failures of one command:

- A **failure** code must not be 0 and must not be a multiple of 256. Linux and macOS keep only the low 8 bits of an exit status, so 256 reads as 0, which is success. .NET refuses such a code: selecting it throws an argument error. When a handler selects it, that is an unexpected exception, so the invocation ends as a host failure, `RCLI5000` (exit code 3 by default). A policy is not checked this way, so a policy should also avoid multiples of 256.
- A **success** may use 0 or an informational nonzero code that its callers document, such as Windows Installer's 3010 ("success, restart required"). Other callers read any nonzero code as failure. Linux and macOS report 3010 as 194, so a tool that runs on them uses codes from 1 to 255.

The outcomes the framework creates itself (usage errors, cancellation and host failures) always take the policy's code. The process returns the selected code, if any, and otherwise the policy's code. The envelope's `exitCode` is that code ([Envelope](protocol/README.md#envelope)), not the status a parent process observes.

The forced-exit codes 130, 131 and 143 under [Termination signals](#termination-signals) are not categories and do not go through the policy.

### Empty command line

The grammar and the hosts answer different questions. The grammar defines the parser's result for a token array: empty input is `unknown-command` at 0 without arguments, unless the catalog has a default command ([Parse order](grammar.md#parse-order), step 2). The corpus checks that parser result. A host decides what to parse:

- The **standalone runner**, started without arguments for a catalog without a default command, does not parse the empty array. It parses `["--help"]` in its place, so it shows root help and exits 0.
- The **embedding host** lets the application choose. By default it parses the empty array, which is `unknown-command` (exit code 2). Otherwise it hands the launch to the application's own user interface without parsing.

With a default command, both hosts parse the empty array, and the default command runs.

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
- **Windows.** The .NET runtime maps console control events to signals (`PosixSignalRegistration.Windows.cs` in dotnet/runtime):

  | Event | Signal | Handled by the runner |
  |---|---|---|
  | Ctrl+C (`CTRL_C_EVENT`) | SIGINT | yes |
  | Ctrl+Break (`CTRL_BREAK_EVENT`) | SIGQUIT | yes |
  | System shutdown (`CTRL_SHUTDOWN_EVENT`) | SIGTERM | yes |
  | Console window closed (`CTRL_CLOSE_EVENT`) | SIGHUP | no: Windows' default handling ends the process |
  | User logoff (`CTRL_LOGOFF_EVENT`) | none | no |

  The forced-exit codes are the same values, 130, 131 and 143. Windows may end the process as soon as the shutdown handler returns, so cleanup after SIGTERM is not guaranteed to finish there. These mappings and the timing of Windows termination are implementation-defined.
- **Elsewhere.** Where the runtime cannot register a signal handler, the runner registers none and the runtime's default applies (implementation-defined).
- **A failed registration.** When registering the handler for one of the three signals fails, for any reason the runtime reports (.NET: `PlatformNotSupportedException`, an `IOException`, `UnauthorizedAccessException` or `Win32Exception` from `PosixSignalRegistration.Create`), that signal keeps the runtime's default handling. The runner still handles the other two, and the invocation runs. A failed registration is never an invocation error. Runic Command Line applies the same rule to the handlers that a prompt on a terminal registers to show the cursor again.

### Embedding host

The embedding host never subscribes to process signals. The application passes it a cancellation token. Cancelling that token is the same as the first signal: the invocation is cancelled and ends as its handler decides. There is no forced exit, and shutdown protection has no effect. The standalone runner also accepts a caller's token; cancelling it behaves like the first signal, without forcing an exit.

## Cancellation and async handlers

Each invocation has one cancellation token. The standalone runner links it to the caller's token and to the signals above. The embedding host uses the application's token. The binder and the handler receive the token, and so do prompts.

- **Cancelled before the handler.** If the token is cancelled before binding starts, neither the binder nor the handler runs. The outcome is cancelled, with the fault `RCLI4000` and no diagnostics.
- **The handler stops.** The handler throws the runtime's cancellation exception (`OperationCanceledException` in .NET) while the token is cancelled. The outcome is cancelled, with the fault `RCLI4000` (English message "The command was cancelled.") and no diagnostics. The same exception while the token is *not* cancelled is an unexpected exception: a host failure, `RCLI5000`.
- **The user cancels a prompt.** Escape or the end of input during a prompt also cancels the invocation with `RCLI4000`.
- **The handler completes anyway.** A handler may return an outcome after a graceful stop, including success. That outcome stands, and its code is the exit code.
- **The outcome is still written.** When cancellation was requested or the outcome is cancelled, the framework writes the outcome with a token that is not cancelled, so the cancelled outcome's human text or JSON envelope still reaches the output. If cancellation arrives while an outcome is being written and stops the write, the invocation is cancelled.
- **Exceptions.** Any other exception from the binder, the handler, scope disposal or the outcome writer is a host failure, `RCLI5000`. The exception itself is not written, except as [debug output](#debug-output). Runtime-fatal exceptions, such as running out of memory, are not caught; they end the process (implementation-defined).

### Framework fault messages

The framework creates three faults itself. Their codes are normative. Their messages are presentation text, which a host may translate like other messages: .NET resolves the first two through the text keys `faults.RCLI4000` and `faults.RCLI5000`.

| Fault | English message | When |
|---|---|---|
| `RCLI4000` | The command was cancelled. | The invocation was cancelled. |
| `RCLI5000` | The command could not be completed. | An unexpected exception, or a binder, handler factory or handler that returned nothing. |
| `RCLI5000` | The command failed unexpectedly. | A writer replaced a fault whose code is not a valid identifier ([Sanitization](protocol/README.md#sanitization)). This text is fixed. |

The protocol example [`examples/host-fault.json`](protocol/examples/host-fault.json) and its writer case carry the message "The command host could not complete the request.". That is a writer input: the example shows that a writer writes the message it is given. It is not the text a host must produce for an unexpected exception.

### Output timing

A handler's output is not held back. In human output, what the handler writes to standard output and standard error goes to those streams as it writes it. In JSON output, standard output carries only the frame, so what the handler writes to standard output goes to standard error instead, also as it writes it. When the application asked for a quiet standard error, the handler's output is discarded in JSON output. The outcome itself is written after the handler returns: the human result, fault, diagnostics and usage line, or the JSON frame. .NET writes through the runtime's console writers, which flush every write (implementation-defined).

## Human output

In human output (the output mode `human` of [Output classification](grammar.md#output-classification)) an outcome is written as text for people: a success's result goes to standard output, and everything else to standard error. JSON output writes the [envelope](protocol/README.md#envelope) instead. The rules come from `Output/CommandOutputDispatcher.cs` (cited as `:<line>`), and the [human output corpus](#corpus) checks them. Both hosts write the same text; only the standalone runner adds the `RCLI5000` hint ([Debug output](#debug-output)).

### Success

1. Each diagnostic of the outcome (warnings and information; a success has no error diagnostic) is written to standard error as a [line](#lines), in order, before the result (`:288-291`; `human.success-diagnostics`).
2. The result's codec writes the payload to standard output (`:291`). The framework's payload types ([Framework payload types](protocol/README.md#framework-payload-types)) write:
   - `runic.text/1`: the text, followed by LF unless it already ends with LF. So empty text writes one LF, `"\n"` (`human.text-empty`), and text that ends with LF is written as it is (`CommandResultCodecs.cs:27-28`). Help and version are successes of this type.
   - `runic.unit/1`: nothing (`CommandResultCodecs.cs:34`). A unit success without diagnostics writes no byte at all (`human.unit`).

   An application's own payload type writes what its codec writes.

### Failure

Everything goes to standard error, as one write, in this order (`:295-317`):

1. Each diagnostic as a [line](#lines), in order (`human.failure-diagnostics`).
2. The **fault line**: the fault message, then LF. It keeps the line breaks the producer wrote, sanitized as a diagnostic line is, unless a translation replaced it or it was redacted (`human.failure-multiline-fault`). It is left out when a diagnostic with the fault's code has the same sanitized message, or when both the fault message and that diagnostic's message were redacted, because that diagnostic already reported it (`:391-421`; `human.failure-diagnostic-describes-fault`). The `RCLI5000` hint, when it applies, follows the message after one space ([Debug output](#debug-output); `human.host-failure-hint`). It applies by the fault's code alone, whatever the failure's category, so a command failure or a usage failure with the code `RCLI5000` gets it too (`human.host-failure-hint-by-code`), and so does a fault whose code a writer replaced with `RCLI5000` ([Sanitization](protocol/README.md#sanitization); `human.invalid-fault-code`); a host failure with another code does not (`human.host-failure-other-code`). In human output, a failure that gets the hint is written without the usage hint of step 4 and without its own human output (`:80-84`; `human.host-failure-hint-replaces-usage-hint`).
3. The fault's [help link line](#lines), when the fault has a `helpUri`, unless a diagnostic with the fault's code already wrote the same link, compared as the full text including its fragment (`:310-314`, `:376-389`; `human.failure-help-link-once`).
4. For a usage failure (the usage category: a parse error, or a binder's or handler's usage outcome), the **usage hint**: the [usage line](help.md#usage-line) of the command the failure belongs to, at the help width ([Width](help.md#width)), then `Run "<application> <command path> --help" for more information.` and LF (text key `hints.usage`, with the help command as its argument; `--help` is the dialect's help spelling). For a parse error the command is the first diagnostic's command path, the root for an error before any command (`human.parse-error`, `human.parse-error-at-root`); for an outcome it is the invoked command (`human.usage-failure`). The [root command](grammar.md#default-and-root-commands) is the root here: its name is no command spelling and its help is root help, so its usage hint is root help's usage line and `<application> --help`, also when the diagnostic's or the invocation's command path is its name (`CommandUsageHint.cs:18-22`; `human.parse-error-in-root-command`, `human.usage-failure-in-root-command`). Under the [Windows slash dialect](grammar.md#windows-slash-dialect) the command path and the help spelling are the dialect's: `Run "tool /deploy /?" for more information.` (`human.parse-error-slash-dialect`). A translated `hints.usage` with technical content gives way to the English text (`CommandUsageHint.cs:24-31`, `CommandPresentation.cs:93-101`, `:68-71`).

### A failure's own human output

A failure outcome may carry the application's own human text (.NET: `CommandOutcome.Failure(..., humanOutput)`), at most 1,048,576 UTF-8 bytes of valid Unicode (`CommandOutcome.cs:168-202`). In human output that text goes to standard output first, exactly as given, without sanitization and without an added LF. The failure is then written to standard error as above, without the usage hint (`:86-96`, `:184-202`; `human.failure-own-output`, `human.usage-failure-own-output`). An `RCLI5000` failure that gets the hint is written without its own text (`:80-84`). The text never appears in the JSON envelope.

With a payload-shaped JSON output alias ([Output classification](grammar.md#output-classification)), standard output carries the failure's declared data as its document, or nothing when it has none (.NET writes the envelope instead when the application asks for a document for every outcome, `CommandJsonOutputOptions.DocumentEveryOutcome`, `:140-147`), and standard error the human text for people (`:113-158`): the diagnostic lines, each followed by its [help link line](#lines) as in step 1 of [Failure](#failure), then, in place of the fault line, the failure's own text exactly as given (`:149-155`, `:335-348`; `human.payload-alias-failure-own-output`, `human.payload-alias-failure-own-output-help-link`). So a diagnostic's help link comes before the failure's own text. That text replaces the fault line, the fault's help link line, the usage hint and the `RCLI5000` hint alike (`human.payload-alias-usage-failure-own-output`, `human.payload-alias-host-failure-own-output`); a diagnostic's help link line is written also when the diagnostic has the fault's code and link (`human.payload-alias-failure-own-output-fault-diagnostic-link`). A failure without its own text writes the lines of [Failure](#failure), the help link lines, the usage hint and the `RCLI5000` hint included (`:157`; `human.payload-alias-failure`, `human.payload-alias-failure-help-links`, `human.payload-alias-usage-failure`, `human.payload-alias-host-failure-hint`). A success writes its diagnostics to standard error and its payload as the document, compact JSON and LF, except that a `runic.unit/1` success writes no document (`:125-137`; `human.payload-alias-success`).

### Lines

- **A diagnostic line** is the diagnostic's message alone, then LF (`:332-349`). The code is for programs, which read it from JSON output. The message is sanitized as [Sanitization](protocol/README.md#sanitization) says, except that it keeps its line breaks: LF and CR LF become LF, and a line does not end with the space that replaced a control character or with the author's own trailing space; a lone CR, like every other control character, becomes a space; leading and trailing line breaks are dropped (`CommandFaultSanitizer.cs:98-112`, `:164-235`; `human.diagnostic-line-breaks`). Only the message is checked for technical content, since human output shows no arguments: a message with technical content becomes the diagnostic content redaction note, and a message that is only white space after sanitizing the diagnostic message note (`CommandFaultSanitizer.cs:85-90`).
- **A help link line** follows the line it documents: `Help for <code>: <link>` and LF, where the link is never redacted (`:351-374`; `human.success-diagnostic-help-link`, `human.failure-fault-help-link`). The link is the `helpUri` as the implementation's writer writes it in JSON output ([Help links](protocol/README.md#help-links)): as given, or with the `dotnet` escaping profile in .NET's normalized absolute form (`Uri.AbsoluteUri`, `:356`), whose steps [Help links](protocol/README.md#help-links) lists: the scheme and host lowercased, the port 443 and an empty port left out, unreserved characters decoded, UTF-8 escapes in upper case, dot segments removed and an empty path written as `/`. The rows whose link that form changes require the capability `escaping-profile:dotnet` (`human.failure-help-link-normalized`, `human.success-diagnostic-help-link-empty-path`, `human.help-link-port-forms`, `human.help-link-unreserved-escapes-decoded`, `human.help-link-utf8-escapes-uppercased`, `human.help-link-other-escapes-kept`, `human.help-link-dot-segments`, `human.help-link-dot-segments-above-root`, `human.help-link-encoded-dot-segments`). A link with reserved escapes in any case is written as given by every writer (`human.help-link-reserved-escapes-kept`). The fault's line is left out when a diagnostic with the fault's code has the same link in that form (`:376-389`; `human.failure-help-link-once-normalized`). With a text resolver, the text key `hints.help-link`, whose arguments are the code and the link, replaces the English line when its text contains the link exactly once and the text before and after the link has no technical content; that text is sanitized, the link is not (`human.failure-help-link-translated`). Otherwise the English line stands (`human.failure-help-link-translation-without-link`, `human.failure-help-link-translation-technical`).

### Corpus

`human-output-corpus.json` has the application name, named `catalogs` in the format of the [catalog features corpus](grammar.md#normalized-results) and `cases`. A case runs its command line `args` through the standalone runner, with an empty environment, in human output unless they give a payload-shaped JSON output alias (the catalog `payload-alias`). Every command's handler returns the case's `outcome`; `texts`, when given, translates text keys, each with `{0}`, `{1}` … standing for the key's arguments. The runner's exit code, standard output and standard error must equal `expected` exactly. A case's `requires` lists the features and capabilities an implementation needs to run it (`dialect:windows-slash`, `escaping-profile:dotnet`). The conformance driver runs a case with `cl.human` ([conformance driver](../../eng/conformance/README.md#command-line)).

An `outcome` is a success, `{success: true, payloadType, payload, diagnostics}` with the payload type `runic.text/1` or `runic.unit/1` (which has no `payload`), or a failure, `{success: false, category, fault, diagnostics, humanOutput}`, whose `category` is one of `usage`, `validation`, `unavailable`, `command-failure` and `host-failure`, and whose `fault` and diagnostics have the form of a [writer case](protocol/README.md#corpus-layout)'s input; `humanOutput` is optional. The corpus compares the English framework texts (diagnostic messages, the usage hint, the `RCLI5000` hint); an implementation whose English differs reports those rows as a deviation.

## Debug output

Debug output writes the exceptions that the framework caught during an invocation to standard error, for developers. Only the standalone runner has it.

These parts are normative: the switch, the stream and its timing, and the sanitization. Everything else in this section is implementation-defined.

**The switch.** `RUNIC_COMMANDLINE_DEBUG` with the value exactly `1` turns it on. Any other value, including `true`, leaves it off. The variable is read through the same environment reader as option fallbacks (see [Environment capture](#environment-capture)). An application that gives the runner explicit parse settings without an environment reader therefore cannot turn it on with the variable.

Runtime-specific switches are implementation-defined. .NET also turns it on when `DOTNET_ENVIRONMENT` is `Development` (compared ignoring ASCII case; read through the same reader) or when a debugger is attached to the process.

**Stream and timing.** It always goes to standard error, never to standard output, so it never mixes with the JSON frame. It is written even when the application asked for a quiet standard error. Exceptions caught so far are written just before the outcome, so the trace comes before the `RCLI5000` line. Any caught later are written when the invocation ends. For a help, version or completion request, they are written after that output. Writing debug output never changes the outcome or the exit code. If writing fails, the failure is ignored.

**Sanitization.** The text is cleaned so that it cannot drive a terminal. It keeps LF and tab; it removes CR and every other C0 and C1 control character, and the formatting characters that reorder or hide text ([Unicode control characters](protocol/README.md#unicode-control-characters) without the line break and tab). It is **not** redacted, so it can contain paths and secrets. Keep the switch unset where standard error is collected, such as in CI.

**Format.** The format is implementation-defined. .NET writes, for each caught exception in the order caught: the header line `Runic.CommandLine observed an exception (RUNIC_COMMANDLINE_DEBUG/Development output):`, then `Exception.ToString()` (type, message, stack trace and inner exceptions), then a line break.

**Help.** With debug output on, a help request whose application translates its texts also writes a warning line to standard error for each description key the translation does not know: `warning <code> at '<location>': <message>`.

**The hint.** The output of a host failure (`RCLI5000`) may end with the hint "Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr." (text key `hints.RCLI5000`). Only the standalone runner writes it, and only when all of these hold:

- debug output is off;
- the application installed no exception observer of its own;
- the output is human, or JSON with the payload-shaped JSON output alias;
- the fault's code, after the writer's [sanitization](protocol/README.md#sanitization), is `RCLI5000`, whatever the failure's category (`CommandOutputDispatcher.cs:74-76`, `:80-84`);
- in JSON output with the payload-shaped alias, the failure has no human output of its own, which takes the fault line's place ([A failure's own human output](#a-failures-own-human-output)).

JSON envelope output never carries the hint, and neither does the embedding host.

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
