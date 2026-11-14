# Framework text keys

`Runic.CommandLine` shows English framework text unless an
`ICommandTextResolver` translates it. The resolver receives a key, the
invocation culture and an ordered list of safe string arguments, and returns
null for a key it does not translate. This page lists every framework key; the
same list is available in code as `CommandTextKeys.All`, and each key has a
constant such as `CommandTextKeys.HelpUsage`.

For Runic Translations, the `Runic.Translations.CommandLine` package supplies a
ready-made resolver with English and German text for every key below, so you
do not map keys by hand. That text lives in this repository, next to the key
list; see [Built-in catalog](#built-in-catalog).

## Rules

- Keys are stable identities. A diagnostic's key is its JSON `messageKey`; it
  never changes with the culture.
- A diagnostic uses `diagnostics.{kind}` unless it names its own key
  (`CommandTextKeys.Diagnostic(kind)`). Applications that create their own
  diagnostics use their own kinds and arguments.
- A fault whose diagnostic is not translated, or that has no diagnostic, uses
  `faults.{code}` with no arguments (`CommandTextKeys.Fault(code)`). The table
  lists the faults the framework raises itself, including the `RCLI6001`–`RCLI6011`
  faults of `Runic.CommandLine.Processes`; any application code can be
  translated this way too. Each framework fault code has exactly one English
  message, because a translation replaces the message by code; its details,
  such as `stream` and `reason` of `RCLI6011`, tell the cases apart.
- A command line without a command, where the catalog has no default command,
  is an `unknown-command` diagnostic (`RCLI1002`) whose key is
  `diagnostics.missing-command` (before 0.12.0: `diagnostics.unknown-command`
  without its argument).
- Human output writes a help link after its diagnostic or fault as the
  `hints.help-link` line. Its text must contain the `link` argument exactly
  once, and the text around the link passes the sanitizers; otherwise the
  English line is shown.
- Output replaces a message that contains technical content such as a path, or
  that is empty after sanitizing, with a `redaction.*` note, in human and JSON
  output alike. A note that is itself unsafe falls back to English. The
  `[redacted]` marker that replaces an unsafe value in a fault's details is not
  translated: details are data for programs, like codes and keys.
- Command, option and argument descriptions use their `DescriptionKey`, and the
  extended help of commands and groups uses their `LongDescriptionKey`. Those
  keys belong to the application.
- `CommandPrompts` resolves its own text (hints, Yes and No, errors and the
  `RCLI4001` diagnostics) through these keys. Prompt questions and choice labels
  are application text: resolve them with
  `new CommandTextContext(context.Culture, context.TextResolver)`.
- Arguments arrive in the order listed. The names describe each position; an
  argument named `parameter` is an option spelling such as `--limit` or an
  argument placeholder such as `<name>`, one named `option` is an option
  spelling, and one named `argument` is an argument placeholder: its value name,
  or else its name, in angle brackets, as the usage line shows it (before
  0.13.0, `diagnostics.missing-argument` received the bare name, such as `name`).
  Option spellings follow the catalog's dialect (`/limit` under the Windows
  slash dialect).
- Help names options in its `help.requires` and `help.conflicts` notes by their
  spelling in the catalog's dialect, such as `[conflicts: --list]` or
  `[conflicts: /l]`, and lists arguments under `help.arguments` as the usage
  line shows them, such as `<name>` (before 0.13.0: option IDs and argument
  names).
- Resolved text passes through the same sanitizers as the English text.
- A usage error in human output ends with the usage line of its command, built
  from the `help.usage`, `help.command-placeholder` and
  `help.options-placeholder` keys as in help, and the `hints.usage` line, whose
  `command` argument is the help command line, such as `tool copy --help`, or
  `tool /copy /?` in a catalog that declares the Windows slash dialect. Help,
  hints and diagnostics write commands and options in the catalog's dialect
  through their arguments, so the dialect adds no keys.

## Built-in catalog

The English and German text of every key is the `runic-command-line` catalog
in [`packages/dotnet/Runic.CommandLine/translations`](../../../packages/dotnet/Runic.CommandLine/translations):
`runic.json`, `en.rmf2` and `de.rmf2`.

- Each message is named after its key with the hyphens removed and the next
  letter capitalized (`help.show-help` becomes `help.showHelp`, written as
  `showHelp` in the `help` group), and each input is named after the key's
  argument (`{$parameter}`). A message may leave out an argument it does not
  show.
- `en.rmf2` is exactly the English that Runic.CommandLine writes without a
  resolver. `faults.RCLI4001` has no message: the framework shows the text of
  its `diagnostics.prompt-*` diagnostic instead.
