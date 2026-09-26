# Meta + ManyChat Video Funnel

This runbook launches the first production slice for the Yartex insurance video
funnel without adding scoring, dashboards, or Loom attribution as hard
dependencies.

## Phase 1 goal

Capture qualified Meta traffic through ManyChat and send a normalized lead
payload into M1.

```text
Facebook / Instagram
  -> comment keyword, ad button, or DM keyword
  -> ManyChat filter questions
  -> correct video
  -> short form and consent
  -> M1 ManyChat lead intake
  -> CRM contact, timeline, business notification, follow-up
```

Status: implemented through the existing ManyChat lead intake endpoint and the
`yartex` tenant notification templates.

## Entry points

Start with two public keywords only:

| Funnel | Keyword | Campaign id |
| --- | --- | --- |
| Financial education | `PROTECCION` | `financial-video-v1` |
| Business opportunity | `OPORTUNIDAD` | `business-opportunity-video-v1` |

Accept these trigger types in `attributes.triggerType`:

- `comment_keyword`
- `ad_button`
- `dm_keyword`

Accept these channels in `attributes.metaChannel`:

- `facebook`
- `instagram`

## ManyChat flow

1. Public reel or ad asks the user to comment a keyword or click the message
   button.
2. ManyChat sends an initial DM and asks for intent:
   - `financial_education`
   - `business_opportunity`
   - `both`
3. ManyChat asks two to four filter questions.
4. ManyChat sends the matching video link.
5. User clicks `Ya vi el video`, `Quiero una consulta`, or `Quiero la clase`.
6. ManyChat captures SMS/email consent.
7. ManyChat sends the External Request to M1.

Do not require a long financial assessment before the webhook. Keep the first
pass short enough that a high-intent lead can continue immediately.

## Financial education fields

Use these attribute names for the `PROTECCION` path:

```json
{
  "funnelType": "financial_education",
  "metaChannel": "instagram",
  "triggerType": "comment_keyword",
  "keyword": "PROTECCION",
  "primaryGoal": "family_protection",
  "timeline": "under_30_days",
  "currentProtection": "employer_only",
  "monthlyRange": "500_1000",
  "state": "TX",
  "requestedNextStep": "consultation"
}
```

Allowed `requestedNextStep` values:

- `consultation`
- `master_class`
- `follow_up`

## Business opportunity fields

Use these attribute names for the `OPORTUNIDAD` path:

```json
{
  "funnelType": "business_opportunity",
  "metaChannel": "facebook",
  "triggerType": "ad_button",
  "keyword": "OPORTUNIDAD",
  "primaryGoal": "extra_income",
  "timeline": "under_30_days",
  "experienceLevel": "new_to_industry",
  "weeklyAvailability": "5_10_hours",
  "state": "FL",
  "requestedNextStep": "master_class"
}
```

## External Request

Use the existing endpoint from `manychat-lead-intake.md`:

```text
POST /api/tenants/yartex/webhooks/manychat/leads
```

Headers:

```text
Content-Type: application/json
X-RNM-ManyChat-Secret: <tenant secret>
```

Body example:

```json
{
  "externalEventId": "financial-video-v1-{{contact.id}}-{{submission_id}}",
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

`externalEventId` must remain stable when ManyChat retries the same submission.
Use a unique submission id only when the same contact is allowed to submit the
same campaign more than once.

M1 returns the internal routing decision in the response:

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

ManyChat can branch on `nextAction.route` after the External Request:

- `consultation`: show the 1:1 scheduling link.
- `master_class`: show the class registration link.
- `follow_up`: keep the user in the education/follow-up path.
- `none`: stop promotional routing and notify the team if needed.

Use `nextAction.type` to decide how to render the response:

- `link`: send `nextAction.message`, then show a button with
  `nextAction.label` and `nextAction.url`.
- `message`: send `nextAction.message` and keep the contact in the current
  ManyChat flow.
- `none`: do not send a promotional CTA.

The action values are configured per tenant under
`integrations.manyChat.routingActions`, keyed by route (`consultation`,
`master_class`, `follow_up`, `none`, or any other route the classification
rules produce, such as `nurture`). The older keys `masterClass` and `followUp`
are still accepted. The tenant preflight check `leadClassification.routes`
fails when a route the rules can produce has no routing action, or when
`master_class` is reachable without a `classes` configuration.

For `yartex`, the intended production pages are:

- Consultation: `https://rnmglobalsolutions.com/consultation`
- Master class registration: `https://rnmglobalsolutions.com/masterclass/register`

