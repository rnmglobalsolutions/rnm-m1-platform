# Seed Sample HVAC Tenant Secrets

Use this runbook after infrastructure deployment creates the Key Vault and grants the Function App managed identity access.

Tenant/provider secrets are not App Settings. The sample tenant config stores secret names, and the app reads the values from Key Vault at runtime:

```text
config/tenants/sample-hvac-tenant.json -> secretNames -> Key Vault
```

## Required Secrets

The sample HVAC tenant needs these tenant-level secrets for the M1 end-to-end path:

```text
tenant-sample-hvac-vapi-webhook-secret
tenant-sample-hvac-twilio-account-sid
tenant-sample-hvac-twilio-auth-token
tenant-rnm-hvac-google-calendar-credentials
```

`tenant-sample-hvac-email-connection` is optional for the current SendGrid path, because SendGrid reads `SENDGRID_API_KEY` from the Function App environment. Keep the email connection secret only for provider-neutral tenant config compatibility.

## Google Calendar Credentials File

Create a local file that is never committed, for example `google-calendar-credentials.json`:

```json
{
  "calendarId": "<GOOGLE_CALENDAR_ID>",
  "refreshToken": "<GOOGLE_OAUTH_REFRESH_TOKEN>",
  "clientId": "<GOOGLE_OAUTH_CLIENT_ID>",
  "clientSecret": "<GOOGLE_OAUTH_CLIENT_SECRET>",
  "timeZone": "America/Chicago",
  "businessStart": "09:00:00",
  "businessEnd": "17:00:00",
  "urgentBusinessStart": "07:30:00",
  "urgentBusinessEnd": "21:00:00",
  "appointmentMinutes": 60,
  "slotStepMinutes": 30,
  "lookAheadDays": 14,
  "includeWeekends": false,
  "includeWeekendsForUrgent": true
}
```

For a short-lived smoke test only, the file can contain an `accessToken` instead of refresh credentials.

## Seed With Script

From the repo root:

```bash
KEY_VAULT_NAME='<KEY_VAULT_NAME>' \
VAPI_WEBHOOK_SECRET='<VAPI_WEBHOOK_SECRET>' \
TWILIO_ACCOUNT_SID='<TWILIO_ACCOUNT_SID>' \
TWILIO_AUTH_TOKEN='<TWILIO_AUTH_TOKEN>' \
GOOGLE_CALENDAR_CREDENTIALS_FILE='./google-calendar-credentials.json' \
./scripts/seed-sample-hvac-tenant-secrets.sh
```

To include the optional email connection placeholder, add it to the same script invocation:

```bash
KEY_VAULT_NAME='<KEY_VAULT_NAME>' \
VAPI_WEBHOOK_SECRET='<VAPI_WEBHOOK_SECRET>' \
TWILIO_ACCOUNT_SID='<TWILIO_ACCOUNT_SID>' \
TWILIO_AUTH_TOKEN='<TWILIO_AUTH_TOKEN>' \
GOOGLE_CALENDAR_CREDENTIALS_FILE='./google-calendar-credentials.json' \
EMAIL_CONNECTION_STRING='<EMAIL_CONNECTION_STRING>' \
./scripts/seed-sample-hvac-tenant-secrets.sh
```

## Verify Secret Presence

Do not print secret values. Verify names only:

```bash
az keyvault secret list \
  --vault-name <KEY_VAULT_NAME> \
  --query "[?starts_with(name, 'tenant-sample-hvac') || name=='tenant-rnm-hvac-google-calendar-credentials'].name" \
  --output table
```

Expected:

```text
tenant-rnm-hvac-google-calendar-credentials
tenant-sample-hvac-twilio-account-sid
tenant-sample-hvac-twilio-auth-token
tenant-sample-hvac-vapi-webhook-secret
```

## Why These Are Not App Settings

These values are tenant-specific. Keeping them in Key Vault under tenant-configured names lets M1 add future tenants without changing Function App settings or redeploying infrastructure.
