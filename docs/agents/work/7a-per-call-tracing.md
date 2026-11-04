# 7a: Per-call tracing

Status: verified (PR pending)
Branch: feature/m1-7a
Hot files: per plan

Source: `docs/backlog.md`, item 7a; `docs/implementation-plan.md`, Phase 1, order 2
(parallel with #17 and #2).

Today each Vapi webhook gets a new correlation ID and `callId` is not logged, so the
availability check and the booking of the same call cannot be linked.

Done when (from the backlog):
- Every voice telemetry event includes `callId`, and one KQL query (saved in a runbook)
  returns the full chain for a call.

## Plan

Planner report, 2026-11-04 (at be4a88e).

Goal: every telemetry event emitted while handling an authenticated Vapi webhook carries the
Vapi `callId`; one saved KQL query returns the whole chain of a call.

### Findings

- `VapiInboundWebhookFunction.Handle` (line 108) uses `CorrelationContextFactory.FromRequest`
  (`x-correlation-id` header or a new GUID), so every webhook gets its own ID, passed through
  mapper, workflow, services, and CRM requests.
- `VapiWebhookPayloadParser.Parse` (lines 92-95) already reads callId
  (`root.callId`, `call.id`, `message.callId`; `call = root.call ?? message.call`) into
  `InboundCallEvent.Session.ProviderCallId`. Nothing logs it. Direct `apiRequest` bodies have a
  callId only if the body has a root `callId`.
- All production events go through the singleton `ApplicationInsightsEventLogger`
  (Program.cs:163), which already filters sensitive names and sanitizes values.
- No `AsyncLocal` or Functions middleware today; no background work on the voice path.

### Decisions (recommended)

1. Keep the correlation ID per request; add `callId` as a separate property. The correlation ID
   is stored in CRM rows, follow-ups, Google event descriptions, SendGrid args, and the SMS
   proof check; per-request IDs distinguish attempts within a call; the header must keep
   working; `apiRequest` mode may have no callId.
2. Carry callId in an ambient `AsyncLocal` scope (`CallTelemetryContext`), opened by the Vapi
   function after authentication and parsing; `ApplicationInsightsEventLogger` adds it to
   every event in scope. No signature changes, no Program.cs change.
3. callId is read only after signature validation, so `webhook.received`, `tenant.resolved`,
   `webhook.validation_*`, and a parse-failure `api.request_failed` never carry it; the saved
   query still returns them through correlationId. Needs Raisel's approval.

### Files

- New `src/RNM.Platform.Application/Observability/CallTelemetryContext.cs`: `AsyncLocal`,
  `BeginCall(callId)` (accepts only values `CorrelationId.TryNormalize` accepts: ≤ 128 chars,
  letters, digits, `-_.:`), `CurrentCallId`, `Enrich(properties)`.
- `ApplicationInsightsEventLogger.cs`: `Enrich` before the existing sensitive-name filter and
  sanitizer.
- `VapiInboundWebhookFunction.cs` (~6 lines in `Handle`): open the scope after parse success,
  before `webhookMapper.Map`; dispose in `finally` (so `dependency_failure` carries callId). Do
  not use the LLM-filled `capturedDuringCallId` tool argument.
- Runbook (config-author): "Trace a call by callId" in `docs/runbooks/vapi-assistant-setup.md`;
  Verification step 8 points to it.
- Briefs (orchestrator): remove the "new correlation ID per webhook" trap; platform brief:
  voice events get `callId` from `CallTelemetryContext`, do not add it by hand.

Hot files: exclusive `VapiInboundWebhookFunction.cs` only. Not touched: `InboundBookingWorkflow.cs`,
`TenantConfiguration.cs`, `ConfigurationValidator.cs`, `JsonTenantConfigurationProvider.cs`,
`VapiWebhookPayloadParser.cs`, `TelemetryEventNames.cs`, `Program.cs`.

### Tests first (unit only)

- `Observability/CallTelemetryContextTests.cs`: nested scopes restore; invalid values ignored
  (whitespace, > 128, `{`, spaces, `+15551234567`); `Enrich` adds inside scope, keeps explicit
  callId, no-op outside; flows into awaited work; concurrent scopes do not leak.
- `Observability/ApplicationInsightsEventLoggerTests.cs` (capturing `ITelemetryChannel`,
  `ConnectionString`): callId added inside scope, absent outside, sanitized.
- `Functions/VapiCallTracingTests.cs`: every event after parse carries callId; availability then
  booking share callId with different correlation IDs; consent tool CRM events carry callId;
  `end-of-call-report` unsupported event carries callId; invalid signature → no callId;
  workflow throws → `dependency_failure` carries callId; context cleared after `Handle`;
  concurrent requests do not mix; no phone, transcript, or `{`/`}` in any value.

### KQL (runbook)

Full chain: collect the correlation IDs of events with the callId, then return every event
with those correlation IDs, ordered by time (with tenantId, toolName, outcome, qualification,
service area, booking, CRM, confirmation states). Also: recent callIds for a tenant, and a
coverage check of `voice.*` events with and without callId.

### Risks

- Tenant isolation: per async flow; read only after the tenant's webhook secret is validated;
  telemetry only, never a storage key or filter.
- PII: callId is an opaque provider ID; name does not match sensitive fragments; value checked
  and sanitized; phone-shaped values rejected.
- Later invocations (confirmation retries, follow-ups, reminders) do not get callId; `ILogger`
  traces are not enriched. Out of scope.

### Open external questions

1. Is `message.call.id` present and identical on every Vapi server message of one call
   (`tool-calls`, `status-update`, `transcript`, `end-of-call-report`)?
2. Vapi call ID format (UUID? length, characters)?
3. Does Vapi's `apiRequest` tool type send a call identifier (header or body variable)? Which
   mode do the Yartex tools use?

Size: S. Notes: outbound webhook (frozen) not changed; #7b can rely on `end-of-call-report`
carrying callId; the `apiRequest` parser uses root `callId` as fallback for `toolCallId`
(parser line 181), relevant to #1/#2.

Approved by Raisel on 2026-11-04, including decision 3 (callId only after signature
validation; pre-parse events reached through correlationId).

Added after research: the runbook tells operators to add a static `parameters` entry
`callId: "{{ call.id }}"` to every `apiRequest` tool, so direct bodies carry a root `callId`
(the parser already reads it). Raisel's live test call confirms which message types carry
`message.call.id` and that the `apiRequest` parameter works.

## Research

Researcher report, 2026-11-04 (Vapi docs).

1. `message.call.id` on every server message: **unconfirmed**. Every delivery is
   `{"message": {...}}`; examples include `call` for `assistant-request`, `tool-calls`,
   `status-update` (base/ended), `end-of-call-report`, `hang`, `transfer-destination-request`;
   examples omit it for `transcript`, `conversation-update`, `speech-update`, and others. The
   `call` object is a placeholder in all examples. Legacy `function-call` is not in the current
   events table. Root `callId` (read by the parser) is not documented anywhere.
   `tool-calls` shows both `toolCallList` and `toolWithToolCallList`.
2. Call ID format: likely UUID (`GET /call/{id}` path is `format: uuid`); length and charset
   unconfirmed. Keep the `CorrelationId.TryNormalize` check (≤ 128, `[A-Za-z0-9-_.:]`),
   which accepts UUIDs.
3. `apiRequest`: no automatic call identifier documented (header or body). Template variables
   `{{ call.id }}`, `{{ customer.number }}`, etc. are documented for static `parameters`, which
   are "merged into the request body". A `parameters` entry `callId: "{{ call.id }}"` is the
   documented route (likely; not tested live).
4. For #7b, `end-of-call-report`: `message.endedReason` (e.g. `customer-ended-call`,
   `assistant-ended-call`, `silence-timed-out`, `voicemail`, many error values);
   `message.call` with `id`, `customer.number`, `startedAt`, `endedAt`, `cost`, `messages`
   (tool calls and results visible); `message.artifact` (`recording`, `transcript`,
   `messages`). Customer-number path in the live payload and a duration field: unconfirmed.
   Redelivery possible (third-party): dedupe on `call.id` + `type`.

