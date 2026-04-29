# ADR 0001: M1 Inbound Booking Scope

## Status

Accepted

## Decision

M1 is limited to the inbound HVAC booking revenue path:

Call -> Qualification -> Service area validation -> Booking -> CRM -> SMS/Email -> Logs

The master strategy document informs business goals and technical posture. `AGENTS.md` controls implementation scope for this repository.

## Consequences

- No dashboard is built in M1.
- No RAG system is built in M1.
- No multi-agent orchestration is built in M1.
- No Reporting Agent or analytics dashboard is built in this M1 slice.
- Observability events are allowed only as foundation for future reporting.
- Provider-specific behavior stays behind adapters.
- Tenant and vertical behavior stays in configuration.
