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
  "consentSms": true,
  "consentEmail": false,
  "consentCapturedAt": "{{consent_captured_at_iso}}",
  "consentTextVersion": "meta-video-funnel-v1",
  "consentDisclosureText": "<exact consent text shown to the subscriber>",
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

Consent is recorded per channel:

- `consentSms`: set to `true` only when the person explicitly agreed to SMS
  follow-up. It controls follow-up SMS, class SMS, and reminders.
- `consentEmail`: set to `true` only when the person explicitly agreed to email
  follow-up. Omit it when the flow never asked.
- `consentDisclosureText`: the exact consent text shown to the person. Required
  evidence for any `true` channel grant, together with `consentTextVersion`.
- `consentCapturedAt`: ISO 8601 timestamp of the grant. Defaults to receipt time.

A grant without `consentDisclosureText` or `consentTextVersion` is **not
rejected**: the lead is stored, the channel is recorded as not granted
(`smsConsentStatus=unknown`), and M1 logs
`lead_intake.consent_evidence_missing`. That lead will not receive follow-up SMS,
so watch that event after changing a flow.

Legacy flows that send only `marketingConsentGranted` (no `consentSms`) are
still accepted: `marketingConsentGranted` is treated as the SMS grant, and it
still needs `consentDisclosureText` to count. Migrate flows to `consentSms`.
Existing `opted_out` contacts remain opted out.

M1 stores internal routing attributes on the CRM contact:

- `leadClassification`
- `classificationReasons`
- `recommendedRoute`

Classification rules are configuration, not code. The vertical file
(`config/verticals/{verticalId}.json`, block `leadClassification`) holds the
default rules: ordered rules per `funnelType` value, where the first match
assigns a tier (`hot`, `warm`, `cold`, `disqualified`) and each tier maps to a
`classification` label and a `route`. A tenant can add its own
`leadClassification` block to override individual tiers or replace a whole
funnel. Routes are an open set: any lowercase token (for example `nurture`)
is valid, as long as the tenant defines a matching
`integrations.manyChat.routingActions` entry. Opt-out always wins, produces the
`none` route, and is not configurable. Run the tenant preflight after any rule
change; it reports the `leadClassification` and `leadClassification.routes`
checks. If the
configuration cannot be loaded at runtime, the lead is still processed with the
platform default (`follow_up`) and M1 logs
`lead_intake.classification.fallback`.

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
