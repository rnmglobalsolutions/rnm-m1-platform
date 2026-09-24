#!/usr/bin/env bash
set -euo pipefail

required() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "Missing required environment variable: ${name}" >&2
    exit 1
  fi
}

required RNM_FUNCTION_HOST
required RNM_INTERNAL_API_KEY
required RNM_MASTERCLASS_STARTS_AT
required RNM_MASTERCLASS_ZOOM_URL

TENANT_ID="${RNM_TENANT_ID:-rnm-insurance-agents}"
SESSION_ID="${RNM_MASTERCLASS_SESSION_ID:-financial-education-next}"
TITLE="${RNM_MASTERCLASS_TITLE:-Financial Education Master Class}"
ENDS_AT="${RNM_MASTERCLASS_ENDS_AT:-}"
TIME_ZONE="${RNM_MASTERCLASS_TIME_ZONE:-America/Chicago}"
CAPACITY="${RNM_MASTERCLASS_CAPACITY:-100}"
CAMPAIGN_ID="${RNM_MASTERCLASS_CAMPAIGN_ID:-financial-video-v1}"
STATUS="${RNM_MASTERCLASS_STATUS:-published}"
HOST="${RNM_FUNCTION_HOST%/}"
URL="${HOST}/api/tenants/${TENANT_ID}/classes/sessions/${SESSION_ID}"
PAYLOAD_FILE="$(mktemp "${TMPDIR:-/tmp}/rnm-masterclass-session.XXXXXX.json")"
trap 'rm -f "${PAYLOAD_FILE}"' EXIT

export TITLE ENDS_AT RNM_MASTERCLASS_STARTS_AT TIME_ZONE RNM_MASTERCLASS_ZOOM_URL CAPACITY CAMPAIGN_ID STATUS
python3 - <<'PY' > "${PAYLOAD_FILE}"
import json
import os

payload = {
    "title": os.environ["TITLE"],
    "status": os.environ["STATUS"],
    "startsAt": os.environ["RNM_MASTERCLASS_STARTS_AT"],
    "timeZone": os.environ["TIME_ZONE"],
    "zoomUrl": os.environ["RNM_MASTERCLASS_ZOOM_URL"],
    "capacity": int(os.environ["CAPACITY"]),
    "campaignId": os.environ["CAMPAIGN_ID"],
    "attributes": {
        "topic": "financial_education",
        "source": "phase_6_setup"
    },
}

ends_at = os.environ.get("ENDS_AT")
if ends_at:
    payload["endsAt"] = ends_at

print(json.dumps(payload, separators=(",", ":")))
PY

echo "Upserting master class session '${SESSION_ID}' for tenant '${TENANT_ID}'..."
curl --fail-with-body -sS \
  -X PUT "${URL}" \
  -H "Content-Type: application/json" \
  -H "x-rnm-api-key: ${RNM_INTERNAL_API_KEY}" \
  --data-binary "@${PAYLOAD_FILE}"

echo
echo "Session configured."
echo "Use this in the RNM website funnel config:"
echo "MASTERCLASS_SESSION_ID=${SESSION_ID}"
