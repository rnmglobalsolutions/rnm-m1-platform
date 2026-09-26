# Masterclass Automation v0.5 Runbook

This runbook covers the MVP masterclass flow. M1 does not create Zoom meetings in
this version. Create the Zoom meeting manually and store the join URL in M1. The
stored `zoomUrl` is shared by every registrant for the same `ClassSession`.

## Tenant Configuration

Add a `classes` section to the tenant config:

```json
{
  "classes": {
    "allowedRegistrationOrigins": [
      "https://www.example.com"
    ],
    "maxRegistrationsPerMinute": 60,
    "reminderOffsetsMinutes": [1440, 60],
    "registrationTemplates": {
      "smsBodyTemplate": "You're registered for {{classTitle}}\n{{classDate}} {{classTime}} {{timeZone}}\nJoin: {{zoomUrl}}\nReply STOP to opt out.",
      "emailSubjectTemplate": "You're registered: {{classTitle}}",
      "emailBodyTemplate": "Hi {{customerName}},\n\nYou're registered for {{classTitle}}.\n\nDate: {{classDate}}\nTime: {{classTime}} {{timeZone}}\nJoin: {{zoomUrl}}\n\nReference: {{registrationId}}"
    },
    "reminderTemplates": {
      "smsBodyTemplate": "Reminder: {{classTitle}} starts {{classDate}} {{classTime}} {{timeZone}}.\nJoin: {{zoomUrl}}\nReply STOP to opt out.",
      "emailSubjectTemplate": "Reminder: {{classTitle}}",
      "emailBodyTemplate": "Hi {{customerName}},\n\nReminder: {{classTitle}} is coming up.\n\nDate: {{classDate}}\nTime: {{classTime}} {{timeZone}}\nJoin: {{zoomUrl}}\n\nReference: {{registrationId}}"
    }
  }
}
```

Also configure the dedicated server-to-server registration secret name:

```json
{
  "secretNames": {
    "classRegistrationWebhookSecret": "rnm-tenant-yartex-class-registration-webhook-secret"
  }
}
```

Create that secret in the environment's Key Vault. Its value must be a strong,
random secret. M1 reads it at runtime; it is not stored in tenant JSON.

Set `RNM_ACTIVE_TENANTS` in the Function App when reminder automation should run:

```text
RNM_ACTIVE_TENANTS=yartex
```

Multiple tenants are comma-separated.

The same timer and due-work table also process appointment reminders. Every
tenant must include `communication.appointmentReminders`; do not create a second
runner. Tenants that offer 1:1 appointments configure all fields:

```json
{
  "communication": {
    "appointmentReminders": {
      "reminderOffsetsMinutes": [1440, 60],
      "reminderStalenessCutoffMinutes": 60,
      "templates": {
        "smsBodyTemplate": "Reminder: {{businessName}} appointment {{bookingDate}} {{bookingTime}} {{timeZone}}.\nMeet: {{onlineMeetingUrl}}\nReply STOP to opt out.",
        "emailSubjectTemplate": "Reminder: appointment with {{businessName}}",
        "emailBodyTemplate": "Hi {{customerName}},\n\nThis is a reminder for your appointment with {{businessName}}.\n\nDate: {{bookingDate}}\nTime: {{bookingTime}} {{timeZone}}\nMeet: {{onlineMeetingUrl}}\n\nReference: {{correlationId}}"
      }
    }
  }
}
```

Tenants that do not offer 1:1 appointments retain the same schema and disable
the feature explicitly:

```json
{
  "communication": {
    "appointmentReminders": {
      "templates": null,
      "reminderOffsetsMinutes": null,
      "reminderStalenessCutoffMinutes": null
    }
  }
}
```

M1 does not supply appointment reminder timing defaults. An omitted or partially
configured `appointmentReminders` block fails tenant configuration validation.

## Flow

1. Create the Zoom meeting manually.
2. Copy the Zoom join URL.
3. Create or update the class session in M1.
4. Register leads through the public RNM website funnel endpoint, a trusted
   server-to-server registration, or an internal request. Meta and ManyChat
   generic lead capture should use their dedicated intake webhook first.
5. Confirm the lead received email and, if consent was granted, SMS.
6. Let the timer process reminders every five minutes, or run reminders manually.
7. Check the class report.

## Create A Class Session

```http
PUT /api/tenants/{tenantId}/classes/sessions/{classSessionId}
x-rnm-api-key: <internal-api-key>
Content-Type: application/json
```

