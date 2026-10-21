# Portable command-line grammar, version 1

## Input model

Input is an already-tokenized ordered array of strings, equivalent to `Main(string[] args)`. The grammar never re-parses quoting, expands variables or globs, invokes a shell, normalizes Unicode, removes empty tokens, or changes culture. Matching is ordinal and command names are case-sensitive.

Commands use invariant names matching `[a-z][a-z0-9-]*`. Command aliases are explicit and normalize to the canonical command path. Nested commands consume leading tokens. The fixture catalog has no root handler, so empty input is a missing-command error.

Long options accept `--name value` and `--name=value`. A zero-arity Boolean flag accepts no value. Short options are explicit one-ASCII-letter aliases such as `-v`; short bundles are unsupported. Slash aliases such as `/verbose` are recognized only when the exact alias is registered. Other slash-prefixed tokens are data, which preserves absolute Unix paths.

`--` ends option recognition. Every subsequent token is positional data, including `--help`, a registered slash alias, an empty string, or a leading-hyphen token. Positional definitions declare order and arity; only the final positional may be unbounded. A command may declare trailing arguments (`IsTrailing`): they receive every token after the first `--`, including later `--` tokens, and its positional definitions then receive only tokens before it. Option and argument values are preserved byte-for-byte after UTF-8 JSON decoding.

Scalar options and flags reject a second occurrence. Options configured with `repeat: append` preserve all occurrences in encounter order, including identical values. Built-in `--help` and `-h` are valid at the root and every command node, never invoke a handler, and normalize aliases to a help result. `--version` is root-only.

## Normalized results

Every corpus row has an immutable `id`, `args`, and `expected` object. `expected.kind` is one of:

- `invocation`: canonical `commandPath` plus ordered `options` and `arguments`. Each binding has a catalog `id` and an ordered string `values` array. A present flag has an empty values array; absent bindings are omitted.
- `help`: canonical `commandPath`; the empty path denotes root help.
- `version`: the reserved root version request.
- `error`: one or more ordered diagnostics.

Diagnostic `tokenIndex` is zero-based. When input ends before a required token, it equals `args.length`. Wave A assigns these stable grammar identities within the registered CLI diagnostic range:

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
| `RCLI1016` | `conflicting-output-selector` |

Usage and parse diagnostics map to the stable usage exit category, whose default process exit code is 2. Diagnostics contain token positions and safe identifiers, not secret-bearing values.

## Output classification

`--format human|json` is the reserved scalar output selector, recognized in separated and equals forms before `--`, before or after the command path. It is case-insensitive for the two invariant values. Duplicate selectors, a missing value, or another value are usage errors. After `--`, the same text is positional data. The corpus uses this default spelling. An implementation may let an application declare another selector spelling and hidden aliases of it (former spellings such as `--output`); a hidden alias is recognized exactly like the selector, a missing or invalid value is reported with the selector's spelling, the same alias given twice is `duplicate-option`, and two different spellings together are `conflicting-output-selector`. A catalog option may not share a spelling with the selector or its aliases.

An explicit valid selector has highest precedence and causes `RUNIC_COMMANDLINE_OUTPUT` to be ignored, even when the captured environment value is invalid. Without an explicit selector, non-empty environment values `human` and `json` are recognized case-insensitively. Null and empty environment values are absent. Any other value, including whitespace-padded text, is invalid. If both sources are absent, the default is `human`.

A catalog may declare a JSON output alias (`catalog.jsonOutputAlias`, `--json` in the fixture catalog): a zero-arity flag that stands for the selector with the value `json`. It is recognized exactly, in the same positions as the selector, before or after the command path and before `--`; after `--` it is positional data. It never binds as a command option or consumes a token as an option value, so an option that expects a value reports a missing value when the alias follows it. The alias with an inline value (`--json=true`) is `unexpected-option-value`. The alias given twice is `duplicate-option`. The alias and the selector together are `conflicting-output-selector`, reported at the later of the two. Diagnostics follow token order, so a missing or invalid value of an earlier selector is reported first. The alias is an explicit selection, so it overrides `RUNIC_COMMANDLINE_OUTPUT` in the same way. An alias may be declared with a payload shape; that changes only what JSON output contains (see the protocol README), not recognition, diagnostics or the classified mode.

Classification is a pure operation over captured arguments and a captured nullable environment value. Implementations must not read or mutate process-global environment state while replaying the corpus.
