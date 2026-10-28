# Framework text keys

`Runic.CommandLine` shows English framework text unless an
`ICommandTextResolver` translates it. The resolver receives a key, the
invocation culture and an ordered list of safe string arguments, and returns
null for a key it does not translate. This page lists every framework key; the
same list is available in code as `CommandTextKeys.All`, and each key has a
constant such as `CommandTextKeys.HelpUsage`.

For Runic Translations, the `Runic.Translations.CommandLine` package supplies a
ready-made resolver with English and German text for every key below, so you
do not map keys by hand. Its catalogs name each message after the key with the
hyphens removed and the next letter capitalized (`help.show-help` becomes
`help.showHelp`) and name each input after the argument (`{$parameter}`).

## Rules

- Keys are stable identities. A diagnostic's key is its JSON `messageKey`; it
  never changes with the culture.
- A diagnostic uses `diagnostics.{kind}` unless it names its own key
  (`CommandTextKeys.Diagnostic(kind)`). Applications that create their own
  diagnostics use their own kinds and arguments.
- A fault whose diagnostic is not translated, or that has no diagnostic, uses
  `faults.{code}` with no arguments (`CommandTextKeys.Fault(code)`). The table
  lists the faults the framework raises itself; any code, including
  `Runic.CommandLine.Processes` and application codes, can be translated this way.
- Command, option and argument descriptions use their `DescriptionKey`. Those
  keys belong to the application.
- `CommandPrompts` resolves its own text (hints, Yes and No, errors and the
  `RCLI4001` diagnostics) through these keys. Prompt questions and choice labels
  are application text: resolve them with
  `new CommandTextContext(context.Culture, context.TextResolver)`.
- Arguments arrive in the order listed. The names describe each position; an
  argument named `parameter` is an option spelling such as `--limit` or an
  argument placeholder such as `<name>`.
- Resolved text passes through the same sanitizers as the English text.
- A usage error in human output ends with the usage line of its command, built
  from the `help.usage`, `help.command-placeholder` and
  `help.options-placeholder` keys as in help, and the `hints.usage` line, whose
  `command` argument is the help command line, such as `tool copy --help`.

## Keys

