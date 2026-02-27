#!/usr/bin/env bash
set -euo pipefail

if ! command -v pi >/dev/null 2>&1; then
  echo "ERROR: 'pi' CLI is not installed or not in PATH."
  echo "Install with: npm install -g @mariozechner/pi-coding-agent"
  exit 1
fi

PI_AGENT_DIR="${PI_CODING_AGENT_DIR:-$HOME/.pi/agent}"
MODELS_FILE="${PI_AGENT_DIR}/models.json"
PROVIDER_ID="${PI_KIMI_PROVIDER_ID:-kimi-coding}"
BASE_URL="${PI_KIMI_BASE_URL:-https://api.kimi.com/coding/v1}"
MODEL_ID="${PI_KIMI_MODEL_ID:-kimi-for-coding}"
API_TYPE="${PI_KIMI_API_TYPE:-openai-completions}"
API_KEY_ENV="${PI_KIMI_API_KEY_ENV:-KIMI_API_KEY}"

mkdir -p "${PI_AGENT_DIR}"

MODELS_FILE="${MODELS_FILE}" PROVIDER_ID="${PROVIDER_ID}" BASE_URL="${BASE_URL}" MODEL_ID="${MODEL_ID}" API_TYPE="${API_TYPE}" API_KEY_ENV="${API_KEY_ENV}" python3 - <<'PY'
import json
import os
from pathlib import Path

models_file = Path(os.environ["MODELS_FILE"])
provider_id = os.environ["PROVIDER_ID"]
base_url = os.environ["BASE_URL"].rstrip("/")
model_id = os.environ["MODEL_ID"]
api_type = os.environ["API_TYPE"]
api_key_env = os.environ["API_KEY_ENV"]

if models_file.exists():
    raw = models_file.read_text(encoding="utf-8").strip()
    data = json.loads(raw) if raw else {}
    if not isinstance(data, dict):
        raise SystemExit(f"models.json must contain an object: {models_file}")
else:
    data = {}

providers = data.setdefault("providers", {})
if not isinstance(providers, dict):
    raise SystemExit(f"'providers' must be an object in {models_file}")

provider = providers.get(provider_id, {})
if not isinstance(provider, dict):
    provider = {}

provider["baseUrl"] = base_url
provider["api"] = api_type
provider["apiKey"] = api_key_env
provider["authHeader"] = True

models = provider.get("models", [])
if not isinstance(models, list):
    models = []

updated = False
for model in models:
    if isinstance(model, dict) and model.get("id") == model_id:
        model.setdefault("name", "Kimi For Coding")
        model.setdefault("reasoning", True)
        model.setdefault("input", ["text", "image"])
        model.setdefault("contextWindow", 262144)
        model.setdefault("maxTokens", 32768)
        model.setdefault("cost", {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0})
        updated = True
        break

if not updated:
    models.append({
        "id": model_id,
        "name": "Kimi For Coding",
        "reasoning": True,
        "input": ["text", "image"],
        "contextWindow": 262144,
        "maxTokens": 32768,
        "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
    })

provider["models"] = models
providers[provider_id] = provider

models_file.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
PY

echo "Configured pi provider override in: ${MODELS_FILE}"
echo "  provider: ${PROVIDER_ID}"
echo "  baseUrl : ${BASE_URL}"
echo "  api     : ${API_TYPE}"
echo "  model   : ${MODEL_ID}"
echo

if [[ "${PI_SKIP_PREFLIGHT:-0}" != "1" ]]; then
  if [[ -z "${KIMI_API_KEY:-}" ]]; then
    echo "Skipping pi preflight because KIMI_API_KEY is not set in this shell."
  else
    PREFLIGHT_MODEL="${PROVIDER_ID}/${MODEL_ID}"
    PREFLIGHT_PROMPT="${PI_PREFLIGHT_PROMPT:-Reply with exactly: ok}"
    set +e
    preflight_output="$(pi --print --model "${PREFLIGHT_MODEL}" --prompt "${PREFLIGHT_PROMPT}" 2>&1)"
    preflight_status=$?
    set -e
    if [[ "${preflight_status}" -eq 0 ]]; then
      echo "Preflight OK: pi executed successfully with model ${PREFLIGHT_MODEL}."
    else
      echo "Preflight failed for pi model ${PREFLIGHT_MODEL}."
      echo "${preflight_output}"
      echo
      if echo "${preflight_output}" | grep -qi "only available for Coding Agents"; then
        echo "Detected provider-side access policy block for pi on Kimi For Coding."
        echo "Use a supported adapter first (kilo/kimi) and track this as a provider compatibility follow-up."
      elif echo "${preflight_output}" | grep -qi "requested resource was not found"; then
        echo "Detected API mismatch (often Responses-vs-Completions path mismatch)."
        echo "Check provider api/baseUrl settings in ${MODELS_FILE}."
      fi
      exit 2
    fi
  fi
fi

echo
echo "Next step (current shell):"
echo "  export KIMI_API_KEY='<your-kimi-key>'"
echo "  pi --model ${PROVIDER_ID}/${MODEL_ID} -p 'ping'"