Conflicts: root `callId` parser fallback not in Vapi docs; signing header (`x-vapi-secret` vs
`x-signature`) unconfirmed, out of scope.

Sources: docs.vapi.ai/server-url/events; docs.vapi.ai/api-reference/calls/get-call;
docs.vapi.ai/tools/static-variables-and-aliases/how-variables-work;
docs.vapi.ai/api-reference/tools/create; docs.vapi.ai/tools/api-request;
docs.vapi.ai/calls/call-ended-reason.

Effect on the plan: none on the code (callId is added when the parser finds it). Runbook adds
the `apiRequest` parameter and a live-call check.

## Implementation

### Round 1 (implementer, 2026-11-04)

Commit `f224912` on `worktree-agent-a264a73b1cbc6cc24`.

Files: new `Application/Observability/CallTelemetryContext.cs` (`BeginCall` accepts only
`CorrelationId.TryNormalize` values, invalid → no-op scope; dispose restores previous;
`CurrentCallId`; `Enrich`); `ApplicationInsightsEventLogger.cs` (one line: `Enrich` before the
sensitive-name filter and sanitizer); `VapiInboundWebhookFunction.cs` (scope opened after
signature validation and parse, before `webhookMapper.Map`; `finally` disposes);
`EndpointTelemetryTests.cs` (`RecordingCrmAdapter` private → internal for reuse; no other change).