```json
{
  "title": "Financial Education Master Class",
  "status": "published",
  "startsAt": "2027-07-15T23:00:00Z",
  "endsAt": "2027-07-16T00:00:00Z",
  "timeZone": "America/Chicago",
  "zoomUrl": "https://zoom.us/j/REPLACE_ME",
  "capacity": 100,
  "campaignId": "financial-education-july"
}
```

## Register A Lead From The Public Website

The RNM website page `/masterclass/register` must call the public funnel
endpoint. This endpoint is designed for browser JavaScript and does not require
secrets in the frontend.

```http
POST /api/tenants/{tenantId}/funnels/masterclass/{classSessionId}/registrations
Content-Type: application/json
Origin: https://rnmglobalsolutions.com
```

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-video-v1",
  "funnelType": "financial_education",
  "primaryGoal": "family_protection",
  "timeline": "under_30_days",
  "state": "TX",
  "consentSms": true,
  "consentEmail": true,
  "consentTextVersion": "web-funnel-v1",
  "consentDisclosureText": "Acepto que Yartex me contacte por SMS/email ...",
  "companyWebsiteConfirm": ""
}
```

Keep `companyWebsiteConfirm` as an empty hidden honeypot field.
`consentDisclosureText` must be the exact checkbox text; the example funnel
(`docs/examples/rnm-funnels/assets/funnel.js`) reads it from the consent label.

## Register A Lead Server-To-Server Or Internally

```http
POST /api/tenants/{tenantId}/classes/{classSessionId}/registrations
X-RNM-Class-Registration-Secret: <tenant-secret>
Content-Type: application/json
```

`Origin` is used only for CORS and never authenticates a request. Never embed
`X-RNM-Class-Registration-Secret` or `x-rnm-api-key` in browser JavaScript.
Internal tests can use `x-rnm-api-key` instead. Server-side funnel integrations
can use `X-RNM-Class-Registration-Secret`. The public website should prefer the
public funnel endpoint above.

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-education-july",
  "source": "WebRegistration",
  "marketingConsentGranted": true,
  "consentSms": true,
  "consentEmail": true,
  "consentCapturedAt": "2026-09-18T15:00:00Z",
  "consentTextVersion": "class-registration-v1",
  "consentDisclosureText": "I agree to receive class notifications by SMS and email. Reply STOP to opt out.",
  "attributes": {
    "intent": "masterclass"
  }
}
```

## Consent Rules

- Explicit `marketingConsentGranted: true` requires `consentCapturedAt` and
  `consentTextVersion` evidence.
- Consent is per channel. SMS confirmation and reminders require
  `smsConsentStatus=opt_in`; class emails require `emailConsentStatus=opt_in`.
- `consentSms` / `consentEmail` set the channel grants. A `true` grant needs
  `consentDisclosureText` and `consentTextVersion`; without them the
  registration is still stored, the channel is recorded as not granted, and M1
  logs `class.registration.consent_evidence_missing`.
- Callers that omit `consentSms` fall back to `marketingConsentGranted` as the
  SMS grant.
- Contacts created before per-channel consent (no `smsConsentStatus` or
  `emailConsentStatus`) keep their previous behavior: legacy `opt_in` covers
  both channels.
- A contact already marked `opted_out` is not reversed from the web registration
  flow.
- An SMS opt-out (STOP) blocks customer SMS and outbound calls, not email.
- An existing `opt_in` remains valid when a later registration contains no new
  grant; the timeline records that no new explicit consent was captured.
- Twilio STOP still wins and updates CRM consent through the existing inbound
  SMS webhook.

Registration confirmation delivery statuses are `Sent`, `Skipped`, `Failed`, or
`RetryScheduled`. `RetryScheduled` means the provider call failed but the durable
confirmation retry queue accepted the work. Repeating the registration does not
send that channel again while its queued retry is outstanding. A plain `Failed`
status can be resumed by an idempotent duplicate request.

## Reminder Processing

The timer runs every five minutes and processes tenants from `RNM_ACTIVE_TENANTS`.
Class and appointment reminder SMS use the shared send-window policy before
dispatch. The policy checks lead timezone attributes in this order: `timeZone`,
`timezone`, `leadTimeZone`, `leadTimezone`; if none is present or valid, it
falls back to the tenant timezone. The default send window is 8:00 AM inclusive
to 9:00 PM exclusive unless the tenant TCPA window overrides it. If SMS is
outside the send window, M1 skips SMS, records `outside_send_window`, and still
sends reminder email when allowed.

