#!/usr/bin/env bash
# generate-models.sh – Regenerate strongly-typed models from the OpenAPI schemas.
#
# Usage:
#   bash scripts/generate-models.sh
#
# Prerequisites:
#   - Node.js ≥ 20 (quicktype via npx; js-yaml loaded via extract-schemas.mjs
#                   with python3 + PyYAML as a fallback if js-yaml is absent)
#
# Outputs:
#   project/docs/openapi/schemas/*.schema.json  (intermediate JSON Schema files)
#   project/dotnet/src/SwarmAssistant.Contracts/Generated/Models.g.cs  (C#)
#   project/src/generated/models.g.ts                                   (TypeScript)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCHEMA_DIR="$REPO_ROOT/project/docs/openapi/schemas"
CS_OUT="$REPO_ROOT/project/dotnet/src/SwarmAssistant.Contracts/Generated/Models.g.cs"
TS_OUT="$REPO_ROOT/project/src/generated/models.g.ts"
OPENAPI_SPEC="$REPO_ROOT/project/docs/openapi/runtime.v1.yaml"
QUICKTYPE_VERSION="${QUICKTYPE_VERSION:-23.2.6}"

patch_csharp_nullability() {
  local file="$1"
  python3 - "$file" <<'PY'
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
text = path.read_text(encoding="utf-8")

text = re.sub(
    r"(public partial class TaskExecutionEventFeed\s*\{.*?\[JsonProperty\(\"runId\"\)\]\s*public )string( RunId \{ get; set; \})",
    r"\1string?\2",
    text,
    count=1,
    flags=re.S,
)
text = re.sub(
    r"(public partial class TaskExecutionEventFeed\s*\{.*?\[JsonProperty\(\"taskId\"\)\]\s*public )string( TaskId \{ get; set; \})",
    r"\1string?\2",
    text,
    count=1,
    flags=re.S,
)
text = re.sub(
    r"(public partial class TaskExecutionEvent\s*\{.*?\[JsonProperty\(\"payload\"\)\]\s*public )string( Payload \{ get; set; \})",
    r"\1string?\2",
    text,
    count=1,
    flags=re.S,
)
text = re.sub(
    r"(public partial class A2ATaskSubmitRequest\s*\{.*?\[JsonProperty\(\"runId\"\)\]\s*public )string( RunId \{ get; set; \})",
    r"\1string?\2",
    text,
    count=1,
    flags=re.S,
)
text = re.sub(
    r"(public partial class TaskSnapshot\s*\{.*?\[JsonProperty\(\"runId\"\)\]\s*public )string( RunId \{ get; set; \})",
    r"\1string?\2",
    text,
    count=1,
    flags=re.S,
)

path.write_text(text, encoding="utf-8")
PY
}

echo "==> Extracting JSON schemas from OpenAPI spec..."
node "$REPO_ROOT/scripts/extract-schemas.mjs" \
  --input "$OPENAPI_SPEC" \
  --output "$SCHEMA_DIR"

echo ""
echo "==> Generating C# models..."
mkdir -p "$(dirname "$CS_OUT")"
npx --yes "quicktype@${QUICKTYPE_VERSION}" \
  "$SCHEMA_DIR"/*.schema.json \
  --src-lang schema \
  --lang csharp \
  --namespace SwarmAssistant.Contracts.Generated \
  --array-type list \
  --features complete \
  --out "$CS_OUT"
patch_csharp_nullability "$CS_OUT"
echo "  wrote $CS_OUT"

echo ""
echo "==> Generating TypeScript models..."
mkdir -p "$(dirname "$TS_OUT")"
npx --yes "quicktype@${QUICKTYPE_VERSION}" \
  "$SCHEMA_DIR"/*.schema.json \
  --src-lang schema \
  --lang typescript \
  --just-types \
  --out "$TS_OUT"
echo "  wrote $TS_OUT"

echo ""
echo "==> Compiling TypeScript models to JavaScript..."
(cd "$REPO_ROOT/project" && npx --yes -p "typescript@5.8.3" tsc --project tsconfig.json)
echo "  wrote ${TS_OUT%.ts}.js"
echo "  wrote ${TS_OUT%.ts}.d.ts"

echo ""
echo "Done. Generated files:"
echo "  $CS_OUT"
echo "  $TS_OUT"
echo "  ${TS_OUT%.ts}.js"
echo "  ${TS_OUT%.ts}.d.ts"
