# Masterclass Automation v0.5 Runbook

This runbook covers the MVP masterclass flow. M1 does not create Zoom meetings in
this version. Create the Zoom meeting manually and store the join URL in M1.

## Tenant Configuration

Add a `classes` section to the tenant config:

```json
{
  "classes": {
    "allowedRegistrationOrigins": [
      "https://www.example.com"
    ],
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

Set `RNM_ACTIVE_TENANTS` in the Function App when reminder automation should run:

```text
RNM_ACTIVE_TENANTS=kenny-commercial-real-estate
```

Multiple tenants are comma-separated.

## Flow

1. Create the Zoom meeting manually.
2. Copy the Zoom join URL.
3. Create or update the class session in M1.
4. Register leads through the public registration endpoint or through an
   internal Postman request.
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
  "startsAt": "2026-07-15T23:00:00Z",
  "endsAt": "2026-07-16T00:00:00Z",
  "timeZone": "America/Chicago",
  "zoomUrl": "https://zoom.us/j/REPLACE_ME",
  "capacity": 100,
  "campaignId": "financial-education-july"
}
```

## Register A Lead

```http
POST /api/tenants/{tenantId}/classes/{classSessionId}/registrations
Content-Type: application/json
```

Browser/funnel requests must come from an origin configured in
`classes.allowedRegistrationOrigins`. Internal tests can use `x-rnm-api-key`.
The public path is intended for a trusted funnel page and is additionally
protected by a small per-instance rate limit. Treat it as a pilot-safe public
entry point, not as strong authentication; add stronger bot protection before
high-volume public campaigns.

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-education-july",
  "source": "WebRegistration",
  "marketingConsentGranted": true,
  "attributes": {
    "intent": "masterclass"
  }
}
```

## Consent Rules

- SMS confirmation and SMS reminders require `marketingConsentGranted: true`.
- A contact already marked `opted_out` is not reversed from the web registration
  flow.
- Email confirmation is treated as transactional for the class registration.
- Twilio STOP still wins and updates CRM consent through the existing inbound
  SMS webhook.

## Reminder Processing

The timer runs every five minutes and processes tenants from `RNM_ACTIVE_TENANTS`.

Manual run:

```http
POST /api/tenants/{tenantId}/classes/reminders/run?maxItems=25
x-rnm-api-key: <internal-api-key>
```

Optional:

```text
dueAt=2026-07-15T22:00:00Z
```

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


--
TESTS

Para probarlo bien, hazlo en este orden:

**1. Prueba técnica local**
```bash
dotnet test RNM.Platform.sln --configuration Release
```

Debe pasar todo. La última vez quedó en:

```text
354 unit tests passed
1 integration test passed
0 failed
```

**2. Configura el tenant**
En el tenant que vas a usar, confirma que existe:

```json
"classes": {
  "allowedRegistrationOrigins": [
    "https://tu-funnel.com"
  ],
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
```

Para reminders automáticos, en Azure Function App agrega:

```text
RNM_ACTIVE_TENANTS=kenny-commercial-real-estate
```

o el tenant real que estés probando.

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
  "startsAt": "2026-07-15T23:00:00Z",
  "endsAt": "2026-07-16T00:00:00Z",
  "timeZone": "America/Chicago",
  "zoomUrl": "https://zoom.us/j/REPLACE_ME",
  "capacity": 100,
  "campaignId": "financial-education-july"
}
```

Verifica que responda OK y que exista en `RnmClassSessions`.

**5. Registra un lead**
```http
POST /api/tenants/{tenantId}/classes/{classSessionId}/registrations
Content-Type: application/json
```

Para Postman puedes usar también `x-rnm-api-key`.

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-education-july",
  "source": "WebRegistration",
  "marketingConsentGranted": true,
  "attributes": {
    "intent": "masterclass"
  }
}
```

Verifica:

- Se crea/actualiza el contacto en CRM.
- Se crea registro en `RnmClassRegistrations`.
- Se envía email.
- Se envía SMS si `marketingConsentGranted = true`.
- Se crean reminders en `RnmClassReminderDue`.

**6. Prueba consentimiento**
Haz otro registro con:

```json
"marketingConsentGranted": false
```

Resultado esperado:

- Email sí puede salir.
- SMS debe quedar skipped por falta de consentimiento.
- No debe romper el registro.

Luego prueba un contacto previamente `opted_out`:

- M1 no debe revertirlo desde web registration.
- No debe enviar SMS.
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
Desde el dominio configurado en `allowedRegistrationOrigins`, manda el POST sin API key.

Debe funcionar solo si el `Origin` coincide. Desde otro dominio debe dar `403`.

**Criterio de éxito**
El flujo está funcionando si puedes hacer esto completo:

Zoom manual → crear `ClassSession` → registrar lead → CRM actualizado → email/SMS enviado → reminders programados/enviados → reporte muestra números reales.