- The catalog also holds the text of the `Runic.CommandLine.Processes` faults,
  `faults.RCLI6001` to `faults.RCLI6011`. The keys are part of
  `CommandTextKeys.All`, and `ProcessTextKeys` in that package names them with
  constants and its own `All`. One catalog for every Command Line package keeps
  one catalog id, registration and override file for an application, and lets
  `Runic.Translations.CommandLine` translate process faults without referencing
  Runic.CommandLine.Processes. The two packages are released together in the
  same version, so the text cannot drift from the package that raises it.
- German uses the formal *Sie*, *Verzeichnis* for directories, and ASCII quotes
  (`'...'`, `"..."`), which every terminal shows and copies.
- The `Runic.CommandLine` package carries these files as plain data under
  `translations/`, together with `translations/text-keys.json`, which the build
  writes from `CommandTextKeys.All`: each key, its argument names in order, and
  its message name (`null` for `faults.RCLI4001`). Nothing under
  `translations/` flows into a consuming project, and Runic.CommandLine does
  not reference Runic Translations. `Runic.Translations.CommandLine` compiles
  the catalog from the Runic.CommandLine package it depends on, so its text
  always belongs to that Command Line release.
- `tests/dotnet/Runic.CommandLine.TextCatalog.Tests` compiles the catalog with
  Runic.Translations.Build and fails when a key has no `en` or `de` message, a
  message declares or uses an input that is not one of its key's arguments, a
  message has no key, `en` differs from the framework's English (including the
  process fault messages), or German output still shows an English framework
  sentence.

### Adding or changing a key

A pull request that adds a key to `CommandTextKeys.All`, or changes its
arguments or English text, in the same change:

1. adds or updates its row in the table below, with the release that
   introduces the key under "Since";
2. adds or updates its message in `en.rmf2` with the framework's English and in
   `de.rmf2` with the German, in the key's group, declaring each argument the
   text shows as `.input {$name :string}`;
3. runs `dotnet run --project tests/dotnet/Runic.CommandLine.TextCatalog.Tests -c Release`.

A new fault code of `Runic.CommandLine.Processes` also adds its one English
message to `ProcessFaultMessages` and its key to `ProcessTextKeys`; the suite
fails when a `ProcessFaultCodes` code lacks either.

