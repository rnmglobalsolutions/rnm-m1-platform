# Contact Form SendGrid Endpoint

RNM Global Solutions website contact form submissions are accepted by the M1 Function App and sent through SendGrid from the backend.

## Endpoint

```text
POST https://<FUNCTION_HOST>/api/contact/system-review
```

The endpoint is anonymous and does not use `x-rnm-api-key`.

## Allowed Browser Origins

Only browser calls from these origins are allowed:

```text
https://www.rnmglobalsolutions.com
https://rnmglobalsolutions.com
```

Azure Function App global CORS is intentionally not used because it applies to every HTTP function in the app. This endpoint handles CORS in `ContactSystemReviewFunction` only and returns CORS headers only for the two RNM website origins.

Keep the Function App CORS blade empty or unconfigured for this behavior. Adding origins there applies browser CORS behavior across every HTTP function in the app, including health, webhooks, and internal test endpoints.

## Request Body

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

Required fields:

- `fullName`
- `email`
- `workflowNeedsImprovement`

`companyWebsiteConfirm` is a honeypot field and must stay empty. If it is filled, the backend returns a safe success response and does not send email.

## Sample Curl

```bash
curl -X POST "https://<FUNCTION_HOST>/api/contact/system-review" \
  -H "Content-Type: application/json" \
  -H "Origin: https://www.rnmglobalsolutions.com" \
  -d '{
    "fullName": "Jane Founder",
    "email": "jane@example.com",
    "phone": "+15551234567",
    "preferredChannels": "Email",
    "currentTools": "CRM, spreadsheets",
    "workflowNeedsImprovement": "We miss follow-ups when leads come in after hours.",
    "website": "https://example.com",
    "companyWebsiteConfirm": ""
  }'
```

Expected success:

```json
{
  "received": true,
  "correlationId": "<correlation-id>"
}
```

## CORS Preflight

Browsers may send an `OPTIONS` preflight before the `POST`. The endpoint handles that preflight directly.

```bash
curl -i -X OPTIONS "https://<FUNCTION_HOST>/api/contact/system-review" \
  -H "Origin: https://www.rnmglobalsolutions.com" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, x-correlation-id"
```

Expected response headers include:

```text
Access-Control-Allow-Origin: https://www.rnmglobalsolutions.com
Access-Control-Allow-Methods: POST, OPTIONS
Access-Control-Allow-Headers: Content-Type, x-correlation-id
Vary: Origin
```

## SendGrid Secret Handling

The frontend never receives the SendGrid API key.

The backend reads `SENDGRID_API_KEY` from the Function App environment. In Azure, Bicep configures that app setting as a Key Vault reference:

```text
SENDGRID_API_KEY=@Microsoft.KeyVault(SecretUri=<KEY_VAULT_URI>secrets/<SENDGRID_SECRET_NAME>/)
```

The Function App managed identity resolves the secret at runtime. The SendGrid API key value must be created in Key Vault manually or by a secure pipeline.

## Telemetry

Telemetry records safe metadata only:

- endpoint
- outcome
- correlation ID
- validation result

Telemetry must not include submitted names, email addresses, phone numbers, websites, or message body text.
