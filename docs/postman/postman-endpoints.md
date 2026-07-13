# RNM Platform M1 Postman Endpoints

Import `docs/postman/RNM.Platform.M1.postman_collection.json` into Postman and create a Postman environment for real values. Do not store real secrets in the collection.

## Variables

| Variable | Purpose |
| --- | --- |
| `functionHost` | Main Function App host, for example `https://<main-app>.azurewebsites.net` |
| `contactFunctionHost` | Contact Function App host, for example `https://<contact-app>.azurewebsites.net` |
| `tenantId` | Tenant route id, for example `sample-hvac-tenant` |
| `campaignId` | Outbound campaign id used by CRM v0.5 lead records |
| `internalApiKey` | Internal API key used by protected test endpoints |
| `vapiWebhookSecret` | Vapi webhook secret for bearer-token testing |
| `twilioSignature` | Twilio-generated request signature |
| `correlationId` | Optional request correlation id |
| `reportFrom` | Pilot report start timestamp |
| `reportTo` | Pilot report end timestamp |
| `testEmail` | Recipient for the test email endpoint |
| `contactOrigin` | Allowed browser origin for the public contact endpoint |
| `contactEmail` | Email address used in contact form endpoint tests |

## GET `/api/health`

Anonymous health probe.

Expected success:

```json
{
  "status": "healthy"
}
```

## GET `/api/tenants/{tenantId}/ready`

Protected readiness endpoint for tenant configuration, provider adapter support, storage, secrets, and messaging configuration.

Headers:

```text
x-rnm-api-key: <INTERNAL_API_KEY>
x-correlation-id: <optional-correlation-id>
```

Expected success is `200 OK` with `status: "ready"`. If a dependency is missing, expect `503 Service Unavailable` with per-check readiness details.

## GET `/api/tenants/{tenantId}/reports/pilot?from=&to=`

Protected pilot reporting endpoint. It computes real CRM v0.5/reporting numbers for the requested tenant and date range.

Headers:

```text
x-rnm-api-key: <INTERNAL_API_KEY>
x-correlation-id: <optional-correlation-id>
```

Query parameters:

```text
from=2026-07-01T00:00:00Z
to=2026-07-07T23:59:59Z
```

Expected success is `200 OK` with speed-to-contact, funnel, projected revenue, activity summary, baseline comparison, and summary text.

## POST `/api/tenants/{tenantId}/webhooks/vapi/inbound`

Inbound Vapi webhook endpoint.

Headers:

```text
Content-Type: application/json
Authorization: Bearer <VAPI_WEBHOOK_SECRET>
x-correlation-id: <optional-correlation-id>
```

Sample body:

```json
{
  "type": "call-started",
  "call": {
    "id": "call-123",
    "customer": {
      "number": "+15551234567"
    }
  }
}
```

Expected valid response is usually `202 Accepted`.

Call lifecycle events are acknowledged quickly and do not run the booking workflow. The workflow starts only when Vapi sends a supported tool call:

```text
check_availability
book_appointment
```

Transition note: Vapi assistants should be configured with the agnostic names above. During migration, M1 still accepts the legacy names `check_hvac_availability` and `book_hvac_appointment` with identical behavior.

### Vapi tool-call body for availability

Use `check_availability` to inspect real calendar availability without creating an appointment. For urgent calls where the caller has not provided a specific day/time yet, send `availabilityMode: "earliest"`. M1 tenant/booking configuration is the source of truth for business hours, urgent scheduling rules, and availability.

```json
{
  "message": {
    "type": "tool-calls",
    "call": {
      "id": "call-123",
      "customer": {
        "number": "+15551234567"
      }
    },
    "toolCallList": [
      {
        "id": "tool-123",
        "name": "check_availability",
        "arguments": {
          "name": "Jane Customer",
          "phoneNumber": "+15551234567",
          "email": "jane@example.com",
          "serviceNeed": "AC not cooling",
          "propertyType": "residential",
          "serviceAddress": "123 Main Street, Addison, TX 75001",
          "zipCode": "75001",
          "urgency": "urgent",
          "availabilityMode": "earliest"
        }
      }
    ]
  }
}
```

For a caller-requested window, use `availabilityMode: "preferred_window"` and include `preferredTime`, for example `tomorrow between 4pm and 6pm America/Chicago`.

Expected valid tool response is `200 OK` with Vapi's tool result shape and an inner JSON result containing `availabilityFound`, `requestedWindowAvailable`, `firstAvailableSlot`, `suggestedSlots`, `timezone`, and `messageForAssistant`. Slot objects include both the raw availability fields (`slotId`, `startsAt`, `endsAt`, `label`) and booking-ready aliases (`selectedSlotId`, `selectedSlotStart`, `selectedSlotEnd`, `selectedSlotLabel`). This tool does not create a booking or send confirmations.

### Vapi tool-call body for booking

Before sending `book_appointment`, run `check_availability` and copy the accepted slot values from `firstAvailableSlot` or one of the returned `suggestedSlots`. Prefer the booking-ready aliases: `selectedSlotId`, `selectedSlotStart`, `selectedSlotEnd`, and `selectedSlotLabel`. M1 will not auto-book a slot unless `customerConfirmedSlot` is `true` and the selected slot is still available.

