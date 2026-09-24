# Google Calendar Manual Client Connect

This runbook is the manual pilot path for connecting one external client's
personal Google Calendar. It does not implement self-service OAuth. M1 already
knows how to use a tenant Key Vault secret that contains Google Calendar
credentials.

## What The Code Uses Today

`GoogleCalendarBookingAdapter` resolves credentials per tenant:

- `GetCredentialsAsync()` loads the tenant config and reads the booking
  credentials secret by name.
- `GetBookingCredentialsSecretName()` returns `secretNames.bookingCredentials`
  when present, otherwise `secretNames.bookingApiKey`.
- The adapter exchanges `refreshToken + clientId + clientSecret` for an access
  token using `grant_type=refresh_token`.
- The adapter creates events by posting to
  `calendars/{calendarId}/events?sendUpdates=none`.

Code references:

- `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs`
  lines 182-197.
- `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs`
  lines 201-232.
- `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs`
  lines 153-158.
- `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs`
  lines 781-839.
- `src/RNM.Platform.Infrastructure/Configuration/TenantSecretNameExtensions.cs`
  lines 14-19.

## Google Cloud Setup

Use one Google Cloud project for this manual pilot.

1. Open Google Cloud Console.
2. Select or create the project used for M1 calendar integrations.
3. Go to **APIs & Services > Library**.
4. Enable **Google Calendar API**.
5. Go to **APIs & Services > OAuth consent screen**.
6. Choose **External** user type.
7. Complete the app information.
8. Keep the app in **Testing** for the pilot.
9. Add Kenny's Google account as a **Test user**.
10. Add yourself as a test user first so you can smoke test with your own
    calendar before Kenny.
11. Add these scopes:

```text
https://www.googleapis.com/auth/calendar.events
https://www.googleapis.com/auth/calendar.freebusy
```

12. Go to **APIs & Services > Credentials**.
13. Create **OAuth client ID**.
14. Application type: **Web application**.
15. Add this redirect URI for the manual flow:

```text
http://localhost:8080/oauth2callback
```

16. Copy the `client_id` and `client_secret`.

Notes:

- The localhost redirect is only for manual token capture. There is no M1
  callback endpoint in this phase.
- If the browser cannot connect after authorization, that is acceptable for the
  manual flow. Copy the `code` query parameter from the browser address bar.
- Use `prompt=consent` and `access_type=offline`; otherwise Google may not
  return a `refresh_token`.

Official Google docs:

- OAuth web server flow:
  https://developers.google.com/identity/protocols/oauth2/web-server
- Google Calendar API scopes:
  https://developers.google.com/workspace/calendar/api/auth

## Generate Authorization URL

From the repo root:

```bash
chmod +x ./scripts/google-calendar-oauth-manual.sh

GOOGLE_CLIENT_ID="<GOOGLE_OAUTH_CLIENT_ID>" \
GOOGLE_REDIRECT_URI="http://localhost:8080/oauth2callback" \
  ./scripts/google-calendar-oauth-manual.sh auth-url
```

Open the printed URL while signed in as the account whose calendar should
receive bookings.

For the first smoke test, use your own Google account. After the flow works,
repeat with Kenny's Google account.

## Exchange Authorization Code

After authorization, copy the `code` query parameter from the redirected URL.
Then run:

```bash
GOOGLE_CLIENT_ID="<GOOGLE_OAUTH_CLIENT_ID>" \
GOOGLE_CLIENT_SECRET="<GOOGLE_OAUTH_CLIENT_SECRET>" \
GOOGLE_REDIRECT_URI="http://localhost:8080/oauth2callback" \
GOOGLE_AUTH_CODE="<CODE_FROM_REDIRECT_URL>" \
  ./scripts/google-calendar-oauth-manual.sh exchange-code
```

The response should include:

```json
{
  "access_token": "...",
  "expires_in": 3599,
  "refresh_token": "...",
  "scope": "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.freebusy",
  "token_type": "Bearer"
}
```

If `refresh_token` is missing, revoke the app access in the Google account and
repeat the authorization with `prompt=consent`. Also confirm the user is listed
as a test user while the app is in Testing mode.

## Tenant Secret Format

