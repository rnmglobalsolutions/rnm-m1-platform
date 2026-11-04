# AGENTS.md — RNM Global Solutions Platform

---

## PURPOSE

This file defines how AI coding agents must behave when working on this repository.

This repository is the long-term, industry-agnostic platform for RNM Global Solutions.

The platform must support multiple industries over time:

* HVAC
* real estate
* law
* dental
* med spa
* insurance
* SaaS
* others

The goal is not to build a demo or a single-vertical system.

The goal is to build a production-ready, multi-tenant, AI-powered revenue capture platform.

---

## PRIMARY EXECUTION PRINCIPLE

Build the smallest production slice that generates revenue now, without creating architectural damage later.

Platform goal (M1):

Every incoming lead, for any tenant, is captured, filtered, booked when qualified,
stored in the CRM, and followed up automatically. Leads that do not book receive
follow-up and nurturing. Each tenant receives a useful monthly report.

Entry channels (tenant-agnostic):

* Voice AI via Vapi
* Facebook, Instagram, and WhatsApp via ManyChat
* Web form (M1 public funnel)

What a qualified lead can book:

* 1:1 appointments, onsite or online
* Master classes (one-to-many sessions)

Calendars: Google Calendar or another provider behind `IBookingAdapter`.
Online meetings: Google Meet or Zoom, chosen per tenant. Zoom meetings are always
created in the tenant's own Zoom account, never in RNM's.

Verticals are configuration only. A new vertical must ship with zero code.

---

## CURRENT PHASE

### ACTIVE

Build only what is in `docs/backlog.md`. That backlog is the single source of
priority. Anything not in it is deferred.

`docs/implementation-plan.md` defines the phases, the agent team, how agents hand work
to each other, and how they record lessons in `docs/agents/lessons.md`. Every agent
follows it.

`RNM_Global_Solutions_MegaPrompt.md` (outside this repo) is the business vision and
moat roadmap. Where it differs from this file or the plan, this file and the plan win.
Next after the current plan: **Platform RNM 2.0** (UI + Azure SQL).

---

### FROZEN

These features exist. Keep them working, but do not extend them unless a backlog
item requires it:

* outbound calling campaigns
* lead CSV import
* contact system review form

---

### DO NOT BUILD YET

* RAG systems
* multi-agent orchestration layer (M3)
* full dashboards or UI
* website chat widget (ManyChat no longer offers one; websites link to Messenger, Instagram, or WhatsApp)
* complex analytics beyond the monthly report
* overengineered abstractions

---

## SUCCESS CRITERIA

System is complete when, for any tenant:

Lead (call, chat, or form) → Qualification and service area → Availability → Booking (1:1 or master class) → CRM → SMS/email confirmation and reminders → Follow-up / nurturing when not booked → Logs → Monthly report

If this does not work end-to-end, stop adding features.

---

## PERFORMANCE TARGETS

Build for a real inbound phone experience.

Targets:

* Inbound answer latency target: under 2 seconds.
* Webhook acknowledgment target: under 500ms when possible.
* Booking availability lookup target: under 3 seconds.
* SMS and email confirmation should be triggered immediately after successful booking.
* Avoid avoidable production cold-start impact where practical.
* Any operation that may exceed webhook or voice latency targets should move to asynchronous processing.

Rules:

* Do not block voice interactions on non-critical work.
* Keep provider calls bounded by timeouts.
* Prefer fast failure with safe recovery over long waits.
* Measure latency with correlation IDs before optimizing.
* Before redesigning webhook flow, measure real Vapi end-to-end latency; if workflow work cannot reliably meet the acknowledgment target, split fast acknowledgment from asynchronous processing.

---

## COST DISCIPLINE

Revenue-first also means cost-aware.

Rules:

* Prefer serverless-first architecture.
* Avoid unnecessary always-on compute.
* Do not introduce infrastructure that increases monthly cost without direct MVP value.
* Monitor AI token usage.
* Monitor Vapi, Twilio, ElevenLabs, Deepgram, and Claude costs.
* Use cheaper models for simple backend classification, extraction, or formatting tasks.
* Use expensive reasoning models only when business value justifies the cost.
* Prefer configuration and small adapters over new services.

---

## MVP HARD BOUNDARIES

The current phase must not include:

