# M1 Production Release Gate

M1 is releasable when the complete revenue path works:

```text
Call -> Qualification -> Service Area -> Booking -> CRM -> SMS/Email -> Logs
```

For RNM's Meta/ManyChat campaign motion, the funnel path is a supplemental
release gate. It must not replace the primary inbound voice gate.

## Automated Gate

Run:

```bash
dotnet clean RNM.Platform.sln
dotnet build RNM.Platform.sln --configuration Release
dotnet test RNM.Platform.sln --configuration Release --no-build
dotnet run --project tools/RNM.Platform.TenantPreflight/RNM.Platform.TenantPreflight.csproj --configuration Release --no-build -- --all --environment repository --json
az bicep build --file infra/main.bicep
az bicep build-params --file infra/prod.bicepparam
```

Required:

- Zero build errors
- Zero build warnings
- All unit and integration tests pass
- All checked-in tenant and vertical manifests pass repository preflight
- Bicep and parameter validation pass
- GitHub production deployment succeeds

## Infrastructure Gate

- Dev, staging, and production are separate resource groups.
- Key Vault is used for provider credentials.
- Managed identity resolves Key Vault references.
- Main and contact Function Apps are isolated.
- Production operations alert email is configured through:

```text
RNM_OPERATIONS_ALERT_EMAIL
```

- Alerts exist for booking failures, confirmation retries, and repeated webhook authentication failures.
- `ConfirmationRetry` runs only in the main Function App.
- Failed confirmation retries reach `confirmation-retries-poison` after five attempts and are investigated.

## Tenant Gate

- No live tenant uses `sample-hvac-tenant`.
- No production tenant uses wildcard ZIP coverage.
- Every tenant has independent secrets and provider accounts.
- Readiness returns `status: "ready"` for the tenant.
- The onboarding acceptance calls pass.

## Campaign And Funnel Gate

Required when the production release includes Meta, Instagram, Facebook,
ManyChat, consultation funnels, or masterclass registration.

- Public RNM website pages are hosted outside M1.
- M1 only exposes the public funnel APIs and keeps static funnel files under
  `docs/examples/rnm-funnels` as implementation references.
- ManyChat sends leads to the tenant ManyChat webhook with a valid internal API
  key.
- ManyChat receives `leadClassification`, `recommendedRoute`,
  `classificationReasons`, and `nextAction`.
- Tenant routing actions point to the real RNM website URLs:

```text
https://rnmglobalsolutions.com/consultation
https://rnmglobalsolutions.com/masterclass/register
```

- The public website calls the browser-safe M1 funnel endpoints:

```text
POST /api/tenants/{tenantId}/funnels/consultation
POST /api/tenants/{tenantId}/funnels/masterclass/{classSessionId}/registrations
```

- Browser JavaScript never includes `x-rnm-api-key`,
  `X-RNM-Class-Registration-Secret`, provider secrets, or CRM credentials.
- The public funnel origin is explicitly allowed in tenant configuration.
- Honeypot fields are present and hidden on public forms.
- For masterclass registrations, a real `ClassSession` exists before traffic is
  sent to the page.
- The `ClassSession` contains the real `startsAt`, timezone, capacity, and
  `zoomUrl`.
- M1 does not create Zoom meetings. The stored `zoomUrl` is shared by every
  registrant in that class session.
- Confirmation templates pass tenant preflight, including campaign-aware tokens
  such as `{{campaignId}}` when used.
- A controlled end-to-end campaign test passes:

```text
Meta/Instagram/Facebook interaction
-> ManyChat webhook
-> M1 classification and nextAction
-> RNM website funnel page
-> M1 public funnel endpoint
-> CRM/contact capture
-> SMS/email confirmation
-> Logs with correlationId and tenantId
```

## Operating Gate

Daily:

- Review booking failures and poisoned confirmation messages.
- Confirm Function App health and readiness.
- Review Twilio delivery failures and SendGrid bounces.

Weekly:

- Review provider credential expiry risk.
- Review latency and booking conversion.
- Export unresolved leads requiring human follow-up.
- Confirm costs per call, SMS, email, and booking.
- Review campaign leads, consultation requests, and masterclass registrations
  that failed CRM sync or confirmation delivery.

Incident priorities:

- P1: Calls cannot book, cross-tenant exposure, or widespread authentication failure.
- P2: CRM or confirmation delivery failure with bookings still succeeding.
- P3: Isolated provider issue or incorrect tenant configuration.

## Commercial Gate

Before collecting recurring production revenue:

- Signed service agreement and support scope
- Privacy policy and data-processing terms
- Call-recording disclosure appropriate for the tenant jurisdiction
- SMS consent/STOP/HELP wording and A2P registration
- Defined onboarding fee, monthly price, usage limits, and overage policy
- Named business escalation contact

## Initial Production Target

Start with one managed paid pilot. Operate it for two weeks before onboarding multiple tenants.

Target metrics:

- At least 50 real or controlled end-to-end calls
- No duplicate bookings
- No silent lead loss
- At least 98% successful backend booking workflows when providers are healthy
- Alert response tested
- Confirmation retry tested

If the Meta/ManyChat campaign path is part of the release, also complete:

- At least 10 controlled ManyChat lead captures
- At least 5 public consultation form submissions
- At least 5 public masterclass registrations
- No browser-exposed secrets in the deployed RNM website pages
- Confirmation SMS/email delivered for each successful registration
