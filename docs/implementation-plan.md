# M1 Implementation Plan

How the items in `docs/backlog.md` get built: in which phases, by which agents, and how
those agents work together and learn.

Rules of precedence, highest first:

1. `AGENTS.md`
2. This plan
3. `docs/backlog.md` (what to build and when it is done)
4. ADRs in `docs/adr/`
5. Area briefs in `docs/agents/briefs/`
6. `docs/agents/lessons.md`

If two of them disagree, the higher one wins and the agent reports the conflict instead
of choosing silently.

---

## How the repo and environments work today

Agents and the orchestrator must respect these facts:

- Pushing any `feature/**` branch runs CI **and opens a PR to `main` automatically**
  (`auto-pr.yml`). Push only after the verifier passes.
- Merging to `main` **deploys to dev automatically** (`deploy-dev.yml`). Dev always runs
  `main`.
- Staging and production deploy **only by hand** (`workflow_dispatch`).
- Real leads never go to dev. A tenant is "live" only in production.
- Compare branches against `origin/main`, not the local `main`, and fetch first. The
  local `main` can be stale.
- Azurite is installed locally (`/usr/local/bin/azurite`, Node 24). Docker is not
  installed.
- Tests run with the .NET 10 SDK at `~/.dotnet` locally:
  `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`. The default `dotnet`
  on this machine fails with exit 145.
- CRM behavior is tested against the real `AzureTableCrmAdapter` on Azurite 3.37.0
  (`Category=Azurite` integration tests). Locally, start Azurite before `dotnet test`;
  in CI it runs as a service container. See `docs/agents/briefs/platform.md`.

---

## Phases

Each phase has **entry criteria** (what must be true to start) and a **release gate**
(what must pass to finish). Sizes: S (one small change), M (several files, one flow),
L (several flows).

### Phase 0: Ready to build

Code and repo:

In this order:

