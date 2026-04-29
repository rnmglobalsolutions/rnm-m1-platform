# AGENTS.md — RNM Global Solutions Platform

---

## PURPOSE

This file defines how AI coding agents must behave when working on this repository.

This repository is the long-term, industry-agnostic platform for RNM Global Solutions.

The platform must support multiple industries over time:

* HVAC (initial wedge)
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

Current wedge:
HVAC inbound AI booking system

HVAC is not the platform identity.

---

## CURRENT PHASE

### ACTIVE

Build only:

1. inbound voice agent
2. qualification flow
3. service area validation
4. booking adapter
5. CRM adapter
6. SMS and email confirmation
7. logging and observability
8. tenant and vertical configuration foundation

---

### DO NOT BUILD YET

* RAG systems
* multi-agent orchestration layer (M3)
* full dashboards or UI
* multi-industry feature parity
* complex analytics
* overengineered abstractions

---

## SUCCESS CRITERIA

System is complete when:

Call → Qualification → Booking → CRM → SMS/Email → Logs

If this does not work end-to-end, stop adding features.

---

## PLATFORM PHILOSOPHY

* Core platform is industry-agnostic
* Vertical logic is configuration, policies, and prompts
* Tenant isolation is mandatory
* Simplicity over complexity
* Revenue-first over feature-first

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

* Keep minimal for now
* Do not build full UI yet
* Must be ready for M3 expansion

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
