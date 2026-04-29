# RNM Platform M1

This repository contains the M1 foundation for the RNM Global Solutions platform.

The master strategy document provides business, positioning, and role context. `AGENTS.md` controls the active implementation scope.

## Scope

M1 is limited to the inbound HVAC booking wedge:

Call -> Qualification -> Service area validation -> Booking -> CRM -> SMS/Email -> Logs

## Included In This Scaffold

- .NET solution and project structure
- .NET 10 target framework
- Azure Functions isolated-worker API scaffold
- Azure Functions API project shell
- Domain, Application, Contracts, Infrastructure, and SharedKernel projects
- Unit and integration test project shells
- Core adapter interfaces:
  - `ICrmAdapter`
  - `IBookingAdapter`
  - `ISmsSender`
  - `IEmailSender`
- Basic DTOs for voice, CRM, booking, messaging, and webhooks
- Tenant and vertical configuration examples
- Safe app settings examples without secrets
- Bicep scaffold placeholders
- Runbook and ADR notes

## Not Included Yet

- Provider API calls
- Dashboard or web UI
- RAG
- Multi-agent orchestration
- Outbound calling
- Reporting agent
- Complex analytics

Reporting is intentionally deferred for this M1 slice. Observability events may be shaped so future reporting can consume them, but no Reporting Agent, dashboard, analytics processor, or reporting email flow is built here.

## Security Notes

- Store secrets in Azure Key Vault.
- Every endpoint must be protected or explicitly public.
- Webhooks must be verified before processing.
- Tenant isolation is mandatory.
- Logs must not expose secrets or sensitive data.
