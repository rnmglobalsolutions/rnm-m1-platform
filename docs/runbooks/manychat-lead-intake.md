# ManyChat Lead Intake

This runbook connects a ManyChat automation to M1 without exposing the platform
internal API key. The endpoint is tenant-scoped and intended for generic lead
capture, not class registration.

## 1. Tenant configuration

Enable the integration in `config/tenants/<tenantId>.json`:

```json
"secretNames": {
  "manyChatWebhookSecret": "tenant-<tenantId>-manychat-webhook-secret"
},
"integrations": {
  "manyChat": {
    "enabled": true,
    "scheduleFollowUp": true,
    "maxRequestsPerMinute": 120
  }
}
```

`scheduleFollowUp=true` invokes the tenant's existing sequence whose trigger is
`followup.required`. Set it to `false` when the source should only create/update
the contact and notify the business.

## 2. Key Vault secret

Generate a random secret of at least 32 bytes and store it in the environment's
Key Vault using the exact name configured above. Example:

```bash
openssl rand -base64 48
az keyvault secret set \
  --vault-name <vault-name> \
  --name tenant-yartex-manychat-webhook-secret \
  --value '<generated-secret>'
```

Do not reuse `x-rnm-api-key`, a Vapi secret, or a Twilio auth token. Run tenant
preflight/readiness after adding the secret.

## 3. ManyChat External Request

Create an External Request action after the form or qualification step.

- Method: `POST`
- URL: `https://<function-app>.azurewebsites.net/api/tenants/yartex/webhooks/manychat/leads`
- Header `Content-Type`: `application/json`
- Header `X-RNM-ManyChat-Secret`: the tenant secret

Body example (replace ManyChat field syntax with the corresponding bot fields):

```json
{
  "externalEventId": "financial-video-v1-{{contact.id}}",
  "externalContactId": "{{contact.id}}",
  "customerName": "{{contact.name}}",
  "customerPhoneNumber": "{{contact.phone}}",
  "customerEmail": "{{contact.email}}",
  "campaignId": "financial-video-v1",
  "marketingConsentGranted": true,
  "consentCapturedAt": "{{consent_captured_at_iso}}",
  "consentTextVersion": "meta-video-funnel-v1",
  "attributes": {
    "funnelType": "financial_education",
    "metaChannel": "instagram",
    "triggerType": "comment_keyword",
    "keyword": "PROTECCION",
    "primaryGoal": "{{primary_goal}}",
    "timeline": "{{timeline}}",
    "currentProtection": "{{current_protection}}",
    "monthlyRange": "{{monthly_range}}",
    "state": "{{state}}",
    "requestedNextStep": "{{requested_next_step}}"
  }
}
```

The placeholders above are illustrative; select the actual ManyChat system and
custom fields in its request editor. `externalEventId` is mandatory and must
remain the same when ManyChat retries the same delivery. The campaign plus
subscriber id is a practical key when each subscriber should enter a campaign
only once. Use a separately generated/stored submission id when repeat entries
to the same campaign are valid. M1 hashes the id before storage; the raw request
is not stored in the receipt table.

Only set `marketingConsentGranted` to `true` when the person explicitly agreed
to future marketing contact. In that case, send an ISO 8601 consent timestamp
and a stable version/name for the displayed consent text. Missing evidence is
rejected. Existing `opted_out` contacts remain opted out.

M1 stores internal routing attributes on the CRM contact:

- `leadClassification`
- `classificationReasons`
- `recommendedRoute`

When the video funnel should send the user to a website page, ManyChat should
use the `nextAction` fields returned by this endpoint. The website pages then
submit through the public funnel endpoints documented in
`meta-manychat-video-funnel.md`; browser JavaScript must not call this ManyChat
webhook because it requires `X-RNM-ManyChat-Secret`.

## 4. Response handling

- `200`: processed, or already completed as a duplicate.
- `202`: the same event is currently being processed. Do not create a new id.
- `400`: invalid body, identifier, phone/email, attribute, or consent evidence.
- `401`: missing/invalid tenant webhook secret.
- `403`: ManyChat is disabled for the tenant.
- `429`: tenant rate limit exceeded; retry later with the same event id.
- `503`: dependency unavailable; retry with the same event id.

Configure retries only for `202`, `429`, and `5xx`. Do not generate a new
`externalEventId` during a retry.

Successful responses include routing context:

```json
{
  "accepted": true,
  "duplicate": false,
  "processing": false,
  "followUpRequested": true,
  "businessNotificationQueued": true,
  "leadClassification": "ready_for_consultation",
  "recommendedRoute": "consultation",
  "classificationReasons": "requested_consultation",
  "nextAction": {
    "route": "consultation",
    "type": "link",
    "label": "Schedule a 1:1 consultation",
    "url": "https://rnmglobalsolutions.com/consultation",
    "message": "Based on your answers, the best next step is a short 1:1 consultation."
  },
  "tenantId": "yartex",
  "correlationId": "..."
}
```

## 5. Production verification

1. Deploy the tenant config and application.
2. Add the Key Vault secret and run tenant preflight/readiness.
3. Send the Postman `ManyChat Lead Intake` request.
4. Verify one contact in `RnmContacts` with `leadSource=ManyChat`, campaign,
   external source id, consent status, submitted attributes, classification,
   and recommended route.
5. Verify `lead.intake.received` and `consent.marketing.external_*` in
   `RnmTimelineEvents`.
6. Verify business SMS/email messages are queued and delivered.
7. Verify follow-up rows exist when `scheduleFollowUp=true`.
8. Repeat the same request with the same `externalEventId`; verify `duplicate=true`
   and no duplicate timeline, follow-up, or notification.
9. Send an invalid secret and verify `401` with no CRM write.
10. Test an existing opted-out contact and verify the state remains `opted_out`.

## Operational notes

The in-memory request counter is a per-Function-instance pilot guard, not a
globally distributed quota. The per-tenant secret and durable receipt table are
the primary security and replay controls. Rotate a compromised secret in Key
Vault and update the ManyChat action immediately.