Stale reminders are skipped. Configure `classes.reminderStalenessCutoffMinutes`
per tenant when needed; the default is 60 minutes. M1 also skips reminders for
classes that have already started, are no longer `published`, or whose start
time changed after the reminder row was created. Claimed rows become eligible
again after a 15-minute recovery lease if a Function execution crashes.

Changing a class start time or status cancels its pending reminder rows and
rebuilds reminders for existing registrations when the updated session remains
published and in the future. Reminders already marked `Sent` are never reset;
only rows skipped specifically because the session changed may be reactivated.

For appointments, configure `communication.appointmentReminders.reminderStalenessCutoffMinutes`.
M1 skips appointment reminder rows when the appointment has already started.
Booking cancellation/reschedule invalidation is not implemented yet; if an
appointment is changed outside M1, pending reminder rows are not automatically
invalidated.

The same timer also processes tenant follow-up automation from `followUps`.
Follow-ups are stored in `RnmFollowUpDue` and are tenant-scoped. This is
SMS/email automation only. It does not place outbound calls and it is not a
full nurture engine.

Manual run:

```http
POST /api/tenants/{tenantId}/classes/reminders/run?maxItems=25
x-rnm-api-key: <internal-api-key>
```

Optional:

```text
dueAt=2026-07-15T22:00:00Z
```

Manual follow-up run:

```http
POST /api/tenants/{tenantId}/followups/run?maxItems=25
x-rnm-api-key: <internal-api-key>
```

Follow-up SMS requires `opt_in` consent and must be inside the shared send
window. Follow-up email also requires `opt_in` because these are marketing
follow-ups, not class registration transaction emails. Configure sequences under
`followUps.sequences`; each sequence is triggered by an event such as
`followup.required`.

## Reporting

```http
GET /api/tenants/{tenantId}/classes/{classSessionId}/report
x-rnm-api-key: <internal-api-key>
```

The report returns real stored counts only:

- registrations
- email confirmations sent
- SMS confirmations sent
- reminders sent
- opted-out registrations

## Tables

All tables are tenant-scoped with `PartitionKey = tenantId`.

- `RnmClassSessions`
- `RnmClassRegistrations`
- `RnmClassReminderDue`

## Current Limits

- No Zoom API meeting creation.
- No Zoom OAuth.
- No attendance sync yet.
- No dashboard UI.
- No nurture sequence after class attendance.
- Public registration rate limiting is best-effort per Function App instance.
- Direct browser submission is intentionally unsupported because a browser
  cannot safely hold the registration secret; use a trusted backend/proxy.


--
TESTS

Para probarlo bien, hazlo en este orden:

**1. Prueba técnica local**
```bash
dotnet test RNM.Platform.sln --configuration Release
```

Debe pasar todo:

```text
All unit and integration tests must pass with `0 failed`.
```

**2. Configura el tenant**
En el tenant que vas a usar, confirma que existe:

```json
{
  "classes": {
    "allowedRegistrationOrigins": [
      "https://rnmglobalsolutions.com",
      "https://www.rnmglobalsolutions.com"
    ],
    "maxRegistrationsPerMinute": 60,
    "reminderOffsetsMinutes": [1440, 60],
    "registrationTemplates": {
      "smsBodyTemplate": "...",
      "emailSubjectTemplate": "...",
      "emailBodyTemplate": "..."
    },
    "reminderTemplates": {
      "smsBodyTemplate": "...",
      "emailSubjectTemplate": "...",
      "emailBodyTemplate": "..."
    }
  }
}
```

Also configure `secretNames.classRegistrationWebhookSecret` and create its
value in Key Vault before testing.

Para reminders automáticos de masterclass, en Azure Function App agrega:

```text
RNM_ACTIVE_TENANTS=yartex
```

o el tenant real de masterclass que estés probando. Kenny Commercial Real Estate
no debe usarse para masterclasses.

**3. Crea la reunión en Zoom manualmente**
En Zoom crea la masterclass y copia el join link.

**4. Crea la sesión en M1**
Usa Postman:

```http
PUT /api/tenants/{tenantId}/classes/sessions/{classSessionId}
x-rnm-api-key: <internal-api-key>
Content-Type: application/json
```

Body:

