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
rnm-tenant-sample-hvac-vapi-webhook-secret
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

Copy the full file contents into the Vapi assistant system prompt. Do not use older prompt snippets from notes or screenshots; the canonical file includes the current safe flow for email confirmation, availability checks, caller-confirmed slot booking, onsite appointment handling, and final confirmation wording. M1 and the tool descriptions carry scheduling rules, service area eligibility, and slot payload details.

## Voice Pipeline

Configure interruption handling so callers can cut in naturally. If the assistant keeps talking over the caller or repeats the same question after a correction, review Vapi's start/stop speaking settings before changing M1.

Recommended baseline for English HVAC calls:

```json
{
  "startSpeakingPlan": {
    "smartEndpointingPlan": {
      "provider": "livekit"
    },
    "waitSeconds": 0.4
  },
  "stopSpeakingPlan": {
    "numWords": 0,
    "voiceSeconds": 0.2,
    "backoffSeconds": 1.0
  }
}
```

For Spanish or mixed-language tests, use transcription endpointing instead of English-only smart endpointing:

```json
{
  "startSpeakingPlan": {
    "transcriptionEndpointingPlan": {
      "onPunctuationSeconds": 0.1,
      "onNoPunctuationSeconds": 1.5,
      "onNumberSeconds": 0.5
    },
    "waitSeconds": 0.4
  },
  "stopSpeakingPlan": {
    "numWords": 0,
    "voiceSeconds": 0.2,
    "backoffSeconds": 1.0
  }
}
```

If interruptions still fail, check the Vapi call transcript and events for `assistant.speech.interrupted`. If that event is absent during a caller interruption, troubleshoot microphone audio, background noise, echo, transcriber language, and Vapi voice pipeline settings. If the event is present but the assistant repeats itself, tighten the system prompt and reduce response length.

## Tools

Create two custom server/API tools for the full safe booking flow.

Do not enable silent or indefinite live transfer behavior for this assistant. If a real transfer destination is configured in Vapi, it must connect quickly and fail back to caller follow-up. If no live transfer destination is configured, the assistant should acknowledge human requests immediately, confirm the callback number, and say the office will follow up.

### Availability tool

Create a custom server/API tool named:

```text
check_availability
```

Configure new assistants with `check_availability`. M1 temporarily accepts the legacy `check_hvac_availability` alias during migration.

Use this tool to check real calendar availability without booking the appointment. M1 is the source of truth for business hours, service availability, scheduling rules, and service area eligibility.

For calls the customer confirms are urgent, use `availabilityMode: earliest` to ask M1 for the fastest available slot. For normal scheduling, or unclear urgency, use `availabilityMode: preferred_window` after the caller provides a day/date and time window.

Description:

```text
Use this tool to check real HVAC appointment availability before booking. This tool does not book the appointment. M1 decides service area eligibility, business hours, scheduling rules, and available slots. Ask if the request is urgent; never infer urgency from the issue alone. If urgent is confirmed, send urgency=urgent and availabilityMode=earliest. If unclear or not urgent, send urgency=non_urgent, availabilityMode=preferred_window, and preferredTime. When availabilityFound=true, offer one returned slot and speak selectedSlotLabel exactly. When false, do not invent availability.
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
    "name": { "type": "string", "description": "Customer full name confirmed with the caller." },
    "phoneNumber": { "type": "string", "description": "Customer callback phone number confirmed with the caller. Use E.164 format when possible." },
    "email": { "type": "string", "description": "Valid customer email address read back and confirmed with the caller. Ask the caller to spell it only after the first attempt is unclear or incorrect." },
    "serviceNeed": { "type": "string", "description": "Short description of the HVAC issue or request." },
    "propertyType": { "type": "string", "description": "Residential, commercial, rental, or other property type." },
    "serviceAddress": { "type": "string", "description": "Full service address confirmed with the caller." },
    "zipCode": { "type": "string", "description": "Five digit service ZIP code. Validate format only; M1 decides service eligibility." },
    "urgency": { "type": "string", "description": "Use urgent only when the caller clearly confirms urgency. Use non_urgent when the caller says it is not urgent or does not answer clearly." },
    "availabilityMode": { "type": "string", "description": "Use earliest only for caller-confirmed urgent requests. Use preferred_window for non-urgent or unclear urgency after collecting preferredTime." },
    "preferredTime": { "type": "string", "description": "Caller preferred appointment window. Required only when availabilityMode is preferred_window. Preserve explicit ranges and AM/PM." }
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
availabilityModeUsed
qualificationState
serviceAreaState
bookingState
bookingFailureReason
availableSlotCount
selectedSlotId
selectedSlotStart
selectedSlotEnd
selectedSlotLabel
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
book_appointment
```

