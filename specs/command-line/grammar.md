# Portable command-line grammar, version 1

## Input model

Input is an already-tokenized ordered array of strings: the arguments a process receives, without the program name. The grammar never re-parses quoting, expands variables or globs, invokes a shell, normalizes Unicode, removes empty tokens, or changes culture. An implementation may let an application opt in to response files, which replace an `@file` argument with the arguments read from that file before the grammar applies; the grammar and its corpus see only the expanded arguments, and token indexes count them. Without the opt-in, an argument starting with `@` is ordinary data. Matching is ordinal and command names are case-sensitive.

Commands use invariant names matching `[a-z][a-z0-9-]*`. Command aliases are explicit and normalize to the canonical command path. Nested commands consume leading tokens. The fixture catalog has no root handler, so empty input is a missing-command error.

Long options accept `--name value` and `--name=value`. A zero-arity Boolean flag accepts no value. Short options are explicit one-ASCII-letter aliases such as `-v`; short bundles are unsupported. Slash aliases such as `/verbose` are recognized only when the exact alias is registered. Other slash-prefixed tokens are data, which preserves absolute Unix paths.

`--` ends option recognition. Every subsequent token is positional data, including `--help`, a registered slash alias, an empty string, or a leading-hyphen token. Positional definitions declare order and arity; only the final positional may be unbounded, and a required positional never follows an optional one. Positional tokens bind to the definitions in order: each definition takes as many tokens as its maximum allows while leaving the minimums of the definitions after it. When there are fewer positional tokens than the minimums add up to, the result is one `missing-argument` diagnostic for the first definition whose minimum, added to the minimums before it, exceeds the number of positional tokens; its single argument is that definition as the usage line shows it, its value name or else its name in angle brackets (the `id` in the fixture catalog), so `copy source.txt` names `<destination>`, and its `binding` is the definition's `id`, `destination`. A positional token left over after every definition reached its maximum is `unexpected-argument` at that token. A command may declare trailing arguments, a final argument definition marked as trailing: it receives every token after the first `--`, including later `--` tokens, and its positional definitions then receive only tokens before it. Option and argument values are preserved byte-for-byte after UTF-8 JSON decoding.

