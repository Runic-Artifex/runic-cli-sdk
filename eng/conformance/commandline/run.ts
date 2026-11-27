// One Command Line case through an adapter (eng/conformance/README.md, "Command Line"). The driver builds every input
// the corpus defines (frames from base64, recipes and documents), and compares every result as the spec does; the
// adapter only calls its implementation's parser, reader and writer and reports what they returned.
import { AdapterError, type Adapter, type Case, type Outcome, type RunContext } from "../core/index";
import { abbreviate, parseStrict, valueEqual } from "./json";

// The capability of an adapter whose writer claims the `dotnet` escaping profile: its writer cases that name an
// example must then match the example byte for byte (protocol README, "Examples, writer cases and escaping profiles").
export const DOTNET_PROFILE = "escaping-profile:dotnet";

const pass: Outcome = { outcome: "pass" };
const fail = (detail: string): Outcome => ({ outcome: "fail", detail });
const b64 = (bytes: Uint8Array) => Buffer.from(bytes).toString("base64");
const fromB64 = (text: string) => new Uint8Array(Buffer.from(text, "base64"));
const utf8 = (text: string) => new TextEncoder().encode(text);

export async function runCommandLineCase(adapter: Adapter, c: Case, { supports, roles }: RunContext): Promise<Outcome> {
  const role = c.roles[0];
  if (!roles.has(role)) return { outcome: "excluded", detail: `role:${role}` };
  const missing = c.requires.filter((feature) => !supports.has(feature));
  if (missing.length) return { outcome: "unsupported", detail: `requires ${missing.join(", ")}` };
  try {
    switch (c.kind) {
      case "parse":
        return await parse(adapter, c.body);
      case "validate":
        return await parse(adapter, c.body, "cl.validate");
      case "catalog":
        return await catalog(adapter, c.body);
      case "classify":
        return await classify(adapter, c.body);
      case "help":
        return await help(adapter, c.body);
      case "human":
        return await human(adapter, c.body);
      case "read-wire":
        return await readWire(adapter, c.body);
      case "read-document":
        return await readDocument(adapter, c.body);
      case "read-example":
        return await readExample(adapter, c.body);
      case "write":
        return await write(adapter, c.body, supports);
      default:
        return fail(`unknown case kind ${c.kind}`);
    }
  } catch (e) {
    if (e instanceof AdapterError && e.code === "unsupported") return { outcome: "unsupported", detail: e.message };
    throw e;
  }
}

// --- Grammar (grammar.md, "Normalized results") ---

// `cl.validate` gives the same normalized result after input validation (grammar.md, "Input validation"), whose
// diagnostics have no token index (`tokenIndex: null` in the corpus).
async function parse(adapter: Adapter, { catalog, row }: any, op = "cl.parse"): Promise<Outcome> {
  const r = await adapter.call(op, { catalog, argv: row.args, env: row.environment ?? {} });
  if (r.error) return fail(`the parser failed: ${r.error}`);
  const e = row.expected;
  if (r.kind !== e.kind) return fail(`${r.kind} ${abbreviate(r)}, expected ${e.kind} ${abbreviate(e)}`);
  // Only an error result has diagnostics.
  if (e.kind !== "error" && r.diagnostics?.length) return fail(`${r.kind} with diagnostics ${abbreviate(r.diagnostics)}`);
  switch (e.kind) {
    case "invocation": {
      const expected = { commandPath: e.commandPath, options: e.options ?? [], arguments: e.arguments ?? [] };
      const actual = { commandPath: r.commandPath, options: r.options, arguments: r.arguments };
      return valueEqual(actual, expected) ? pass : fail(`invocation ${abbreviate(actual)}, expected ${abbreviate(expected)}`);
    }
    case "help":
      return valueEqual(r.commandPath, e.commandPath) ? pass : fail(`help for ${abbreviate(r.commandPath)}, expected ${abbreviate(e.commandPath)}`);
    case "version":
      return pass;
    case "error":
      return diagnostics(r.diagnostics, e.diagnostics ?? []);
    default:
      return fail(`unknown expected kind ${e.kind}`);
  }
}

// --- Catalog validity (grammar.md, "Catalog validity") ---

// A valid catalog builds. An invalid one is rejected with at least one issue; how many an implementation reports is
// implementation-defined, and each corpus catalog breaks one rule, so every reported issue has the expected code.
async function catalog(adapter: Adapter, { catalog, row }: any): Promise<Outcome> {
  const r = await adapter.call("cl.catalog", { catalog });
  if (r.error) return fail(`the catalog builder failed: ${r.error}`);
  const e = row.expected;
  if (r.kind !== e.kind) return fail(`${r.kind} ${abbreviate(r)}, expected ${e.kind}${e.code ? ` with ${e.code}` : ""}`);
  if (e.kind === "valid") return pass;
  const issues: any[] = r.issues ?? [];
  if (!issues.length) return fail("invalid without an issue");
  const other = issues.find((issue) => issue.code !== e.code);
  return other ? fail(`issue ${abbreviate(other)}, expected only ${e.code}`) : pass;
}

