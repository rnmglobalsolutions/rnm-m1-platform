#!/usr/bin/env bash
set -euo pipefail

# Platform secret names. They must match infra/main.bicep / infra/*.bicepparam.
PLATFORM_SECRETS=(
  "internalApiKey:rnm-internal-api-key"
  "sendGridApiKey:rnm-sendgrid-api-key"
)

usage() {
  cat <<'EOF'
Seed Key Vault secrets for one tenant (or the platform) in one environment.

The list of required secrets comes from the tenant preflight, so it always
matches the tenant JSON (providers, ManyChat, classes, ...). Values are read
without echo and passed to Azure through a file descriptor, so they never
appear in shell history or the process list.

Usage:
  scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --tenant <TENANT_ID> [options]
  scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --platform [options]

Options:
  --tenant <id>        Tenant id (config/tenants/<id>.json). Repeatable.
  --platform           Seed platform secrets (internal API key, SendGrid key).
  --overwrite          Also prompt for secrets that already exist (rotation).
  --dry-run            Only report which secrets exist or are missing.
  --config-root <dir>  Config directory (default: config).

At each prompt:
  <value>              Store the typed value.
  @<path>              Store the contents of a file (e.g. Google Calendar JSON).
  gen                  Generate a random 32-byte secret (webhook secrets you
                       define yourself, internal API key). Copy it afterwards with:
                       az keyvault secret show --vault-name <kv> --name <secret> --query value -o tsv
  (empty)              Skip this secret.

Environment:
  DOTNET               dotnet executable with the .NET 10 SDK (default: dotnet).

Requires: az (logged in, with Key Vault Secrets Officer on the vault), jq, openssl.
EOF
}

die() {
  echo "error: $*" >&2
  exit 2
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

vault=""
config_root="config"
platform=false
overwrite=false
dry_run=false
tenants=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --vault) vault="${2:-}"; shift 2 ;;
    --tenant) tenants+=("${2:-}"); shift 2 ;;
    --platform) platform=true; shift ;;
    --overwrite) overwrite=true; shift ;;
    --dry-run) dry_run=true; shift ;;
    --config-root) config_root="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
done

[[ -n "$vault" ]] || { usage >&2; die "--vault is required"; }
[[ "$platform" == true || ${#tenants[@]} -gt 0 ]] || { usage >&2; die "use --tenant <id> and/or --platform"; }

require_command az
require_command jq
require_command openssl
az account show --only-show-errors >/dev/null || die "run 'az login' first"
az keyvault show --name "$vault" --only-show-errors --query id -o tsv >/dev/null \
  || die "Key Vault '$vault' not found or not accessible"

# Prints "logicalName:secretName" lines for a tenant, using the preflight as the source of truth.
tenant_secrets() {
  local tenant="$1" report
  report="$("${DOTNET:-dotnet}" run --project tools/RNM.Platform.TenantPreflight -- \
    --config-root "$config_root" --environment repository --tenant "$tenant" --json 2>/dev/null)" || true
  jq -er '.tenants[0].requiredSecrets | if length == 0 then error("none") else .[] | "\(.logicalName):\(.secretName)" end' \
    <<<"$report" 2>/dev/null \
    || die "could not read required secrets for tenant '$tenant' (run the tenant preflight to see why)"
}

secret_exists() {
  az keyvault secret show --vault-name "$vault" --name "$1" --only-show-errors --query id -o tsv >/dev/null 2>&1
}

secret_deleted() {
  az keyvault secret show-deleted --vault-name "$vault" --name "$1" --only-show-errors --query recoveryId -o tsv >/dev/null 2>&1
}

store_secret() {
  local name="$1" value="$2"
  # printf is a shell builtin: the value never appears as a process argument.
  az keyvault secret set --vault-name "$vault" --name "$name" --only-show-errors --output none \
    --file <(printf '%s' "$value")
}

created=0
skipped=0
existing=0
blocked=0

seed() {
  local logical="$1" name="$2" input value
  if secret_exists "$name"; then
    if [[ "$overwrite" != true || "$dry_run" == true ]]; then
      echo "  [exists]  $name ($logical)"
      existing=$((existing + 1))
      return
    fi
  elif secret_deleted "$name"; then
    # Purge protection keeps deleted names reserved; the secret must be recovered, not recreated.
    echo "  [deleted] $name is soft-deleted. Recover it with:"
    echo "            az keyvault secret recover --vault-name $vault --name $name"
    blocked=$((blocked + 1))
    return
  elif [[ "$dry_run" == true ]]; then
    echo "  [missing] $name ($logical)"
    blocked=$((blocked + 1))
    return
  fi

  read -rsp "  $name ($logical) — value, @file, gen, or empty to skip: " input </dev/tty
  echo
  case "$input" in
    "") echo "  [skipped] $name"; skipped=$((skipped + 1)); return ;;
    gen) value="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n')" ;;
    @*)
      [[ -f "${input:1}" ]] || die "file not found: ${input:1}"
      value="$(cat "${input:1}")"
      ;;
    *) value="$input" ;;
  esac

  store_secret "$name" "$value"
  unset value input
  echo "  [stored]  $name"
  created=$((created + 1))
}

echo "Key Vault: $vault"

if [[ "$platform" == true ]]; then
  echo "Platform secrets:"
  for entry in "${PLATFORM_SECRETS[@]}"; do
    seed "${entry%%:*}" "${entry#*:}"
  done
fi

for tenant in "${tenants[@]}"; do
  echo "Tenant $tenant:"
  entries="$(tenant_secrets "$tenant")" || exit 2
  while IFS= read -r entry; do
    seed "${entry%%:*}" "${entry#*:}"
  done <<<"$entries"
done

echo
echo "Stored: $created  Existing: $existing  Skipped: $skipped  Missing/blocked: $blocked"
if [[ "$created" -gt 0 ]]; then
  echo "Platform secrets are App Setting references: restart the Function Apps to pick up new values now."
  echo "Then verify: GET /api/tenants/<tenantId>/readiness"
fi