Tests: 31 new — `CallTelemetryContextTests` (18), `ApplicationInsightsEventLoggerTests` (3),
`VapiCallTracingTests` (10). Unit 541 → 572; integration 10 (incl. Azurite). Build 0 warnings;
preflight pass. Failed first: build (35 errors, type missing), then 6 `VapiCallTracingTests`
until the function opened the scope.

Parser finding (not changed): a direct `apiRequest` body with a root `callId` and no
`toolCallId`/`id` gets `ToolCall.ToolCallId = callId` (parser ~181, 196). Harmless today:
direct-mode responses never use `ToolCallId`; `ProviderActionId` is never read; no strict
unknown-field rejection. Caveat for #1 and later: do not use `ToolCallId`/`ProviderActionId` as
an idempotency key for direct `apiRequest` calls (all tools in a call would share one ID).

## Verification

### Round 1 (verifier, 2026-11-04): PASS

At `f224912`. Build 0 warnings; unit 572/572, integration 10/10 (own Azurite); preflight pass
(`--all --environment repository`).

Checked: scope opens only after signature validation and parse; `BeginCall` is synchronous;
every exit path goes through `finally`; dispose restores and is idempotent; no background work
in `src`; unauthenticated body cannot set callId; value limited to 128 chars of
`[A-Za-z0-9-_.:]` then sanitized; non-Vapi endpoints unchanged; explicit `callId` wins
(case-insensitive); `EndpointTelemetryTests.cs` only private → internal; no #2 hot file touched.

Findings (low, informational): direct `apiRequest` `ToolCallId` = root `callId` (do not use as
idempotency key); later invocations and `ILogger` traces carry no callId (out of scope).

Tests added: none. First-pass verification: yes.

Runbook (config-author, copied from its worktree): "Trace a call by callId" in
`docs/runbooks/vapi-assistant-setup.md` with KQL 1 (full chain), KQL 2 (recent callIds per
tenant), KQL 3 (coverage), a live-call check for Raisel, the `callId = {{ call.id }}` static
parameter for `apiRequest` tools, and Verification step 8 pointing to KQL 1.

## Lessons

None added. Note for the process: config-author ran in its own worktree and could not write to
the implementer's worktree; the orchestrator copied the file. Next time, expect that and copy.
Verifier's suggestion (open AsyncLocal scopes only after authentication, synchronously, dispose
in `finally`, test cleared after the handler) went into the platform brief.