Configure new assistants with `book_appointment`. M1 temporarily accepts the legacy `book_hvac_appointment` alias during migration.

M1 acknowledges Vapi call lifecycle events quickly. Booking should run only after the caller has accepted one exact slot returned by `check_availability`.

After the caller accepts the exact slot, the assistant should call the booking tool immediately. If it speaks before the tool call starts, use only this short status phrase: "One moment while I confirm that appointment." Do not ask a question after that phrase. The assistant must not say the appointment is booked until M1 returns `bookingSucceeded=true`, and it must not report a problem or offer a retry unless M1 returns `bookingSucceeded=false`.

Description:

```text
Use only after the caller has accepted one exact slot returned by check_availability. This validates service area, creates or updates the CRM contact, re-checks slot availability, books the appointment, and sends confirmations. Copy selectedSlotId, selectedSlotStart, selectedSlotEnd, and selectedSlotLabel exactly from the accepted availability result. Do not invent, shorten, translate, reinterpret, or transform slot IDs, datetime values, labels, or timezone values. Set customerConfirmedSlot=true only after the caller clearly accepts the exact selectedSlotLabel. Do not claim the appointment is booked unless bookingSucceeded=true.
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
- Add one static parameter to every `apiRequest` tool (both `check_availability` and `book_appointment`): name `callId`, value `{{ call.id }}`. Vapi merges static parameters into the request body, so the direct body carries a root `callId` and M1 can link the tool calls of one call. Do not add other static body fields. Do not add `callId` to the schema's required fields or to the LLM-filled properties; Vapi fills it, not the model. Vapi documents `{{ call.id }}` for static parameters but this is not yet tested live: confirm on the first live test call (see "Trace a call by callId").
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
      "description": "Valid customer email address read back and confirmed with the caller. Ask the caller to spell it only after the first attempt is unclear or incorrect."
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
      "description": "Use urgent only when the caller clearly confirms urgency. Use non_urgent when the caller says it is not urgent or does not answer clearly."
    },
    "preferredTime": {
      "type": "string",
      "description": "Accepted appointment slot label from M1 when available. Otherwise use the caller preferred appointment window. Preserve explicit ranges, AM/PM, date wording, and timezone."
    },
    "selectedSlotId": {
      "type": "string",
      "description": "Copy exactly from the accepted firstAvailableSlot.selectedSlotId or suggestedSlots item returned by check_availability. If only slotId is present, copy slotId exactly."
    },
    "selectedSlotStart": {
      "type": "string",
      "description": "Copy exactly from the accepted firstAvailableSlot.selectedSlotStart or suggestedSlots item returned by check_availability. If only startsAt is present, copy startsAt exactly. Do not recalculate timezone."
    },
    "selectedSlotEnd": {
      "type": "string",
      "description": "Copy exactly from the accepted firstAvailableSlot.selectedSlotEnd or suggestedSlots item returned by check_availability. If only endsAt is present, copy endsAt exactly. Do not recalculate timezone."
    },
    "selectedSlotLabel": {
      "type": "string",
      "description": "Copy exactly from the accepted firstAvailableSlot.selectedSlotLabel or suggestedSlots item returned by check_availability. If only label is present, copy label exactly. Do not shorten, translate, correct, or reinterpret the weekday, date, time, or timezone."
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

## State-Mode Assistants (Life Insurance)

The base voice prompt for these assistants is `config/prompts/life-insurance-inbound-voice.md`
(the HVAC one is `config/prompts/hvac-inbound-voice.md`).

The HVAC schemas above are for ZIP-mode verticals. A vertical or tenant whose
effective service area mode is `state` (the `life-insurance` vertical, for
example Yartex) qualifies a caller by US state, not by ZIP. Its tools differ from
the HVAC schemas in three ways:

- Add a `state` property and make it required. Remove `zipCode`. `serviceAddress`
  is not needed for state mode.
- Do not add `leadSource` or `campaignId`. Callers cannot know them. M1 fills
  them with `InboundVoice` and `inbound-voice`. If a tool does send a value, the
  value it sends wins.
- Keep the other fields the vertical's `qualificationFields` list (`name`,
  `phoneNumber`, `email`, `intent`, `preferredTime`) and, for the booking tool,
  the same `selectedSlot*` and `customerConfirmedSlot` fields as the HVAC booking
  schema.

`state` property:

```json
"state": {
  "type": "string",
  "description": "Two-letter USPS state code, for example TX. Ask the caller which state they live in and convert it to the two-letter code. Do not guess from an address or area code."
}
```

Availability tool parameters for a state-mode assistant:

```json
{
  "type": "object",
  "properties": {
    "name": { "type": "string", "description": "Caller full name confirmed with the caller." },
    "phoneNumber": { "type": "string", "description": "Callback phone number confirmed with the caller. Use E.164 format when possible." },
    "email": { "type": "string", "description": "Valid email address read back and confirmed with the caller." },
    "state": { "type": "string", "description": "Two-letter USPS state code, for example TX." },
    "intent": { "type": "string", "description": "Short description of what the caller wants." },
    "preferredTime": { "type": "string", "description": "Caller preferred appointment window. Preserve explicit ranges and AM/PM." }
  },
  "required": ["name", "phoneNumber", "email", "state", "intent"]
}
```

M1 normalizes the state (trim, remove dots, two-letter code or English full
name). The tool should still ask for the two-letter code. M1 compares it with the
tenant `serviceArea.states` list and never parses a state from an address.

Values of `serviceAreaState` in the tool result for state mode:

| `serviceAreaState` | Meaning | Assistant behavior |
|---|---|---|
| `InServiceArea` | State is on the tenant list. | Continue with availability or booking. |
| `OutOfServiceArea` | State is valid but not on the list. | Do not offer a slot. Say the team cannot book this state and use the tenant referral message. |
| `MissingState` | No state was sent (`qualificationState` is `MissingRequiredFields`). | Ask "Which state do you live in?" and call the tool again. |
| `InvalidState` | The value is not a US state (`qualificationState` is `InvalidInput`). | Ask again for the two-letter state code, for example TX. |
| `NeedsEscalation` | The tenant has no states configured. | Offer human follow-up. This is a configuration problem: run the tenant preflight. |
| `NotRequired` | Mode is `none`; no state check (`qualificationState` is `Qualified`). | Continue; do not ask for a state. |

Spanish state names are not normalized. The assistant converts them to the
two-letter code before calling the tool.

## Expected Tool Result

Availability tool result:

```json
{
  "results": [
    {
      "name": "check_availability",
      "toolCallId": "<tool-call-id>",
      "result": "{\"accepted\":true,\"processed\":true,\"availabilityFound\":true,\"requestedWindowAvailable\":null,\"availabilityModeUsed\":\"earliest\",\"qualificationState\":\"Qualified\",\"serviceAreaState\":\"InServiceArea\",\"bookingState\":\"AvailabilityFound\",\"bookingFailureReason\":null,\"availableSlotCount\":1,\"timezone\":\"America/Chicago\",\"firstAvailableSlot\":{\"slotId\":\"slot-1\",\"startsAt\":\"2026-05-29T16:00:00.0000000-05:00\",\"endsAt\":\"2026-05-29T16:30:00.0000000-05:00\",\"label\":\"Friday, May 29, 2026 at 4:00 PM America/Chicago\",\"selectedSlotId\":\"slot-1\",\"selectedSlotStart\":\"2026-05-29T16:00:00.0000000-05:00\",\"selectedSlotEnd\":\"2026-05-29T16:30:00.0000000-05:00\",\"selectedSlotLabel\":\"Friday, May 29, 2026 at 4:00 PM America/Chicago\"},\"selectedSlotId\":\"slot-1\",\"selectedSlotStart\":\"2026-05-29T16:00:00.0000000-05:00\",\"selectedSlotEnd\":\"2026-05-29T16:30:00.0000000-05:00\",\"selectedSlotLabel\":\"Friday, May 29, 2026 at 4:00 PM America/Chicago\",\"messageForAssistant\":\"The earliest available appointment is Friday, May 29, 2026 at 4:00 PM America/Chicago. Ask the caller if that works for them before booking.\"}"
    }
  ]
}
```

When Vapi sends a `toolCallList` webhook envelope, the RNM webhook returns Vapi's tool result shape:

```json
{
  "results": [
    {
      "name": "book_appointment",
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

For availability, the assistant must speak the returned top-level `selectedSlotLabel` exactly. Do not recalculate or reinterpret the weekday, date, year, time, or timezone from `selectedSlotStart` or `firstAvailableSlot.startsAt`.

For troubleshooting, check `qualificationState`, `bookingState`, `bookingFailureReason`, and `availableSlotCount`. If `qualificationState` is not `Qualified`, the issue is missing/invalid caller details, not calendar availability. If `qualificationState` is `Qualified`, `bookingState` is `Failed`, and `bookingFailureReason` is `AdapterFailure`, the issue is the booking availability provider or credentials. If `qualificationState` is `Qualified`, `bookingState` is `NoAvailability`, and `availableSlotCount` is `0`, the issue is real calendar availability.

## Demo Call Script

Use an urgent in-service-area example:

```text
My AC is not cooling and this is urgent. I am at 123 Main Street, Addison, Texas 75001. It is a residential home. My name is Jane Customer, my number is +1 555 123 4567, and my email is jane@example.com.
```

Expected urgent behavior:

```text
The assistant acknowledges urgency, collects required contact and service details, calls check_availability with earliest behavior, offers the first returned slot, and books only after the caller accepts that exact slot.
```

Use a non-urgent in-service-area example:

```text
I need AC maintenance. This is not urgent. I am at 123 Main Street, Addison, Texas 75001. It is a residential home. I would like tomorrow between 4pm and 6pm America/Chicago. My name is Jane Customer, my number is +1 555 123 4567, and my email is jane@example.com.
```

Expected non-urgent behavior:

```text
The assistant asks or confirms urgency, treats unclear urgency as non-urgent, collects a preferred day/time window, calls check_availability with urgency=non_urgent and availabilityMode=preferred_window, and includes preferredTime.
```

Use another valid ZIP example:

```text
My heater is not working. I am at 456 Oak Street, Dallas, Texas 99999. I would like next Monday morning.
```

## Trace a call by callId

Use this to follow one phone call from the availability check to the booking, CRM, and confirmations.

What carries `callId`:

- Every custom event logged while handling an authenticated Vapi webhook, after the payload is parsed, has a `callId` custom dimension. M1 reads it from the payload (`callId`, `call.id`, or `message.callId`; `call` can be at the root or inside `message`).
- The first events of each request do not have it: `webhook.received`, `tenant.resolved`, and the signature validation events (`webhook.validation_succeeded`, `webhook.validation_failed`), plus an `api.request_failed` for an unreadable payload. M1 reads `callId` only after the webhook secret is validated. KQL 1 reaches these events through `correlationId`.
- The correlation ID stays one per webhook request (or the `x-correlation-id` header). One call has several correlation IDs, for example one for the availability check and one for the booking. `callId` ties them together.
- Events from later invocations (confirmation retries, follow-ups, reminders) and Function App traces do not carry `callId`. Use the correlation ID stored with the booking or contact for those.

Where to find the `callId`: Vapi dashboard, Call Logs, open the call. The call ID is shown at the top of the call detail (UUID).

Run these in Application Insights, Logs, for the Function App resource. Set the time range to cover the call.

KQL 1, full chain of a call. Replace `<CALL_ID>`:

```kusto
let targetCallId = "<CALL_ID>";
let lookback = 3d;
let correlationIds = customEvents
    | where timestamp > ago(lookback)
    | where tostring(customDimensions.callId) == targetCallId
    | distinct correlationId = tostring(customDimensions.correlationId)
    | where isnotempty(correlationId);
customEvents
| where timestamp > ago(lookback)
| where tostring(customDimensions.correlationId) in (correlationIds)
| project
    timestamp,
    name,
    callId = tostring(customDimensions.callId),
    correlationId = tostring(customDimensions.correlationId),
    tenantId = tostring(customDimensions.tenantId),
    toolName = tostring(customDimensions.toolName),
    providerEventType = tostring(customDimensions.providerEventType),
    outcome = tostring(customDimensions.outcome),
    qualificationState = tostring(customDimensions.qualificationState),
    serviceAreaState = tostring(customDimensions.serviceAreaState),
    bookingState = tostring(customDimensions.bookingState),
    bookingFailureReason = tostring(customDimensions.bookingFailureReason),
    crmState = tostring(customDimensions.crmState),
    confirmationState = tostring(customDimensions.confirmationState)
| order by timestamp asc
```

Rows with an empty `callId` are the early events of the same requests. Rows where `outcome` or a state column is empty are events that do not report that field.

KQL 2, recent callIds for a tenant. Replace `<TENANT_ID>`:

```kusto
customEvents
| where timestamp > ago(1d)
| where name == "voice.tool_call_received"
| where tostring(customDimensions.tenantId) == "<TENANT_ID>"
| where isnotempty(tostring(customDimensions.callId))
| summarize
    firstSeen = min(timestamp),
    lastSeen = max(timestamp),
    toolCalls = count()
    by callId = tostring(customDimensions.callId)
| order by lastSeen desc
```

KQL 3, coverage check: how many `voice.*` events have a `callId`, by event name.

```kusto
customEvents
| where timestamp > ago(1d)
| where name startswith "voice."
| summarize
    withCallId = countif(isnotempty(tostring(customDimensions.callId))),
    withoutCallId = countif(isempty(tostring(customDimensions.callId)))
    by name
| order by name asc
```

Expected: `voice.tool_call_received`, `voice.tool_call_responded`, `voice.event_processed`, and `voice.event_unsupported` have `withoutCallId = 0` for Vapi calls. A non-zero `withoutCallId` means Vapi did not send a call ID in that message type, or the `apiRequest` static parameter is missing. Outbound campaign events are not part of this check.

### Live-call check (Raisel)

Run once after this change is deployed:

1. Make one test call that reaches an availability check and a booking.
2. Copy the `callId` from the Vapi dashboard Call Logs.
3. Run KQL 1 with that `callId`.
4. Confirm `callId` appears on the `voice.tool_call_received` and `voice.tool_call_responded` events for both tools, and that the availability and booking requests have different `correlationId` values but the same `callId`.
5. Confirm `callId` appears on the `end-of-call-report` event (`voice.event_unsupported` or `voice.event_processed`) and on any `status-update` events. If a message type has none, note which one; the Vapi docs do not confirm `call` on every message type.
6. For `apiRequest` tools, confirm the `callId` parameter arrives: the tool-call events show the call's `callId`. If they do not, check the `callId` = `{{ call.id }}` static parameter on that tool in Vapi and report the result.
7. Run KQL 3 and confirm there are no `voice.*` events without `callId` from this call.

## Verification

Before client demos:

1. Confirm dev deployment passed.
2. Confirm `rnm-tenant-sample-hvac-vapi-webhook-secret` exists in Key Vault.
3. Confirm `rnm-tenant-sample-hvac-google-calendar-credentials` exists in Key Vault and includes either refresh credentials or a valid short-lived `accessToken`.
4. Confirm the Function App storage account is available for the `AzureTable` CRM/contact ledger.
5. Confirm `rnm-sendgrid-api-key` exists and SendGrid sender/domain is verified.
6. Confirm Twilio SMS can be sent, or tell demo viewers SMS is pending 10DLC campaign approval.
7. Confirm `communication.smsFromPhoneNumber` in `config/tenants/sample-hvac-tenant.json` has been replaced with the dedicated demo Twilio number added to the RNM Global Solutions Messaging Service/campaign.
8. Make one in-service-area test call and verify:
   - Azure Table contact record created or updated.
   - Google Calendar appointment created with the service address visible as the event location.
   - Twilio SMS sent when SMS is enabled.
   - SendGrid email sent to the confirmed email address.
   - Application Insights has webhook, workflow, booking, CRM, confirmation, and SMS status telemetry for the call. Use KQL 1 in "Trace a call by callId" with the call's `callId`; one call has several correlation IDs, one per webhook request.
9. Make one test call using ZIP `99999` and verify the valid ZIP is accepted for the demo.
10. Make one test call with an invalid ZIP such as `75A01` and verify the assistant asks for the ZIP again.
11. Make one call where the issue sounds serious but the caller does not confirm urgency; verify the assistant uses `urgency=non_urgent` and asks for a preferred window.
12. Make one urgent test call and verify the assistant does not ask for a preferred appointment window before checking earliest availability.
13. Ask for a human during a failed booking path and verify the assistant responds immediately with callback follow-up instead of waiting silently.
