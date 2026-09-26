# Tenant Onboarding Preflight

Use this preflight before deploying or activating any tenant. The tenant JSON is
the source of truth for provider selection, secret names, communication settings,
and scheduled automation requirements.

The preflight does not connect to Azure or read secret values. It is safe to run
locally and in CI. Runtime dependencies are verified separately by the protected
tenant readiness endpoint after deployment.

## Validate One Tenant For Production

From the repository root:

```bash
dotnet run \
  --project tools/RNM.Platform.TenantPreflight/RNM.Platform.TenantPreflight.csproj \
  -- \
  --tenant <tenant-id> \
  --environment production
```

The command exits with code `1` when a required production check fails. It
currently blocks unsupported providers, invalid tenant/vertical configuration,
wildcard service areas, placeholder phone numbers, invalid email addresses, and
placeholder secret names.

Every tenant must include `communication.appointmentReminders`. Configure its
`templates`, `reminderOffsetsMinutes`, and `reminderStalenessCutoffMinutes`
fields in full when 1:1 appointment reminders are enabled. If the tenant does
not use 1:1 appointments, keep the object and set all three fields explicitly to
`null`. Omission and partial configuration are structural errors.

It also lists, without reading their values:

- the exact Key Vault secret names required by the selected providers;
- the Function App settings required by the tenant;
- whether `RNM_ACTIVE_TENANTS` must include the tenant for reminders, classes,
  or follow-ups;
- the readiness endpoint to call after deployment.

## Validate Every Checked-In Manifest

```bash
dotnet run \
  --project tools/RNM.Platform.TenantPreflight/RNM.Platform.TenantPreflight.csproj \
  -- \
  --all \
  --environment repository \
  --json
```

Repository mode treats known production-only issues, such as sample wildcard
coverage and placeholder tenant phone numbers, as warnings. Structural errors
still block. CI runs this command on every feature push and pull request.

## Interpreting Status

- `valid`: every applicable check passed.
- `warning`: configuration loads, but one or more production-only values still
  require attention.
- `blocked`: the tenant must not be deployed or activated.

Do not interpret local `valid` as runtime readiness. The preflight cannot prove
that a secret exists, Managed Identity can read it, a provider accepts it, or
the Function App has the deployed app settings.

## Seed The Tenant Secrets

The same required-secret list drives the seeding script, one environment at a
time. Check first, then seed what is missing:

```bash
scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --tenant <tenant-id> --dry-run
scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --tenant <tenant-id>
```

Values are read without echo and never reach shell history. Answer each prompt
with the value, `@path/to/file` (for JSON credentials such as Google Calendar),
`gen` (for webhook secrets M1 defines, which you then copy into Vapi, ManyChat,
etc.), or leave it empty to skip. Existing secrets are left untouched unless
`--overwrite` is passed. A soft-deleted secret must be recovered, not recreated;
the script prints the recover command. Use `--platform` for the internal API key
and SendGrid key. You need Key Vault Secrets Officer on the vault (see
`RNM_KEY_VAULT_ADMIN_OBJECT_ID` in `github-oidc-bootstrap.md`). If `dotnet` on
the PATH lacks the .NET 10 SDK, set `DOTNET=<path to dotnet>`.

## Runtime Verification

After deployment, call:

```bash
curl \
  -H "x-rnm-api-key: <INTERNAL_API_KEY>" \
  "https://<FUNCTION_APP_HOST>/api/tenants/<tenant-id>/readiness"
```

Only activate live traffic when the response has `status: "ready"`. A production
preflight plus a deployed readiness result closes both halves of onboarding:
checked-in configuration and real environment dependencies.
