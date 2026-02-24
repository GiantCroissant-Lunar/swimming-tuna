#!/usr/bin/env bash
# verify-models.sh – Fail if generated model files are stale.
#
# Regenerates models into a temp directory, then diffs against the committed
# files. Exits with a non-zero status (and prints a diff) if any file has
# changed, so CI can enforce that models are always up to date.
#
# Usage:
#   bash scripts/verify-models.sh

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCHEMA_DIR="$REPO_ROOT/project/docs/openapi/schemas"
CS_OUT="$REPO_ROOT/project/dotnet/src/SwarmAssistant.Contracts/Generated/Models.g.cs"
TS_OUT="$REPO_ROOT/project/src/generated/models.g.ts"
JS_OUT="$REPO_ROOT/project/src/generated/models.g.js"
DTS_OUT="$REPO_ROOT/project/src/generated/models.g.d.ts"
OPENAPI_SPEC="$REPO_ROOT/project/docs/openapi/runtime.v1.yaml"
QUICKTYPE_VERSION="${QUICKTYPE_VERSION:-23.2.6}"

TMPDIR="$(mktemp -d)"
TMP_SCHEMA_DIR="$TMPDIR/schemas"
TMP_CS="$TMPDIR/Models.g.cs"
TMP_TS="$TMPDIR/models.g.ts"
TMP_JS="$TMPDIR/models.g.js"
TMP_DTS="$TMPDIR/models.g.d.ts"

cleanup() { rm -rf "$TMPDIR"; }
trap cleanup EXIT

echo "==> Extracting schemas into temp dir..."
node "$REPO_ROOT/scripts/extract-schemas.mjs" \
  --input "$OPENAPI_SPEC" \
  --output "$TMP_SCHEMA_DIR" > /dev/null

echo "==> Generating C# models into temp file..."
npx --yes "quicktype@${QUICKTYPE_VERSION}" \
  "$TMP_SCHEMA_DIR"/*.schema.json \
  --src-lang schema \
  --lang csharp \
  --namespace SwarmAssistant.Contracts.Generated \
  --array-type list \
  --features complete \
  --out "$TMP_CS" 2>/dev/null

echo "==> Generating TypeScript models into temp file..."
npx --yes "quicktype@${QUICKTYPE_VERSION}" \
  "$TMP_SCHEMA_DIR"/*.schema.json \
  --src-lang schema \
  --lang typescript \
  --just-types \
  --out "$TMP_TS" 2>/dev/null

echo "==> Compiling TypeScript models into temp JS..."
# Write a minimal tsconfig pointing at the temp TS file
TMP_TSCONFIG="$TMPDIR/tsconfig.json"
cat > "$TMP_TSCONFIG" <<EOF
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "declaration": true,
    "outDir": "$TMPDIR",
    "rootDir": "$TMPDIR",
    "strict": true,
    "skipLibCheck": true
  },
  "files": ["$TMP_TS"]
}
EOF
npx --yes -p "typescript@5.8.3" tsc --project "$TMP_TSCONFIG" 2>/dev/null

EXIT_CODE=0

compare_file() {
  local label="$1"
  local expected="$2"
  local actual="$3"

  if [ ! -f "$actual" ]; then
    echo "FAIL: $label – committed file not found at $actual"
    echo "      Run 'task models:generate' to generate it."
    EXIT_CODE=1
    return
  fi

  if ! diff -q "$expected" "$actual" > /dev/null 2>&1; then
    echo "FAIL: $label is stale. Run 'task models:generate' and commit the result."
    diff "$expected" "$actual" || true
    EXIT_CODE=1
  else
    echo "OK:   $label is up to date."
  fi
}

compare_file "C# models (Models.g.cs)"        "$TMP_CS" "$CS_OUT"
compare_file "TypeScript models (models.g.ts)" "$TMP_TS" "$TS_OUT"
compare_file "Compiled JS models (models.g.js)"  "$TMP_JS" "$JS_OUT"
compare_file "TypeScript declarations (models.g.d.ts)" "$TMP_DTS" "$DTS_OUT"

if ! diff -qr "$TMP_SCHEMA_DIR" "$SCHEMA_DIR" > /dev/null 2>&1; then
  echo "FAIL: OpenAPI schema artifacts are stale. Run 'task models:generate' and commit the result."
  diff -ru "$TMP_SCHEMA_DIR" "$SCHEMA_DIR" || true
  EXIT_CODE=1
else
  echo "OK:   OpenAPI schema artifacts are up to date."
fi

if [ "$EXIT_CODE" -ne 0 ]; then
  echo ""
  echo "One or more generated files are stale."
  echo "Run 'task models:generate' and commit the updated files."
  exit 1
fi

echo ""
echo "All generated model files are up to date."