The adapter reads these exact JSON field names:

- `calendarId` required.
- `accessToken` optional, short-lived smoke tests only.
- `refreshToken` required for normal refresh flow.
- `clientId` required for normal refresh flow.
- `clientSecret` required for normal refresh flow.
- `tokenUri` optional; defaults to `https://oauth2.googleapis.com/token`.
- `timeZone` optional; defaults to tenant `timeZone`.
- `businessStart` optional; defaults to `09:00:00`.
- `businessEnd` optional; defaults to `17:00:00`.
- `urgentBusinessStart` optional; defaults to `businessStart`.
- `urgentBusinessEnd` optional; defaults to `businessEnd`.
- `appointmentMinutes` optional; defaults to 60, max 480.
- `slotStepMinutes` optional; defaults to 30, max 240.
- `lookAheadDays` optional; defaults to 14, max 60.
- `includeWeekends` optional; defaults to false.
- `includeWeekendsForUrgent` optional; defaults to false.

For Kenny, store this secret in Key Vault:

```bash
az keyvault secret set \
  --vault-name <KEY_VAULT_NAME> \
  --name tenant-kenny-google-calendar-credentials \
  --value '{
    "calendarId": "primary",
    "refreshToken": "<KENNY_GOOGLE_REFRESH_TOKEN>",
    "clientId": "<GOOGLE_OAUTH_CLIENT_ID>",
    "clientSecret": "<GOOGLE_OAUTH_CLIENT_SECRET>",
    "timeZone": "America/Chicago",
    "businessStart": "09:00:00",
    "businessEnd": "17:00:00",
    "appointmentMinutes": 30,
    "slotStepMinutes": 30,
    "lookAheadDays": 14,
    "includeWeekends": false
  }'
```

Use `calendarId: "primary"` when the authorized Google account is Kenny's
calendar owner and bookings should go to his primary calendar.

## Kenny Tenant Config

The tenant file is:

```text
config/tenants/kenny-commercial-real-estate.json
```

The exact Google Calendar secret name resolved for this tenant is:

```text
tenant-kenny-google-calendar-credentials
```

That is because the tenant config sets:

```json
"bookingCredentials": "tenant-kenny-google-calendar-credentials"
```

and `GetBookingCredentialsSecretName()` prefers `bookingCredentials` over
`bookingApiKey`.

Kenny is configured with:

```json
"bookingProvider": "GoogleCalendar"
```

and:

```json
"timeZone": "America/Chicago"
```

## Verification Checklist

1. Use your own Google account first.
2. Add your Google account as an OAuth test user.
3. Generate the auth URL with the script.
4. Authorize using your Google account.
5. Exchange the authorization code for a `refresh_token`.
6. Store the secret in Key Vault using the Kenny secret name temporarily, or use
   a separate test tenant secret if you prefer not to touch Kenny's secret yet.
7. Run the existing Postman direct availability request against:

```text
POST /api/tenants/kenny-commercial-real-estate/webhooks/vapi/inbound
```

8. Confirm `check_availability` returns real availability from the Google
   calendar.
9. Run the existing Postman direct booking request using the returned slot.
10. Confirm the event appears in the authorized Google Calendar.
11. Delete the test event.
12. Repeat the same flow with Kenny's Google account and replace the Key Vault
    secret with Kenny's `refreshToken`.

## Google Meet Verification

The current Google Calendar adapter requests Google Meet creation for each
created event.

Evidence:

- `CreateEventPayload()` returns `summary`, `description`, `location`, `start`,
  `end`, `attendees`, `conferenceData`, and `extendedProperties`.
- `conferenceData.createRequest.conferenceSolutionKey.type` is `hangoutsMeet`.
- The insert URL includes `conferenceDataVersion=1`.
- The adapter reads the returned Meet link from `hangoutLink` first, then from a
  video `conferenceData.entryPoints[]` URI.

Manual check:

1. Create a real booking with `book_appointment`.
2. Open the Google Calendar event.
3. Confirm the event shows a Google Meet link.
4. Confirm the API/tool response includes `onlineMeetingUrl`.
5. Confirm templates that include `{{onlineMeetingUrl}}` render the same Meet
   link in customer/business SMS or email.