Keys and their arguments are API. A changed meaning gets a new key. Keys are
added in minor releases only, never in patches, so the
`Runic.Translations.CommandLine` release for a minor covers all its patches. A
removed key is named in the release notes and listed under
[Removed keys](#removed-keys), because an application's build-time override of
it then fails with `RTR0103`.

## Keys

| Key | Arguments (in order) | English text | Since |
| --- | --- | --- | --- |
| `help.usage` | | Usage | 0.6.0-preview.4 |
| `help.command-placeholder` | | command | 0.6.0-preview.4 |
| `help.options-placeholder` | | options | 0.6.0-preview.4 |
| `help.default-command` | | default | 0.6.0-preview.4 |
| `help.completion` | | Generate bash, zsh, fish or PowerShell completions | 0.6.0-preview.4 |
| `help.commands` | | Commands | 0.6.0-preview.4 |
| `help.arguments` | | Arguments | 0.6.0-preview.4 |
| `help.options` | | Options | 0.6.0-preview.4 |
| `help.show-help` | | Show help | 0.6.0-preview.4 |
| `help.show-version` | | Show version | 0.6.0-preview.4 |
| `help.output-format` | | Select output format | 0.6.0-preview.4 |
| `help.json-output-alias` | `option` | Same as {option} json | 0.7.0-preview.1 |
| `help.json-payload-output-alias` | | Write the result as JSON | 0.7.0-preview.2 |
| `help.response-files` | | Read more arguments from a file | 0.9.0 |
| `help.examples` | | Examples | 0.6.0-preview.4 |
| `help.exit-codes` | | Exit codes | 0.12.0 |
| `help.required` | | required | 0.6.0-preview.4 |
| `help.default` | | default | 0.6.0-preview.4 |
| `help.choices` | | choices | 0.6.0-preview.4 |
| `help.environment` | | env | 0.6.0-preview.4 |
| `help.existing` | | existing | 0.6.0-preview.4 |
| `help.path-kind.file` | | file | 0.6.0-preview.4 |
| `help.path-kind.directory` | | directory | 0.6.0-preview.4 |
| `help.minimum` | | min | 0.6.0-preview.4 |
| `help.maximum` | | max | 0.6.0-preview.4 |
| `help.requires` | | requires | 0.6.0-preview.4 |
| `help.conflicts` | | conflicts | 0.6.0-preview.4 |
| `completion.invalid-shell` | | Choose a completion shell: bash, zsh, fish or powershell. | 0.6.0-preview.4 |
| `prompts.select-hint` | | Arrow keys to move, Enter to confirm | 0.7.0-preview.1 |
| `prompts.multi-select-hint` | | Arrow keys to move, Space to select, A for all, Enter to confirm | 0.7.0-preview.1 |
| `prompts.yes` | | Yes | 0.7.0-preview.1 |
| `prompts.no` | | No | 0.7.0-preview.1 |
| `prompts.choose-one` | `count` | Enter a number from 1 to {count} | 0.7.0-preview.1 |
| `prompts.choose-many` | `count` | Enter numbers from 1 to {count}, separated by commas | 0.7.0-preview.1 |
| `prompts.answer-yes-no` | `yes`, `no` | Answer {yes} or {no}. | 0.7.0-preview.1 |
| `prompts.required` | | An answer is required. | 0.7.0-preview.1 |
| `prompts.cancelled` | | Cancelled | 0.7.0-preview.1 |
| `prompts.done` | | Done | 0.7.0-preview.1 |
| `faults.RCLI4000` | | The command was cancelled. | 0.6.0-preview.4 |
| `faults.RCLI4001` | | The message of its `diagnostics.prompt-*` diagnostic, below. | 0.7.0-preview.1 |
| `faults.RCLI5000` | | The command could not be completed. | 0.6.0-preview.4 |
| `faults.RCLI6001` | | The process request is not permitted by the configured policy. | 0.12.0 |
| `faults.RCLI6002` | | The process request is not permitted by the configured policy. | 0.12.0 |
| `faults.RCLI6003` | | The executable policy returned an invalid decision. | 0.12.0 |
| `faults.RCLI6004` | | The executable policy could not evaluate the request. | 0.12.0 |
| `faults.RCLI6005` | | The process could not be started. | 0.12.0 |
| `faults.RCLI6006` | | The process lifecycle could not be observed safely. | 0.12.0 |
| `faults.RCLI6007` | | Windows batch files run through cmd.exe and require AllowWindowsBatchFiles. | 0.12.0 |
| `faults.RCLI6008` | | The batch file path or an argument contains characters that cmd.exe would interpret. | 0.12.0 |
| `faults.RCLI6009` | | The output handler failed. | 0.12.0 |
| `faults.RCLI6010` | | The process did not exit before its timeout. | 0.12.0 |
| `faults.RCLI6011` | | The captured output is incomplete: it exceeded its capture limit, or a descendant process held the output pipe open. | 0.12.0 |
| `hints.RCLI5000` | | Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr. | 0.6.0-preview.4 |
| `hints.usage` | `command` | Run "{command}" for more information. | 0.10.0 |
| `hints.help-link` | `code`, `link` | Help for {code}: {link} | 0.12.0 |
| `redaction.diagnostic-content` | | The diagnostic content was redacted. | 0.12.0 |
| `redaction.diagnostic-message` | | The diagnostic message was redacted. | 0.12.0 |
| `redaction.fault` | | The command failed; details were redacted. | 0.12.0 |
| `diagnostics.unknown-option` | `option` | The option '{option}' is not recognized. | 0.6.0-preview.4 |
| `diagnostics.unknown-option-suggestion` | `option`, `suggestion` | The option '{option}' is not recognized. Did you mean '{suggestion}'? | 0.6.0-preview.4 |
| `diagnostics.unknown-command` | `command` | The command '{command}' is not recognized. | 0.6.0-preview.4 |
| `diagnostics.unknown-command-suggestion` | `command`, `suggestion` | The command '{command}' is not recognized. Did you mean '{suggestion}'? | 0.6.0-preview.4 |
| `diagnostics.missing-command` | | No command was given. | 0.12.0 |
| `diagnostics.missing-option-value` | `option` | The {option} option requires a value. | 0.6.0-preview.4 |
| `diagnostics.unexpected-option-value` | `option` | The {option} option does not take a value. | 0.6.0-preview.4 |
| `diagnostics.missing-argument` | `argument` | The {argument} argument is missing. | 0.6.0-preview.4 |
| `diagnostics.unexpected-argument` | | Too many arguments were given. | 0.6.0-preview.4 |
| `diagnostics.duplicate-option` | `option` | The {option} option was given more than once. | 0.6.0-preview.4 |
| `diagnostics.unsupported-short-bundle` | | Short options cannot be combined; give each one separately. | 0.6.0-preview.4 |
| `diagnostics.invalid-output-mode` | `option` | {option} must be human or json. | 0.6.0-preview.4 |
| `diagnostics.conflicting-output-selector` | `option`, `conflict` | {option} and {conflict} both select the output format. Use one of them. | 0.7.0-preview.1 |
| `diagnostics.transport-output-option-collision` | | The command line is invalid. | 0.6.0-preview.4 |
| `diagnostics.missing-required-option` | `option`, `command` | The {option} option is required. | 0.6.0-preview.4 |
| `diagnostics.unexpected-root-help-argument` | | The application's help takes no other arguments. | 0.6.0-preview.4 |
| `diagnostics.response-file-unreadable` | `file` | The response file '{file}' could not be read. | 0.9.0 |
| `diagnostics.response-file-invalid-utf8` | `file` | The response file '{file}' is not valid UTF-8. | 0.9.0 |
| `diagnostics.response-file-unterminated-quote` | `file` | The response file '{file}' has an unterminated quote. | 0.9.0 |
| `diagnostics.response-file-too-deep` | `file` | The response file '{file}' is nested more than 16 levels deep. | 0.9.0 |
| `diagnostics.response-file-cycle` | `file` | The response file '{file}' includes itself. | 0.9.0 |
| `diagnostics.invalid-environment-value` | `option`, `variable` | {variable} must be true/false, yes/no or 1/0 for {option}. | 0.6.0-preview.4 |
| `diagnostics.invalid-choice` | `parameter`, `choices` | {parameter} must be one of: {choices}. | 0.6.0-preview.4 |
| `diagnostics.invalid-choice-hidden` | `parameter`, `help` (since 0.12.0) | {parameter} must be one of the choices shown in {help}. | 0.6.0-preview.4 |
| `diagnostics.invalid-integer` | `parameter` | {parameter} requires a whole number. | 0.6.0-preview.4 |
| `diagnostics.invalid-number` | `parameter` | {parameter} requires a number. | 0.6.0-preview.4 |
| `diagnostics.invalid-guid` | `parameter` | {parameter} requires a GUID such as 0f8fad5b-d9cb-469f-a165-70867728950e. | 0.6.0-preview.4 |
| `diagnostics.invalid-boolean` | `parameter` | {parameter} requires true or false. | 0.6.0-preview.4 |
| `diagnostics.invalid-value` | `parameter` | {parameter} has an invalid value. | 0.6.0-preview.4 |
| `diagnostics.validation-failed` | `parameter` | {parameter} has a value that failed validation. | 0.6.0-preview.4 |
| `diagnostics.missing-value` | `parameter` | {parameter} requires a value. | 0.6.0-preview.4 |
| `diagnostics.out-of-range` | `parameter`, `minimum`, `maximum` | {parameter} requires a number between {minimum} and {maximum}. | 0.6.0-preview.4 |
| `diagnostics.below-minimum` | `parameter`, `minimum` | {parameter} requires a number of at least {minimum}. | 0.6.0-preview.4 |
| `diagnostics.above-maximum` | `parameter`, `maximum` | {parameter} requires a number of at most {maximum}. | 0.6.0-preview.4 |
| `diagnostics.option-requires` | `option`, `required` | {option} requires {required}. | 0.6.0-preview.4 |
| `diagnostics.option-conflict` | `option`, `conflict` | {option} cannot be combined with {conflict}. | 0.6.0-preview.4 |
| `diagnostics.invalid-file-path` | `parameter` | {parameter} requires a valid file path. | 0.6.0-preview.4 |
| `diagnostics.invalid-directory-path` | `parameter` | {parameter} requires a valid directory path. | 0.6.0-preview.4 |
| `diagnostics.file-not-found` | `parameter` | {parameter} requires an existing file; check the path and permissions. | 0.6.0-preview.4 |
| `diagnostics.directory-not-found` | `parameter` | {parameter} requires an existing directory; check the path and permissions. | 0.6.0-preview.4 |
| `diagnostics.environment-value-source` | `parameter`, `variable` | The value for {parameter} came from environment variable {variable}. | 0.6.0-preview.4 |
| `diagnostics.prompt-requires-terminal` | `prompt` | {prompt} needs an answer, but the command is not running in an interactive terminal. Pass the value on the command line. | 0.7.0-preview.1 |
| `diagnostics.prompt-json-output` | `prompt` | {prompt} needs an answer, but JSON output never prompts. Pass the value on the command line. | 0.7.0-preview.1 |
| `diagnostics.prompt-disabled` | `prompt` | {prompt} needs an answer, but prompts are disabled. Pass the value on the command line. | 0.7.0-preview.1 |

## Removed keys

None so far. A key removed in a minor release is listed here, with the release
and its replacement, until the next minor, so an application that overrides it
can find what to use instead.

`CommandTextContext.ValidateDescriptionKeys(catalog)` reports application
`DescriptionKey` and `LongDescriptionKey` values a resolver does not resolve
(`RCLI0023`).