```json
{
  "message": {
    "type": "tool-calls",
    "call": {
      "id": "call-123",
      "customer": {
        "number": "+15551234567"
      }
    },
    "toolCallList": [
      {
        "id": "tool-123",
        "name": "book_appointment",
        "arguments": {
          "name": "Jane Customer",
          "phoneNumber": "+15551234567",
          "email": "jane@example.com",
          "serviceNeed": "AC not cooling",
          "propertyType": "residential",
          "serviceAddress": "123 Main Street, Addison, TX 75001",
          "zipCode": "75001",
          "urgency": "today",
          "preferredTime": "Friday, May 29 at 4:00 PM",
          "selectedSlotId": "<copy firstAvailableSlot.selectedSlotId>",
          "selectedSlotStart": "<copy firstAvailableSlot.selectedSlotStart>",
          "selectedSlotEnd": "<copy firstAvailableSlot.selectedSlotEnd>",
          "selectedSlotLabel": "<copy firstAvailableSlot.selectedSlotLabel>",
          "customerConfirmedSlot": true
        }
      }
    ]
  }
}
```

Expected valid tool response is `200 OK` with Vapi's tool result shape. Treat `bookingSucceeded: true` as the only booking confirmation signal.

### Vapi direct API request body for booking

Vapi's `apiRequest` Tool UI may send the request body as a flat JSON object instead of a `toolCallList` envelope. M1 accepts this shape for `book_appointment` when all required booking fields are present:

```json
{
  "name": "Jane Customer",
  "phoneNumber": "+15551234567",
  "email": "jane@example.com",
  "serviceNeed": "AC not cooling",
  "propertyType": "residential",
  "serviceAddress": "123 Main Street, Addison, TX 75001",
  "zipCode": "75001",
  "urgency": "today",
  "preferredTime": "Friday, May 29 at 4:00 PM",
  "selectedSlotId": "<copy firstAvailableSlot.selectedSlotId>",
  "selectedSlotStart": "<copy firstAvailableSlot.selectedSlotStart>",
  "selectedSlotEnd": "<copy firstAvailableSlot.selectedSlotEnd>",
  "selectedSlotLabel": "<copy firstAvailableSlot.selectedSlotLabel>",
  "customerConfirmedSlot": true
}
```

Expected valid direct response is `200 OK` with `bookingSucceeded`, `crmSucceeded`, `confirmationSucceeded`, `outcome`, `tenantId`, and `correlationId` at the top level. Treat `bookingSucceeded: true` as the only booking confirmation signal.

### Vapi direct API request body for availability

```json
{
  "name": "Jane Customer",
  "phoneNumber": "+15551234567",
  "email": "jane@example.com",
  "serviceNeed": "AC not cooling",
  "propertyType": "residential",
  "serviceAddress": "123 Main Street, Addison, TX 75001",
  "zipCode": "75001",
  "urgency": "urgent",
  "availabilityMode": "earliest"
}
```

Expected valid direct response is `200 OK` with `availabilityFound`, `firstAvailableSlot`, `suggestedSlots`, `timezone`, `outcome`, `tenantId`, and `correlationId` at the top level.

## POST `/api/tenants/{tenantId}/webhooks/vapi/outbound`

Outbound Vapi webhook endpoint. Vapi calls this after outbound call lifecycle events. Terminal events close the CRM loop by recording the outbound attempt.

Headers:

```text
Content-Type: application/json
Authorization: Bearer <VAPI_WEBHOOK_SECRET>
x-correlation-id: <optional-correlation-id>
```

Sample terminal body:

```json
{
  "message": {
    "type": "end-of-call-report",
    "call": {
      "id": "call-123",
      "status": "ended",
      "endedReason": "customer-ended-call",
      "metadata": {
        "providerContactId": "contact-123",
        "correlationId": "optional-correlation-id"
      }
    }
  }
}
```

You can also pass `contactId`, `campaignId`, and `correlationId` in the query string. Expected valid response is `202 Accepted`.

## POST `/api/tenants/{tenantId}/outbound/campaigns/{campaignId}/run`

Protected manual outbound pilot trigger. It selects eligible CRM v0.5 leads for the campaign, enforces consent and TCPA window, and starts outbound calls through Vapi. This is intentionally manual for the pilot; there is no scheduler.

Headers:

```text
x-rnm-api-key: <INTERNAL_API_KEY>
x-correlation-id: <optional-correlation-id>
```

Expected success is `202 Accepted` with started, skipped, failed counts, and per-lead outcomes. If outbound voice config is missing, expect a safe `400 Bad Request` and no calls placed.

## POST `/api/tenants/{tenantId}/crm/campaigns/{campaignId}/leads/import-csv`

Protected CSV lead import for clients without a CRM. The body is raw `text/csv`.

Headers:

```text
Content-Type: text/csv
x-rnm-api-key: <INTERNAL_API_KEY>
x-correlation-id: <optional-correlation-id>
```

Required columns:

```text
firstName,lastName,phone
```

Recommended columns:

