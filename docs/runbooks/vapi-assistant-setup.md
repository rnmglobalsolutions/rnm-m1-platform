# Vapi Assistant Setup

Use this runbook to configure the RNM Global Solutions HVAC Demo assistant for M1.

## Server URL

Configure the assistant Server URL to call the main Function App:

```text
https://<FUNCTION_APP_HOST>/api/tenants/sample-hvac-tenant/webhooks/vapi/inbound
```

Use one of these auth methods:

- Preferred: `Authorization: Bearer <VAPI_WEBHOOK_SECRET>`
- Supported legacy header: `X-Vapi-Secret: <VAPI_WEBHOOK_SECRET>`

The secret value must match the Key Vault secret:

```text
tenant-sample-hvac-vapi-webhook-secret
```

## Assistant Identity

Name:

```text
RNM Global Solutions HVAC Demo Assistant
```

First message:

```text
Thank you for calling the RNM Global Solutions HVAC Demo. How can I help with your heating or cooling system today?
```

System prompt:

Use the canonical prompt from:

```text
config/prompts/hvac-inbound-voice.md
```

Copy the full file contents into the Vapi assistant system prompt. Do not use older prompt snippets from notes or screenshots; the canonical file includes the current safe flow for email confirmation, availability checks, caller-confirmed slot booking, urgent/weekend rules, onsite appointment handling, and final confirmation wording.

## Tools

Create two custom server/API tools for the full safe booking flow.

Do not enable silent or indefinite live transfer behavior for this assistant. If a real transfer destination is configured in Vapi, it must connect quickly and fail back to caller follow-up. If no live transfer destination is configured, the assistant should acknowledge human requests immediately, confirm the callback number, and say the office will follow up.

### Availability tool

Create a custom server/API tool named:

```text
check_hvac_availability
```

Use this tool to check real calendar availability without booking the appointment. For urgent calls, use `availabilityMode: earliest` to ask M1 for the fastest available slot, including urgent-only Monday-Sunday 7:30am-9:00pm availability when configured. For normal scheduling, use `availabilityMode: preferred_window` after the caller provides a day/date and time window.

Description:

```text
Use this tool to check real HVAC appointment availability before booking. For urgent service, use it to find the earliest available slot. For non-urgent service, use it after the caller gives a preferred day and time. This tool must not book the appointment.
```

Method:

```text
POST
```

URL:

```text
https://<FUNCTION_APP_HOST>/api/tenants/sample-hvac-tenant/webhooks/vapi/inbound
```

Headers:

```text
Authorization: Bearer <VAPI_WEBHOOK_SECRET>
Content-Type: application/json
```

Availability tool parameters:

```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string", "description": "Customer full name." },
    "phoneNumber": { "type": "string", "description": "Customer callback phone number in E.164 format when possible." },
    "email": { "type": "string", "description": "Valid customer email address confirmed with the caller." },
    "serviceNeed": { "type": "string", "description": "Short description of the HVAC issue or request." },
    "propertyType": { "type": "string", "description": "Residential, commercial, rental, or other property type." },
    "serviceAddress": { "type": "string", "description": "Full service address." },
    "zipCode": { "type": "string", "description": "Five digit service ZIP code." },
    "urgency": { "type": "string", "description": "How urgent the request is." },
    "availabilityMode": { "type": "string", "description": "Use earliest for urgent first-available lookup, or preferred_window for a caller-requested window." },
    "preferredTime": { "type": "string", "description": "Caller preferred appointment window. Required when availabilityMode is preferred_window." }
  },
  "required": [
    "name",
    "phoneNumber",
    "email",
    "serviceNeed",
    "propertyType",
    "serviceAddress",
    "zipCode",
    "urgency",
    "availabilityMode"
  ]
}
```

Expected availability response fields include:

```text
availabilityFound
requestedWindowAvailable
firstAvailableSlot.slotId
firstAvailableSlot.startsAt
firstAvailableSlot.endsAt
firstAvailableSlot.label
firstAvailableSlot.selectedSlotId
firstAvailableSlot.selectedSlotStart
firstAvailableSlot.selectedSlotEnd
firstAvailableSlot.selectedSlotLabel
suggestedSlots
timezone
messageForAssistant
```