| Key | Arguments (in order) | English text |
| --- | --- | --- |
| `help.usage` | | Usage |
| `help.command-placeholder` | | command |
| `help.options-placeholder` | | options |
| `help.default-command` | | default |
| `help.completion` | | Generate bash, zsh, fish or PowerShell completions |
| `help.commands` | | Commands |
| `help.arguments` | | Arguments |
| `help.options` | | Options |
| `help.show-help` | | Show help |
| `help.show-version` | | Show version |
| `help.output-format` | | Select output format |
| `help.json-output-alias` | `option` | Same as {option} json |
| `help.json-payload-output-alias` | | Write the result as JSON |
| `help.response-files` | | Read more arguments from a file |
| `help.examples` | | Examples |
| `help.required` | | required |
| `help.default` | | default |
| `help.choices` | | choices |
| `help.environment` | | env |
| `help.existing` | | existing |
| `help.path-kind.file` | | file |
| `help.path-kind.directory` | | directory |
| `help.minimum` | | min |
| `help.maximum` | | max |
| `help.requires` | | requires |
| `help.conflicts` | | conflicts |
| `completion.invalid-shell` | | Choose a completion shell: bash, zsh, fish or powershell. |
| `prompts.select-hint` | | Arrow keys to move, Enter to confirm |
| `prompts.multi-select-hint` | | Arrow keys to move, Space to select, A for all, Enter to confirm |
| `prompts.yes` | | Yes |
| `prompts.no` | | No |
| `prompts.choose-one` | `count` | Enter a number from 1 to {count} |
| `prompts.choose-many` | `count` | Enter numbers from 1 to {count}, separated by commas |
| `prompts.answer-yes-no` | `yes`, `no` | Answer {yes} or {no}. |
| `prompts.required` | | An answer is required. |
| `prompts.cancelled` | | Cancelled |
| `prompts.done` | | Done |
| `faults.RCLI4000` | | The command was cancelled. |
| `faults.RCLI4001` | | The message of its `diagnostics.prompt-*` diagnostic, below. |
| `faults.RCLI5000` | | The command could not be completed. |
| `hints.RCLI5000` | | Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr. |
| `hints.usage` | `command` | Run "{command}" for more information. |
| `diagnostics.unknown-option` | `option` | The option '{option}' is not recognized. |
| `diagnostics.unknown-option-suggestion` | `option`, `suggestion` | The option '{option}' is not recognized. Did you mean '{suggestion}'? |
| `diagnostics.unknown-command` | `command` (absent when no command was typed) | The command '{command}' is not recognized. Without `command`: No command was given. |
| `diagnostics.unknown-command-suggestion` | `command`, `suggestion` | The command '{command}' is not recognized. Did you mean '{suggestion}'? |
| `diagnostics.missing-option-value` | `option` | The {option} option requires a value. |
| `diagnostics.unexpected-option-value` | `option` | The {option} option does not take a value. |
| `diagnostics.missing-argument` | `argument` | The {argument} argument is missing. |
| `diagnostics.unexpected-argument` | | Too many arguments were given. |
| `diagnostics.duplicate-option` | `option` | The {option} option was given more than once. |
| `diagnostics.unsupported-short-bundle` | | Short options cannot be combined; give each one separately. |
| `diagnostics.invalid-output-mode` | `option` | {option} must be human or json. |
| `diagnostics.conflicting-output-selector` | `option`, `conflict` | {option} and {conflict} both select the output format. Use one of them. |
| `diagnostics.transport-output-option-collision` | | The command line is invalid. |
| `diagnostics.missing-required-option` | `option`, `command` | The {option} option is required. |
| `diagnostics.unexpected-root-help-argument` | | The application's help takes no other arguments. |
| `diagnostics.response-file-unreadable` | `file` | The response file '{file}' could not be read. |
| `diagnostics.response-file-invalid-utf8` | `file` | The response file '{file}' is not valid UTF-8. |
| `diagnostics.response-file-unterminated-quote` | `file` | The response file '{file}' has an unterminated quote. |
| `diagnostics.response-file-too-deep` | `file` | The response file '{file}' is nested more than 16 levels deep. |
| `diagnostics.response-file-cycle` | `file` | The response file '{file}' includes itself. |
| `diagnostics.invalid-environment-value` | `option`, `variable` | {variable} must be true/false, yes/no or 1/0 for {option}. |
| `diagnostics.invalid-choice` | `parameter`, `choices` | {parameter} must be one of: {choices}. |
| `diagnostics.invalid-choice-hidden` | `parameter` | {parameter} must be one of the choices shown in --help. |
| `diagnostics.invalid-integer` | `parameter` | {parameter} requires a whole number. |
| `diagnostics.invalid-number` | `parameter` | {parameter} requires a number. |
| `diagnostics.invalid-guid` | `parameter` | {parameter} requires a GUID such as 0f8fad5b-d9cb-469f-a165-70867728950e. |
| `diagnostics.invalid-boolean` | `parameter` | {parameter} requires true or false. |
| `diagnostics.invalid-value` | `parameter` | {parameter} has an invalid value. |
| `diagnostics.validation-failed` | `parameter` | {parameter} has a value that failed validation. |
| `diagnostics.missing-value` | `parameter` | {parameter} requires a value. |
| `diagnostics.out-of-range` | `parameter`, `minimum`, `maximum` | {parameter} requires a number between {minimum} and {maximum}. |
| `diagnostics.below-minimum` | `parameter`, `minimum` | {parameter} requires a number of at least {minimum}. |
| `diagnostics.above-maximum` | `parameter`, `maximum` | {parameter} requires a number of at most {maximum}. |
| `diagnostics.option-requires` | `option`, `required` | {option} requires {required}. |
| `diagnostics.option-conflict` | `option`, `conflict` | {option} cannot be combined with {conflict}. |
| `diagnostics.invalid-file-path` | `parameter` | {parameter} requires a valid file path. |
| `diagnostics.invalid-directory-path` | `parameter` | {parameter} requires a valid directory path. |
| `diagnostics.file-not-found` | `parameter` | {parameter} requires an existing file; check the path and permissions. |
| `diagnostics.directory-not-found` | `parameter` | {parameter} requires an existing directory; check the path and permissions. |
| `diagnostics.environment-value-source` | `parameter`, `variable` | The value for {parameter} came from environment variable {variable}. |
| `diagnostics.prompt-requires-terminal` | `prompt` | {prompt} needs an answer, but the command is not running in an interactive terminal. Pass the value on the command line. |
| `diagnostics.prompt-json-output` | `prompt` | {prompt} needs an answer, but JSON output never prompts. Pass the value on the command line. |
| `diagnostics.prompt-disabled` | `prompt` | {prompt} needs an answer, but prompts are disabled. Pass the value on the command line. |

`CommandTextContext.ValidateDescriptionKeys(catalog)` reports application
`DescriptionKey` values a resolver does not resolve (`RCLI0023`).
