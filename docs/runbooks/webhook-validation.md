# Webhook Validation

Every public webhook must be authenticated before its payload is processed.

## Vapi

`VapiInboundWebhookFunction` accepts the tenant-specific secret through one of the supported Vapi mechanisms:

- `Authorization: Bearer <secret>`
- `X-Vapi-Secret: <secret>`
- `x-signature` HMAC-SHA256

The expected secret is resolved from the tenant configuration and Azure Key Vault. Invalid requests return `401` and emit:

```text
webhook.validation_failed
security.auth_failed
```

## Twilio

`TwilioSmsStatusWebhookFunction` validates `X-Twilio-Signature` with the tenant Twilio auth token and the exact public callback URL/form values.

Invalid requests return `401` and are logged without exposing the signature, auth token, or message contents.

## Production Check

Before go-live:

1. Send one valid Vapi tool call and confirm `webhook.validation_succeeded`.
2. Send one request with an invalid Vapi secret and confirm `401`.
3. Trigger one real Twilio status callback and confirm `sms.status.received`.
4. Send one invalid Twilio signature and confirm `401`.
5. Confirm the webhook security alert fires after repeated invalid requests.