* full dashboards
* RAG or vector databases
* multi-agent orchestration supervisor
* SaaS billing
* advanced analytics
* enterprise RBAC implementation
* complex UI
* multi-vertical feature parity
* long-term nurture systems unless required for the current revenue slice

If a requested change crosses these boundaries, defer it unless it is required to complete:

Lead → Qualification → Booking (1:1 or master class) → CRM → Confirmations and reminders → Follow-up / nurturing → Logs → Monthly report

---

## FAILURE PHILOSOPHY

The system must fail safely.

Rules:

* Never lose a lead silently.
* If CRM fails, preserve the lead and booking intent.
* If booking fails, escalate or create a retryable event.
* If SMS or email fails, log, retry when appropriate, and alert when needed.
* External provider failures must not corrupt tenant state.
* All failure logs must include correlationId and tenantId when available.
* Return safe errors only; do not expose stack traces, secrets, or raw provider payloads.
* Expected business stops should be logged as outcomes, not treated as runtime crashes.

---

## DELIVERY DISCIPLINE

Do not add features until this path works end-to-end:

Lead → Qualification → Booking (1:1 or master class) → CRM → Confirmations and reminders → Follow-up / nurturing → Logs → Monthly report

Rules:

* Any new abstraction must support the current revenue path.
* If a change does not help the current MVP, defer it.
* Prefer simple, testable code over theoretical flexibility.
* Keep changes small enough to verify.
* Add tests proportional to risk and blast radius.
* Update runbooks when operational behavior changes.
* Do not hide incomplete provider behavior behind optimistic responses.

---

## PLATFORM PHILOSOPHY

* Core platform is industry-agnostic
* Vertical logic is configuration, policies, and prompts
* Tenant isolation is mandatory
* Simplicity over complexity
* Revenue-first over feature-first
* Customizable per tenant without forking (see TENANT CUSTOMIZATION)

---

## TENANT CUSTOMIZATION

M1 must adapt to each tenant's needs while staying one platform. Solve every tenant
request at the lowest level that works:

1. **Configuration.** Tenant JSON overrides the vertical: qualification fields and
   rules, routes, templates, follow-up sequences, hours, service area, appointment
   type, language, providers, and voice prompt additions. Custom lead fields need no
   code: they are stored as contact attributes and used in templates as
   `{{attr.fieldName}}`.
2. **Connectors.** A new provider (CRM, calendar, meeting) is a new adapter behind an
   existing port (`ICrmAdapter`, `IBookingAdapter`, conferencing adapter). Built once,
   then available to every tenant through configuration.
3. **Tenant events.** M1 sends signed events to a URL the tenant configures. The tenant
   extends M1 with its own systems (Zapier, Make, its own API) without changing M1.
4. **Custom code, last resort.** If nothing above works, build it as a general feature
   behind a configuration flag that any tenant can enable.

Never:

* fork the code or keep a branch per tenant
* check a tenant ID or tenant name in code (`if tenant == ...`)
* build a generic plugin system before a tenant needs it

---

## REPO STRUCTURE

```text
/src
  /RNM.Platform.Api
  /RNM.Platform.Application
  /RNM.Platform.Domain
  /RNM.Platform.Infrastructure
  /RNM.Platform.Contracts
  /RNM.Platform.SharedKernel
  /RNM.Platform.Web

/tests
  /RNM.Platform.UnitTests
  /RNM.Platform.IntegrationTests

/infra
  /modules
  main.bicep
  dev.bicepparam
  staging.bicepparam
  prod.bicepparam

/docs
  /adr
  /runbooks

/config
  /tenants
  /verticals
  /playbooks
  /policies
  /prompts
```

---

## WEB / UI STRATEGY

* RNM.Platform.Web is the future dashboard layer
* It will host:

  * reporting dashboards
  * tenant admin UI
  * platform control panel

Rules:

* Do not build UI in the current plan
* The UI is built in **Platform RNM 2.0**, together with the move from Azure Table
  Storage to Azure SQL (ADR 0004)
* Keep current code ready for that move: storage stays behind adapters

---

## VERTICAL STRATEGY

All vertical differences must live in:

/config/verticals/{vertical}/

Examples:

* HVAC
* RealEstate
* Law
* Dental

Rules:

* Do not hardcode vertical logic
* Prefer configuration over code
* Only create vertical modules if necessary

---

## REQUIRED STACK

### Cloud

* Azure only

### Backend

* .NET
* Azure Functions

