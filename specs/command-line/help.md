# Command Line help text, version 1

This document specifies the help text that a help request writes: the result `help` of the [grammar](grammar.md#help-version-and-groups) for a command path, the empty path being root help. In human output the text goes to standard output; in JSON output it is the `runic.text/1` payload of a `help` response ([Framework payload types](protocol/README.md#framework-payload-types)). Help is presentation text, not a contract a program parses, but its layout is fixed so that implementations agree and tests can compare it.

The rules come from the shipped .NET library, `Runic.CommandLine`: `Application/CommandHelpFormatter.cs` (cited as `:<line>` below). The [help corpus](help-corpus.json) checks them ([Corpus](#corpus)).

## Width

Help is formatted for a line width W, in terminal cells, of at least 20 (`MinimumWidth`, `:16`; .NET rejects a smaller width with an argument error, `:37`).

- **Which width.** The standalone runner uses the terminal's width minus one when it writes human output to the process terminal and standard output is not redirected, if that is at least 20, so that a full line never triggers the automatic wrap of the legacy Windows console. Otherwise it uses 80: for redirected output, for a console the application supplies (such as a test's) and for JSON output (`CommandPresentation.cs:135-149`). The usage lines of usage errors use the same width ([Human output](hosting.md#human-output)).
- **Cells.** A wide or fullwidth character (the East Asian Width classes W and F of Unicode UAX #11, which .NET approximates with a fixed table of ranges, `:345-368`) takes two cells. A character of the General_Category Mn, Me, Cf or Cc takes none, except tab and U+00AD SOFT HYPHEN, which take one. Every other character takes one cell (`:321-343`). An unpaired surrogate counts as U+FFFD.
- **Wrapping.** Text is split at spaces into words. A line breaks before a word that would pass column W, and a word is never split, so a word wider than the space left stays whole and the line is longer than W. The one exception is between wide characters: each wide character is a word of its own that is joined to its neighbours without a space, together with the combining marks after it, and closing punctuation (`、。，．）」』】〕〉》！？：；`) stays with the character before it, so no line starts with it (`:247-319`). Runs of spaces count as one space. Continuation lines start at the block's indent, given below.

## Layout

The text is these blocks in order, each one that is present preceded by one empty line, except the usage line, which comes first. Every line ends with LF, including the last; no line ends with a space, and the text has no empty line at its end.

1. The [usage line](#usage-line).
2. The command's description, when it is not empty (`:42-46`).
3. The command's long description, when it has one (`:47-51`).
4. The sections Commands, Arguments, Options, Exit codes and Examples ([Sections](#sections)), each only when it has a row.

"The command" is the command at the help path, or for root help the catalog's [root command](grammar.md#default-and-root-commands); root help of a catalog without a root command has no description, no long description and no arguments (`:108-118`). A [group](grammar.md#help-version-and-groups) is a command here: its help shows its description and its subcommands.

**Paragraphs.** A description and a long description are written from column 0, each source line wrapped on its own (`:205-231`). An empty source line is an empty line. In a long description, a line that starts with white space or contains a run of three spaces is a list, aligned columns or a code block and is written as it is, never wrapped, even when it is wider than W. A line that starts with a list marker, `- `, `* ` or one to three digits followed by `.` or `)` and a space, continues under the text after its marker (`:233-240`). Line breaks may be LF, CR LF or CR.

### Usage line

The line is `Usage: `, then the application name, then (`:124-141`):

- for a command path, the path as the catalog spells it, separated by single spaces, and in the catalog's dialect: `/cache /clear` under the [Windows slash dialect](grammar.md#windows-slash-dialect) (`help.slash.subcommand`);
- for root help, ` [command]` when the catalog has a root command and at least one other command that help lists, or ` <command>` when it has no root command and has any command, hidden ones included;
- each argument of the command that help does not hide: trailing arguments as `[-- name...]`, any other argument as `<name>` when its minimum is at least 1 and `[name]` when it is 0, with `...` before the closing bracket when its maximum is not 1. The name is the argument's value name, or else its name;
- ` [options]`.

The line wraps at W, and its continuation lines are indented by the width of `Usage: ` (`help.reference.command-narrow`, `help.reference.trailing-arguments-narrow`).

### Sections

A section is the line `<heading>:`, then one row per entry (`:174-203`). A row is two spaces, its term and its description, followed by its notes:

- **The term column.** The cap is min(32, max(8, ⌊2W/5⌋)). The term width T is the width of the widest term of the section that is at most the cap, or the cap when none is. The description column is C = 2 + T + 2. A term no wider than T is padded with spaces to column C; the description of a wider term starts on the next line at column C. A row with neither description nor notes is the term alone.
- **The body.** The description, with trailing white space removed, then each note after one space. The body wraps at W with continuation lines at column C; each line of a multi-line description is wrapped on its own and continues at column C.
- **Notes.** A note at most min(⌊W/2⌋, W − C) cells wide never breaks inside; a wider note breaks at its spaces like the description (`:182-191`).

The sections, in order:

1. **Commands** (`:52-58`, `:120-122`): for root help, the root commands, without the root command; for a command, its subcommands. Each command is listed in help order (catalog order unless the application declares another, `CommandCatalog.cs:78-84`), except a hidden one. The term is the command's name as the catalog spells it, then ` (default)` for the [default command](grammar.md#default-and-root-commands) (`help.reference.default-command`), then its aliases in parentheses, separated by `, `: `copy (default) (cp)`. The description is the command's.

   Root help of .NET also lists `completion <shell>`, "Generate bash, zsh, fish or PowerShell completions", for its built-in completion command, unless the application declares a command named `completion` or a completion option of its own (`:56-57`, `CommandCatalog.cs:146`; with a completion option, that option's row in Options takes its place, `:80-81`). Completion is not part of this specification: an implementation without it has no such row.
2. **Arguments** (`:60-64`): each argument of the command that help does not hide, in definition order. The term is the name or value name in angle brackets (`<target>`), whatever its arity. The notes are those below, with `[required]` for an argument whose minimum is at least 1.
3. **Options** (`:66-82`): the options of the command that help does not hide, its own in definition order and then the [global options](grammar.md#global-options); for root help without a root command, the global options. The term is the option's spelling, then its aliases, separated by `, `, then for an option whose maximum is not 0 a space and `<value>`: its value name, or else its `id`, with `...` before the `>` when its maximum is not 1 (`--tag <name...>`). Then, in every help, the framework's rows:

   | Term | Description | When |
   |---|---|---|
   | `-h, --help` | Show help | always |
   | `--version` | Show version | always, also in a command's help, although `--version` after a command is `unknown-option` (`grammar.version.not-command-built-in`) |
   | `--format <human|json>` (the output selector's spelling) | Select output format | always |
   | the JSON output alias, such as `--json` | Same as --format json | the catalog has one; "Write the result as JSON" for a payload-shaped alias |
   | `@<file>` | Read more arguments from a file | the application opted in to response files |
   | the completion option, then ` <shell>` | Generate bash, zsh, fish or PowerShell completions | root help, when the application declares one |

   Under the [Windows slash dialect](grammar.md#windows-slash-dialect) every spelling is the dialect's: commands and their aliases (`/deploy (/dp)`), options and their aliases (`/target, /t <name>`), the related options of `[requires: …]` and `[conflicts: …]`, and the framework's rows, `/?, /help`, `/version`, `/format <human|json>` and the JSON output alias (`/json`, "Same as /format json"), whatever spelling the catalog declared (`help.slash.root`, `help.slash.command`).
4. **Exit codes** (`:84-87`): the exit codes the command documents, in their order. The term is the code in decimal, the description its meaning.
5. **Examples** (`:88-93`): the command's examples, each as two spaces and the example, never wrapped, since they are copied into a shell.

**Hidden.** A command, option or argument that the application hides is left out of every row and of the usage line. A hidden command's own help is still written when it is asked for (`help.reference.hidden-command`).

### Notes

An option's or argument's notes, in this order, each in square brackets (`:145-169`):

| Note | When |
|---|---|
| `[required]` | a required option, or an argument whose minimum is at least 1 |
| `[default: <value>]` | the help declares a nonempty default value, and the parameter is neither sensitive nor a flag (an option whose maximum is 0, which is off unless given) |
| `[choices: <a>, <b>]` | the parameter declares [choices](grammar.md#choices) and is not sensitive; they are listed as declared, separated by `, ` |
| `[env: <NAME>]` | the option has an [environment fallback](grammar.md#environment-fallbacks), also when it is sensitive |
| `[file]`, `[directory]`, `[existing file]`, `[existing directory]` | the parameter declares a [path kind](grammar.md#input-validation), with `existing ` when it must exist |
| `[min: <n>]`, `[max: <n>]` | the parameter declares a bound; the number is written as in diagnostics ([Input validation](grammar.md#input-validation)) |
| `[requires: <options>]`, `[conflicts: <options>]` | the option declares relations; each related option is named by its spelling in the catalog's dialect, separated by `, ` |

## Text keys

Every fixed text above is the English default of a text key, which an application may translate like other presentation text ([text keys](../../docs/guides/command-line/text-keys.md)): `help.usage` (Usage), `help.command-placeholder` (command), `help.options-placeholder` (options), `help.commands`, `help.arguments`, `help.options`, `help.exit-codes` and `help.examples` (the headings), `help.default-command` (default), `help.completion`, `help.show-help`, `help.show-version`, `help.output-format`, `help.json-output-alias` (with the selector's spelling as its argument), `help.json-payload-output-alias`, `help.response-files`, and the notes' `help.required`, `help.default`, `help.choices`, `help.environment`, `help.existing`, `help.path-kind.file`, `help.path-kind.directory`, `help.minimum`, `help.maximum`, `help.requires` and `help.conflicts`. The widths above are measured on the resolved text. An application's descriptions, long descriptions and exit code descriptions may have text keys of their own.

An application may replace the help writer for human output, for example with a styled one (.NET: `CommandApp.HelpPresenter`, `CommandPresentation.cs:86-90`; Runic.CommandLine.Spectre). That output is implementation-defined; JSON output always carries the text above.

## Corpus

`help-corpus.json` has the application name the usage lines start with (`applicationName`), named `catalogs` in the format of the [catalog features corpus](grammar.md#normalized-results), and `cases`. Each case names its `catalog`, the help `path` and the `width` W, and `expected.lines`: the help text is each line followed by LF. A conforming implementation writes exactly that text. The conformance driver runs a case with `cl.help` ([conformance driver](../../eng/conformance/README.md#command-line)).

A corpus catalog declares help text with an optional `help` object on a command, a group, an option or an argument:

- on a command or group: `description`, `longDescription`, `examples` (an array of strings), `exitCodes` (an array of `{code, description}`) and `hidden`;
- on an option or argument: `description`, `valueName`, `defaultValue` and `hidden`.

An argument may be `trailing` ([Input model](grammar.md#input-model)); its arity is then 0 to unbounded.

The catalogs give no command `completion` a description except a hidden one, which replaces .NET's built-in completion command, so their root help has no completion row. The case `help.completion.root` shows that row; it `requires` the feature `help:completion-command`, which an implementation lists when it has the built-in command.

The rows of the catalog `slash` declare the Windows slash dialect (`dialect`, [Catalog features](grammar.md#catalog-features)) and require the feature `dialect:windows-slash`, which an implementation lists when it has that dialect.

The rows of the catalogs `reference` and `reference-default-command` are the .NET reference snapshots that the Rust producer also tests against (`native/crates/command-line/tests/help-reference` on the Rust branch), byte for byte. The corpus compares the English texts of the text keys above; an implementation whose English differs reports its rows as a deviation.