### Booking tool

Create a custom server/API tool named:

```text
book_hvac_appointment
```

M1 acknowledges Vapi call lifecycle events quickly. Booking should run only after the caller has accepted a specific slot.

Description:

```text
Use after the caller has provided the required HVAC booking details. This validates service area, creates or updates the CRM contact, checks availability, books the appointment, and sends confirmations.
```

Method:

```text
POST
```

URL:

```text
https://<FUNCTION_APP_HOST>/api/tenants/sample-hvac-tenant/webhooks/vapi/inbound
```

Headers:

```text
Authorization: Bearer <VAPI_WEBHOOK_SECRET>
Content-Type: application/json
```

For the Vapi `apiRequest` Tool UI:

- Add the request body fields manually if the UI does not allow pasting the whole schema.
- Keep all required fields marked required.
- Enable schema lock/no additional properties after all fields are added.
- Do not add static body fields for the first M1 test.
- If the browser-based Test Tool shows a network/CORS error, retry with Vapi's CORS proxy option or test from Postman. Do not enable broad CORS on the main Function App for webhooks.

Tool parameters:

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string",
      "description": "Customer full name."
    },
    "phoneNumber": {
      "type": "string",
      "description": "Customer callback phone number in E.164 format when possible. Must contain at least 10 digits."
    },
    "email": {
      "type": "string",
      "description": "Valid customer email address confirmed with the caller."
    },
    "serviceNeed": {
      "type": "string",
      "description": "Short description of the HVAC issue or request."
    },
    "propertyType": {
      "type": "string",
      "description": "Residential, commercial, rental, or other property type."
    },
    "serviceAddress": {
      "type": "string",
      "description": "Full service address."
    },
    "zipCode": {
      "type": "string",
      "description": "Five digit service ZIP code."
    },
    "urgency": {
      "type": "string",
      "description": "How urgent the request is, such as emergency, today, this week, maintenance, or quote."
    },
    "preferredTime": {
      "type": "string",
      "description": "Accepted appointment slot label or caller preferred appointment window. Preserve explicit ranges and AM/PM."
    },
    "selectedSlotId": {
      "type": "string",
      "description": "Slot ID copied exactly from firstAvailableSlot.selectedSlotId or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "selectedSlotStart": {
      "type": "string",
      "description": "Slot start copied exactly from firstAvailableSlot.selectedSlotStart or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "selectedSlotEnd": {
      "type": "string",
      "description": "Slot end copied exactly from firstAvailableSlot.selectedSlotEnd or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "selectedSlotLabel": {
      "type": "string",
      "description": "Human-readable slot label copied exactly from firstAvailableSlot.selectedSlotLabel or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "customerConfirmedSlot": {
      "type": "boolean",
      "description": "Set to true only after the caller accepts the exact selectedSlotLabel."
    }
  },
  "required": [
    "name",
    "phoneNumber",
    "email",
    "serviceNeed",
    "propertyType",
    "serviceAddress",
    "zipCode",
    "urgency",
    "preferredTime",
    "selectedSlotId",
    "selectedSlotStart",
    "selectedSlotEnd",
    "selectedSlotLabel",
    "customerConfirmedSlot"
  ]
}
```

## Expected Tool Result

Availability tool result:

```json
{
  "results": [
    {
      "name": "check_hvac_availability",
      "toolCallId": "<tool-call-id>",
      "result": "{\"accepted\":true,\"processed\":true,\"availabilityFound\":true,\"requestedWindowAvailable\":null,\"timezone\":\"America/Chicago\",\"firstAvailableSlot\":{\"slotId\":\"slot-1\",\"startsAt\":\"2026-05-29T16:00:00.0000000-05:00\",\"endsAt\":\"2026-05-29T16:30:00.0000000-05:00\",\"label\":\"Friday, May 29 at 4:00 PM\"},\"messageForAssistant\":\"The earliest available appointment is Friday, May 29 at 4:00 PM. Ask the caller if that works for them before booking.\"}"
    }
  ]
}
```

When Vapi sends a `toolCallList` webhook envelope, the RNM webhook returns Vapi's tool result shape:

```json
{
  "results": [
    {
      "name": "book_hvac_appointment",
      "toolCallId": "<tool-call-id>",
      "result": "{\"accepted\":true,\"processed\":true,\"outcome\":\"Completed\",\"bookingSucceeded\":true,\"crmSucceeded\":true,\"confirmationSucceeded\":true}"
    }
  ]
}
```

When Vapi sends a direct `apiRequest` body from the Tool UI, the RNM webhook returns the result directly:

```json
{
  "accepted": true,
  "processed": true,
  "outcome": "Completed",
  "bookingSucceeded": true,
  "crmSucceeded": true,
  "confirmationSucceeded": true,
  "tenantId": "sample-hvac-tenant",
  "correlationId": "<correlation-id>"
}
```

The assistant should treat `bookingSucceeded: true` as booked. Any other value means the assistant should offer human follow-up instead of claiming a booking.

For `bookingSucceeded: false`, use the returned `messageForAssistant` as internal guidance. Do not read raw JSON, provider names, IDs, or failure details to the caller. If the message says to offer human follow-up, acknowledge the issue immediately and do not leave the caller waiting for an unconfigured transfer.

## Demo Call Script

Use an urgent in-service-area example:

```text
My AC is not cooling and this is urgent. I am at 123 Main Street, Addison, Texas 75001. It is a residential home. My name is Jane Customer, my number is +1 555 123 4567, and my email is jane@example.com.
```

Expected urgent behavior:

```text
The assistant acknowledges urgency, collects required contact and service details, calls check_hvac_availability with earliest behavior, offers the first returned slot, and books only after the caller accepts that exact slot.
```

Use a non-urgent in-service-area example:

```text
I need AC maintenance. I am at 123 Main Street, Addison, Texas 75001. It is a residential home. I would like tomorrow between 4pm and 6pm America/Chicago. My name is Jane Customer, my number is +1 555 123 4567, and my email is jane@example.com.
```

Use another valid ZIP example:

```text
My heater is not working. I am at 456 Oak Street, Dallas, Texas 99999. I would like next Monday morning.
```

## Verification

Before client demos:

1. Confirm dev deployment passed.
2. Confirm `tenant-sample-hvac-vapi-webhook-secret` exists in Key Vault.
3. Confirm `tenant-rnm-hvac-google-calendar-credentials` exists in Key Vault and includes either refresh credentials or a valid short-lived `accessToken`.
4. Confirm the Function App storage account is available for the `AzureTable` CRM/contact ledger.
5. Confirm `rnm-dev-sendgrid-api-key` exists and SendGrid sender/domain is verified.
6. Confirm Twilio SMS can be sent, or tell demo viewers SMS is pending 10DLC campaign approval.
7. Confirm `communication.smsFromPhoneNumber` in `config/tenants/sample-hvac-tenant.json` has been replaced with the dedicated demo Twilio number added to the RNM Global Solutions Messaging Service/campaign.
8. Make one in-service-area test call and verify:
   - Azure Table contact record created or updated.
   - Google Calendar appointment created with the service address visible as the event location.
   - Twilio SMS sent when SMS is enabled.
   - SendGrid email sent to the confirmed email address.
   - Application Insights has webhook, workflow, booking, CRM, confirmation, and SMS status telemetry under the correlation ID.
9. Make one test call using ZIP `99999` and verify the valid ZIP is accepted for the demo.
10. Make one test call with an invalid ZIP such as `75A01` and verify the assistant asks for the ZIP again.
11. Make one urgent test call and verify the assistant does not ask for a preferred appointment window before checking earliest availability.
12. Ask for a human during a failed booking path and verify the assistant responds immediately with callback follow-up instead of waiting silently.
