# Brief: platform, configuration, infrastructure, tests, and reports

## Key files

| Area | Files |
|---|---|
| Composition root | `src/RNM.Platform.Api/Program.cs` (append-only hot file) |
| Runtime settings | `src/RNM.Platform.Api/Runtime/RnmRuntimeConfiguration.cs` |
| Tenant config | `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs`, `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs`, `src/RNM.Platform.Application/Configuration/ConfigurationValidator.cs` |
| Preflight | `tools/RNM.Platform.TenantPreflight/Program.cs` |
| Secrets | `src/RNM.Platform.Infrastructure/Secrets/` |
| Telemetry | `src/RNM.Platform.Application/Observability/TelemetryEventNames.cs` (append-only), `SafeTelemetryProperties.cs` |
| Readiness | `src/RNM.Platform.Api/Functions/ReadinessFunction.cs` |
| Reports | `src/RNM.Platform.Application/Reporting/PilotReportingService.cs`, `src/RNM.Platform.Infrastructure/Reporting/AzureTableReportingReadAdapter.cs` |
| Infra | `infra/main.bicep`, `infra/modules/*.bicep`, `infra/*.bicepparam` |
| CI/CD | `.github/workflows/*.yml` |
| Decisions | `docs/adr/0004-crm-storage-table-storage-now-sql-later.md` |

## Environment facts

- .NET 10 SDK at `~/.dotnet`: `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`.
  The default `dotnet` fails with exit 145.
- Azurite is installed at `/usr/local/bin/azurite` (Node 24). Docker is not installed.
- Compare against `origin/main` after `git fetch`; the local `main` can be stale.
- Pushing `feature/**` opens a PR automatically; merging to `main` deploys to dev;
  staging and production deploy by hand.
- Two Function Apps from one build: `main` and the public `contact` app. Functions are
  turned off per app with `AzureWebJobs.<Name>.Disabled` settings in `infra/main.bicep`.
  A new anonymous function must be disabled on the app that should not expose it.

## Invariants

- Every endpoint is protected or explicitly public; every HTTP trigger is `Anonymous`
  at the host level and checks auth in code. An empty expected key rejects.
- Tenant secret names start with `rnm-tenant-`. Platform secrets: `rnm-internal-api-key`,
  `rnm-sendgrid-api-key`, `rnm-sendgrid-webhook-verification-key`,
  `rnm-email-unsubscribe-signing-key`.
- New telemetry events go in `TelemetryEventNames`; properties go through
  `SafeTelemetryProperties`.

## Known traps

- Tenant JSON is read from disk and fully validated on every call (no cache).
- Key Vault secrets are read on every webhook and every SMS (no cache).
- Reports read a tenant's whole history and filter dates in memory (item 19, deferred).
- The setting named `RNM_INTERNAL_API_KEY_SECRET_NAME` holds the key value (Key Vault
  reference), not a name.
- The only integration test is a placeholder; no test touches real Table Storage until
  the Azurite item.
- Unused scaffold types: `Domain/Calls`, `Domain/Leads`, `Domain/Appointments`,
  `Contracts/Booking`, `Contracts/Messaging`, `SharedKernel/Results`, `SharedKernel/Time`.
  Do not build on them.
