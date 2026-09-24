#!/usr/bin/env bash
set -euo pipefail

SCOPES="https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.freebusy"
TOKEN_URL="https://oauth2.googleapis.com/token"
AUTH_URL="https://accounts.google.com/o/oauth2/v2/auth"

usage() {
  cat <<'USAGE'
Manual Google Calendar OAuth helper for one tenant.

This script is intentionally outside the app runtime. It does not write Key Vault
secrets and does not implement self-service OAuth.

Commands:
  auth-url
    Prints the Google authorization URL.

    Required env:
      GOOGLE_CLIENT_ID
      GOOGLE_REDIRECT_URI

    Optional env:
      GOOGLE_STATE

  exchange-code
    Exchanges an authorization code for Google tokens.

    Required env:
      GOOGLE_CLIENT_ID
      GOOGLE_CLIENT_SECRET
      GOOGLE_REDIRECT_URI
      GOOGLE_AUTH_CODE

Examples:
  GOOGLE_CLIENT_ID="..." \
  GOOGLE_REDIRECT_URI="http://localhost:8080/oauth2callback" \
    ./scripts/google-calendar-oauth-manual.sh auth-url

  GOOGLE_CLIENT_ID="..." \
  GOOGLE_CLIENT_SECRET="..." \
  GOOGLE_REDIRECT_URI="http://localhost:8080/oauth2callback" \
  GOOGLE_AUTH_CODE="copy-code-from-redirect-url" \
    ./scripts/google-calendar-oauth-manual.sh exchange-code
USAGE
}

require_env() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "Missing required environment variable: ${name}" >&2
    exit 2
  fi
}

urlencode() {
  python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"
}

command="${1:-}"
case "${command}" in
  auth-url)
    require_env GOOGLE_CLIENT_ID
    require_env GOOGLE_REDIRECT_URI

    state="${GOOGLE_STATE:-manual-google-calendar-connect}"
    printf '%s?client_id=%s&redirect_uri=%s&response_type=code&scope=%s&access_type=offline&prompt=consent&include_granted_scopes=true&state=%s\n' \
      "${AUTH_URL}" \
      "$(urlencode "${GOOGLE_CLIENT_ID}")" \
      "$(urlencode "${GOOGLE_REDIRECT_URI}")" \
      "$(urlencode "${SCOPES}")" \
      "$(urlencode "${state}")"
    ;;

  exchange-code)
    require_env GOOGLE_CLIENT_ID
    require_env GOOGLE_CLIENT_SECRET
    require_env GOOGLE_REDIRECT_URI
    require_env GOOGLE_AUTH_CODE

    curl -sS -X POST "${TOKEN_URL}" \
      -H "Content-Type: application/x-www-form-urlencoded" \
      --data-urlencode "code=${GOOGLE_AUTH_CODE}" \
      --data-urlencode "client_id=${GOOGLE_CLIENT_ID}" \
      --data-urlencode "client_secret=${GOOGLE_CLIENT_SECRET}" \
      --data-urlencode "redirect_uri=${GOOGLE_REDIRECT_URI}" \
      --data-urlencode "grant_type=authorization_code"
    printf '\n'
    ;;

  -h|--help|help|"")
    usage
    ;;

  *)
    echo "Unknown command: ${command}" >&2
    usage >&2
    exit 2
    ;;
esac