// A corpus diagnostic: code, kind and token index; `arguments` exactly when the corpus lists them; `binding` exactly,
// so a diagnostic without one in the corpus expects none (grammar.md, "Normalized results"). Messages are presentation
// text and not compared.
function diagnostics(actual: any[], expected: any[]): Outcome {
  if (actual.length !== expected.length) return fail(`${actual.length} diagnostics ${abbreviate(actual)}, expected ${expected.length} ${abbreviate(expected)}`);
  for (let k = 0; k < expected.length; k++) {
    const a = actual[k], e = expected[k];
    for (const member of ["code", "kind", "tokenIndex"])
      if (a[member] !== e[member]) return fail(`diagnostic ${k}: ${member} ${abbreviate(a[member])}, expected ${abbreviate(e[member])} (${abbreviate(a)})`);
    if (e.arguments && !valueEqual(a.arguments, e.arguments)) return fail(`diagnostic ${k}: arguments ${abbreviate(a.arguments)}, expected ${abbreviate(e.arguments)}`);
    if ((a.binding ?? null) !== (e.binding ?? null)) return fail(`diagnostic ${k}: binding ${abbreviate(a.binding ?? null)}, expected ${abbreviate(e.binding ?? null)}`);
  }
  return pass;
}

// --- Output classification (grammar.md, "Output classification") ---

async function classify(adapter: Adapter, { catalog, row }: any): Promise<Outcome> {
  const r = await adapter.call("cl.classify", { catalog, argv: row.args, env: row.environment ?? {} });
  if (r.error) return fail(`the parser failed: ${r.error}`);
  const e = row.expected;
  if (r.kind !== e.kind) return fail(`${r.kind} ${abbreviate(r)}, expected ${e.kind} ${abbreviate(e)}`);
  if (e.kind === "error") return diagnostics(r.diagnostics, e.diagnostics ?? []);
  if (r.diagnostics?.length) return fail(`output mode ${r.mode} with diagnostics ${abbreviate(r.diagnostics)}`);
  return r.mode === e.mode && r.source === e.source ? pass : fail(`mode ${r.mode} from ${r.source}, expected ${e.mode} from ${e.source}`);
}

// --- Help text (help.md) ---

// The text must equal the row's lines, each followed by LF, exactly.
async function help(adapter: Adapter, { catalog, applicationName, row }: any): Promise<Outcome> {
  const r = await adapter.call("cl.help", { catalog, applicationName, path: row.path, width: row.width });
  if (r.error) return fail(`the help formatter failed: ${r.error}`);
  return sameText("help", r.text, row.expected.lines.map((line: string) => line + "\n").join(""));
}

// --- Human output (hosting.md, "Human output") ---

// The runner's exit code, standard output and standard error must equal the row's exactly.
async function human(adapter: Adapter, { catalog, applicationName, row }: any): Promise<Outcome> {
  const r = await adapter.call("cl.human", { catalog, applicationName, argv: row.args, outcome: row.outcome, texts: row.texts ?? {} });
  if (r.error) return fail(`the runner failed: ${r.error}`);
  const e = row.expected;
  if (r.exitCode !== e.exitCode) return fail(`exit code ${r.exitCode}, expected ${e.exitCode} (stdout ${abbreviate(r.stdout)}, stderr ${abbreviate(r.stderr)})`);
  const out = sameText("stdout", r.stdout, e.stdout);
  return out.outcome === "pass" ? sameText("stderr", r.stderr, e.stderr) : out;
}

// Names the first line that differs.
function sameText(what: string, actual: unknown, expected: string): Outcome {
  if (actual === expected) return pass;
  if (typeof actual !== "string") return fail(`${what} is ${abbreviate(actual)}, not text`);
  const a = actual.split("\n"), e = expected.split("\n");
  let k = 0;
  while (k < a.length && k < e.length && a[k] === e[k]) k++;
  return fail(`${what} differs at line ${k + 1}: ${abbreviate(a[k] ?? "(end)")}, expected ${abbreviate(e[k] ?? "(end)")} (${abbreviate(actual, 600)})`);
}

// --- Strict reading (protocol README, "Strict reading") ---

function wireFrame(row: any): Uint8Array {
  if (row.wireUtf8Base64 !== undefined) return fromB64(row.wireUtf8Base64);
  const recipe = row.wireRecipe;
  if (recipe?.kind !== "repeat-utf8") throw new Error(`${row.id}: unknown wire recipe ${recipe?.kind}`);
  return utf8(recipe.prefix + recipe.value.repeat(recipe.count) + recipe.suffix);
}

async function readWire(adapter: Adapter, { client, row }: any): Promise<Outcome> {
  const frame = wireFrame(row);
  if (row.expectedWireBytes !== undefined && frame.length !== row.expectedWireBytes)
    return fail(`corpus: the recipe gives ${frame.length} bytes, not expectedWireBytes ${row.expectedWireBytes}`);
  return read(adapter, frame, client, row.expected);
}