Both pages must exist before paid traffic is sent to this funnel.

## Public page endpoints

Status: backend endpoints are implemented. Static page examples live in
`docs/examples/rnm-funnels/` as reference only; M1 does not host the RNM website.

The public pages must not call the ManyChat webhook or class-registration
webhook directly because those endpoints require secrets. Browser JavaScript
must use these public funnel endpoints instead.

### Consultation page

Page URL:

```text
https://rnmglobalsolutions.com/consultation
```

Browser request:

```http
POST /api/tenants/yartex/funnels/consultation
Content-Type: application/json
Origin: https://rnmglobalsolutions.com
```

Body:

```json
{
  "submissionId": "browser-generated-uuid",
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-video-v1",
  "funnelType": "financial_education",
  "primaryGoal": "family_protection",
  "timeline": "under_30_days",
  "state": "TX",
  "currentProtection": "employer_only",
  "monthlyRange": "500_1000",
  "consentSms": true,
  "consentEmail": true,
  "companyWebsiteConfirm": ""
}
```

Expected success:

```json
{
  "received": true,
  "leadClassification": "ready_for_consultation",
  "recommendedRoute": "consultation",
  "classificationReasons": "requested_consultation",
  "correlationId": "..."
}
```

### Master class page

Page URL:

```text
https://rnmglobalsolutions.com/masterclass/register
```

Before the page is live, create a published class session in M1 and store its
`sessionId` in the page configuration.

Browser request:

```http
POST /api/tenants/yartex/funnels/masterclass/{sessionId}/registrations
Content-Type: application/json
Origin: https://rnmglobalsolutions.com
```

Body:

```json
{
  "customerName": "Jane Lead",
  "customerPhoneNumber": "+15551234567",
  "customerEmail": "jane@example.com",
  "campaignId": "financial-video-v1",
  "funnelType": "financial_education",
  "primaryGoal": "family_protection",
  "timeline": "under_30_days",
  "state": "TX",
  "consentSms": true,
  "consentEmail": true,
  "companyWebsiteConfirm": ""
}
```

Expected success:

```json
{
  "registered": true,
  "duplicate": false,
  "sessionId": "financial-education-next",
  "classTitle": "Financial Education Master Class",
  "startsAt": "2027-07-15T23:00:00+00:00",
  "timeZone": "America/Chicago",
  "correlationId": "..."
}
```

### Frontend security rules

- Do not embed `X-RNM-ManyChat-Secret`.
- Do not embed `X-RNM-Class-Registration-Secret`.
- Do not embed `x-rnm-api-key`.
- Keep `companyWebsiteConfirm` as a hidden honeypot field.
- Generate a unique `submissionId` for consultation form submissions.
- Keep forms short; do not collect SSN, medical details, exact income, exact
  debts, policy numbers, or detailed assets.

### Static page package

The static package contains:

```text
docs/examples/rnm-funnels/config.js
docs/examples/rnm-funnels/consultation/index.html
docs/examples/rnm-funnels/masterclass/register/index.html
docs/examples/rnm-funnels/assets/funnel.js
docs/examples/rnm-funnels/assets/styles.css
```

Before deployment, update `config.js`:

```js
window.RNM_FUNNEL_CONFIG = {
  API_BASE_URL: "https://<function-app>.azurewebsites.net/api",
  TENANT_ID: "yartex",
  MASTERCLASS_SESSION_ID: "<published-session-id>"
};
```

## Consent rules

