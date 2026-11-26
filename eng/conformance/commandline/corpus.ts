// The Command Line corpus (specs/command-line, `runic.commandline/1`) as a list of cases, one per corpus row, keyed
// `<file>/<id>`: the corpus file relative to specs/command-line without `.json`, then the row id. An example has no row
// id, so its key is its file alone (`protocol/examples/success`). The order is stable: the grammar corpus, output
// classification, then the protocol's wire inputs, semantic structures, writer cases and examples.
//
// A case's body carries everything the run compares against, including the shared parts its row uses (the fixture
// catalog, the client context, the sample payload contract, the example bytes), so a change to any of them changes the
// case's sha256 in cases.json.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { caseHash, type Case } from "../core/index";

export const CORPUS_ID = "commandline/1";

// The roles an implementation may claim: parsing a command line (grammar and output classification), reading
// envelopes strictly, and writing them.
export type Role = "parser" | "reader" | "writer";

const readJson = (path: string) => JSON.parse(readFileSync(path, "utf8"));

// `root` is the corpus's conformance directory (specs/command-line/conformance); the corpus files are its parent's.
export function loadCommandLineCorpus(root: string): Case[] {
  const spec = dirname(root);
  const cases: Case[] = [];
  const add = (file: string, id: string | undefined, kind: string, role: Role, body: any) => {
    const stem = file.replace(/\.json$/, "");
    // The set is the corpus file; the examples are one set.
    const set = id === undefined ? dirname(stem) : stem;
    cases.push({ id: id === undefined ? stem : `${stem}/${id}`, kind, set, file, roles: [role], requires: [], sha256: caseHash(body), body });
  };

  const grammar = readJson(join(spec, "grammar-corpus.json"));
  for (const row of grammar.cases) add("grammar-corpus.json", row.id, "parse", "parser", { catalog: grammar.catalog, row });

  // Output classification replays its rows against the grammar corpus's fixture catalog (grammar.md, "Output
  // classification"), so that catalog is part of each case.
  const classification = readJson(join(spec, "output-classification-corpus.json"));
  for (const row of classification.cases)
    add("output-classification-corpus.json", row.id, "classify", "parser", { catalog: grammar.catalog, environmentVariable: classification.environmentVariable, row });

  const manifest = readJson(join(spec, "protocol/manifest.json"));
  const contract = (type: string) => ({ type, contract: manifest.samplePayloads[type] ?? null });

  const wire = readJson(join(spec, "protocol/wire-inputs.json"));
  const wireType = wire.clientContext.supportedPayloadTypes[0];
  for (const row of wire.cases) add("protocol/wire-inputs.json", row.id, "read-wire", "reader", { client: { ...wire.clientContext, ...contract(wireType) }, row });

  const semantic = readJson(join(spec, "protocol/invalid-structures.json"));
  for (const row of semantic.cases) {
    const types: string[] = row.clientContext?.supportedPayloadTypes ?? [];
    if (types.length !== 1) throw new Error(`protocol/invalid-structures.json ${row.id}: a semantic case names one supported payload type`);
    add("protocol/invalid-structures.json", row.id, "read-document", "reader", { client: { ...row.clientContext, ...contract(types[0]) }, row });
  }

  const writer = readJson(join(spec, "protocol/writer-cases.json"));
  for (const row of writer.cases) {
    const example = row.expected.example ? readFileSync(join(spec, "protocol", row.expected.example), "utf8") : undefined;
    add("protocol/writer-cases.json", row.id, "write", "writer", { row, example });
  }

  // Every example is a valid frame that a strict reader accepts, with the members it states. The manifest lists them,
  // so a new example needs no change here. A failure example's client supports the wire corpus's payload type; it
  // never decodes a payload.
  for (const fixture of manifest.fixtures.filter((f: any) => f.class === "valid")) {
    const file = join("protocol", fixture.path);
    const text = readFileSync(join(spec, file), "utf8");
    const type = JSON.parse(text).payloadType ?? wireType;
    add(file, undefined, "read-example", "reader", { client: { supportedPayloadTypes: [type], ...contract(type) }, fixture, text });
  }

  // The manifest's case counts are the corpus's own inventory; a file that disagrees is a corpus error.
  for (const fixture of manifest.fixtures.filter((f: any) => typeof f.caseCount === "number")) {
    const stem = join("protocol", fixture.path).replace(/\.json$/, "");
    const count = cases.filter((c) => c.set === stem).length;
    if (count !== fixture.caseCount) throw new Error(`protocol/manifest.json lists ${fixture.caseCount} cases for ${fixture.path}, the file has ${count}`);
  }

  const seen = new Set<string>();
  for (const c of cases) {
    if (seen.has(c.id)) throw new Error(`duplicate case id ${c.id}`);
    seen.add(c.id);
  }
  return cases;
}