| # | Task | Owner | Size |
|---|---|---|---|
| 1 | Review the uncommitted SendGrid email compliance work. It adds two anonymous endpoints (unsubscribe, SendGrid event webhook), so they get a security review first. Commit it on `feature/master-class` | Orchestrator | S |
| 2 | Commit `AGENTS.md`, `docs/backlog.md`, this plan, ADRs 0003 and 0004, and the Zoom runbook on the same branch. Update the local `main` from `origin/main` (it is stale; `origin/main` already contains all earlier `feature/master-class` work through PR #51) | Orchestrator | S |
| 3 | Push `feature/master-class`; the PR to `main` opens automatically with only these new commits. Before merging, add the new dev secrets (`rnm-email-unsubscribe-signing-key`, `rnm-sendgrid-webhook-verification-key`), because merging deploys to dev. Merge | Raisel | S |
| 4 | From `main`: create the agent definitions, area briefs, `docs/agents/lessons.md` (with the first lesson), `docs/agents/work/`, the path hook, and the deny rules. Test that the hook blocks an out-of-area edit | Orchestrator | M |
| 5 | Confirm the full Google OAuth scope list M1 will need (calendar now, Meet attendance for item 10) | researcher | S |
| 6 | First real use of the loop: add Azurite for tests (local and as a CI service) and one real `AzureTableCrmAdapter` round-trip test | planner, implementer, verifier | M |

Worktrees start from `main`, so steps 1 to 3 must finish before any agent writes code.

Raisel (not code):

- Publish RNM's Google OAuth app "In production" and request verification with the
  full scope list from the researcher. Reconnect calendars authorized in Testing.
- Start A2P 10DLC for Yartex.
- Provision Yartex: Key Vault secrets (dev and prod), Twilio number, Vapi number,
  ManyChat Pro connected to Facebook, Instagram, and WhatsApp, Google Calendar.
- Set `operationsAlertEmail` so production alerts reach you.

Release gate: `main` builds with 0 warnings, all tests pass (including the Azurite
test, locally and in CI), tenant preflight passes, the agent files exist, the path hook
blocked a test edit, and the Azurite item went through the full loop (plan, implement,
verify, PR) with its work file recorded.

### Phase 1: The lead flow is reliable

Entry criteria: Phase 0 gate passed. Yartex provisioning done in dev (needed for the
gate, not to start coding).

| Order | Item | Size | Depends on | Parallel with |
|---|---|---|---|---|
| 1 | #17 Contact attribute limit guard | S | Azurite (Phase 0) | #7a |
| 2 | #7a Per-call tracing | S | — | #17, #2 |
| 3 | #2 Configurable service area | M | — | #7a |
| 4 | #1 One qualification for every channel (includes `channel` and `entryPoint`) | L | #2, #17 | #5 |
| 5 | #5 SMS replies reach the business and stop follow-ups | M | — | #1 |
| 6 | #6 Follow-up starts and stops at the right moments | M | #1 | — |
| 7 | #7b Hang-ups captured | M | #1, #6 | #20 |
| 8 | #20 Unsubscribe links without the email address in the URL | S | — | #7b |

#17 runs first because it changes `AzureTableCrmAdapter.cs` (exclusive) and stops lead
loss; #1 may also change that file.

#6 and #7b both change `InboundBookingWorkflow.cs` (exclusive), so they run one after
the other.

Config (config-author, Raisel approves text): Yartex templates in Spanish, Yartex voice
prompt in `config/prompts/`, Yartex consent text, Yartex service area by state.

Release gate (human test, Yartex in dev):
- One call in Spanish and one in English: qualifies by state, books a 1:1, gets the email
  confirmation.
- A call that hangs up mid-way creates the lead and starts follow-up; a call that booked
  does not.
- A ManyChat lead from Instagram: classified, stored with `channel` and `entryPoint`,
  follow-up scheduled.
- The same person by voice and by ManyChat ends up as one CRM contact.
- The saved KQL query shows the full chain of a call by `callId`.

Milestone, **Yartex live**: Raisel deploys to production, sets
`activeTenants = yartex`, points Vapi and ManyChat at the production URLs, and makes one
real test call. Voice and ManyChat with email go live; SMS turns on when 10DLC is
approved (item #5 is already done by then).

### Phase 2: Booking from every channel

Entry criteria: Phase 1 gate passed.

| Order | Item | Size | Depends on | Parallel with |
|---|---|---|---|---|
| 1 | #4 Appointment type per tenant: onsite, Google Meet, or Zoom | L | — | #13 (health job and alert only) |
| 2 | #13 Calendar and Zoom connection health | M | — | #4 |
| 3 | #3 1:1 booking from ManyChat and the web form | L | #1, #4 | — |
| 4 | #16 Language per lead | M | #1, #3 | — |
| 5 | #18 Appointment outcome by email (attended, closed) | M | #4, #16 | — |

#16 and #18 both change `TenantConfiguration.cs` (exclusive), so they run one after the
other.

#3 and #16 both change `InboundLeadIntakeService.cs` (exclusive), so they run one after
the other.

Note on #4 and #13: both touch `GoogleCalendarBookingAdapter`. #13's health job and
alert run in parallel with #4; #13's **access-token cache** waits until #4 is merged.

Release gate:
- Onsite booking (HVAC demo tenant) has no meeting link; Google Meet booking has one;
  Zoom booking creates the meeting in a test Zoom account owned by a test tenant.
- A forced failure after the calendar event is created leaves no orphan event or meeting.
- A ManyChat lead books a 1:1 inside the conversation and gets the same confirmations as
  a caller.
- A Spanish-speaking lead gets Spanish confirmations; an English one, English.
- A revoked Google token raises an alert within one day and shows in `/readiness`.
- After a test 1:1, the business gets the outcome email; one click records "attended"
  and "closed" in the CRM, and the same link cannot be used twice.

Milestone, **first external pilots**: tenants that need 1:1 booking only (for example
Kenny). Each goes to production the same way as Yartex.

### Phase 3: Master classes end to end

Entry criteria: Phase 2 gate passed.

| Order | Item | Size | Depends on | Parallel with |
|---|---|---|---|---|
| 1 | #8 Master class registration from every channel | M | #1 | #9 |
| 2 | #9 Meeting created automatically for master classes; the session blocks the calendar | M | #4 | #8 |
| 3 | #10 Attendance and post-class follow-up | M | #9 | — |

Release gate:
- A lead registers by voice, by ManyChat, and by web form; capacity is enforced.
- Creating a session creates the meeting and blocks the host's calendar; a 1:1 cannot be
  booked on top of it.
- After a test session, attendance is read; attendees get the 1:1 offer, no-shows get the
  next-session offer, people who could not be matched stay `unknown`.

Milestone: **tenants that use master classes** (Yartex, life insurance agents) run them
on M1.

### Phase 4: Reports and fast onboarding

Entry criteria: Phase 3 gate passed.

| Order | Item | Size | Depends on | Parallel with |
|---|---|---|---|---|
| 1 | #11 Automatic monthly report by email | M | #1, #10, #18 | #15 |
| 2 | #15 Voice prompt additions per tenant | S | — | #11 |
| 3 | #12 New tenant setup script | M | #13, #15 | — |

Release gate:
- The monthly report for Yartex arrives by email with every metric in item #11, by
  channel.
- A new tenant (the construction company or the lender) goes from JSON to working Vapi
  assistant with one script run; a second run changes nothing.
- The new vertical ships with **zero code**. If it needs code, that is a platform bug to
  fix, not a tenant feature.

Milestone: **construction company and lender**. The lender also needs a compliance
review before go-live: no rates, APR, approvals, or protected data in classification.

### Phase 5: Extension

| Order | Item | Size | Depends on |
|---|---|---|---|
| 1 | #14 Tenant events to the tenant's own systems | M | #1, #3, #8 |

Move #14 earlier if a signed tenant needs M1 connected to its own system.

#19 (date-range queries and archiving) is not scheduled. The orchestrator starts it as
soon as its measured trigger is hit in production.

Release gate: a test tenant receives signed events in Make or Zapier for booking,
no-booking, and attendance; a failing endpoint does not slow the lead flow.

### After this plan: Platform RNM 2.0

Not scheduled here. Platform RNM 2.0 adds the UI (client panel, lead list, inbox,
dashboards) and moves storage from Azure Table Storage to Azure SQL (ADR 0004). It gets
its own plan when this one is done.

### Rollback

- Code: revert the PR. Merging the revert redeploys dev; production is redeployed by
  hand from the previous good commit.
- Config: tenant JSON is in git; revert the commit and redeploy.
- A production problem with a tenant: remove it from `activeTenants` to stop reminders
  and follow-ups while it is fixed.

---

## Agent team

The **orchestrator** is the main Claude Code session working with Raisel.

Claude Code lets subagents start other subagents and message each other. **This plan
turns both off on purpose**: no agent gets the `Agent` tool, and agents do not message
each other. Everything passes through the orchestrator, so every handoff is recorded in
the work file and nothing happens that Raisel cannot see.

| Agent | Model | Tools | Writes to | Does |
|---|---|---|---|---|
| **Orchestrator** (main session) | The session's model | All | Work files, lessons, backlog status, plan retros | Picks the next item, starts agents, passes results between them, pushes branches, keeps merge order |
| **planner** | Opus | Read-only (read and search files) | Nothing; returns the plan as its report | Plans one item: files, tests, hot files, tenant-isolation and security risks, open external questions. Rejects work that breaks `AGENTS.md` |
| **implementer** | Sonnet | Read, edit, shell | Code and tests, in its own worktree and branch | Writes a failing test first, then the minimum code. One item per instance |
| **verifier** | Sonnet | Read, shell, edit | New tests only | Builds, runs all tests and preflight, reviews the diff against the item's "Done when", `AGENTS.md`, and security. May **add** tests; never edits or deletes existing ones without listing it in its report |
| **config-author** | Sonnet | Read, edit, shell | `config/`, `docs/runbooks/` | Tenant and vertical JSON, templates in English and Spanish, voice prompts, runbooks. Runs preflight. Raisel approves legal and consent text |
| **researcher** | Haiku | Web search and fetch, read | Nothing; returns findings with sources | Confirms external facts before code depends on them |

### Who does what, by phase

| Phase | planner | implementer + verifier | config-author | researcher |
|---|---|---|---|---|
| 0 | Azurite setup | Azurite | — | Google OAuth scope list |
| 1 | #17, #7a, #2, #1, #5, #6, #7b, #20 | Same | Yartex templates, prompt, service area | Vapi `end-of-call-report` and `callId` fields |
| 2 | #4, #13, #3, #16, #18 | Same | Appointment type and Spanish templates per tenant | Zoom scopes and host limits, ManyChat external request limits |
| 3 | #8, #9, #10 | Same | Class templates | Zoom and Google Meet participant APIs |
| 4 | #11, #15, #12 | Same | Construction and lending verticals, report email template | Vapi assistant API |
| 5 | #14 | Same | Zapier / Make runbook | Webhook signing conventions |

### Area briefs

One file per area in `docs/agents/briefs/`, listing key files, invariants, and known
traps. The implementer reads the brief for its item.

- `channels.md`: Vapi, ManyChat, web form, lead capture, qualification
- `booking.md`: calendar and meeting adapters, availability, cleanup
- `follow-up.md`: follow-up, reminders, SMS eligibility, consent, language
- `classes.md`: master classes, registration, attendance
- `platform.md`: configuration, telemetry, infra, secrets, reports, tests and Azurite

---

## How agents work together

### Only the orchestrator writes shared records

Subagents return a structured report; the orchestrator writes it into the shared files.
This avoids read-only agents needing write access and avoids parallel branches editing
the same file.

- **Work file per item**: `docs/agents/work/<item>-<short-name>.md`. Sections: Plan,
  Research, Implementation, Verification, Lessons. It is committed on the item's branch
  and merges with the code.
- **Lessons**: `docs/agents/lessons.md`, appended by the orchestrator.

### The loop for one item

```text
1. Orchestrator creates the work file and runs the planner.
2. If the plan has open external questions, the orchestrator runs the researcher and
   records the answers.
3. Raisel approves the plan (for S items Raisel may let the orchestrator approve).
4. Orchestrator starts the implementer with worktree isolation. Claude Code creates the
   worktree from `main` on a branch named `worktree-<name>`. The implementer commits
   there; it does not push.
5. Orchestrator starts the verifier with its own worktree; its first step is
   `git checkout --detach <implementer commit>` (git does not allow one branch in two
   worktrees). If the verifier adds tests, it commits them on that detached HEAD and
   reports the SHA; the orchestrator cherry-picks that commit onto the implementer's
   branch.
6. Fail: the findings go back to the SAME implementer, which keeps its context. Repeat
   from 5.
7. Pass: orchestrator checks the changed paths match the agents' allowed areas and
   pushes the implementer's commit as `feature/m1-<item>`
   (`git push origin HEAD:feature/m1-<item>` from the worktree). CI runs, the PR opens
   automatically, and Raisel reviews and merges.
8. After 3 failed verification rounds, work stops and Raisel decides: split the item,
   change the plan, or drop it.
```

### Parallel work

- At most **2 implementers** at a time, only on items marked parallel.
- **Exclusive hot files**: only one active branch may change each of these:
  - `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs`
  - `src/RNM.Platform.Application/Configuration/ConfigurationValidator.cs`
  - `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs`
  - `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs`
  - `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs`
  - `src/RNM.Platform.Application/LeadIntake/InboundLeadIntakeService.cs`
  - `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs`
  - `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs`
- **Append-only hot files**: several branches may add lines, never change existing
  ones; conflicts are resolved at rebase:
  - `src/RNM.Platform.Api/Program.cs` (new registrations)
  - `src/RNM.Platform.Application/Observability/TelemetryEventNames.cs`
  - `src/RNM.Platform.Application/Crm/CrmModels.cs` (new constants only; any other
    change makes it exclusive)
- The planner lists the hot files each item touches. Before starting a parallel pair,
  the orchestrator checks that they share no exclusive hot file.
- Each branch rebases on `main` before merging; merges go in the order the orchestrator
  sets.

---

## Guardrails

Instructions alone are not enough; these are enforced:

1. **Tool lists** in each agent definition. No agent gets the `Agent` tool. The planner
   and researcher get no edit or shell tools.
2. **Path hook**: a `PreToolUse` hook in `.claude/settings.json` reads `agent_type` and
   blocks edits outside each agent's area (implementer: `src/`, `tests/`, `tools/`;
   verifier: `tests/`; config-author: `config/`, `docs/runbooks/`). Claude Code has no
   per-agent path setting in the agent file, so the hook is the enforcement point. Phase
   0 includes a test that the hook blocks an out-of-area edit.
3. **Project deny rules** in `.claude/settings.json`, which also apply to subagents:
   Azure CLI and Functions Core Tools, Key Vault, the `scripts/seed-*` scripts,
   `git push`, `gh pr merge`, and branch deletion. Bash rules match command text, so a
   rewritten command can slip past them; rules 2 and 4 and the human merge cover that.
4. **Path check before push**: the orchestrator compares `git diff --name-only main...`
   with the allowed areas. Anything outside goes back for review.
5. **Human merge**: only Raisel merges to `main` and deploys to production.
6. Agent definitions do not enable Claude Code's per-agent `memory`. Learning goes only
   through `docs/agents/lessons.md`, which is reviewed, instead of notes no one checks.

---

## How agents learn from their mistakes

Agents do not remember between sessions. They learn through files they read every time
they start.

### `docs/agents/lessons.md`

The orchestrator adds an entry when:

- the verifier fails an item,
- a bug is found after merge,
- Raisel corrects an agent,
- a release gate fails.

Entry format:

```text
### YYYY-MM-DD: <short title>
What happened: <one or two sentences>
Rule: <what to always or never do from now on>
Applies to: <agents or areas>
Source: <work file or PR>
```

Keeping it useful:

- **At most 30 active lessons.** At each phase retro, lessons that repeat or apply to
  everyone move into `AGENTS.md` (Raisel approves) or an area brief; lessons now covered
  there are removed; wrong lessons are deleted.
- First lesson, from this review: "CRM behavior must be tested against the real
  `AzureTableCrmAdapter` on Azurite; in-memory doubles hid the `LeadStatus` column vs
  `Attr_leadStatus` bug."

### Phase retro

At the end of every phase the orchestrator adds a short retro at the bottom of this plan:

- items done, items that needed rework, bugs found after merge
- first-pass verification rate (target: above 80%)
- lessons added, promoted, and removed
- changes to this plan for the next phase

---

## Rules every agent follows

Every agent definition starts with these:

1. Read `AGENTS.md`, this plan, `docs/backlog.md`, `docs/agents/lessons.md`, the work
   file for the item, and the area brief.
2. Work only on the assigned item. Anything else goes into the report as a note.
3. Never touch production, Key Vault, deployments, or Twilio, Vapi, Zoom, ManyChat, or
   Google accounts. Never push or merge.
4. Never hardcode a tenant or vertical. Follow TENANT CUSTOMIZATION in `AGENTS.md`.
5. A fact about an external service that is not confirmed in the work file is marked
   "unconfirmed" in the report and never guessed in code.
6. Use the .NET SDK at `~/.dotnet` (see "How the repo and environments work today").
7. Report what was not done and what failed, plainly.

---

## Raisel's checkpoints

| When | What Raisel does |
|---|---|
| Before each item | Approves the plan (a few minutes; S items can be delegated) |
| After each item | Reviews and merges the PR |
| End of each phase | Runs the human release-gate test, reads the retro |
| Each milestone | Deploys to production and makes a real test call |
| Any time | Approves consent and legal text, `AGENTS.md` changes, anything touching accounts or production |

---

## Retros

(Added at the end of each phase.)