```text
email,leadSource,intent,targetPropertyAddress,assignedAgent,estimatedValue,timeZone,consentStatus
```

`consentStatus` may be `opt_in`, `unknown`, or `opted_out`. Missing or unclear consent imports as `unknown`, never `opt_in`.

Expected success is `200 OK` with created, updated, skipped, row errors, and consent breakdown.

## POST `/api/tenants/{tenantId}/crm/phone-index/backfill`

Protected repair endpoint that rebuilds `RnmContactPhoneIndex` for one tenant from `RnmContacts`. It is idempotent and safe to run after deploy or after importing legacy leads.

Headers:

```text
x-rnm-api-key: <INTERNAL_API_KEY>
x-correlation-id: <optional-correlation-id>
```

Expected success is `200 OK` with contactsScanned, indexed, skipped, and failed counts. Contact records remain the source of truth; index failures are repairable and do not change dedup results.

## POST `/api/tenants/{tenantId}/webhooks/twilio/sms-status`

Twilio SMS delivery-status webhook endpoint.

Headers:

```text
Content-Type: application/x-www-form-urlencoded
X-Twilio-Signature: <TWILIO_SIGNATURE>
x-correlation-id: <optional-correlation-id>
```

Sample form fields:

```text
MessageSid=SM1234567890
MessageStatus=delivered
To=+15551234567
From=<NEW_DEMO_TWILIO_NUMBER>
```

Twilio signatures depend on the exact URL and form fields. Use a real Twilio webhook call or generate the signature with Twilio tooling for a valid request.

## POST `/api/tenants/{tenantId}/webhooks/twilio/sms-inbound`

Twilio inbound SMS webhook endpoint. Currently used for STOP/opt-out handling.

Headers:

```text
Content-Type: application/x-www-form-urlencoded
X-Twilio-Signature: <TWILIO_SIGNATURE>
x-correlation-id: <optional-correlation-id>
```

Sample form fields:

```text
MessageSid=SM1234567890
Body=STOP
From=+15551234567
To=<NEW_DEMO_TWILIO_NUMBER>
```

Expected valid response is `202 Accepted`. Invalid or unsigned requests are rejected.

## POST `/api/test/email/send`

Protected dev/test endpoint for verifying SendGrid email sending without running the full booking workflow.

Headers:

```text
Content-Type: application/json
x-rnm-api-key: <INTERNAL_API_KEY>
x-correlation-id: <optional-correlation-id>
```

Body:

```json
{
  "toEmail": "your-test-email@example.com",
  "subject": "RNM Platform Test Email",
  "body": "This is a test email from SendGrid integration."
}
```

Expected success:

```json
{
  "sent": true,
  "providerMessageId": "<sendgrid-message-id>",
  "failureReason": null,
  "correlationId": "<correlation-id>"
}
```

This endpoint is available outside production by default. In production, it only runs when `RNM_ENABLE_TEST_EMAIL_ENDPOINT=true` is explicitly configured.

## POST `/api/contact/system-review`

Public RNM website contact form endpoint for system review requests. Use `contactFunctionHost`, not the main `functionHost`.

This endpoint is anonymous and intentionally does not use `x-rnm-api-key`. It is deployed to a separate contact Function App with app-level CORS limited to the RNM website origins. The main Function App keeps app-level CORS empty.

Allowed browser origins:

```text
https://www.rnmglobalsolutions.com
https://rnmglobalsolutions.com
```

Headers:

```text
Content-Type: application/json
Origin: https://www.rnmglobalsolutions.com
x-correlation-id: <optional-correlation-id>
```

Body:

```json
{
  "fullName": "Jane Founder",
  "email": "jane@example.com",
  "phone": "+15551234567",
  "preferredChannels": "Email",
  "currentTools": "CRM, spreadsheets",
  "workflowNeedsImprovement": "We miss follow-ups when leads come in after hours.",
  "website": "https://example.com",
  "companyWebsiteConfirm": ""
}
```

Expected success:

```json
{
  "received": true,
  "correlationId": "<correlation-id>"
}
```

`companyWebsiteConfirm` is a honeypot field and must stay empty. When it is filled, the endpoint returns the same safe success response and does not send email.

## OPTIONS `/api/contact/system-review`

App-level CORS preflight for the separate public contact Function App.

Headers:

```text
Origin: https://www.rnmglobalsolutions.com
Access-Control-Request-Method: POST
Access-Control-Request-Headers: Content-Type, x-correlation-id
```

Expected successful preflight response includes at least:

```text
Access-Control-Allow-Origin: https://www.rnmglobalsolutions.com
```

Calls from other browser origins do not receive allow headers. Azure Functions may still return `204 No Content` for disallowed preflight requests, but without `Access-Control-Allow-Origin`; browsers treat that as blocked. The function also validates `Origin` on POST requests.

## Internal API Key Resolution

Azure resolves `RNM_INTERNAL_API_KEY_SECRET_NAME` through a Key Vault reference on the main Function App:

```text
@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/rnm-internal-api-key/)
```

At runtime, the app setting value is the internal API key itself. Use that value in Postman as `internalApiKey`.
