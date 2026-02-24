# TypeScript Model Regeneration

The TypeScript client models are generated automatically from the OpenAPI spec
(`project/docs/openapi/runtime.v1.yaml`) via [quicktype](https://github.com/quicktype/quicktype)
and compiled to JavaScript so they can be imported at runtime by the CLI.

## Generated files

| File | Description |
|------|-------------|
| `project/docs/openapi/schemas/*.schema.json` | Intermediate JSON Schema files extracted from the OpenAPI spec |
| `project/src/generated/models.g.ts` | TypeScript interfaces and enums (quicktype output) |
| `project/src/generated/models.g.js` | Compiled JavaScript – enum values available at runtime |
| `project/src/generated/models.g.d.ts` | TypeScript declaration file for editor type checking |
| `project/dotnet/src/SwarmAssistant.Contracts/Generated/Models.g.cs` | C# models (quicktype output) |

> **Do not edit generated files by hand.** Re-run the generation script instead.

## Regeneration

### Prerequisites

- Node.js ≥ 20
- `npm install` run inside `project/` (installs `quicktype` and `typescript`)

### Run

```bash
task models:generate
# or directly:
bash scripts/generate-models.sh
```

The script:

1. Extracts JSON Schema files from `runtime.v1.yaml` via `scripts/extract-schemas.mjs`
2. Runs `quicktype` to generate `models.g.ts` (TypeScript) and `Models.g.cs` (C#)
3. Compiles `models.g.ts` → `models.g.js` + `models.g.d.ts` via `tsc`

Commit all generated outputs after regeneration (schemas, `.ts`, `.js`, `.d.ts`, and `.cs`; see “Updating the OpenAPI spec” below).

## Verification (CI)

To fail if the committed files are out of date with the current OpenAPI spec:

```bash
task models:verify
# or directly:
bash scripts/verify-models.sh
```

This regenerates all models into a temp directory and diffs them against the
committed versions. It exits non-zero if any file has changed.

## How the CLI uses the models

The compiled `models.g.js` is imported by `project/src/lib/store.mjs` to access the
`TaskState` enum values at runtime:

```js
import { TaskState } from "../generated/models.g.js";

updateTask(task, { status: TaskState.Planning });
updateTask(task, { status: TaskState.Done });
```

The `models.g.d.ts` declaration file enables TypeScript-aware editors to
type-check JavaScript files that import from `models.g.js`.

## Updating the OpenAPI spec

When you update `project/docs/openapi/runtime.v1.yaml`:

1. Run `task models:generate` to regenerate all derived files
2. Commit the updated schema files, `.ts`, `.js`, `.d.ts`, and `.cs` files together
3. Run `task models:verify` to confirm everything is in sync

## TypeScript compilation config

The TypeScript compiler is configured by `project/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "declaration": true,
    "outDir": "src/generated",
    "rootDir": "src/generated",
    "strict": true,
    "skipLibCheck": true
  },
  "files": ["src/generated/models.g.ts"]
}
```

Only `models.g.ts` is compiled. The rest of the CLI uses plain `.mjs` files.
