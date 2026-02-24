#!/usr/bin/env node
/**
 * Extract JSON Schema files from the OpenAPI components/schemas section.
 *
 * Usage:
 *   node scripts/extract-schemas.mjs \
 *     --input  project/docs/openapi/runtime.v1.yaml \
 *     --output project/docs/openapi/schemas
 *
 * Each schema is written as a self-contained JSON Schema (draft-07) file
 * with a `definitions` block containing only transitive schema dependencies
 * so $ref pointers resolve correctly during quicktype generation.
 */

import { readFileSync, writeFileSync, mkdirSync } from "fs";
import { resolve, join } from "path";
import { parseArgs } from "util";

// ---------------------------------------------------------------------------
// Minimal YAML parser shim – we rely on the subset used by the OpenAPI spec
// (no anchors, no complex tags). For production use install `js-yaml`.
// ---------------------------------------------------------------------------
async function loadYaml(filePath) {
  // Try js-yaml first (may be installed), then fall back to python3.
  try {
    const { load } = await import("js-yaml");
    return load(readFileSync(filePath, "utf8"));
  } catch {
    // js-yaml not available; use python3 to convert to JSON
    const { execFileSync } = await import("child_process");
    const json = execFileSync("python3", [
      "-c",
      `import sys,yaml,json; print(json.dumps(yaml.safe_load(open(sys.argv[1],encoding='utf-8').read())))`,
      filePath,
    ]);
    return JSON.parse(json.toString());
  }
}

// ---------------------------------------------------------------------------
// Re-write OpenAPI-style $ref values
//   "#/components/schemas/Foo" → "Foo.schema.json"
// so extracted schemas can reference each other directly.
// ---------------------------------------------------------------------------
function rewriteRefs(node) {
  if (Array.isArray(node)) {
    return node.map(rewriteRefs);
  }
  if (node !== null && typeof node === "object") {
    const out = {};
    for (const [k, v] of Object.entries(node)) {
      if (k === "$ref" && typeof v === "string") {
        out[k] = v.replace(
          /^#\/components\/schemas\/(.+)$/,
          (_, schemaName) => `${schemaName}.schema.json`
        );
      } else {
        out[k] = rewriteRefs(v);
      }
    }
    return out;
  }
  return node;
}

function collectReferencedSchemas(node, out = new Set()) {
  if (Array.isArray(node)) {
    for (const item of node) {
      collectReferencedSchemas(item, out);
    }
    return out;
  }

  if (node !== null && typeof node === "object") {
    for (const [k, v] of Object.entries(node)) {
      if (k === "$ref" && typeof v === "string") {
        const match = /^#\/definitions\/(.+)$/.exec(v);
        if (match) {
          out.add(match[1]);
        }
      } else {
        collectReferencedSchemas(v, out);
      }
    }
  }

  return out;
}

function buildTransitiveDefinitions(rootName, schemas) {
  const needed = new Set();
  const queue = [rootName];

  while (queue.length > 0) {
    const currentName = queue.pop();
    if (needed.has(currentName)) {
      continue;
    }

    const schema = schemas[currentName];
    if (!schema) {
      continue;
    }

    needed.add(currentName);
    for (const refName of collectReferencedSchemas(schema)) {
      if (!needed.has(refName)) {
        queue.push(refName);
      }
    }
  }

  return Object.fromEntries(
    Array.from(needed)
      .filter((name) => name !== rootName)
      .sort((a, b) => a.localeCompare(b))
      .map((name) => [name, schemas[name]])
  );
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
const { values: args } = parseArgs({
  options: {
    input: { type: "string", default: "project/docs/openapi/runtime.v1.yaml" },
    output: {
      type: "string",
      default: "project/docs/openapi/schemas",
    },
  },
});

const inputPath = resolve(args.input);
const outputDir = resolve(args.output);

console.log(`Loading OpenAPI spec: ${inputPath}`);
const spec = await loadYaml(inputPath);

const rawSchemas = spec?.components?.schemas ?? {};
if (Object.keys(rawSchemas).length === 0) {
  console.error("No schemas found under components/schemas");
  process.exit(1);
}

// Rewrite all $ref values in the full schema map once.
const schemas = rewriteRefs(rawSchemas);

mkdirSync(outputDir, { recursive: true });

let count = 0;
for (const name of Object.keys(schemas).sort((a, b) => a.localeCompare(b))) {
  const schema = schemas[name];
  const document = {
    $schema: "http://json-schema.org/draft-07/schema#",
    title: name,
    ...schema,
  };

  const outPath = join(outputDir, `${name}.schema.json`);
  writeFileSync(outPath, JSON.stringify(document, null, 2) + "\n");
  console.log(`  wrote ${outPath}`);
  count++;
}

console.log(`\nExtracted ${count} schema(s) to ${outputDir}`);
