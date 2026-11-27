# Conformance driver

The language-neutral conformance driver runs a corpus against any implementation through a small adapter process in the
implementation's language. Its core is runic-sdk's
[`eng/conformance/core`](https://github.com/Runic-Artifex/runic-sdk/tree/main/eng/conformance/core), vendored here
unchanged (R7 G11, T1-15); the adapter protocol is runic-sdk's
[`eng/conformance/PROTOCOL.md`](https://github.com/Runic-Artifex/runic-sdk/blob/main/eng/conformance/PROTOCOL.md).
This repository has one corpus module, Command Line ([below](#command-line)).

```sh
bun eng/conformance/cli.ts dotnet-commandline   # build the .NET adapter, run every case, compare with the status file
bun eng/conformance/cli.ts list | cases [--write] | lint
```

The development shell (`flake.nix`) provides Bun 1.4.4, the version runic-sdk pins.

| Path | Content |
| --- | --- |
| [`cli.ts`](cli.ts) | the command's entry point, which registers this repository's corpus modules |
| [`implementations.json`](implementations.json) | each implementation: its corpus, roles, adapter build and command, and status file |
| [`commandline/`](commandline) | the Command Line module (`commandline/1`) |
| [`core/`](core) | runic-sdk's driver core, vendored: never edit it here |
| [`core.vendor.json`](core.vendor.json) | the stamp: the runic-sdk commit and the SHA-256 of each vendored file |
| [`vendor.mjs`](vendor.mjs) | the freshness check and the update (the same script is in runic-translations-sdk) |

## The vendored core

`core/` is a copy of runic-sdk's `eng/conformance/core` at the commit in `core.vendor.json`. A change to the core goes
to runic-sdk first; this repository then updates its copy.

**Update it** from a runic-sdk commit or branch, from the repository root:

```sh
bun eng/conformance/vendor.mjs update <commit>   # or main; replaces core/ and rewrites the stamp
bun eng/conformance/vendor.mjs check
bun test eng/conformance/vendor.test.mjs
bun eng/conformance/cli.ts                       # and each implementation, once there are some
```

Pin a commit on runic-sdk's `main`. A stacked change may pin a runic-sdk pull request's head commit until it merges, then
moves to the merge commit. `update` and `upstream` fetch runic-sdk over the network, or read a local clone that has the
commit with `--sdk <dir>`.

**Checks:**

- `vendor.mjs check` (offline): each file of `core/` has the stamp's hash, and the stamp lists exactly the files of
  `core/`. Line endings do not count. A local edit fails with a hint to update from runic-sdk instead.
  `vendor.test.mjs` runs it in the `workflows` job of every CI run, and shows that a one-byte edit fails.
- `vendor.mjs upstream [<ref>]` (network): the stamp and the copy match runic-sdk at the pinned commit, or at `<ref>`
  (`upstream main` shows whether the pin is behind). This catches a stamp edited by hand. CI runs it only when
  `core/` or `core.vendor.json` changes, as the Cargo.lock checks run only when the lock changes (R1 section 7), so an
  ordinary pull request does not depend on reaching GitHub.

## Command Line

The module `commandline/1` runs the corpus of [`specs/command-line`](../../specs/command-line/README.md): the grammar
corpus, output classification, the catalog features corpus, and the protocol's wire inputs, semantic structures, writer
cases and examples. Its
committed files are in [`specs/command-line/conformance`](../../specs/command-line/conformance):

- `cases.json`: one line per case, keyed `<file>/<id>` with the corpus file relative to `specs/command-line` and without
  `.json` (`grammar-corpus/grammar.empty-input`, `protocol/wire-inputs/wire.empty`); an example has no row id, so its
  key is its file (`protocol/examples/success`). Each case's `sha256` covers its row and the shared parts it uses (the
  fixture catalog, the client context, the sample payload contract, the example bytes). `cases` checks it (CI passes
  `--check`), and `cases --write` rewrites it after a corpus change.
- `status/dotnet.json`: Runic.CommandLine's expected result for every case. It passes all of them, at maturity
  `stable`. A case an implementation does not pass needs `known-failure` with an issue (a `development` or `preview`
  implementation only) or `deviation` citing its row of a difference table (D011, R7 T1-13).

```sh
bun eng/conformance/cli.ts dotnet-commandline [--only <text>] [--set protocol/writer-cases] [--verbose]
bun eng/conformance/cli.ts cases --check
bun eng/conformance/cli.ts lint
```

The driver ([`commandline/run.ts`](commandline/run.ts)) builds every input and compares every result as the spec
defines it. The adapter only calls its implementation and reports what it returned; a failure it cannot name in the
corpus's terms comes back as `unmapped:<type>`, and the case fails.

| Op | Role | args | ok |
| --- | --- | --- | --- |
| `cl.parse` | parser | `{catalog, argv, env}` | the normalized result of grammar.md: `{kind: "invocation", commandPath, options, arguments}`, `{kind: "help", commandPath}`, `{kind: "version"}` or `{kind: "error"}`, each with `diagnostics: [{code, kind, tokenIndex, arguments, binding}]`; or `{error}` |
| `cl.validate` | parser | `{catalog, argv, env}` | the normalized result after input validation (grammar.md, "Input validation"): as `cl.parse`, and an error's diagnostics may be validation diagnostics, whose `tokenIndex` is null |
| `cl.catalog` | parser | `{catalog}` | `{kind: "valid"}`, or `{kind: "invalid", issues: [{code, location}]}` for a catalog the implementation rejects (grammar.md, "Catalog validity"); or `{error}` |
| `cl.classify` | parser | `{catalog, argv, env}` | `{kind: "output-mode", mode, source, diagnostics}` or `{kind: "error", diagnostics}`; or `{error}` |
| `cl.readEnvelope` | reader | `{input, payloadType, contract}` | `{result: "accepted", envelope, payloadDecodeAttempted}` or `{result: "rejected", reason, payloadDecodeAttempted}` |
| `cl.write` | writer | `{input}` | `{output}`, the framed response as base64; or `{error}` |

- **Grammar and classification.** `catalog` is the grammar corpus's fixture catalog, or for a row of the catalog
  features corpus the catalog the row names, and `env` the row's captured environment (a variable it does not list, or
  lists as `null`, is unset). A catalog features row runs `cl.parse`, `cl.validate` or `cl.catalog` as its `operation`
  says. For `cl.catalog` the driver compares the kind, and that every issue of an invalid catalog has the row's code;
  how many issues an implementation reports is not compared. A row's `requires` are the case's: an implementation
  that does not list one of them among its `describe` features gets `unsupported` without a call. The driver compares the kind, the path and
  the ordered bindings; for each diagnostic the code, kind and token index, `arguments` when the corpus lists them, and
  `binding` exactly. A result other than an error has no diagnostics.
- **Reading.** `input` is the frame as base64: a wire case's bytes or recipe, a semantic document as compact JSON and
  LF, or an example file. The client supports one `payloadType`; `contract` is its entry of the manifest's
  `samplePayloads`, for an adapter that registers its payload types from it (the .NET adapter has record types for
  them). The driver compares the rejection reason, `payloadDeserializationAttempted` where the corpus states it, and,
  for an accepted frame, the envelope the reader returns with the frame's known members: the envelope's, the fault's
  (with `helpUri`, and of `data` only its `type`) and each diagnostic's.
- **Writing.** `input` is the writer case's input. The output must be one compact JSON object and LF, strict UTF-8 and
  strict JSON (no duplicate member), whose value equals the expected example or document. An adapter that lists the
  capability `escaping-profile:dotnet` must also match each example byte for byte.

**Roles.** An implementation lists the roles it has: `parser`, `reader` and `writer`. A case of another role is
`excluded` (`role:writer`).

**The .NET adapter** is [`tests/dotnet/Runic.CommandLine.Conformance`](../../tests/dotnet/Runic.CommandLine.Conformance),
a plain program on the shipped library's public API, never packed. The solution build compiles it. CI checks the case
list and the status files in the `workflows` job, and runs the adapter in `managed-and-pack` after `eng/test.sh`, with
`--no-build` (Linux only).