```json
{
  "title": "Financial Education Master Class",
  "status": "published",
  "startsAt": "2027-07-15T23:00:00Z",
  "endsAt": "2027-07-16T00:00:00Z",
  "timeZone": "America/Chicago",
  "zoomUrl": "https://zoom.us/j/REPLACE_ME",
  "capacity": 100,
  "campaignId": "financial-education-july"
}
```

Verifica que responda OK y que exista en `RnmClassSessions`.

**5. Registra un lead**

Para probar como navegador desde la pagina publica:

```http
POST /api/tenants/{tenantId}/funnels/masterclass/{classSessionId}/registrations
Content-Type: application/json
Origin: https://rnmglobalsolutions.com
```

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-video-v1",
  "funnelType": "financial_education",
  "primaryGoal": "family_protection",
  "timeline": "under_30_days",
  "state": "TX",
  "consentSms": true,
  "consentEmail": true,
  "companyWebsiteConfirm": ""
}
```

Para Postman/server-to-server usa el endpoint interno de registro:

```http
POST /api/tenants/{tenantId}/classes/{classSessionId}/registrations
Content-Type: application/json
```

Para Postman usa `x-rnm-api-key` o
`X-RNM-Class-Registration-Secret`, nunca solo `Origin`.

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-education-july",
  "source": "WebRegistration",
  "marketingConsentGranted": true,
  "consentSms": true,
  "consentEmail": true,
  "consentCapturedAt": "2026-09-18T15:00:00Z",
  "consentTextVersion": "class-registration-v1",
  "consentDisclosureText": "I agree to receive class notifications by SMS and email. Reply STOP to opt out.",
  "attributes": {
    "intent": "masterclass"
  }
}
```

Verifica:

- Se crea/actualiza el contacto en CRM.
- Se crea registro en `RnmClassRegistrations`.
- Se envía email si `consentEmail = true`.
- Se envía SMS si `consentSms = true` (o, en integraciones antiguas sin
  `consentSms`, si `marketingConsentGranted = true`).
- Se crean reminders en `RnmClassReminderDue`.

**6. Prueba consentimiento**
Haz otro registro con:

```json
"marketingConsentGranted": false,
"consentSms": false,
"consentEmail": false
```

Resultado esperado:

- El registro se guarda, pero no sale SMS ni email por falta de `opt_in` en
  cada canal.
- SMS y email deben quedar skipped por falta de consentimiento.
- No debe romper el registro.

Repite con `consentSms: true` sin `consentDisclosureText`:

- El registro se guarda.
- `smsConsentStatus` queda `unknown` y no sale SMS.
- Application Insights registra `class.registration.consent_evidence_missing`.

Luego prueba un contacto que respondió STOP:

- M1 no debe revertirlo desde web registration.
- No debe enviar SMS.
- El email depende solo de `emailConsentStatus`; STOP no lo bloquea.
- Debe dejar timeline/evento de consentimiento bloqueado o declinado.

**7. Prueba reminders manualmente**
```http
POST /api/tenants/{tenantId}/classes/reminders/run?maxItems=25
x-rnm-api-key: <internal-api-key>
```

Si quieres forzar reminders sin esperar, crea una sesión próxima o usa `dueAt` en query:

```text
dueAt=2026-07-15T22:00:00Z
```

Verifica:

- Reminder cambia de `pending` a `sent`, `skipped` o `failed`.
- SMS/email reminder salen según consentimiento y templates.
- No se duplican si ya fue reclamado/procesado.

**8. Prueba el reporte**
```http
GET /api/tenants/{tenantId}/classes/{classSessionId}/report
x-rnm-api-key: <internal-api-key>
```

Debe devolver conteos reales:

- registrations
- confirmed emails sent
- confirmed SMS sent
- reminders sent
- opted-out registrations

**9. Prueba desde funnel real**
El navegador en `https://rnmglobalsolutions.com/masterclass/register` envia el
formulario al endpoint publico:

```text
/api/tenants/yartex/funnels/masterclass/{classSessionId}/registrations
```

No debe incluir `X-RNM-Class-Registration-Secret` ni `x-rnm-api-key`. El `Origin`
debe coincidir con los origenes permitidos del tenant.

**Criterio de éxito**
El flujo está funcionando si puedes hacer esto completo:

Zoom manual → crear `ClassSession` → registrar lead → CRM actualizado → email/SMS enviado → reminders programados/enviados → reporte muestra números reales.
