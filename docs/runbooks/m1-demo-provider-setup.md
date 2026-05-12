# M1 Demo Provider Setup

M1 no longer requires GoHighLevel before RNM has paying clients. The demo tenant can run on a low-cost provider stack:

- CRM/contact ledger: Azure Table Storage
- Booking: Google Calendar
- Email: SendGrid
- SMS: Twilio, once 10DLC is approved
- Secrets: Azure Key Vault

The end-to-end demo target stays the same:

```text
Call -> qualification -> service area -> booking -> CRM/contact record -> email/SMS confirmation -> telemetry
```

## Tenant Configuration

The RNM HVAC demo tenant uses provider names in `config/tenants/sample-hvac-tenant.json`:

```json
{
  "providers": {
    "crmProvider": "AzureTable",
    "bookingProvider": "GoogleCalendar",
    "smsProvider": "Twilio",
    "emailProvider": "SendGrid"
  }
}
```

GoHighLevel remains available by switching the tenant later:

```json
{
  "providers": {
    "crmProvider": "GoHighLevel",
    "bookingProvider": "GoHighLevelCalendar"
  }
}
```

## Required Key Vault Secrets

Google Calendar booking needs a tenant booking credentials secret. The demo tenant expects:

```text
tenant-rnm-hvac-google-calendar-credentials
```

Example secret shape:

```json
{
  "calendarId": "primary",
  "refreshToken": "<GOOGLE_OAUTH_REFRESH_TOKEN>",
  "clientId": "<GOOGLE_OAUTH_CLIENT_ID>",
  "clientSecret": "<GOOGLE_OAUTH_CLIENT_SECRET>",
  "timeZone": "America/Chicago",
  "businessStart": "09:00:00",
  "businessEnd": "17:00:00",
  "appointmentMinutes": 60,
  "slotStepMinutes": 30,
  "lookAheadDays": 14,
  "includeWeekends": false
}
```

For a short-lived local test only, `accessToken` can be used instead of refresh credentials, but refresh credentials are the safer demo setup.

Set the secret:

```bash
az keyvault secret set \
  --vault-name <KEY_VAULT_NAME> \
  --name tenant-rnm-hvac-google-calendar-credentials \
  --file ./google-calendar-credentials.json
```

Azure Table CRM uses the Function App `AzureWebJobsStorage` connection string by default. No CRM secret value is required for `AzureTable`, but the tenant keeps a provider-neutral CRM secret name so future external CRMs can be added without reshaping config.

## Google Calendar Setup

1. Create or choose the Google Calendar used for demos.
2. Create OAuth credentials in Google Cloud.
3. Grant the app access to the Calendar API scope needed to read availability and create events.
4. Store refresh credentials in Key Vault using the JSON format above.
5. Deploy the Function App.
6. Place a test call through Vapi and confirm an event appears on the demo calendar.

## Switching Providers Later

GHL can be restored per tenant by setting:

```json
"crmProvider": "GoHighLevel",
"bookingProvider": "GoHighLevelCalendar"
```

HubSpot can be added later as another `ICrmProviderAdapter`. Calendly can be added later as a booking-link provider, but Calendly's API does not directly create bookings during the call. The expected Calendly flow is:

```text
Qualified lead -> CRM contact -> send scheduling link -> Calendly webhook -> CRM update -> confirmation
```

## Demo Checklist

Before demoing:

- Azure dev deployment is current.
- `SENDGRID_API_KEY` is present in Key Vault.
- Google Calendar credentials are present in Key Vault.
- `sample-hvac-tenant` is selected from the Vapi webhook payload or tenant resolution.
- Twilio SMS is disabled or used only when campaign approval allows it.
- Application Insights receives workflow, booking, CRM, confirmation, and webhook telemetry.