### Infrastructure

* Bicep only

### Voice

* Vapi
* Deepgram
* ElevenLabs
* Claude Sonnet

### Messaging

* Twilio

### AI Routing

* Claude Sonnet for voice reasoning
* GPT-5.4 nano for backend tasks
* Claude Opus for complex reasoning only

---

## ARCHITECTURE RULES

### Adapter pattern required

* ICrmAdapter
* IBookingAdapter
* ISmsSender
* IEmailSender

### Separation of concerns

* Domain
* Application
* Infrastructure
* API layer

### No hardcoding

All tenant and vertical logic must be configurable.

### Correlation IDs required

Every request must be traceable.

### Structured logging required

Log meaningful events with context.

### Simplicity first

Avoid overengineering.

---

## BACKEND SECURITY RULES

Security is mandatory.

### Security principles

* Default deny
* Every endpoint must be protected or explicitly public
* Every webhook must be verified
* Tenant isolation must be enforced
* Never expose secrets
* Minimize sensitive data

### Authentication

Use:

* Azure Managed Identity (preferred)
* API key for early internal endpoints
* Azure Entra ID for future expansion

Rules:

* No unauthenticated internal endpoints
* Store keys in Key Vault
* Rotate keys via configuration

### Authorization

* Enforce tenant boundaries
* Prepare for RBAC:

  * PlatformAdmin
  * TenantAdmin
  * Operator
  * Viewer

### Tenant isolation

Mandatory:

* Every record has tenantId
* Every query filters tenantId
* No cross-tenant access

### Webhook security

Validate:

* Twilio signatures
* Vapi authenticity
* CRM webhooks

Reject invalid requests.

### Secrets management

Use Azure Key Vault for:

* Twilio
* CRM
* Booking providers
* Email providers
* AI keys
* Webhook secrets

Rules:

* Never commit secrets
* Use managed identity
* Every tenant Key Vault secret name (tenant `secretNames`) must start with `rnm-tenant-`

### Data protection

* TLS required
* Encryption at rest
* Mask sensitive logs
* Do not log secrets

### Security logging

Log:

* authentication failures
* webhook failures
* tenant violations

### Rate limiting

* Protect public endpoints
* Prevent abuse

### Secure coding

Do:

* validate input
* fail safely
* use typed models

Do not:

* trust input blindly
* expose stack traces
* return secrets

### Minimum security for current phase

* API key or managed identity
* Key Vault
* webhook validation
* tenant isolation
* safe logging

---

## VOICE AGENT RULES

### Purpose

* answer calls
* qualify leads
* guide conversation
* book appointments
* escalate when needed

### Tone

Must be:

* professional
* calm
* efficient

Avoid:

* robotic
* overly sales-driven
* aggressive
* overly casual

### Must do

* guide conversation
* ask structured questions
* confirm information
* move toward booking

### Must not do

* invent facts
* promise unavailable services
* give legal or medical advice
* argue
* mislead users

### Response style

* short
* clear
* goal-oriented

### Escalation

Escalate when:

* requested by user
* system uncertainty
* complex scenario

---

## INBOUND FLOW

1. call received
2. log start
3. greet
4. capture data
5. validate service area
6. check availability
7. confirm booking
8. CRM sync
9. SMS and email confirmation
10. log result
11. end call

---

## EDGE CASES

Handle:

* hang-ups
* invalid data
* no availability
* CRM failures
* booking failures
* SMS failures
* email failures
* escalation scenarios

---

## TWILIO RULES

### Per client

* one subaccount per client
* one phone number minimum

### USA

* use 10DLC numbers

### Future

* Canada: local numbers
* Europe: sender ID or local routing

---

## TESTING

Test:

* booking success and failure
* service area validation
* SMS delivery
* email delivery
* CRM operations
* escalation flow

---

## DEVELOPMENT PRIORITY

1. domain and contracts
2. adapters
3. API
4. call flow
5. booking
6. SMS and email
7. logging
8. edge cases

---

## DO NOT

* overbuild
* hardcode verticals
* skip security
* build UI early
* ignore tenant isolation

---

## DECISION RULE

Before implementing anything, ask:

Does this help reach a working revenue system faster?

If not, do not build it.

---

## FINAL INSTRUCTION

You are building a scalable AI revenue platform.

Focus on:

* clean architecture
* working systems
* business value

---

END.
