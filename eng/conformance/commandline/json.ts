// JSON as the Command Line protocol compares it (specs/command-line/protocol/README.md, "Examples, writer cases and
// escaping profiles"): a strict reader for a writer's output, and value equality.

const LITERAL = /true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;
const STRING = /"(?:[^"\\\u0000-\u001f]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"/y;

// Parses one strict RFC 8259 JSON text: no comments, no trailing commas, no second value, and no object with two
// members of the same name after unescaping, which JSON.parse would let the last one win. Throws with the offset.
export function parseStrict(text: string): unknown {
  let i = 0;
  const fail = (what: string): never => {
    throw new Error(`${what} at offset ${i}`);
  };
  const ws = () => {
    while (i < text.length && " \t\n\r".includes(text[i])) i++;
  };
  const value = (): unknown => {
    ws();
    const c = text[i];
    if (c === "{") {
      i++;
      const out: Record<string, unknown> = {};
      const names = new Set<string>();
      ws();
      if (text[i] === "}") return i++, out;
      for (;;) {
        ws();
        if (text[i] !== '"') fail("expected a member name");
        const name = string();
        if (names.has(name)) fail(`duplicate member ${JSON.stringify(name)}`);
        names.add(name);
        ws();
        if (text[i++] !== ":") fail("expected ':'");
        Object.defineProperty(out, name, { value: value(), enumerable: true, writable: true, configurable: true });
        ws();
        if (text[i] === ",") {
          i++;
          continue;
        }
        if (text[i++] === "}") return out;
        fail("expected ',' or '}'");
      }
    }
    if (c === "[") {
      i++;
      const out: unknown[] = [];
      ws();
      if (text[i] === "]") return i++, out;
      for (;;) {
        out.push(value());
        ws();
        if (text[i] === ",") {
          i++;
          continue;
        }
        if (text[i++] === "]") return out;
        fail("expected ',' or ']'");
      }
    }
    if (c === '"') return string();
    LITERAL.lastIndex = i;
    const literal = LITERAL.exec(text);
    if (!literal) fail("expected a JSON value");
    i += literal![0].length;
    return JSON.parse(literal![0]);
  };
  const string = (): string => {
    STRING.lastIndex = i;
    const match = STRING.exec(text);
    if (!match) fail("invalid string");
    i += match![0].length;
    return JSON.parse(match![0]);
  };
  const result = value();
  ws();
  if (i !== text.length) fail("text after the JSON value");
  return result;
}

// Two JSON values are equal when they are the same literal, numbers with the same mathematical value, strings with the
// same characters after unescaping, arrays with equal elements in order, or objects with the same member names and
// equal values in any member order.
export function valueEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (Array.isArray(a) || Array.isArray(b)) return Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, k) => valueEqual(x, b[k]));
  if (a && b && typeof a === "object" && typeof b === "object") {
    const ka = Object.keys(a), kb = Object.keys(b);
    return ka.length === kb.length && ka.every((k) => Object.hasOwn(b, k) && valueEqual((a as any)[k], (b as any)[k]));
  }
  return false;
}

// A value for a failure message, cut to a readable length.
export function abbreviate(value: unknown, limit = 240): string {
  const text = typeof value === "string" ? value : JSON.stringify(value);
  return text === undefined ? String(value) : text.length > limit ? `${text.slice(0, limit)}… (${text.length} characters)` : text;
}
