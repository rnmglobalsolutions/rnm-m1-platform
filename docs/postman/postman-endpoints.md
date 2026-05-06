# RNM Platform M1 Postman Endpoints

Import `docs/postman/RNM.Platform.M1.postman_collection.json` into Postman and create a Postman environment for real values. Do not store real secrets in the collection.

## Variables

| Variable | Purpose |
| --- | --- |
| `functionHost` | Function App host, for example `https://<app>.azurewebsites.net` |
| `tenantId` | Tenant route id, for example `sample-hvac-tenant` |
| `internalApiKey` | Internal API key used by protected test endpoints |
| `vapiWebhookSecret` | Vapi webhook secret for bearer-token testing |
| `twilioSignature` | Twilio-generated request signature |
| `correlationId` | Optional request correlation id |
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
From=+15550001000
```

Twilio signatures depend on the exact URL and form fields. Use a real Twilio webhook call or generate the signature with Twilio tooling for a valid request.

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

Public RNM website contact form endpoint for system review requests.

This endpoint is anonymous and intentionally does not use `x-rnm-api-key`. Browser CORS is enforced in `ContactSystemReviewFunction` itself, not through Function App global CORS, so the restriction applies only to this route.

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

Route-level CORS preflight for the public contact endpoint.

Headers:

```text
Origin: https://www.rnmglobalsolutions.com
Access-Control-Request-Method: POST
Access-Control-Request-Headers: Content-Type, x-correlation-id
```

Expected successful preflight response includes:

```text
Access-Control-Allow-Origin: https://www.rnmglobalsolutions.com
Access-Control-Allow-Methods: POST, OPTIONS
Access-Control-Allow-Headers: Content-Type, x-correlation-id
Vary: Origin
```

Calls from other browser origins do not receive allow headers and are rejected by the endpoint for POST requests. Non-browser callers such as Postman are not governed by browser CORS enforcement, but the endpoint still validates the `Origin` header when one is provided.

## Internal API Key Resolution

Azure resolves `RNM_INTERNAL_API_KEY_SECRET_NAME` through a Key Vault reference:

```text
@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/rnm-internal-api-key/)
```

At runtime, the app setting value is the internal API key itself. Use that value in Postman as `internalApiKey`.
