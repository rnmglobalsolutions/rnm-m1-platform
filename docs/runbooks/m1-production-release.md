# M1 Production Release Gate

M1 is releasable when the complete revenue path works:

```text
Call -> Qualification -> Service Area -> Booking -> CRM -> SMS/Email -> Logs
```

## Automated Gate

Run:

```bash
dotnet clean RNM.Platform.sln
dotnet build RNM.Platform.sln --configuration Release
dotnet test RNM.Platform.sln --configuration Release --no-build
az bicep build --file infra/main.bicep
az bicep build-params --file infra/prod.bicepparam
```

Required:

- Zero build errors
- Zero build warnings
- All unit and integration tests pass
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
- Readiness returns `200` for the tenant.
- The onboarding acceptance calls pass.

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