// A semantic case is written as compact JSON followed by LF; no expected reason depends on how its strings are escaped.
async function readDocument(adapter: Adapter, { client, row }: any): Promise<Outcome> {
  return read(adapter, utf8(JSON.stringify(row.document) + "\n"), client, row.expected);
}

async function readExample(adapter: Adapter, { client, text }: any): Promise<Outcome> {
  return read(adapter, utf8(text), client, { kind: "accepted" });
}

async function read(adapter: Adapter, frame: Uint8Array, client: any, expected: any): Promise<Outcome> {
  const r = await adapter.call("cl.readEnvelope", { input: b64(frame), payloadType: client.supportedPayloadTypes[0], contract: client.contract });
  const attempted = expected.payloadDeserializationAttempted;
  if (attempted !== undefined && r.payloadDecodeAttempted !== attempted)
    return fail(`the reader ${r.payloadDecodeAttempted ? "tried" : "did not try"} to decode the payload; expected ${attempted ? "an attempt" : "none"} (${r.result} ${r.reason ?? ""})`);
  if (expected.kind === "protocol-error") {
    if (r.result === "accepted") return fail(`accepted, expected the rejection ${expected.reason}`);
    return r.reason === expected.reason ? pass : fail(`rejected with ${r.reason}, expected ${expected.reason}`);
  }
  if (r.result !== "accepted") return fail(`rejected with ${r.reason}, expected acceptance`);
  // An accepted frame reads back to the members it states; unknown members and the payload are not compared.
  const stated = known(parseStrict(new TextDecoder().decode(frame.subarray(0, frame.length - 1))));
  return valueEqual(r.envelope, stated) ? pass : fail(`read ${abbreviate(r.envelope, 600)}, the frame states ${abbreviate(stated, 600)}`);
}

// The envelope members a reader reports: the known members of the envelope, the fault and each diagnostic, with the
// optional ones only when present, and of failure data only its type.
function known(document: any) {
  const pick = (from: any, required: string[], optional: string[]) => {
    const out: any = {};
    for (const m of required) out[m] = from[m];
    for (const m of optional) if (Object.hasOwn(from, m)) out[m] = from[m];
    return out;
  };
  const fault = document.fault && {
    ...pick(document.fault, ["code", "message", "details", "retryable"], ["helpUri"]),
    ...(Object.hasOwn(document.fault, "data") ? { data: { type: document.fault.data.type } } : {}),
  };
  return {
    ...pick(document, ["requestId", "command", "success", "exitCode", "payloadType"], []),
    fault,
    diagnostics: document.diagnostics.map((d: any) =>
      pick(d, ["code", "kind", "commandPath", "messageKey", "message", "phase", "severity", "tokenIndex", "arguments"], ["binding", "helpUri"]),
    ),
  };
}

// --- Writing (protocol README, "Examples, writer cases and escaping profiles") ---

async function write(adapter: Adapter, { row, example }: any, supports: Set<string>): Promise<Outcome> {
  const r = await adapter.call("cl.write", { input: row.input });
  if (r.error) return fail(`the writer failed: ${r.error}`);
  const bytes = fromB64(r.output);
  const n = bytes.length;
  if (n < 3 || bytes[0] !== 0x7b || bytes[n - 2] !== 0x7d || bytes[n - 1] !== 0x0a)
    return fail(`not one JSON object followed by one LF: ${abbreviate(new TextDecoder().decode(bytes))}`);
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes.subarray(0, n - 1));
  } catch {
    return fail("the frame is not strict UTF-8");
  }
  if (/[\n\r]/.test(text)) return fail(`the frame is not compact: it has a line break before its LF`);
  let actual: unknown;
  try {
    actual = parseStrict(text);
  } catch (e) {
    return fail(`the frame is not strict JSON: ${(e as Error).message}`);
  }
  const expected = example !== undefined ? JSON.parse(example) : row.expected.document;
  if (!valueEqual(actual, expected)) return fail(`wrote ${abbreviate(text, 600)}, expected the value ${abbreviate(expected, 600)}`);
  if (example !== undefined && supports.has(DOTNET_PROFILE)) {
    const want = utf8(example);
    const at = firstDifference(bytes, want);
    if (at >= 0) {
      const show = (b: Uint8Array) => abbreviate(new TextDecoder().decode(b.subarray(Math.max(0, at - 40), at + 40)));
      return fail(`the value matches ${row.expected.example}, but not its dotnet profile bytes from byte ${at}: wrote …${show(bytes)}…, expected …${show(want)}…`);
    }
  }
  return pass;
}

function firstDifference(a: Uint8Array, b: Uint8Array): number {
  for (let i = 0; i < Math.min(a.length, b.length); i++) if (a[i] !== b[i]) return i;
  return a.length === b.length ? -1 : Math.min(a.length, b.length);
}
