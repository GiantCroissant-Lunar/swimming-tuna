#!/usr/bin/env bash
set -euo pipefail

if ! command -v pi >/dev/null 2>&1; then
  echo "ERROR: 'pi' CLI is not installed or not in PATH."
  echo "Install with: npm install -g @mariozechner/pi-coding-agent"
  exit 1
fi

PI_AGENT_DIR="${PI_CODING_AGENT_DIR:-$HOME/.pi/agent}"
MODELS_FILE="${PI_AGENT_DIR}/models.json"
PROVIDER_ID="${PI_ZAI_PROVIDER_ID:-zai}"
BASE_URL="${PI_ZAI_BASE_URL:-https://api.z.ai/api/coding/paas/v4}"
MODEL_ID="${PI_ZAI_MODEL_ID:-glm-4.7}"
API_TYPE="${PI_ZAI_API_TYPE:-openai-completions}"
API_KEY_ENV="${PI_ZAI_API_KEY_ENV:-ZAI_API_KEY}"

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
        model.setdefault("name", "GLM-4.7")
        model.setdefault("reasoning", True)
        model.setdefault("input", ["text"])
        model.setdefault("contextWindow", 204800)
        model.setdefault("maxTokens", 131072)
        model.setdefault("cost", {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0})
        updated = True
        break

if not updated:
    models.append({
        "id": model_id,
        "name": "GLM-4.7",
        "reasoning": True,
        "input": ["text"],
        "contextWindow": 204800,
        "maxTokens": 131072,
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
  if [[ -z "${!API_KEY_ENV-}" ]]; then
    echo "Skipping pi preflight because ${API_KEY_ENV} is not set in this shell."
  else
    PREFLIGHT_PROMPT="${PI_PREFLIGHT_PROMPT:-Reply with exactly: ok}"
    set +e
    preflight_output="$(pi --print --provider "${PROVIDER_ID}" --model "${MODEL_ID}" --prompt "${PREFLIGHT_PROMPT}" 2>&1)"
    preflight_status=$?
    set -e
    if [[ "${preflight_status}" -eq 0 ]]; then
      echo "Preflight OK: pi executed successfully with ${PROVIDER_ID}/${MODEL_ID}."
    else
      echo "Preflight failed for pi model ${PROVIDER_ID}/${MODEL_ID}."
      echo "${preflight_output}"
      echo
      if echo "${preflight_output}" | grep -Eqi "401|403|unauthorized|authentication"; then
        echo "Likely auth failure. Verify ${API_KEY_ENV} and provider permissions."
      elif echo "${preflight_output}" | grep -Eqi "not found|model"; then
        echo "Model/provider mismatch. Verify ${PROVIDER_ID}/${MODEL_ID} and endpoint ${BASE_URL}."
      fi
      exit 2
    fi
  fi
fi

echo
echo "Next step (current shell):"
echo "  export ${API_KEY_ENV}='<your-zai-key>'"
echo "  pi --provider ${PROVIDER_ID} --model ${MODEL_ID} -p 'ping'"