Scalar options and flags reject a second occurrence. Options configured with `repeat: append` preserve all occurrences in encounter order, including identical values. Built-in `--help` and `-h` are valid at the root and every command node, never invoke a handler, and normalize aliases to a help result. `--version` is root-only. The `help` word is described under [Help, version and groups](#help-version-and-groups).

A token is a long option when it starts with `--`, and a short option when it is `-` followed by exactly one UTF-16 code unit. Any longer token that starts with a single `-` is an unsupported short bundle, so `-vh`, `-v=1` and `-😀` (three UTF-16 code units) are `unsupported-short-bundle`, while `-é` is an unknown short option. A lone `-` is data. Lengths here and under [Diagnostic arguments](#diagnostic-arguments) count UTF-16 code units, unlike the protocol's byte limits.

## Normalized results

Every corpus row has an immutable `id`, `args`, and `expected` object. `expected.kind` is one of:

- `invocation`: canonical `commandPath` plus ordered `options` and `arguments`. Each binding has a catalog `id` and an ordered string `values` array. A present flag has an empty values array; absent bindings are omitted. Options are ordered by their first occurrence on the command line; later occurrences of an `append` option add their values to that binding. Options bound from [environment fallbacks](#environment-fallbacks) follow, in catalog order. Arguments are in definition order.
- `help`: canonical `commandPath`; the empty path denotes root help.
- `version`: the reserved root version request.
- `error`: the ordered diagnostics. Version 1 reports exactly one error diagnostic, for the first error in [Parse order](#parse-order). The one exception is an invalid choice that came from an environment fallback: it adds one `information` diagnostic, `RCLI2006` `environment-value-source`, which names the option and the variable.

A corpus row may also give `environment`, the captured environment variables that [environment fallbacks](#environment-fallbacks) read; a variable it does not list is unset.

Diagnostic `tokenIndex` is zero-based. When input ends before a required token, it equals `args.length`. So do the checks made after the whole command line was read: a missing required option, an invalid environment fallback and an invalid choice. An unknown command is reported at index 0, even when output or global options precede the token. An invalid `RUNIC_COMMANDLINE_OUTPUT` value is also reported at index 0. A corpus diagnostic may also list `arguments`, the diagnostic's ordered arguments; when it does, an implementation must report exactly those. A corpus diagnostic lists `binding`, the catalog `id` of the one declared argument or option it names, exactly when the diagnostic has one: `missing-argument`, `missing-option-value`, `unexpected-option-value` and `duplicate-option` of a declared option do, while `unknown-command`, `unknown-option`, `unexpected-argument` and `unsupported-short-bundle` name no declared binding and have none. Diagnostic messages are presentation text and not part of the grammar. Wave A assigns these stable grammar identities within the registered CLI diagnostic range:

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
| `invalid-choice` | the option or argument, then the choices joined with `, `. When the option is sensitive or the list exceeds 512 UTF-16 code units, the second argument is the help request (`--help`) instead |

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
3. **The help word and root requests**, after skipping leading output and global options: see [Help, version and groups](#help-version-and-groups).
4. **The command path.** The first token after leading output and global options must name a root command. Otherwise the default command applies if there is one, or the result is `unknown-command` at 0. Subcommand names then extend the path while they match.
5. **The command's tokens**, from left to right: `--`, `--help` or `-h` (help for the path), output tokens, options with their values, and positional tokens. Errors here are reported at their token.
6. **After the last token**, in this order:
   1. for each option in catalog order, its environment fallback, then whether it is required;
   2. for a group, a positional token is `unknown-command` at that token;
   3. positional binding (`missing-argument`, `unexpected-argument`);
   4. an invalid `RUNIC_COMMANDLINE_OUTPUT`;
   5. choices: options in binding order, then arguments.

## Help, version and groups

`help` is a word, not an option. Take the first token after leading output and global options and their values. When it is `help`, and the tokens after it (other than output and global options) name a complete command path, the result is help for that path: `help cache clear`, `help stat`. The path may use canonical names or aliases. `help` alone is root help. When the tokens after `help` do not name a complete path, including a partial one such as `help cache nosuch`, the result is `unexpected-root-help-argument` at the first token after `help` that is not an output token. After `--` or after a command name, `help` is data.

`--help` or `-h` as that first token is root help, whatever follows; `--version` there is the version request.

A group is a command node without a handler. Invoking a group with no further positional token is help for the group. A positional token after it is `unknown-command` at that token, with the group's path.

## Catalog features

The fixture catalog declares the features below with optional members:

- `globalOptions` on the catalog;
- `group` on a command;
- `required`, `choices`, `environmentVariable` and `acceptsNegativeNumbers` on an option;
- `acceptsNegativeNumbers` on an argument.

**Global options** belong to every command and may also precede the command path. They are flags or take exactly one value.

**Required options** must be bound, from the command line or an environment fallback; otherwise the result is `missing-required-option`.

### Environment fallbacks

An option may name an environment variable. When the command line does not give the option and the variable is set, its value binds the option; an empty value counts as set. For a flag, `true`, `yes` and `1` bind it, and `false`, `no` and `0` leave it unbound; the words are compared ignoring ASCII case. Any other value is `invalid-environment-value`. A value from the command line always wins.

### Choices

An option or argument may declare choices. A value matches a choice that equals it exactly. Otherwise it matches the first choice that equals it ignoring case, and binds as the declared spelling.

Choices are case-insensitive by per-character simple case mapping. ASCII letters always match their other case, and so does a letter with a one-to-one mapping, such as `Ö` and `ö`. Nothing expands: `GRÖSSE` does not match `größe`. Case-insensitive matching of other non-ASCII letters is implementation-defined in version 1, so portable choices use ASCII. A value that matches no choice is `invalid-choice`.

### Negative numbers

An option that expects a value, or the positional argument that would take the token, may accept negative numbers. Then a token that is a negative number is a value rather than an option. A negative number is `-` followed by one of:

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
