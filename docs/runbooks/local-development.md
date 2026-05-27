# Local Development

## Setup

1. Install the .NET SDK version pinned in `global.json`.
2. Copy `src/RNM.Platform.Api/local.settings.json.example` to `local.settings.json`.
3. Set `RNM_REQUIRE_INTERNAL_API_KEY=false` for purely local webhook testing, or set `RNM_INTERNAL_API_KEY_SECRET_NAME` to the local internal API key value when testing protected endpoints.
4. Use secret names in tenant config only. Do not commit secret values.
5. Keep tenant and vertical behavior in `/config`.

## Local Secret Values

Local development uses environment variables as the secret source. If a tenant config references a secret named `tenant-sample-hvac-vapi-webhook-secret`, the local setting/environment variable must use that exact name.

For the sample HVAC tenant, useful local values are:

```json
{
  "tenant-sample-hvac-vapi-webhook-secret": "<LOCAL_VAPI_WEBHOOK_SECRET>",
  "tenant-sample-hvac-twilio-account-sid": "<TWILIO_ACCOUNT_SID>",
  "tenant-sample-hvac-twilio-auth-token": "<TWILIO_AUTH_TOKEN>",
  "tenant-rnm-hvac-google-calendar-credentials": "{ \"calendarId\": \"primary\", \"accessToken\": \"<SHORT_LIVED_GOOGLE_ACCESS_TOKEN>\" }",
  "SENDGRID_API_KEY": "<SENDGRID_API_KEY>"
}
```

Use a short-lived Google `accessToken` only for quick local smoke tests. Dev/staging/prod should use refresh credentials stored in Key Vault.

## Local Verification

Run the full solution check before pushing:

```bash
dotnet restore RNM.Platform.sln
dotnet build RNM.Platform.sln
dotnet test RNM.Platform.sln
```

Publish once before deployment changes to verify config packaging:

```bash
dotnet publish src/RNM.Platform.Api/RNM.Platform.Api.csproj \
  --configuration Release \
  --output ./publish-check

test -f ./publish-check/config/tenants/sample-hvac-tenant.json
test -f ./publish-check/config/verticals/hvac.json
```

## Current Provider Stack

The sample HVAC tenant uses:

- CRM/contact ledger: `AzureTable`
- Booking: `GoogleCalendar`
- SMS: `Twilio`
- Email: `SendGrid`

Provider calls are implemented behind adapters. Local end-to-end provider testing requires real provider credentials or local fakes added explicitly for tests.
