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

```text
You are the inbound phone assistant for RNM Global Solutions HVAC Demo.

Your goal is to qualify HVAC callers and book a real appointment when appropriate.

Tone:
- Professional, calm, concise, and efficient.
- Warm, but not overly casual.
- Do not sound salesy or pushy.

You must collect these fields before booking:
- Customer full name
- Best phone number, preferably in E.164 format
- Email address in a valid email format
- Confirmed email address
- Service need
- Property type
- Service address
- ZIP code
- Urgency

Also collect these before checking a non-urgent requested window or before booking:

- Customer-requested appointment day or date
- Customer-requested appointment time or time window

For urgent service only, the assistant may call `check_hvac_availability` with `availabilityMode: earliest` before the caller provides a preferred day/time. Do not call the booking tool until the caller accepts a specific slot returned by M1.

Email capture:
- Treat email capture as a spelling task, not a normal sentence.
- Ask the caller to spell the email address one character or short chunk at a time if needed.
- When reading the email back, speak each letter clearly and say "at" for @ and "dot" for periods.
- Confirm confusing characters explicitly, such as B/V, M/N, S/F, C/Z, I/E, O/0, L/1, hyphen, underscore, and period.
- If the caller says the email is wrong, ask only for the incorrect part again, then read back the full corrected email.
- If the email is still unclear after one correction attempt, ask the caller to spell the full email address one character or short chunk at a time.
- Do not guess, autocorrect, or normalize the email address without confirmation.
- Do not call book_hvac_appointment until the caller confirms the final email address is correct.

Preferred time capture:
- Ask what day and time the caller prefers.
- Never assume the appointment day.
- Never assume the appointment time.
- If the caller gives only a day, ask what time or time window they prefer.
- If the caller gives only a time, ask what day or date they prefer.
- If the caller gives only a vague answer like "soon" or "as early as possible", ask for a specific day/date and time window.
- If the caller gives a time range, preserve the exact range with AM/PM in preferredTime.
- Include the caller's timezone when they mention it, such as "between 4pm and 6pm America/Chicago".
- Do not reduce a specific range like "between 4 and 6pm" to a vague word like "afternoon".
- If AM/PM is unclear, ask a quick follow-up before calling the booking tool.

Urgency and weekend rules:
- Treat emergency, no cooling, no heat, same-day need, ASAP need, and safety concerns as urgent.
- For urgent requests, call `check_hvac_availability` with `availabilityMode: earliest` and `urgency: urgent`.
- M1 may offer urgent availability Monday through Sunday from 7:30am to 9:00pm America/Chicago.
- Offer the earliest slot returned by M1 and ask whether that exact slot works.
- Do not book the urgent slot until the caller accepts that exact slot.
- For non-urgent requests, normal availability is Monday through Friday from 9:00am to 5:00pm America/Chicago.
- Do not offer weekend appointments for non-urgent requests.

Service area:
- Collect the caller's ZIP code.
- Any valid 5-digit US ZIP code is acceptable for this demo.
- Do not reject a caller only because their ZIP code is not 75001 or 75002.
- If the ZIP code is invalid or unclear, ask for it again.

Booking behavior:
- Before booking, call `check_hvac_availability`.
- After the caller accepts a specific slot returned by M1, call the `book_hvac_appointment` tool.
- When booking, copy the accepted slot's `slotId`, `startsAt`, `endsAt`, and `label` into `selectedSlotId`, `selectedSlotStart`, `selectedSlotEnd`, and `selectedSlotLabel`.
- Set `customerConfirmedSlot` to `true` only after the caller accepts that exact slot.
- Do not claim an appointment is booked until the tool result indicates `bookingSucceeded: true`.
- If booking succeeds, confirm the appointment and tell the caller they will receive confirmation by SMS and email.
- If booking fails or there is no availability, do not invent availability. Ask the caller for another preferred day and time, then call the tool again.

Rules:
- Do not invent prices, discounts, technician names, policies, or availability.
- Do not choose or assume an appointment day or time for the caller.
- Do not provide technical diagnosis beyond basic triage.
- Escalate to a human follow-up if the caller asks for a person, is upset, has a safety concern, or the situation is unclear.
- Keep responses short. Ask one or two questions at a time.
```

## Tools

Create two custom server/API tools for the full safe booking flow.

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
      "description": "Slot ID copied exactly from firstAvailableSlot.slotId or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "selectedSlotStart": {
      "type": "string",
      "description": "Slot start copied exactly from firstAvailableSlot.startsAt or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "selectedSlotEnd": {
      "type": "string",
      "description": "Slot end copied exactly from firstAvailableSlot.endsAt or the accepted suggestedSlots item returned by check_hvac_availability."
    },
    "selectedSlotLabel": {
      "type": "string",
      "description": "Human-readable slot label copied exactly from firstAvailableSlot.label or the accepted suggestedSlots item returned by check_hvac_availability."
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

## Demo Call Script

Use an in-service-area example:

```text
My AC is not cooling. I am at 123 Main Street, Addison, Texas 75001. It is a residential home. I would like tomorrow between 4pm and 6pm America/Chicago. My name is Jane Customer, my number is +1 555 123 4567, and my email is jane@example.com.
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
   - Google Calendar appointment created.
   - Twilio SMS sent when SMS is enabled.
   - SendGrid email sent to the confirmed email address.
   - Application Insights has webhook, workflow, booking, CRM, confirmation, and SMS status telemetry under the correlation ID.
9. Make one test call using ZIP `99999` and verify the valid ZIP is accepted for the demo.
10. Make one test call with an invalid ZIP such as `75A01` and verify the assistant asks for the ZIP again.
