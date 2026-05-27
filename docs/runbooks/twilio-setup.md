# Twilio Setup

## M1 Requirement

- One Twilio subaccount per tenant.
- One phone number minimum per tenant.
- USA tenants must use 10DLC numbers.
- Store all credentials in Azure Key Vault.
- Never commit Twilio secrets.

## Scaffold Notes

The sample tenant config stores only Key Vault secret names, not secret values.

## Required Secrets

For the sample HVAC tenant, seed these Key Vault secrets:

```bash
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name tenant-sample-hvac-twilio-account-sid --value '<TWILIO_ACCOUNT_SID>'
az keyvault secret set --vault-name <KEY_VAULT_NAME> --name tenant-sample-hvac-twilio-auth-token --value '<TWILIO_AUTH_TOKEN>'
```

The auth token is also used to validate Twilio webhook signatures.

## SMS Status Callback

Configure the Twilio Messaging status callback URL to the main Function App:

```text
https://<FUNCTION_APP_HOST>/api/tenants/sample-hvac-tenant/webhooks/twilio/sms-status
```

## RNM HVAC Demo Number

For the RNM Global Solutions HVAC Demo, use a dedicated Twilio number separate from the main RNM Global Solutions number:

```text
RNM Global Solutions main number: +13462201580
RNM Global Solutions HVAC Demo number: <NEW_DEMO_TWILIO_NUMBER>
```

Add the demo number to the RNM Global Solutions Messaging Service connected to the approved RNM A2P 10DLC campaign when the demo SMS content remains RNM-branded and transactional.

After buying the demo number, update:

```json
"smsFromPhoneNumber": "<NEW_DEMO_TWILIO_NUMBER>"
```

in `config/tenants/sample-hvac-tenant.json`.

Until the dedicated demo number is purchased and connected to the RNM campaign, the sample tenant may temporarily use the approved RNM Global Solutions number `+13462201580` for end-to-end testing.

Do not use this RNM campaign for client-branded production SMS such as `Mango HVAC` or `Ramirez HVAC`. Real client-branded SMS should use the client's own approved brand/campaign or an appropriate ISV setup.

Expected Twilio form fields include:

```text
MessageSid=SM...
MessageStatus=queued|sent|delivered|undelivered|failed
ErrorCode=<optional>
To=<recipient phone>
From=<tenant phone>
```

The endpoint:

- verifies `X-Twilio-Signature`
- rejects invalid signatures with `401`
- rejects missing `MessageSid` or `MessageStatus` with `400`
- logs `sms.status.received`
- does not log phone numbers or raw form payloads

## Manual Validation

Prefer a real Twilio callback for signature validation because the signature depends on the exact public URL and form fields.

For local or Postman testing, generate the signature with Twilio tooling or an equivalent HMAC-SHA1 calculation over:

```text
<full request URL><sorted form key/value pairs>
```

Then send it as:

```text
X-Twilio-Signature: <computed signature>
```
