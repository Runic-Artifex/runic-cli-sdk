# `runic.commandline/1` machine response protocol

This directory is the language-neutral, offline contract for Runic.CommandLine
command-line machine output. `--format json` (the output selector) or the captured
`RUNIC_COMMANDLINE_OUTPUT=json` value selects this protocol, as does an
application's JSON output alias such as `--json`. An alias that an application
declares with the payload shape to keep an existing
contract writes the bare payload or failure-data document instead, which is
outside this protocol: stdout holds only that document and LF, and stderr holds
the human diagnostics followed by the application's failure text or, without
it, the fault line. The selector and the environment value still select this
protocol. The namespace of
the implementation remains `Runic.CommandLine.*`; neither a parser
package nor a hosting framework is part of the wire contract.

The words **MUST**, **MUST NOT**, **SHOULD**, and **MAY** are normative.

## Wire framing and encoding

A response is exactly one compact JSON object encoded as strict UTF-8 and
followed by exactly one byte `0A` (LF). The first byte MUST be `{`; the final
two bytes MUST be `}` and LF. A producer MUST NOT emit a UTF-8 BOM, a leading
or trailing space, CRLF, a second JSON value, an ANSI escape sequence, a log
line, or progress output on stdout. JSON strings escape at least `"`, `\` and
U+0000–U+001F, as RFC 8259 requires; which other characters a writer escapes is
its escaping profile (see [Examples, writer cases and escaping
profiles](#examples-writer-cases-and-escaping-profiles)). stderr MAY contain
operator diagnostics, but a client MUST NOT need stderr to interpret the
response.

The complete framed response MUST be at most 1,048,576 bytes, including the
terminal LF, and JSON nesting MUST be at most 32 levels: the root object is
level 1 and each object or array inside a container is one level deeper, so a
frame may hold an array at level 32 but not one at level 33. A reader rejects an
oversized response while continuing to drain the process pipe. NDJSON and
streaming are not `runic.commandline/1`.

## Envelope

Every envelope contains all nine keys below. Mutually exclusive values remain
present as JSON `null`. Producers emit the keys in this order for reproducible
output; consumers MUST NOT depend on property order.

| Key | JSON type | Rule |
|---|---|---|
| `protocol` | string | Exactly `runic.commandline/1`. |
| `requestId` | string | Valid opaque request identifier, as defined below. |
| `command` | string | Non-null canonical command path separated by one ASCII space. |
| `success` | Boolean | True only for the success outcome category. |
| `exitCode` | integer | Signed 32-bit process exit code; nonzero when `success` is false, zero or an informational success code when it is true. |
| `payloadType` | string or null | Required and non-null on success; null on failure. |
| `payload` | any JSON value | Typed result on success, including JSON null when its type permits null; null on failure. |
| `fault` | object or null | Null on success; required and non-null on failure. |
| `diagnostics` | array | Zero to 64 safe diagnostic objects. |

Success has `success: true`, `exitCode: 0`, a supported non-null
`payloadType`, a payload value, and `fault: null`. A command may select an
informational success code instead of 0, such as Windows Installer's 3010
("success, restart required"); the envelope then reports that code with
`success: true` (see [`examples/informational-success.json`](examples/informational-success.json)).
Readers before Runic Command Line 0.12 reject such an envelope. Failure has
`success: false`, a nonzero `exitCode`, `payloadType: null`, `payload: null`,
and a fault. A failure MUST NOT report 0, and SHOULD NOT report another
multiple of 256, which Linux and macOS truncate to zero in the process status.
The exit-code categories, their default codes (1, 2, 3 and 130), host
exit-code policies and per-outcome codes are specified in
[Exit codes](../hosting.md#exit-codes) of the hosting specification. A
reader checks only that a failure's `exitCode` is not 0, so it accepts 256:
`exitCode` is the code the producer reports, not the status a parent process
observes.

The `payload` key being JSON null is not evidence of failure. Clients use
`success`, `payloadType`, and `fault` together and validate all invariants.

## Identifier validation

Limits below count UTF-8 bytes after JSON decoding, not UTF-16 code units or
escaped source characters.

- `requestId` is 1 to 128 UTF-8 bytes. It is opaque, is never interpreted as
  authorization or executable input, and MUST NOT contain:
  - a control character (General_Category Cc) or a protocol Unicode control
    character (see [Unicode control characters](#unicode-control-characters));
  - a character with the Unicode `White_Space` property: U+0009–U+000D, U+0020,
    U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and
    U+3000;
  - a noncharacter: U+FDD0–U+FDEF and every code point whose low 16 bits are
    FFFE or FFFF (U+FFFE, U+1FFFE, …, U+10FFFF);
  - an unpaired surrogate (which a writer checks; on the wire, a strict reader
    rejects its escape earlier, as `malformed-json`).

  Other format characters (General_Category Cf), such as the soft hyphen
  U+00AD, are allowed. A caller-supplied invalid ID is rejected before
  execution; a dispatcher otherwise generates one.
- `command` is 1 to 512 UTF-8 bytes and matches
  `[a-z][a-z0-9-]*( [a-z][a-z0-9-]*)*`.
- `payloadType` is an independently versioned ASCII identifier of 3 to 128
  bytes matching `[a-z][a-z0-9.-]*/[1-9][0-9]*`. A client MUST compare the
  complete identifier with its allow-list before deserializing `payload` and
  MUST report an unsupported or mismatched identifier as a protocol failure,
  not as a command fault.
- Fault and diagnostic codes are 1 to 64 ASCII bytes matching
  `[A-Z][A-Z0-9_.-]*`. Codes owned by this library use `RCLI####`, except
  the application range `RCLI8000` through `RCLI8999`, which the library never
  emits (see [Diagnostic object](#diagnostic-object)).

### Reserved command values

Responses that do not come from a command use reserved `command` values:

- `root` for a parse or usage error at the root, before any command was
  resolved, for example an unknown command or an invalid output selector value.
  An error after a command was resolved names that command's path.
- `help` for a help response and `version` for a version response from the
  framework, whatever path the help describes.

No application command, subcommand or alias may be named `root`, `help` or
`version` (catalog validation `RCLI0002`, generator `RCLI9001`), so a client can
tell these responses from command responses by `command` alone. `output` is
reserved as well.

### Framework payload types

Two payload types belong to the framework rather than to an application:

- `runic.text/1`: a JSON string. Help and version responses are successes
  with this type, and the help or version text as the payload.
- `runic.unit/1`: the empty object `{}`, the payload of a command that returns
  no value.

An application SHOULD NOT declare either identity for payloads of its own.

The protocol deliberately defines no JSON Schema `$id` and assigns no domain
name. Payload owners choose their own type identifier within their documented
application boundary.

## Fault object

A fault contains every key below in this output order:

```json
{"code":"RCLI3001","message":"The command could not complete.","details":{},"retryable":false}
```

- `code` is a stable code satisfying the identifier rule.
- `message` is a nonempty safe presentation string of at most 4,096 UTF-8
  bytes.
- `details` is an object with at most 32 unique entries. Keys are 1 to 64 UTF-8
  bytes; values are strings of at most 1,024 UTF-8 bytes. A writer emits the
  entries sorted by key in UTF-16 code-unit order, comparing the keys' UTF-16
  encodings unit by unit. That order differs from code-point order for keys
  outside the BMP: a key starting with U+1F600 (`😀`) sorts before one
  starting with U+E000. Readers MUST NOT depend on the order.
- `retryable` is Boolean and means retrying the same logical operation may
  succeed. It never authorizes an automatic retry.
- `helpUri` is optional and follows `retryable`; see [Help links](#help-links).

### Optional declared failure data

A producer MAY append an optional `data` member to the fault. Existing
version-1 readers ignore this additive member and still receive the ordinary
safe fault. The nine envelope fields and all success/failure invariants remain
unchanged, including failure `payloadType: null`, `payload: null`, and a nonzero
exit code. An ordinary failure omits `data` entirely.

```json
{"type":"sample.clone-recovery/1","payload":{"retainedDirectory":"/home/user/Project copy","observationComplete":true}}
```

`data` MUST be an object with required `type` and `payload` members. `type`
uses the same complete, independently versioned identity grammar as
`payloadType`. `payload` is any JSON value, including null if its declared
contract permits it. Its encoded UTF-8 JSON value MUST be at most 65,536 bytes
(excluding the `data` wrapper), with at most 24 nested objects and arrays.
Producers and readers count the value's compact form: its original bytes,
including escapes, without insignificant whitespace between tokens. The
complete frame remains subject to the one-mebibyte and 32-level limits.
Duplicate property names at any depth of this payload are invalid. Invalid or
oversized data MUST be rejected rather than truncated.

This extension contains application-owned domain fields explicitly declared
through a stable identity and closed JSON serialization metadata. These fields
are intended for the command's consumer and are separate from fault presentation
text and diagnostic arguments: exact directory paths, target names, and
independent recovery observations are preserved without the presentation
sanitizer. Producers MUST exclude secrets, exception/log text, and unrelated
internal implementation state from the declared domain contract.

A reader that supports `data` MUST validate its structure, identity grammar,
duplicates, and bounds before retaining it. It MUST compare the complete
identity against its application allow-list before decoding `payload` with
pre-registered JSON metadata. An unknown identity leaves the safe ordinary
fault usable and MUST NOT cause arbitrary deserialization or type activation.
A matching identity whose data cannot be decoded is a protocol failure, not
successful recovery. Domain data never authorizes a retry or proves that an
operation succeeded; applications interpret their documented recovery contract.

## Diagnostic object

A diagnostic contains every key below in this output order:

```json
{"code":"RCLI2001","kind":"invalid-format","commandPath":["export"],"messageKey":"diagnostics.invalid-format","message":"The value is invalid.","phase":"binding","severity":"error","tokenIndex":2,"arguments":["format"]}
```

- `code` is exactly `RCLI` followed by four ASCII digits other than
  `0000`. `RCLI8000` through `RCLI8999` are reserved for applications: this
  library and its generators never emit them, so an application can define
  its own diagnostics there without colliding with library codes. Every other
  `RCLI` code belongs to the library. Within the application range, each
  application documents its own codes; a client distinguishes applications by
  `command` and `payloadType`, not by code.
- `kind` is a stable symbolic identity of at most 128 UTF-8 bytes matching
  `[a-z][a-z0-9-]*`, without consecutive or trailing hyphens. Writers MUST NOT
  emit another kind, and a strict reader rejects one (`invalid-diagnostic`).
- `commandPath` is the ordered canonical path. The catalog root is an empty
  array. It has at most 64 segments, each matching `[a-z][a-z0-9-]*`; joining
  the segments with one ASCII space MUST be at most 512 UTF-8 bytes.
- `messageKey` is a stable localization key of 1 to 128 ASCII bytes. Its first
  character is an ASCII letter; remaining characters are ASCII letters,
  digits, dots, underscores, or hyphens.
- `message` is nonempty and at most 4,096 UTF-8 bytes.
- `phase` is exactly `parse`, `binding`, or `execution`.
- `severity` is exactly `error`, `warning`, or `information`.
- `tokenIndex` is null or a nonnegative signed 32-bit integer. It indexes the
  already-tokenized argument array and MAY equal its length for missing input.
- `arguments` is an ordered array of at most 16 safe strings. Each value is at
  most 1,024 UTF-8 bytes. Raw token values are excluded unless the command
  definition explicitly classifies the value as safe.
- `binding` is optional and follows `arguments`. When present it is the stable
  catalog ID of the one argument or option the diagnostic names, 1 to 128 ASCII
  bytes matching `[a-z][a-z0-9-]*`, such as `destination` for
  `"arguments":["<destination>"]`. `arguments` name the binding as users see it
  (its value name, or its spelling in the catalog's dialect) and change with
  those; `binding` does not change with the value name, the dialect or the
  culture, so a client that needs to know which input was wrong reads
  `binding`. A diagnostic that names no declared binding, or more than one,
  omits it. Since Runic Command Line 0.13.0; earlier version-1 readers ignore
  it. See [`examples/missing-argument.json`](examples/missing-argument.json).
- `helpUri` is optional and follows `arguments` and `binding`; see [Help links](#help-links).

Fault messages, fault details, diagnostic messages, and diagnostic arguments
are consumer-safe surfaces. They MUST NOT contain secrets, original values of
sensitive options, stack traces, exception type names, environment-variable
values, absolute internal paths, line breaks, terminal controls, or Unicode
control characters (see below). Unexpected exceptions are logged only to an authorized
diagnostic sink and map to the sanitized `RCLI5000` host fault. The developer
opt-in `RUNIC_COMMANDLINE_DEBUG=1`, which every implementation recognizes, MAY
make stderr such a sink; an implementation may add an opt-in of its platform,
such as the .NET host's `DOTNET_ENVIRONMENT=Development`. stdout, the frame and
its fault remain unchanged.

### Unicode control characters

In this protocol, the Unicode control characters are:

- the C0 and C1 controls and DEL (U+0000–U+001F, U+007F–U+009F), and the line
  and paragraph separators U+2028 and U+2029;
- the bidirectional embeddings and overrides U+202A–U+202E (LRE, RLE, PDF, LRO,
  RLO) and the isolates U+2066–U+2069 (LRI, RLI, FSI, PDI);
- the tag characters U+E0000–U+E007F;
- the zero-width space U+200B, the word joiner U+2060 and the zero-width
  no-break space or byte order mark U+FEFF.

They can reorder or hide text on a terminal or in a viewer, as in Trojan Source.
A writer replaces them in a consumer-safe surface as [Sanitization](#sanitization)
describes. A strict reader rejects a response that carries one in a fault
message or detail key or value (`unsafe-fault-content`), a diagnostic message or
argument (`unsafe-diagnostic-content`), or the `requestId`
(`invalid-request-id`). The zero-width non-joiner U+200C, the zero-width joiner
U+200D, the direction marks U+200E (LRM) and U+200F (RLM) and the Arabic letter
mark U+061C are not control characters: Persian, Indic and right-to-left text
and emoji sequences need them. Whether a frame shows them raw is a matter of
the writer's escaping profile; the `dotnet` profile escapes them, like the
control characters, as `\uXXXX`.

### Sanitization

A writer sanitizes every fault and diagnostic before output, human or JSON. The
steps below are exact; the writer cases in [`writer-cases.json`](writer-cases.json)
check them.

**Technical content.** A text has technical content when it contains one of the
case-sensitive substrings `Exception`, `/home/`, `/Users/`, `/root/`, `/tmp/`
and `\\` (two backslashes), or an ASCII letter followed by `:\` or `:/` (a
drive letter, for example `C:\` or `c:/`). The check looks at the original text,
before the replacements and truncation below. It is a heuristic, not a
guarantee: other absolute paths, such as `/var/folders/...`, `/srv/...` or
`/nix/store/...`, are not redacted, so producers remain responsible for keeping
such text out of faults.

**Text with a limit of N UTF-8 bytes.** The text is read as Unicode scalars in
order (a UTF-16 implementation reads an unpaired surrogate as U+FFFD), and the
output is built as follows:

1. A [Unicode control character](#unicode-control-characters) adds one space,
   unless the output so far ends with a space (U+0020). A run of control
   characters therefore becomes one space, and a control character after a
   space adds nothing. Spaces in the text itself are kept as they are.
2. Every other scalar is added unchanged.
3. The output stops before the first scalar or space that would make it longer
   than N bytes. Truncation never splits a scalar, and nothing is trimmed
   afterwards.

The limits are 4,096 bytes for messages, 64 for detail keys and 1,024 for detail
values and diagnostic arguments. A text "is only whitespace" below when it is
empty or every character has the `White_Space` property.

**Faults**, in this order:

1. A code that does not satisfy the identifier rule replaces the whole fault
   with `RCLI5000`, the message `The command failed unexpectedly.`, no details,
   `retryable` false and no help link. The steps below do not apply.
2. A well-formed code is kept, whether it is a library `RCLI` code, an
   application-range `RCLI8xxx` code, or an application's own prefix such as
   `RAS1001`. The message is sanitized. If the original message has technical
   content, or the sanitized message is only whitespace, the message becomes
   the fault redaction note (see
   [`examples/redacted-fault.json`](examples/redacted-fault.json)).
3. Details are taken in the order the producer gave them until 32 are kept.
   Each key is sanitized. A detail is dropped, with its value, when its original
   key has technical content, its sanitized key is only whitespace, or its
   sanitized key equals one already kept (the first one wins). A kept detail's
   value becomes `[redacted]` when the original value has technical content,
   and is sanitized otherwise. Dropped details do not count toward the 32.
4. `retryable` and the help link are kept. The details are written sorted, as
   [Fault object](#fault-object) says.

**Diagnostics:**

- If the original message or any original argument has technical content, the
  message becomes the diagnostic content redaction note and `arguments` is
  empty.
- Otherwise the message is sanitized and becomes the diagnostic message
  redaction note if it is only whitespace, and each argument is sanitized.
- The other members are identifiers and are written as given.

**Replacement texts.** The code `RCLI5000`, its message `The command failed
unexpectedly.` and the detail value `[redacted]` are fixed. The three redaction
notes are presentation text: the fault note `The command failed; details were
redacted.`, the diagnostic content note `The diagnostic content was redacted.`
and the diagnostic message note `The diagnostic message was redacted.` are their
English defaults, and a writer MAY present them in the user's language like
other messages (Runic Command Line resolves them through the `redaction.*` text
keys). The writer cases expect the English defaults.

Sanitization never changes `exitCode`, which comes from the command's exit
category, or from the code its outcome selected, rather than from the fault code.

## Help links

A producer MAY add a `helpUri` member to a fault or diagnostic that links its
code to documentation, and omits it otherwise. Existing version-1 readers
ignore this additive member. Its value is a string of 1 to 2,048 characters from
U+0021 to U+007E that is an RFC 3986 `absolute-URI` with the scheme `https`
(compared ignoring ASCII case), an authority with a nonempty host, and no user
information. A reader that supports it rejects any other value, including
`null`, with `invalid-help-uri`. The link is the producer's fixed documentation
address, not presentation text: it is exempt from the technical-content
redaction above and MUST NOT be built from user input or contain secrets. A
writer writes the address it was given; the `dotnet` profile writes .NET's
normalized absolute form (`Uri.AbsoluteUri`, which lowercases the scheme and
host and writes an empty path as `/`), so the writer cases use addresses that
are already in that form.

## Strict reading

A strict reader checks a frame in the order below and stops at the first check
that fails. It reports that check's reason, so every conforming reader reports
the same reason for the same frame, also when the frame breaks several rules
(the `*.precedence.*` cases of the corpus). The manifest lists the reasons in
`readerReasons`.

**Safe text.** A strict reader rejects text that a writer would have changed: a
fault message, detail key or value, or diagnostic message or argument is unsafe
when it contains a [Unicode control character](#unicode-control-characters),
exceeds its byte limit, has [technical content](#sanitization), or is empty
where the member must not be. Three members must not be empty: a fault
message, a fault detail key and a diagnostic message. A detail value and a
diagnostic argument may be empty. Fault codes must satisfy the identifier rule.

Safe text is checked before "only whitespace". So an empty fault message or
detail key is `unsafe-fault-content`, and an empty diagnostic message is
`unsafe-diagnostic-content` (`invalid.fault-message-empty`,
`invalid.fault-detail-key-empty`, `invalid.diagnostic-message-empty`). The
`invalid-envelope` and `invalid-diagnostic` checks for text that is only
whitespace therefore see only nonempty text.

**Frame.** On the raw bytes:

| # | Check | Reason |
|---|---|---|
| 1 | The frame is empty. | `missing-envelope` |
| 2 | The frame exceeds the reader's bound, by default 1,048,576 bytes. | `framed-byte-limit` |
| 3 | The frame starts with EF BB BF. | `utf8-bom` |
| 4 | The bytes before the last byte are not strict UTF-8. | `invalid-utf8` |
| 5 | The frame is shorter than 3 bytes, its last byte is not LF, or the byte before the LF is a space, tab, CR or LF. | `invalid-framing` |
| 6 | The first byte is not `{`: a space, tab, CR or LF there is `invalid-framing`, any other byte `stdout-contamination`. | `invalid-framing`, `stdout-contamination` |
| 7 | A scan of the bytes before the LF, from left to right and skipping string contents, finds an object or array at level 33, or a byte other than JSON whitespace after the root object closes; the first of the two found wins. This runs before parsing, so it applies to malformed text too. | `json-depth-limit`, `multiple-json-values` |
| 8 | The text is not one strict RFC 8259 JSON value (no comments, no trailing commas), or a string or member name anywhere in it, including unknown members and the payload, has an escape that decodes to an unpaired surrogate (a `\uD800`–`\uDBFF` escape not directly followed by a `\uDC00`–`\uDFFF` escape, or a `\uDC00`–`\uDFFF` escape not directly preceded by one; the I-JSON rule of RFC 7493). | `malformed-json` |
| 9 | Any object in the frame, including unknown members and the payload, has two members with the same name. Names are compared after unescaping, so `"protocol"` and `"protocol"` are the same. | `duplicate-property` |

**Envelope.** A required member that is absent is `missing-required-field`; a
member of the wrong JSON type is `invalid-envelope` unless the table says
otherwise. Members are checked in this order:

| # | Check | Reason |
|---|---|---|
| 10 | `protocol` is a string, equal to `runic.commandline/1`. | `unsupported-protocol` if not equal |
| 11 | `requestId` is present and a string. | `invalid-request-id` if not a string |
| 12 | `command` is a string. | |
| 13 | `requestId` follows [Identifier validation](#identifier-validation). | `invalid-request-id` |
| 14 | `command` follows Identifier validation. | `invalid-command` |
| 15 | `success` is a Boolean, `exitCode` a signed 32-bit integer, `payloadType` a string or null, and `payload`, `fault` and `diagnostics` are present. | |
| 16 | `diagnostics` is an array of at most 64 diagnostics, each checked in array order as below. | `invalid-envelope` |

**Diagnostic**, in this order: it is an object; `code` and `kind` are strings;
`commandPath` is an array of at most 64 strings whose joined form is at most
512 bytes (`invalid-diagnostic`) and whose segments match their pattern
(`invalid-diagnostic-command-path`); `messageKey` is a string that matches its
pattern (`invalid-diagnostic-message-key`); `message` is a string, and `kind` is
at most 128 bytes and `message` safe (`unsafe-diagnostic-content`); `phase` and
`severity` are strings with a listed value (`invalid-diagnostic`); `tokenIndex`
is null or a signed 32-bit integer; `arguments` is an array of at most 16
strings (`invalid-envelope`), each safe (`unsafe-diagnostic-content`); then
`code`, `kind` and `tokenIndex` follow [Diagnostic object](#diagnostic-object)
and `message` is not only whitespace (`invalid-diagnostic`); then `helpUri`
(`invalid-help-uri`) and `binding` (`invalid-diagnostic`).

**Outcome.** On success:

| # | Check | Reason |
|---|---|---|
| 17 | `payloadType` is not null. | `missing-payload-type` |
| 18 | `payloadType` follows Identifier validation. | `invalid-payload-type` |
| 19 | `payloadType` is a type the client supports. | `unsupported-payload-type` |
| 20 | `fault` is null. | `outcome-invariant` |
| 21 | The payload decodes with the client's contract for that type. | `payload-shape-mismatch` |

On failure:

| # | Check | Reason |
|---|---|---|
| 17 | `exitCode` is not 0. | `outcome-invariant` |
| 18 | `payloadType` and `payload` are null. | `outcome-invariant` |
| 19 | `fault` is not null. | `missing-fault` |
| 20 | `fault` is an object; `code` and `message` are strings; the code satisfies the identifier rule and the message is safe (`unsafe-fault-content`); `retryable` is a Boolean; `details` is an object whose members, in order, number at most 32 and have string values (`invalid-envelope`) and safe keys and values (`unsafe-fault-content`); the message and every detail key are not only whitespace (`invalid-envelope`). | see the check |
| 21 | `fault.helpUri`, when present, is valid. | `invalid-help-uri` |
| 22 | `fault.data`, when present, is an object with a string `type` and a `payload`; `type` follows the payload type grammar (`invalid-failure-data`); the payload's compact form is at most 65,536 bytes (`failure-data-byte-limit`) and at most 24 levels deep (`failure-data-depth-limit`). | see the check |

Unknown members are accepted at every level ([Compatibility](#compatibility)).

## Compatibility

A version-1 reader MUST ignore unknown fields at the envelope, fault,
diagnostic, and payload levels while still enforcing all known fields and
limits. Producers MUST emit every required known field. Adding an optional
unknown field is compatible. Removing a required field, changing a known
field's JSON type, weakening an invariant, or reinterpreting a known value
requires a new protocol major identity. Duplicate JSON property names are
invalid rather than last-value-wins, at every level, including inside the
payload.

Payload compatibility is independent of envelope compatibility. A client
MUST validate `payloadType` before invoking its pre-registered, non-reflection
JSON metadata. Arbitrary object deserialization and type-name activation are
not supported.

## Examples, writer cases and escaping profiles

The codes `RCLI2001`, `RCLI3000` and `RCLI3001` in the examples and corpora are
illustrative. Runic Command Line does not emit them, and an implementation
should not define them as library codes.

The examples are value-normative. A writer conforms when, for every case in
`writer-cases.json`, it writes one framed response whose JSON value equals the
expected one: the example the case names, or the case's `expected.document`.
Two JSON values are equal when they are the same literal, numbers with the same
mathematical value, strings with the same Unicode scalars after unescaping,
arrays with equal elements in the same order, or objects with the same member
names and equal values in any member order.

Byte identity with the examples is required only of a writer that claims an
escaping profile. The protocol names one, `dotnet`: what Runic Command Line's
.NET writer produces. A `dotnet` writer writes:

- compact JSON with no whitespace between tokens; the envelope, fault and
  diagnostic members in the orders given above, optional members only when
  present; fault details sorted in UTF-16 code-unit order;
- integers in their shortest decimal form;
- a binary64 floating-point number (a .NET `double`) as the shortest decimal
  text that reads back as the same value, as .NET's `Utf8JsonWriter` writes it
  (`CommandJsonEnvelopeWriter.cs:107` serializes the payload with it): the
  digits without a trailing `.0` (`1`, `1.5`, `0.1`), and exponent form when the
  decimal point would move more than max(17, number of digits) places right or
  more than three places left. Exponent form is the first digit, the other
  digits after a `.`, `E`, the sign and at least two exponent digits:
  `0.0001`, but `1E-05` and `1.25E-07`; `10000000000000000` (10^16), but
  `1E+17` and `1.2345678901234568E+17`; `5E-324` and
  `1.7976931348623157E+308` at the ends of the range. Negative zero is `-0`. A
  value that is not finite has no JSON text: .NET's writer refuses it with an
  `ArgumentException`, which is an exception from the outcome writer
  ([Cancellation and async
  handlers](../hosting.md#cancellation-and-async-handlers));
- in strings, `\` as `\\`, U+0008, U+0009, U+000A, U+000C and U+000D as `\b`,
  `\t`, `\n`, `\f` and `\r`, and `/` unescaped; every other escaped scalar as
  `\uXXXX` with upper-case hex digits, and a scalar above U+FFFF as its escaped
  UTF-16 surrogate pair (`😀`);
- help links in .NET's normalized absolute form ([Help links](#help-links)).

A number's text shows only in bytes, so value equality cannot check it: the
example [`examples/number-text.json`](examples/number-text.json) and its writer
case `writer.number-text` hold doubles of the sample type
`sample.measurement/1`, whose writer input gives each one as a plain JSON
number. A writer that claims the profile writes the payload through that type's
own metadata, as an application writes its typed results, and so matches the
example's number text byte for byte; .NET's writer copies a payload that is
already a JSON value as given. Negative zero has no case: the adapter protocol
carries the writer input as JSON, where `-0` and `0` are one value.

The `dotnet` profile escapes exactly these scalars and writes every other one
as UTF-8:

- U+0000–U+001F, U+007F–U+009F, and `"` `&` `'` `+` `<` `>` `` ` `` (so `"` is
  `"` and `<` is `<`);
- the space separators (General_Category Zs) other than U+0020, and U+2028 and
  U+2029;
- U+061C, U+200B–U+200F, U+202A–U+202E, U+2060, U+2066–U+2069 and U+FEFF;
- the private use area U+E000–U+F8FF, and every scalar above U+FFFF;
- every BMP code point that is unassigned (General_Category Cn) in the Unicode
  version of the .NET runtime that writes the frame, Unicode 16.0 for .NET 10.
  These include the noncharacters U+FDD0–U+FDEF, U+FFFE and U+FFFF.

Only the last rule depends on the runtime: a code point that a later Unicode
version assigns is written raw by a runtime with that version. The examples
contain no such code point, so their bytes do not change with the runtime, and a
writer that implements the profile with another Unicode version still matches
every example byte for byte. Another writer may escape differently and still
conform. Runic Command Line's ASCII escaping option writes the `dotnet` profile
with every remaining non-ASCII scalar escaped as well; it is value-equal to the
profile.

## Corpus layout

`manifest.json` records the fixture inventory, all machine-readable limits, the
reader reasons in check order (`readerReasons`), the escaping profiles and the
sample payload types. Files in `examples/` are complete valid envelopes.
`invalid-structures.json` contains parseable JSON documents that violate
envelope semantics, or that a reader accepts (`expected.kind` `accepted`); a
runner writes each document as compact JSON followed by LF, and no expected
reason depends on how it escapes strings. `wire-inputs.json` represents
malformed or hostile stdout bytes as base64, so every checked-in `.json` file
remains strict, valid JSON and can be validated offline without executing a
process. A wire case may instead give a `wireRecipe` of kind `repeat-utf8`: the
UTF-8 encoding of `prefix`, then `value` repeated `count` times, then `suffix`,
whose length is `expectedWireBytes` when that is given.

`writer-cases.json` gives a writer's inputs and the output each must produce.
`input` holds `requestId`, `command`, `success` and `exitCode`; on success
`payloadType` and `payload`; on failure `fault` and optionally `failureData`
(`type` and `payload`); and `diagnostics`. The fault and the diagnostics are the
producer's raw values, before sanitization: a fault has `code`, `message`,
`details` as an ordered array of `[key, value]` pairs (order matters, since the
first of two keys that sanitize alike wins), `retryable` and optionally
`helpUri`; a diagnostic has every member of the diagnostic object.
`expected.example` names an example file, and `expected.document` gives the
expected envelope inline.

**Client context.** Every semantic case names `clientContext`, and the wire
cases share the file's `clientContext`. `supportedPayloadTypes` lists the one
payload type the reading client supports. `expectedPayloadJsonKind`, when given,
is the JSON kind that type decodes from. `expected.payloadDeserializationAttempted`
tells whether the client must have tried to decode the payload before the
failure. The manifest's `samplePayloads` give each sample type's contract: a
payload decodes when it is null and the type is `nullable`, or when it has the
type's `jsonKind` and each listed member that is present has the listed JSON
type (`string`, `integer` for a number without fraction or exponent, or
`number` for any JSON number). Other members are unknown and ignored.
