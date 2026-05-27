#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Seed Key Vault secrets for the sample HVAC tenant.

Required environment variables:
  KEY_VAULT_NAME
  VAPI_WEBHOOK_SECRET
  TWILIO_ACCOUNT_SID
  TWILIO_AUTH_TOKEN
  GOOGLE_CALENDAR_CREDENTIALS_FILE

Optional environment variables:
  EMAIL_CONNECTION_STRING

Example:
  KEY_VAULT_NAME=rnm-m1-dev-abc123 \
  VAPI_WEBHOOK_SECRET='...' \
  TWILIO_ACCOUNT_SID='...' \
  TWILIO_AUTH_TOKEN='...' \
  GOOGLE_CALENDAR_CREDENTIALS_FILE=./google-calendar-credentials.json \
  ./scripts/seed-sample-hvac-tenant-secrets.sh
EOF
}

require_env() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "Missing required environment variable: ${name}" >&2
    echo >&2
    usage >&2
    exit 2
  fi
}

require_command() {
  local name="$1"
  if ! command -v "${name}" >/dev/null 2>&1; then
    echo "Required command not found: ${name}" >&2
    exit 2
  fi
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

require_command az
require_env KEY_VAULT_NAME
require_env VAPI_WEBHOOK_SECRET
require_env TWILIO_ACCOUNT_SID
require_env TWILIO_AUTH_TOKEN
require_env GOOGLE_CALENDAR_CREDENTIALS_FILE

if [[ ! -f "${GOOGLE_CALENDAR_CREDENTIALS_FILE}" ]]; then
  echo "Google Calendar credentials file not found: ${GOOGLE_CALENDAR_CREDENTIALS_FILE}" >&2
  exit 2
fi

az account show --only-show-errors >/dev/null

echo "Seeding sample HVAC tenant secrets into Key Vault: ${KEY_VAULT_NAME}"

az keyvault secret set \
  --only-show-errors \
  --vault-name "${KEY_VAULT_NAME}" \
  --name tenant-sample-hvac-vapi-webhook-secret \
  --value "${VAPI_WEBHOOK_SECRET}" \
  --output none

az keyvault secret set \
  --only-show-errors \
  --vault-name "${KEY_VAULT_NAME}" \
  --name tenant-sample-hvac-twilio-account-sid \
  --value "${TWILIO_ACCOUNT_SID}" \
  --output none

az keyvault secret set \
  --only-show-errors \
  --vault-name "${KEY_VAULT_NAME}" \
  --name tenant-sample-hvac-twilio-auth-token \
  --value "${TWILIO_AUTH_TOKEN}" \
  --output none

az keyvault secret set \
  --only-show-errors \
  --vault-name "${KEY_VAULT_NAME}" \
  --name tenant-rnm-hvac-google-calendar-credentials \
  --file "${GOOGLE_CALENDAR_CREDENTIALS_FILE}" \
  --output none

if [[ -n "${EMAIL_CONNECTION_STRING:-}" ]]; then
  az keyvault secret set \
    --only-show-errors \
    --vault-name "${KEY_VAULT_NAME}" \
    --name tenant-sample-hvac-email-connection \
    --value "${EMAIL_CONNECTION_STRING}" \
    --output none
fi

echo "Sample HVAC tenant secrets seeded."
