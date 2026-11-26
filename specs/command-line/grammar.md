# Portable command-line grammar, version 1

## Input model

Input is an already-tokenized ordered array of strings: the arguments a process receives, without the program name. The grammar never re-parses quoting, expands variables or globs, invokes a shell, normalizes Unicode, removes empty tokens, or changes culture. An implementation may let an application opt in to response files, which replace an `@file` argument with the arguments read from that file before the grammar applies; the grammar and its corpus see only the expanded arguments, and token indexes count them. Without the opt-in, an argument starting with `@` is ordinary data. Matching is ordinal and command names are case-sensitive.

Commands use invariant names matching `[a-z][a-z0-9-]*`. Command aliases are explicit and normalize to the canonical command path. Nested commands consume leading tokens. The fixture catalog has no root handler, so empty input is a missing-command error.

Long options accept `--name value` and `--name=value`. A zero-arity Boolean flag accepts no value. Short options are explicit one-ASCII-letter aliases such as `-v`; short bundles are unsupported. Slash aliases such as `/verbose` are recognized only when the exact alias is registered. Other slash-prefixed tokens are data, which preserves absolute Unix paths.

`--` ends option recognition. Every subsequent token is positional data, including `--help`, a registered slash alias, an empty string, or a leading-hyphen token. Positional definitions declare order and arity; only the final positional may be unbounded, and a required positional never follows an optional one. Positional tokens bind to the definitions in order: each definition takes as many tokens as its maximum allows while leaving the minimums of the definitions after it. When there are fewer positional tokens than the minimums add up to, the result is one `missing-argument` diagnostic for the first definition whose minimum, added to the minimums before it, exceeds the number of positional tokens; its single argument is that definition as the usage line shows it, its value name, or else its name, in angle brackets (the `id` in the fixture catalog), so `copy source.txt` names `<destination>`, and its `binding` is the definition's `id`, `destination`. A positional token left over after every definition reached its maximum is `unexpected-argument` at that token. A command may declare trailing arguments, a final argument definition marked as trailing: it receives every token after the first `--`, including later `--` tokens, and its positional definitions then receive only tokens before it. Option and argument values are preserved byte-for-byte after UTF-8 JSON decoding.

