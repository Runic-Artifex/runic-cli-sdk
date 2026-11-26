// The old-name guards (D079.11): the guard files match their inputs, and the generator rejects invalid lists.
// tests/dotnet/Runic.CommandLine.Tests/OldNameGuardsTests.cs runs the generated files in MSBuild.
import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { generate, load, render } from "./old-names.mjs";

const root = resolve(import.meta.dir, "../..");
const shipped = join(root, "eng/rename/old-names.json");
const testOnly = join(root, "tests/dotnet/Runic.CommandLine.Tests/OldNameGuards/old-names.json");

test("the shipped guard file matches eng/rename/old-names.json, whose lists are empty", () => {
  expect(generate(shipped, { check: true })).toEqual([]);
  const { input, outputs } = load(shipped);
  expect(outputs).toEqual([join(root, "packages/dotnet/Runic.CommandLine/buildTransitive/Runic.CommandLine.RenamedNames.targets")]);
  expect([input.packages, input.msbuild, input.environment]).toEqual([[], [], []]);
  expect(input.family.diagnostics).toEqual({ packages: "RCLI7001", msbuild: "RCLI7002", environment: "RCLI7003" });
  expect(readFileSync(join(root, "packages/dotnet/Runic.CommandLine/buildTransitive/Runic.CommandLine.targets"), "utf8"))
    .toContain(`<Import Project="$(MSBuildThisFileDirectory)Runic.CommandLine.RenamedNames.targets" Condition="'$(_RunicCommandLineRenamedNamesImported)' != 'true'" />`);
});

test("the test-only guard file matches its list, which has every kind", () => {
  expect(generate(testOnly, { check: true })).toEqual([]);
  const { input } = load(testOnly);
  // It differs from the shipped input only in its lists and its paths.
  expect({ ...input.family, root: undefined, input: undefined, outputs: undefined })
    .toEqual({ ...load(shipped).input.family, root: undefined, input: undefined, outputs: undefined });
  for (const list of ["packages", "msbuild", "environment"]) expect(input[list].length).toBeGreaterThan(0);
});

test("the generator rejects an invalid list", () => {
  const input = load(shipped).input;
  const withLists = lists => ({ ...input, ...lists });
  expect(() => render(withLists({ msbuild: [{ old: "RunicA", new: "RaxA", kind: "metadata" }] }))).toThrow(/kind "metadata"/);
  expect(() => render(withLists({ packages: [{ old: "Runic.A", new: "Runic.A" }] }))).toThrow(/renamed to itself/);
  expect(() => render(withLists({ environment: [{ old: "RUNIC_A", new: "RAX_A" }, { old: "runic_a", new: "RAX_B" }] }))).toThrow(/listed twice/);
  expect(() => render(withLists({ packages: [{ old: "Runic.A\" Condition=\"true", new: "RunicArtifex.A" }] }))).toThrow(/valid package ID/);
  expect(() => render({ ...input, environment: undefined })).toThrow(/no "environment" list/);
});