Set `marketingConsentGranted` to `true` only after explicit consent. When it is
`true`, M1 requires both:

- `consentCapturedAt`
- `consentTextVersion`

Suggested consent copy:

```text
I agree Yartex may contact me by SMS/email about my request. Message/data rates may apply. Reply STOP to opt out.
```

If the user declines consent, send `marketingConsentGranted=false` and keep the
conversation inside the active Meta messaging window.

## Compliance boundaries

- Do not show a public readiness score.
- Do not collect SSN, policy numbers, medical details, exact income, exact
  debts, or detailed assets in Phase 1.
- Do not combine financial education and recruiting claims in the same video.
- Do not claim guaranteed income, replacement salary, guaranteed returns, or
  universal tax benefits.

## Verification

1. Submit a `financial-video-v1` test lead.
2. Verify one CRM contact with `campaignId=financial-video-v1`.
3. Verify these contact attributes are present:
   - `funnelType`
   - `metaChannel`
   - `triggerType`
   - `keyword`
   - `primaryGoal`
   - `timeline`
   - `requestedNextStep`
4. Verify M1 added internal classification attributes:
   - `leadClassification`
   - `classificationReasons`
   - `recommendedRoute`
5. Verify `lead.intake.received` and consent timeline events.
6. Verify the business SMS/email includes campaign, funnel, classification, and
   route context.
7. Repeat the same `externalEventId` and verify `duplicate=true`.

## Next phases

Phase 2 is implemented inside `InboundLeadIntakeService` and adds internal
classification:

- `ready_for_consultation`
- `education_needed`
- `follow_up`
- `not_qualified`
- `opted_out`

Phase 3 routes by classification to consultation, master class, or follow-up.

## Phase 6: Master Class Session Setup

Status: M1 has the internal session upsert endpoint and an operator script. A
real session still requires the actual class date/time, Zoom join URL, deployed
Function host, and internal API key.

Create the Zoom meeting manually first, then run:

```bash
chmod +x ./scripts/upsert-rnm-masterclass-session.sh

export RNM_FUNCTION_HOST="https://<function-app>.azurewebsites.net"
export RNM_INTERNAL_API_KEY="<internal-api-key>"
export RNM_MASTERCLASS_SESSION_ID="financial-education-next"
export RNM_MASTERCLASS_STARTS_AT="2027-07-15T23:00:00Z"
export RNM_MASTERCLASS_ENDS_AT="2027-07-16T00:00:00Z"
export RNM_MASTERCLASS_ZOOM_URL="https://zoom.us/j/REPLACE_ME"
export RNM_MASTERCLASS_CAMPAIGN_ID="financial-video-v1"

./scripts/upsert-rnm-masterclass-session.sh
```

Required values:

- `RNM_FUNCTION_HOST`
- `RNM_INTERNAL_API_KEY`
- `RNM_MASTERCLASS_STARTS_AT`
- `RNM_MASTERCLASS_ZOOM_URL`

Optional values:

- `RNM_TENANT_ID`, default `yartex`
- `RNM_MASTERCLASS_SESSION_ID`, default `financial-education-next`
- `RNM_MASTERCLASS_TITLE`, default `Financial Education Master Class`
- `RNM_MASTERCLASS_ENDS_AT`
- `RNM_MASTERCLASS_TIME_ZONE`, default `America/Chicago`
- `RNM_MASTERCLASS_CAPACITY`, default `100`
- `RNM_MASTERCLASS_CAMPAIGN_ID`, default `financial-video-v1`
- `RNM_MASTERCLASS_STATUS`, default `published`

After the script succeeds, copy the session id into the separate RNM website
project's funnel config:

```js
window.RNM_FUNNEL_CONFIG = {
  API_BASE_URL: "https://<function-app>.azurewebsites.net/api",
  TENANT_ID: "yartex",
  MASTERCLASS_SESSION_ID: "financial-education-next"
};
```

Do not publish the `/masterclass/register` page until the session exists and is
`published`.
