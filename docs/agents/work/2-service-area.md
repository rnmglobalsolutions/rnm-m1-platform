# 2: Configurable service area

Status: verified (PR pending)
Branch: feature/m1-2
Hot files: per plan

Source: `docs/backlog.md`, item 2; `docs/implementation-plan.md`, Phase 1, order 3
(parallel with #7a).

Today a tenant with no ZIP codes can never book by voice: `ServiceAreaValidator` returns
`NeedsEscalation` when `zipCodes` is empty, and `MissingRequiredFields` when the caller
gives no ZIP.

Done when (from the backlog):
- A vertical declares the service area mode: `zip`, `state` (with allowed states), or
  `none`, and a tenant can override it.
- HVAC still requires a ZIP; life insurance qualifies with a state and no ZIP.
- Fields a caller cannot know (`leadSource`, `campaignId`) are filled by M1, not by the
  Vapi tool.

Config for Phase 1 (config-author, Raisel approves): Yartex service area by state.

## Plan

Planner report, 2026-11-04 (at be4a88e).

Goal: each vertical declares a service area mode (`zip`, `state`, `none`) and a tenant can
override it; voice qualification enforces the effective mode; M1 fills `leadSource` and
`campaignId` itself.

### Today

- Only voice checks service area: `InboundBookingWorkflow.CreateQualificationRequest`
  (lines 355-378) passes every vertical `qualificationFields` entry as required (except
  `preferredTime` for availability), the tenant ZIP list, and ZIP/address aliases.
  `ServiceAreaValidator` returns `MissingZipCode`, `InvalidZipCode`, `NeedsEscalation` (empty
  list), `InServiceArea` (`*` or listed), `OutOfServiceArea`. `cities` is parsed, never read.
- ManyChat and web form (`InboundLeadIntakeService`) never check service area (#1's job).
- Tenants: sample HVAC `["*"]`; Kenny 5 Laredo ZIPs; Yartex `zipCodes: []`,
  `cities: ["United States"]`, so every Yartex voice lead escalates.
- Life-insurance vertical lists `leadSource` and `campaignId` in `qualificationFields`, so the
  Vapi tool must send them. Existing test relies on a tool-sent `leadSource: "zillow"`.
- Vapi tool schemas live in the Vapi dashboard, documented in
  `docs/runbooks/vapi-assistant-setup.md` (HVAC only).

### Design

- Vertical JSON: `"serviceArea": {"mode", "states"}` and `serviceAreaFieldAliases.stateFields`
  (default `state`, `stateCode`, `serviceState`). Tenant JSON: optional `mode`, `states`.
- New `ServiceAreaPolicy.Resolve(vertical, tenant)`: mode = tenant, else vertical, else `zip`
  (existing JSON unchanged); states = tenant list if any, else vertical; ZIPs from tenant;
  `IsConfigured` false when the mode's list is empty.
- States: uppercase USPS codes (50 states, DC, PR, GU, VI, AS, MP; no military, no `*`).
  Caller input normalized (trim, remove dots, 2-letter code or English full name); stored in
  `fields["state"]`; from tool argument only, never parsed from an address.
- Validator: mode in {zip, state, none}; valid codes; tenant `zip` needs ZIPs, `state` needs
  states; no mode keeps the old rule extended to "at least one zip, state, or city".
  Cross-layer check in preflight.

| Mode | In area | Out | Missing | Unrecognized | Empty list |
|---|---|---|---|---|---|
| zip (unchanged) | Qualified | OutOfServiceArea | MissingZipCode → MissingRequiredFields | InvalidZipCode → InvalidInput | NeedsEscalation |
| state | Qualified | OutOfServiceArea | MissingState (new) → MissingRequiredFields | InvalidState (new) → InvalidInput | NeedsEscalation |
| none | NotRequired (new) → Qualified | — | — | — | — |

New enum values appended (5, 6, 7). In state/none mode a valid ZIP is still captured, an
invalid one dropped. `QualificationService` gets explicit switch arms.

`leadSource`/`campaignId`: workflow passes defaults `InboundVoice` / `inbound-voice` (channel
constants) as `StructuredFields`; a value the tool sends wins.

### Files

Domain: `ServiceAreaConfiguration.cs` (+`Mode`, `States`, `ServiceAreaModes`),
`VerticalConfiguration.cs` (+`ServiceArea`, `StateFields`). Application: new
`Qualification/ServiceAreaPolicy.cs`; `ServiceAreaValidator.cs` (new overload; old one calls it
in zip mode); `QualificationModels.cs`; `QualificationService.cs` (state extraction,
normalization, mode decision, `serviceAreaMode` telemetry property);
`InboundBookingWorkflow.cs` (`CreateQualificationRequest` only); `ConfigurationValidator.cs`.
Infrastructure: `JsonTenantConfigurationProvider.cs`, `JsonVerticalConfigurationProvider.cs`
(DTOs). Tools: preflight `serviceArea.policy` and `serviceArea.configured` (Required in
production, Warning in repository).

Config (config-author, same branch, after implementer commit): `hvac.json`,
`commercial-real-estate.json`, `residential-real-estate.json` mode `zip`; `life-insurance.json`
mode `state`, `stateFields`, remove `leadSource`/`campaignId` from `qualificationFields`;
`yartex.json` states list (Raisel approves), remove `"United States"` city. Runbooks:
state-mode tool schema in `vapi-assistant-setup.md`; preflight checks in
`tenant-onboarding-preflight.md`.

Hot files: exclusive `InboundBookingWorkflow.cs` (`CreateQualificationRequest` only),
`ConfigurationValidator.cs`, `JsonTenantConfigurationProvider.cs`. Append-only: none. Not
touched: `VapiInboundWebhookFunction.cs`, `TenantConfiguration.cs`,
`InboundLeadIntakeService.cs`. Size: M (11 small production edits).

### Tests first

`ServiceAreaPolicyTests` (resolve precedence, not configured, state normalization accepts and
rejects); `ServiceAreaValidatorTests` (state mode in/out/missing/invalid/escalation/no ZIP
needed; none mode); `QualificationServiceTests` (state mode qualifies, missing, invalid, out,
normalized code stored, aliases, none mode, `serviceAreaMode` logged without state value);
`InboundBookingWorkflowTests` (real life-insurance vertical qualifies with state and no ZIP;
real HVAC still requires ZIP; tenant `none` overrides; defaults filled when tool omits them;
existing "zillow" test stays green); `ConfigurationValidatorTests`;
`JsonConfigurationProviderTests` (incl. `RepositoryVerticals_DeclareExpectedModes`).

### Risks

- No new tenant-isolation surface; USPS table is reference data, not vertical logic. State
  value not logged.
- Until config lands, life insurance stays zip mode (as today).
- Voice now writes `leadSource=InboundVoice` when missing; on a ManyChat contact that is last
  touch wins (as ManyChat already does). #1 settles attribution.
- Spanish state names not normalized (tool asks for USPS code).

### Open questions

- Researcher (non-blocking, runbook only): can a Vapi tool parameter declare a JSON-schema
  `enum`?
- Raisel: Yartex states list; approve "default only when missing" and values `InboundVoice` /
  `inbound-voice`.

### Notes (outside this item)

- **Voice leads stopped at qualification (out of area, escalation) create no CRM contact:
  lost silently.** For #1 or #7b.
- `MissingRequiredFields` message does not name the missing field (needs
  `VapiInboundWebhookFunction.cs`; #1).
- Out-of-area message says "service address"; odd in state mode.
- `state` not in `CrmApplicationService.ContactAttributeFields` (one line, #1 if the report needs it).
- Yartex voice prompt must ask for the caller's state. `cities` unused.

Approved by Raisel on 2026-11-04:
- `leadSource` / `campaignId`: M1 default `InboundVoice` / `inbound-voice` only when the tool
  sends none; a tool-sent value wins.
- Yartex states: `CA`, `FL`, `NE`, `TX` (more may come later; config change only).
- Runs in parallel with #7a (no shared exclusive hot file).
- Researcher question on Vapi `enum` skipped (runbook only, non-blocking).

## Research

## Implementation

### Round 1 (implementer, 2026-11-04)

Commits on `worktree-agent-a0aa224b7f07faecc`: `14fdc2e` (code and tests, all green) and
`edc9aab` ("pending config": 7 tests fail until config-author's change).

Files (commit 1): `ServiceAreaConfiguration.cs` (+`Mode`, `States`,
`VerticalServiceAreaConfiguration`, `ServiceAreaModes`); `VerticalConfiguration.cs`
(+`ServiceArea`, `StateFields` default `state`, `stateCode`, `serviceState`); new
`Qualification/UsStates.cs` (56 USPS codes, `TryNormalize`); new `ServiceAreaPolicy.cs`;
`QualificationModels.cs` (+`ServiceAreaMode`, `AllowedStates`, `StateFields`; enum 5-7);
`ServiceAreaValidator.cs` (new overload; old one untouched); `QualificationService.cs` (state
from tool argument only, normalized into `fields["state"]`, explicit switch arms,
`serviceAreaMode` telemetry, state value never logged); `InboundBookingWorkflow.cs`
(`CreateQualificationRequest` + two constants; defaults `InboundVoice` / `inbound-voice`, tool
value wins, blank tool value does not override); `ConfigurationValidator.cs`;
`JsonTenantConfigurationProvider.cs`, `JsonVerticalConfigurationProvider.cs`; preflight
`serviceArea.policy` and `serviceArea.configured` (Required in production, Warning otherwise).

Tests: `ServiceAreaPolicyTests` (new) and additions to `ServiceAreaValidatorTests`,
`QualificationServiceTests`, `InboundBookingWorkflowTests`, `ConfigurationValidatorTests`,
`JsonConfigurationProviderTests`. Commit 1: unit 624, integration 1, Azurite 9, all pass.
Commit 2 adds `RepositoryServiceAreaConfigurationTests.cs` and workflow tests: 633 unit, 7 fail
pending config. Failed first: test project did not compile (types missing). Preflight
(`--all --environment repository`): exit 0, warning (existing placeholders plus
`serviceArea.configured` for Yartex).

Config needed by the pending tests: hvac, commercial-real-estate, residential-real-estate
`"serviceArea": {"mode": "zip"}`; life-insurance `"serviceArea": {"mode": "state"}` (no
states), remove `leadSource`/`campaignId` from `qualificationFields`,
`serviceAreaFieldAliases.stateFields` with `"state"` (keep zip and address aliases); yartex
`serviceArea.states: ["CA","FL","NE","TX"]`, remove `"United States"` city, no tenant mode.

Deviations: preflight has no unit tests (no test project in `tools/`). `state` reaches the
qualification result but not the CRM contact (`ContactAttributeFields`; #1).
Notes: defaults now reach CRM attributes for every voice lead, including HVAC.

## Config (config-author, 2026-11-04)

Verticals hvac, commercial-real-estate, residential-real-estate: `"serviceArea": {"mode": "zip"}`.
life-insurance: mode `state`, `stateFields` `["state","stateCode","serviceState"]`, removed
`leadSource`/`campaignId` from `qualificationFields`. yartex: `states` CA, FL, NE, TX; `cities`
emptied; no tenant mode. Runbooks: "State-Mode Assistants (Life Insurance)" in
`vapi-assistant-setup.md`; "Service Area Checks" in `tenant-onboarding-preflight.md`. No other
config needed changes. Orchestrator copied the files from the config-author's worktree, ran
build (0 warnings), unit tests (633 pass, including the 7 pending-config tests) and preflight
(Yartex: mode `state`, 4 states). Before push, the "pending config" commit was squashed with
the config commit so no commit on the branch is red.

Draft for Raisel (not applied): Yartex `referralMessage` "Yartex is currently licensed in
California, Florida, Nebraska, and Texas. A team member can follow up with you about other
options."

## Verification

### Round 1 (verifier, 2026-11-04): PASS

At `f189281`. Build 0 warnings; unit 643 (incl. 9 new), integration 10 (own Azurite); preflight
`--all --environment repository` exit 0 (only known warnings); `yartex --environment production`
serviceArea.* PASS, blocked only by existing placeholders (SMS phone, business notification
phone, postal address).

Checked: precedence and backward compatibility (old validator API untouched, optional params,
Kenny and sample HVAC unchanged); every switch on `ServiceAreaDecisionState` /
`QualificationResultState` has explicit arms; state from tool argument only, ignored in zip
mode, never logged; defaults never override a real value (blank skipped);
`InboundLeadIntakeService` does not read `qualificationFields`; #7a files untouched; runbooks
match code.

Findings (low, not blocking):
1. `QualificationService.cs:282-290` `FindState` takes the first non-blank alias: invalid
   `state` + valid `stateCode` → InvalidState (fails safe).
2. `UsStates.cs:515-525`: two-letter English words are valid codes ("in" → IN, "or" → OR, "me"
   → ME, "hi" → HI). Only a risk if the assistant passes free text instead of the code.
3. `state` not stored on the CRM contact (#1).

Tests added: `96163ba` (cherry-picked as `a50a916`), `UsStatesEdgeCaseTests.cs` (9 cases).
First-pass verification: yes.

## Lessons

Process (verifier suggestion, adopted): when an item needs both code and repository config,
the orchestrator applies the config before verification and squashes so no commit is red. For
the next item with config, run config-author right after the implementer and before
verification, and do not push a "pending config" commit on its own.
