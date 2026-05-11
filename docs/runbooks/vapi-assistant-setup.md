# Vapi Assistant Setup

Use this runbook to configure the RNM HVAC Co. demo assistant for M1.

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
RNM HVAC Co. Inbound Booking Assistant
```

First message:

```text
Thank you for calling RNM HVAC Co. How can I help with your heating or cooling system today?
```

System prompt:

```text
You are the inbound phone assistant for RNM HVAC Co.

Your goal is to qualify HVAC callers and book a real appointment when appropriate.

Tone:
- Professional, calm, concise, and efficient.
- Warm, but not overly casual.
- Do not sound salesy or pushy.

You must collect these fields before booking:
- Customer full name
- Best phone number
- Service need
- Property type
- Service address
- ZIP code
- Urgency
- Preferred appointment time window
- Email address, optional but ask once

Service area:
- The current demo service ZIP codes are 75001 and 75002.
- If the caller is outside the service area, apologize briefly and say the office can follow up.
- Do not promise service outside the configured area.

Booking behavior:
- After collecting the required fields, call the `book_hvac_appointment` tool.
- Do not claim an appointment is booked until the tool result indicates `bookingSucceeded: true`.
- If booking succeeds, confirm the appointment and tell the caller they will receive confirmation by SMS and, if an email was provided, email.
- If booking fails or there is no availability, apologize and offer to have the office follow up.

Rules:
- Do not invent prices, discounts, technician names, policies, or availability.
- Do not provide technical diagnosis beyond basic triage.
- Escalate to a human follow-up if the caller asks for a person, is upset, has a safety concern, or the situation is unclear.
- Keep responses short. Ask one or two questions at a time.
```

## Tool

Create a custom server/API tool named:

```text
book_hvac_appointment
```

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
      "description": "Customer callback phone number in E.164 format when possible."
    },
    "email": {
      "type": "string",
      "description": "Customer email address. Optional."
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
      "description": "Caller preferred appointment window."
    }
  },
  "required": [
    "name",
    "phoneNumber",
    "serviceNeed",
    "propertyType",
    "serviceAddress",
    "zipCode",
    "urgency",
    "preferredTime"
  ]
}
```

## Expected Tool Result

The RNM webhook returns Vapi's tool result shape:

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

The assistant should treat `bookingSucceeded: true` as booked. Any other value means the assistant should offer human follow-up instead of claiming a booking.

## Demo Call Script

Use an in-service-area example:

```text
My AC is not cooling. I am at 123 Main Street, Addison, Texas 75001. It is a residential home. I would like tomorrow afternoon if possible. My name is Jane Customer, my number is +1 555 123 4567, and my email is jane@example.com.
```

Use an out-of-service-area example:

```text
My heater is not working. I am at 456 Oak Street, Dallas, Texas 99999.
```

## Verification

Before client demos:

1. Confirm dev deployment passed.
2. Confirm `tenant-sample-hvac-vapi-webhook-secret` exists in Key Vault.
3. Confirm `tenant-sample-hvac-ghl-api-key` includes `accessToken`, `locationId`, and `calendarId`.
4. Confirm `rnm-dev-sendgrid-api-key` exists and SendGrid sender/domain is verified.
5. Confirm Twilio SMS can be sent, or tell demo viewers SMS is pending 10DLC campaign approval.
6. Make one test call and verify:
   - GHL contact created or updated.
   - GHL appointment created.
   - SendGrid email sent when an email is provided.
   - Application Insights has the correlation ID events.