Scalar options and flags reject a second occurrence. Options configured with `repeat: append` preserve all occurrences in encounter order, including identical values. Built-in `--help` and `-h` are valid at the root and every command node, never invoke a handler, and normalize aliases to a help result. `--version` is root-only. The `help` word is described under [Help, version and groups](#help-version-and-groups).

A token is a long option when it starts with `--`, and a short option when it is `-` followed by exactly one UTF-16 code unit. Any longer token that starts with a single `-` is an unsupported short bundle, so `-vh`, `-v=1` and `-😀` (three UTF-16 code units) are `unsupported-short-bundle`, while `-é` is an unknown short option. A lone `-` is data. Lengths here and under [Diagnostic arguments](#diagnostic-arguments) count UTF-16 code units, unlike the protocol's byte limits.

## Normalized results

Every corpus row has an immutable `id`, `args`, and `expected` object. `expected.kind` is one of:

- `invocation`: canonical `commandPath` plus ordered `options` and `arguments`. Each binding has a catalog `id` and an ordered string `values` array. A present flag has an empty values array; absent bindings are omitted. Options are ordered by their first occurrence on the command line; later occurrences of an `append` option add their values to that binding. Options bound from [environment fallbacks](#environment-fallbacks) follow, in [catalog order](#global-options). Arguments are in definition order.
- `help`: canonical `commandPath`; the empty path denotes root help.
- `version`: the reserved root version request.
- `error`: the ordered diagnostics. Version 1 reports exactly one error diagnostic, for the first error in [Parse order](#parse-order). The one exception is an invalid choice of an option bound from its environment fallback: the `invalid-choice` diagnostic is followed by one note, `RCLI2006` `environment-value-source`, with phase `parse` and severity `information`. The note has the `tokenIndex` and command path of the `invalid-choice` diagnostic, the option's `id` as its `binding`, and two `arguments`: the option as the `invalid-choice` diagnostic names it, then the variable (`grammar.option.environment-choice-source`). No other diagnostic gets the note; `invalid-environment-value` already names the variable.

A corpus row may also give `environment`, the captured environment variables that [environment fallbacks](#environment-fallbacks) read; a variable it does not list is unset.

Diagnostic `tokenIndex` is zero-based. When input ends before a required token, it equals `args.length`. So do the checks made after the whole command line was read: a missing required option, an invalid environment fallback and an invalid choice. An unknown command is reported at index 0, even when output or global options precede the token. An invalid `RUNIC_COMMANDLINE_OUTPUT` value is also reported at index 0. A corpus diagnostic may also list `arguments`, the diagnostic's ordered arguments; when it does, an implementation must report exactly those. A corpus diagnostic lists `binding`, the catalog `id` of the one declared argument or option it names, exactly when the diagnostic has one. These kinds have one: `missing-argument`, `missing-required-option`, `invalid-environment-value` and `environment-value-source`; `invalid-choice` (the option or argument); and `missing-option-value`, `unexpected-option-value` and `duplicate-option` of a declared option. These kinds have none: the same three kinds when they name the output selector, its aliases or the JSON output alias; and `unknown-command`, `unknown-option`, `unexpected-argument`, `unsupported-short-bundle`, `unexpected-root-help-argument`, `invalid-output-mode`, `conflicting-output-selector`, `transport-output-option-collision` and the response-file kinds, which name no declared binding. Diagnostic messages are presentation text and not part of the grammar. Wave A assigns these stable grammar identities within the registered CLI diagnostic range:

| Code | Kind |
|---|---|
| `RCLI1001` | `unknown-option` |
| `RCLI1002` | `unknown-command` |
| `RCLI1003` | `missing-option-value` |
| `RCLI1004` | `unexpected-option-value` |
| `RCLI1005` | `missing-argument` |
| `RCLI1006` | `unexpected-argument` |
| `RCLI1007` | `duplicate-option` |
| `RCLI1008` | `unsupported-short-bundle` |
| `RCLI1010` | `invalid-output-mode` |
| `RCLI1011` | `transport-output-option-collision` |
| `RCLI1012` | `missing-required-option` |
| `RCLI1013` | `unexpected-root-help-argument` |
| `RCLI1014` | `invalid-environment-value` |
| `RCLI1015` | `invalid-choice` |
| `RCLI1016` | `conflicting-output-selector` |

`RCLI1011` is reported at the first token after the command path when the resolved command declares an option spelled like the output selector. Catalog validation rejects such a catalog, so a valid catalog never produces it.

Response-file expansion, where an application opts in, reports `RCLI1017` `response-file-unreadable`, `RCLI1018` `response-file-invalid-utf8`, `RCLI1019` `response-file-unterminated-quote`, `RCLI1020` `response-file-too-deep` and `RCLI1021` `response-file-cycle`, with the index of the command-line argument that named the first file.

Usage and parse diagnostics map to the stable usage exit category, whose default process exit code is 2. Diagnostics contain token positions and safe identifiers, not secret-bearing values.

### Diagnostic arguments

Each kind has these `arguments`, in order. An option is named by its canonical spelling (`--encoding`), the output selector by its spelling (`--format`), and an argument as the usage line shows it (`<destination>`).

| Kind | Arguments |
|---|---|
| `unknown-option` | the token, up to its first `=` |
| `unknown-command` | the token; none when the command line holds only output and global options, or nothing |
| `missing-option-value`, `unexpected-option-value`, `duplicate-option` | the option, or the selector or JSON output alias spelling |
| `missing-argument` | the argument |
| `unexpected-argument`, `unsupported-short-bundle`, `unexpected-root-help-argument`, `transport-output-option-collision` | none |
| `invalid-output-mode` | the selector; `RUNIC_COMMANDLINE_OUTPUT` for an invalid environment value |
| `conflicting-output-selector` | the later spelling, then the earlier one |
| `missing-required-option` | the option, then the command path joined with spaces |
| `invalid-environment-value` | the option, then the variable |
| `invalid-choice` | the option or argument, then the choices joined with `, `. When the option or argument is [sensitive](#catalog-features) or the list exceeds 512 UTF-16 code units, the second argument is the help request (`--help`) instead |
| `environment-value-source` | the option, then the variable |

A token echoed as an argument becomes `<invalid>` when it is empty, is longer than 128 UTF-16 code units, or contains a control character (General_Category Cc) or a protocol [Unicode control character](protocol/README.md#unicode-control-characters).

**Suggestions.** An `unknown-command` or `unknown-option` diagnostic may end with one more argument, a suggestion:

- **Candidates.** For a command, the visible command spellings and aliases at that level. For an option, the command's visible option spellings and aliases, plus `--help`, `-h`, `--version`, the selector and the JSON output alias.
- **Length bounds.** A token shorter than 2 or longer than 64 UTF-16 code units gets no suggestion. The distance limit is 1 for a token shorter than 5 code units and 2 for a longer one. Candidates longer than 64 code units are skipped, and so are candidates whose length differs from the token's by more than the limit.
- **Distance.** The token and each candidate are lowercased (invariant culture) and compared by optimal string alignment distance: Levenshtein distance plus the transposition of two adjacent code units.
- **Choice.** The suggestion is the candidate with the smallest distance within the limit. Among equal distances, the first in ordinal order wins.
- **Former selector.** An unknown `--output` whose value is `human` or `json` suggests the selector instead (see [Output classification](#output-classification)).

**Message keys.** A diagnostic's `messageKey` is `diagnostics.<kind>`, with three exceptions:

- `diagnostics.<kind>-suggestion` when the diagnostic has a suggestion;
- `diagnostics.missing-command` for an `unknown-command` without arguments;
- `diagnostics.invalid-choice-hidden` for an `invalid-choice` that names the help request.

Messages themselves are presentation text.

## Parse order

A parser reads a command line in this order and reports the first error it finds:

1. **Output selection.** The selector, its aliases and the JSON output alias are scanned over the whole command line, up to the first `--`, before anything else. A duplicate, a conflict, an alias with a value, a missing value or an invalid value is reported first, so `status --quiet --format xml` is `invalid-output-mode` at 3, not `unknown-option` at 1. A selector without an inline value is missing its value when it is the last token, or when the next token:
   - is `--`, `--help`, `-h` or `--version`;
   - starts with `-`;
   - is another output token;
   - is an option of the command that the leading tokens name.
2. **Empty input** is `unknown-command` at 0 without arguments, unless the catalog has a default command.
   1. **Leading global option values.** Walk the leading output and global options. The first global option that takes a value, has no inline value, and is not followed by its value (see [Global options](#global-options)) is `missing-option-value` at the token after it. Its command path is the default command's when the catalog has one, and otherwise empty.
3. **The help word and root requests**, after skipping leading output and global options: see [Help, version and groups](#help-version-and-groups).
4. **The command path.** The first token after leading output and global options must name a root command. Otherwise the default command applies if there is one, or the result is `unknown-command` at 0. Subcommand names then extend the path while they match.
5. **The command's tokens**, from left to right: `--`, `--help` or `-h` (help for the path), output tokens, options with their values, and positional tokens. Errors here are reported at their token.
6. **After the last token**, in this order:
   1. for each option in [catalog order](#global-options), its environment fallback, then whether it is required;
   2. for a group, a positional token is `unknown-command` at that token;
   3. positional binding (`missing-argument`, `unexpected-argument`);
   4. an invalid `RUNIC_COMMANDLINE_OUTPUT`;
   5. choices: options in binding order, then arguments.

## Help, version and groups

`help` is a word, not an option. Take the first token after leading output and global options and their values. When it is `help`, and the tokens after it (other than output and global options) name a complete command path, the result is help for that path: `help cache clear`, `help stat`. The path may use canonical names or aliases. `help` alone is root help. When the tokens after `help` do not name a complete path, including a partial one such as `help cache nosuch`, the result is `unexpected-root-help-argument` at the first token after `help` that is not an output token. After `--` or after a command name, `help` is data.

`--help` or `-h` as that first token is root help, whatever follows; `--version` there is the version request.

A group is a command node without a handler. Invoking a group with no further positional token is help for the group. A positional token after it is `unknown-command` at that token, with the group's path.

## Catalog features

A catalog declares the features below with optional members:

- `globalOptions` on the catalog;
- `group` on a command;
- `required`, `choices`, `environmentVariable`, `acceptsNegativeNumbers` and `sensitive` on an option;
- `choices`, `acceptsNegativeNumbers` and `sensitive` on an argument.

`sensitive` is a Boolean, false when absent. A sensitive option or argument has values that must not appear in diagnostics, help or suggestions; in this grammar it changes only the second argument of `invalid-choice` (see [Diagnostic arguments](#diagnostic-arguments)). The fixture catalog uses neither `sensitive` nor choices on an argument.

### Global options

Global options belong to every command, including groups and subcommands, and may also precede the command path. Their catalog rules:

- **Arity.** A global option is a flag (arity 0..0) or takes exactly one value (1..1). Any other arity makes the catalog invalid with `RCLI0028`.
- **Settings.** A global option rejects a second occurrence (`repeat: error`), and it is never required or sensitive. It may name an environment variable, declare choices and accept negative numbers.
- **A command option with the same `id`** is the global option for that command, so it must agree with it; otherwise the catalog is invalid with `RCLI0019`. It has the same long spelling, the same arity and the same set of aliases, `repeat: error`, and is neither required nor sensitive. When both declare an environment variable, or both declare choices, they are the same variable, or the same choices in the same order. It accepts negative numbers only when the global option does. For that command, the option has the environment variable and the choices that either of them declares, and accepts negative numbers when the global option does. Help text is presentation and not part of the grammar. Runic Command Line takes each help member from the command option where it sets one, and from the global option otherwise.

**Catalog order** is the order of a command's options for [environment fallbacks](#environment-fallbacks) and required options (step 6.1 of [Parse order](#parse-order)), and for the options those fallbacks bind in an invocation. It is the command's own options in declaration order, then the global options in declaration order. A global option that is also a command option keeps the command option's place.

An option's value ends at a token that ends option values and is not a negative number the option accepts; a missing value is `missing-option-value` at that token, or at `args.length` when the input ends. Those tokens are `--`, `--help`, `-h`, `--version`, an output token, any token that starts with `--`, any other token of two or more UTF-16 code units that starts with `-`, and a slash alias of the resolved command. A global option before the command path follows the same rule, before the parser looks for the help word, root requests and the command path (step 2.1 of [Parse order](#parse-order)). There, no command is resolved yet, so the slash aliases that end a value are those of the global options. So `--profile --help`, `--profile --version` and `--profile` alone are `missing-option-value` at 1 (`grammar.option.global-value-before-command-takes-help.v2`), as `status --profile --help` is at 2, and so is `--profile --verbose status`. Any other token is the value: `--profile status` and `--profile help` name no command (`unknown-command` at 0 without arguments). Once the command is resolved, the value binds as every option value does (step 5).

**Required options** must be bound, from the command line or an environment fallback; otherwise the result is `missing-required-option`.

### Environment fallbacks

An option may name an environment variable. When the command line does not give the option and the variable is set, its value binds the option; an empty value counts as set. For a flag, `true`, `yes` and `1` bind it, and `false`, `no` and `0` leave it unbound; the words are compared ignoring ASCII case. Any other value is `invalid-environment-value`. For an option that takes values, the variable's whole value binds as exactly one value, whatever the option's maximum or `repeat` policy: it is never split. So an option whose arity requires two or more values cannot name an environment variable; the catalog is invalid with `RCLI0030`. A value from the command line always wins. A value from a fallback is checked against the option's choices like any other (`grammar.option.environment-choice-canonical`).

### Choices

An option or argument may declare choices. A value matches a choice that equals it exactly. Otherwise it matches the first choice that equals it ignoring case, and binds as the declared spelling.

Choices are case-insensitive by per-character simple case mapping. ASCII letters always match their other case, and so does a letter with a one-to-one mapping, such as `Ö` and `ö`. Nothing expands: `GRÖSSE` does not match `größe`. Case-insensitive matching of other non-ASCII letters is implementation-defined in version 1, so portable choices use ASCII. A value that matches no choice is `invalid-choice`.

Every value of an option or argument that takes several is checked and bound as its declared spelling. The `invalid-choice` diagnostic names the option or argument, not the value, so an option is reported once however many of its values match no choice. The first option in binding order with such a value is reported, then the first argument in definition order.

An empty list declares no choices, and every value is accepted. A flag binds no values, so choices on a flag make the catalog invalid with `RCLI0029`.

### Negative numbers

An option that expects a value, or the positional argument that would take the token, may accept negative numbers. Then a token that is a negative number is a value rather than an option.

The positional argument that would take a token is the one that [positional binding](#input-model) gives it if it is the last positional token. Bind the positional tokens before it, and the token itself, to the command's argument definitions in order, trailing arguments excluded. When these tokens are no more than the minimums add up to, each definition takes its minimum in order, so the token goes to the first definition whose minimum, added to the minimums before it, exceeds the number of tokens before it: `plot -3` gives `-3` to `points`, which does not accept negative numbers, so it is `unknown-option` at 1 (`grammar.number.argument-fills-minimums-first`). Otherwise each definition takes as many tokens as its maximum allows while leaving the minimums of the definitions after it: `plot x -3` gives `-3` to `delta` (`grammar.number.argument-leaves-later-minimum`). The parser decides when it reaches the token, so a later positional token can still move a negative number into an earlier definition: `plot x -3 y` binds `-3` to `points` (`grammar.number.argument-moves-to-earlier-definition`). When no definition takes the token, no positional argument accepts it as a negative number, so it keeps its option meaning: `deploy --target prod 1 -3` is `unknown-option` at 4, because `count` takes at most one token (`grammar.number.argument-past-maximum`).

A negative number is `-` followed by one of:

- `[0-9]+(\.[0-9]*)?` or `\.[0-9]+`, then an optional exponent `[eE][+-]?[0-9]+`, then optional trailing whitespace (U+0009–U+000D, U+0020);
- `Infinity` or `NaN`, in any ASCII case.

Other tokens that start with `-` keep their option meaning, so `--offset -x` is `missing-option-value`.

## Output classification

`--format human|json` is the reserved scalar output selector, recognized in separated and equals forms before `--`, before or after the command path. It is ASCII case-insensitive for the two invariant values. Duplicate selectors, a missing value, or another value are usage errors. After `--`, the same text is positional data. The corpus uses this default spelling. An implementation may let an application declare another selector spelling and hidden aliases of it (former spellings such as `--output`); a hidden alias is recognized exactly like the selector, a missing or invalid value is reported with the selector's spelling, the same alias given twice is `duplicate-option`, and two different spellings together are `conflicting-output-selector`. A catalog option may not share a spelling with the selector or its aliases. `--output` is no default spelling: where the catalog does not declare it, `--output human|json` is `unknown-option` (`unknown-command` before the command path) like any other unknown option, and an implementation should suggest the selector in its message, because `--output` was the selector's spelling before `--format`; the code, kind and token index are those of any unknown option. With another value it suggests nothing.

An explicit valid selector has highest precedence and causes `RUNIC_COMMANDLINE_OUTPUT` to be ignored, even when the captured environment value is invalid. Without an explicit selector, non-empty environment values `human` and `json` are recognized ASCII case-insensitively. Null and empty environment values are absent. Any other value, including whitespace-padded text, is invalid. If both sources are absent, the default is `human`.

A catalog may declare a JSON output alias (`catalog.jsonOutputAlias`, `--json` in the fixture catalog): a zero-arity flag that stands for the selector with the value `json`. It is recognized exactly, in the same positions as the selector, before or after the command path and before `--`; after `--` it is positional data. It never binds as a command option or consumes a token as an option value, so an option that expects a value reports a missing value when the alias follows it. The alias with an inline value (`--json=true`) is `unexpected-option-value`. The alias given twice is `duplicate-option`. The alias and the selector together are `conflicting-output-selector`, reported at the later of the two. Diagnostics follow token order, so a missing or invalid value of an earlier selector is reported first. The alias is an explicit selection, so it overrides `RUNIC_COMMANDLINE_OUTPUT` in the same way. An alias may be declared with a payload shape; that changes only what JSON output contains (see the protocol README), not recognition, diagnostics or the classified mode.

Classification is a pure operation over captured arguments and a captured nullable environment value. Implementations must not read or mutate process-global environment state while replaying the corpus.

## Windows slash dialect

The grammar above is the portable grammar, the default. An implementation may let an application declare another
dialect for its catalog; the grammar corpus covers only the portable grammar. Runic Command Line offers the Windows
slash dialect and case-insensitive matching, which may be declared together or apart. Results are the same normalized results: canonical command paths, option and argument IDs, and values exactly
as given. Token indexes count the arguments as given.

Case-insensitive matching compares command names, aliases, display spellings and option spellings, including `--help`,
`-h`, `--version`, the output selector, its aliases, the JSON output alias and the completion option, ignoring
case as [Choices](#choices) describes. Values, arguments and data are never changed.

Under the slash dialect a command line is read in this order:

1. Leading output and global options may precede the command, as in the portable grammar, in either spelling.
2. The command path is read next. A token `/word` or `word` that spells a command at the current level (its name, an
   alias or its display spelling) is that command, and the first token that does not ends the path. A subcommand
   therefore wins over a parent option written with the same word. `help` or `/help` followed by a command path asks
   for that path's help, as `help` does in the portable grammar.
3. `/?` and `/help` request help and `/version` the version. The output selector, its hidden aliases, the JSON output
   alias and the completion option are recognized as `/name` (with `/name value` or `/name=value` for a value) as
   well as in their portable spellings.
4. After the path, `/name` is a flag or option of the command, and `/name value` or `/name=value` gives it a value.
   An option declared as `--name`, `-n` or `/name` is written `/name` or `/n`; its portable spellings are still
   recognized, and an unknown `-x` or `--name` is still `unknown-option`.
5. Every other token is data, as an unknown word after the command path is in the portable grammar. A token that
   starts with `/` but spells no command, option or framework request, such as `/weird-name`, `/tmp/file` or an LDAP
   path, binds as a positional argument unchanged. An unknown `/word` where a command belongs is `unknown-command`.
6. `--` still ends option recognition; every later token is data.

Ambiguous tokens follow these rules: a token that spells a known command (in the path) or option, `/?`, `/help`,
`/version` or a framework option is that word, never data, even where a value is expected, so an option that
expects a value reports `missing-option-value` before it. Pass such a value inline (`/name=/help`) or after `--`.
Within one command, two option spellings that the dialect writes alike (`--n` and `-n`, `--verbose` and `/verbose`)
are a catalog error, as are option spellings `/help` and `/version`.

Diagnostics keep their codes, kinds, token indexes and argument positions. Arguments that name a declared option
or a framework option are written in the dialect (`/name`, `/format`), and an `unknown-command` suggestion is the
command's display spelling with its slash (`/policy`). Help and usage hints write commands, options, `/?` and
`/version` the same way.
