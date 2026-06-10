# Onboard An HVAC Tenant

Use this checklist for every paying HVAC business. Do not point live calls at `sample-hvac-tenant`.

## Required Business Data

- Legal and display business name
- Primary timezone
- Service ZIP codes
- Standard and urgent business hours
- Weekend policy
- Appointment duration and slot interval
- Services accepted and escalation policy
- Operations email and phone
- Customer confirmation sender email
- Twilio number and Messaging Service/campaign
- Booking provider and calendar ID
- CRM provider and location/account ID

## Tenant Configuration

Create:

```text
config/tenants/<tenant-id>.json
```

Rules:

- `tenantId` must exactly match the filename and Vapi webhook route.
- Production ZIP codes must be explicit. `["*"]` is rejected.
- `businessName` is used in calendar and GHL appointment titles.
- Phone numbers use E.164 format.
- Use tenant-specific Key Vault secret names.
- Do not share Twilio subaccounts or webhook secrets between customers.

## Provider Setup

1. Create the Twilio subaccount and purchase/assign the tenant number.
2. Complete required A2P 10DLC registration before production SMS traffic.
3. Verify the SendGrid sender/domain.
4. Connect Google Calendar or GoHighLevel with refreshable credentials.
5. Store provider credentials in the environment Key Vault.
6. Create the Vapi assistant and configure both M1 tools.
7. Set the webhook URL:

```text
https://<FUNCTION_APP_HOST>/api/tenants/<tenant-id>/webhooks/vapi/inbound
```

## Readiness

Call the protected readiness endpoint:

```bash
curl \
  -H "x-rnm-api-key: <INTERNAL_API_KEY>" \
  "https://<FUNCTION_APP_HOST>/api/tenants/<tenant-id>/ready"
```

Expected result:

```json
{
  "status": "ready",
  "tenantId": "<tenant-id>",
  "checks": [
    { "name": "tenantConfiguration", "ready": true },
    { "name": "storage", "ready": true },
    { "name": "bookingProvider", "ready": true },
    { "name": "crmProvider", "ready": true },
    { "name": "smsProvider", "ready": true },
    { "name": "emailProvider", "ready": true },
    { "name": "sendGrid", "ready": true },
    { "name": "emailConfiguration", "ready": true },
    { "name": "providerSecrets", "ready": true }
  ]
}
```

Do not route calls until every check is `true`.

## Acceptance Calls

Run and record these calls:

1. Urgent request: caller confirms urgency; M1 returns earliest availability.
2. Non-urgent request: caller provides a preferred day/time window.
3. Caller rejects first slot and accepts a later returned slot.
4. Caller interrupts or corrects name, phone, email, address, and ZIP.
5. Email correction requires spelling only from the second attempt onward.
6. Slot becomes unavailable before booking; no duplicate or invented booking.
7. Calendar/booking provider fails; human follow-up is offered.
8. CRM post-booking sync fails; booking remains valid and failure is logged.
9. SMS/email provider fails; retry is queued and later succeeds.
10. Human assistance is requested; the caller is not left waiting indefinitely.

For successful booking calls verify:

- Calendar/GHL appointment
- Customer name and contact details
- Service, property type, address, ZIP, urgency
- CRM contact, note, tags, and booking relationship
- Customer SMS/email
- Business email and urgent SMS policy
- Correlation ID across Application Insights events

## Go-Live Approval

The tenant is ready only when:

- Readiness returns `200`.
- All acceptance calls pass.
- Alert email receives a test alert.
- SMS compliance is approved.
- Business owner approves wording, hours, service area, and escalation behavior.
- Support contact and incident owner are recorded.
