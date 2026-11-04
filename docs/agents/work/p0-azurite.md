# P0-6: Azurite for tests and one real AzureTableCrmAdapter round-trip test

Status: done (PR #54, merged 2026-11-03)
Branch: feature/m1-p0-azurite
Hot files: none in the plan list; `.github/workflows/ci.yml`, `deploy-staging.yml`, `deploy-prod.yml` treated as exclusive

Source: `docs/implementation-plan.md`, Phase 0, step 6. Not a numbered backlog item; it
is the prerequisite for backlog item #17 and the first lesson in `docs/agents/lessons.md`.

Scope:
- Azurite available to tests locally (`/usr/local/bin/azurite`, Node 24, no Docker) and
  as a service in CI (`.github/workflows/`).
- One integration test that writes a contact through the real `AzureTableCrmAdapter`
  against Azurite and reads it back.
- Tests that need Azurite must be clearly marked so the unit test run does not need it.

Done when:
- `dotnet build` with 0 warnings; all tests pass locally, including the Azurite test.
- The Azurite test runs and passes in CI.
- Tenant preflight passes.

## Plan

Planner report, 2026-11-03.

Goal: the real `AzureTableCrmAdapter` writes a contact with attributes (including
`leadStatus`) to Azurite, reads it back, and cannot see it from a second tenant. Runs and
must pass locally and in CI; never skips silently.

Repo facts checked by the planner:
- `tests/RNM.Platform.IntegrationTests` is in the solution (xUnit 2.9.3, references Api
  and Infrastructure; csproj needs no change). Only test is a placeholder.
- `ci.yml` runs `dotnet test RNM.Platform.sln` on `ubuntu-latest`. `deploy-staging.yml`
  (line 73) and `deploy-prod.yml` (line 74) also run the solution tests; `deploy-dev.yml`
  does not.
- The adapter has no options or injected client. Its constructor reads env vars:
  `RNM_CRM_TABLE_STORAGE_CONNECTION_STRING` (fallback `AzureWebJobsStorage`) and five
  `RNM_CRM_*_TABLE_NAME` variables. The test sets them, builds the adapter, restores them.
- The adapter swallows exceptions, so the fixture probes Azurite itself (no retries, ~3 s).
- `leadStatus` attribute is stored as `Attr_leadStatus`; `request.LeadStatus` goes to a
  separate top-level `LeadStatus` column (item #6 territory, not asserted here).
- Local Azurite is 3.14.2 (2021).

Files:
- `tests/RNM.Platform.IntegrationTests/Crm/AzuriteTableFixture.cs` (new): connection from
  `RNM_TEST_AZURITE_CONNECTION_STRING` or the explicit Azurite dev account string
  (127.0.0.1:10002); refuses any endpoint that is not localhost/`devstoreaccount1`; fast
  probe with clear "start Azurite with ..." message; unique table names and tenant IDs per
  run; deletes its own tables on dispose.
- `tests/RNM.Platform.IntegrationTests/Crm/AzuriteCollection.cs` (new): non-parallel
  collection.
- `tests/RNM.Platform.IntegrationTests/Crm/AzureTableCrmAdapterAzuriteTests.cs` (new),
  `[Trait("Category", "Azurite")]`:
  - `UpsertContact_ThenFind_ReturnsContactWithAttributes` (by phone and by email; custom
    attribute round-trips)
  - `UpsertContact_LeadStatusAttribute_IsQueryableByStatus` (first lesson)
  - `Contact_IsNotVisibleToAnotherTenant` (phone, email, status; same email in tenant B
    is a new contact; tenant A unchanged)
- `.github/workflows/ci.yml`: Azurite `services:` container on port 10002. Edited by the
  orchestrator (the implementer may not touch `.github/`).
- `.github/workflows/deploy-staging.yml`, `deploy-prod.yml`: same service, if Raisel
  approves.

No production code changes. Hot files: none (adapter is read only). Treat the three
workflow files as exclusive while this item is open; #17 starts after this merges and
reuses the fixture.

Skip vs fail: always fail, never skip (deploy workflows set `CI=true` too, so an
environment-based skip could turn a production gate green without running the test).

Extra Done-when added by the plan:
- CI log shows the three Azurite tests executed, not skipped.
- With Azurite stopped, they fail within ~5 s with the start message.
- Unit test project passes with Azurite stopped.
- Staging and prod workflows have the Azurite service or an explicit filter Raisel approved.
- No production code changed.

Open external questions: pinned Azurite image tag and default command; API version
compatibility with `Azure.Data.Tables` 12.10.0; whether local 3.14.2 supports what the
tests use.

Notes for after merge: update the platform brief (placeholder trap, local start command)
and the plan line "No test touches real Table Storage today". Item #17 adds its
300-attribute test to this fixture.

Approved by Raisel on 2026-11-03, with these decisions:
1. Plan approved as written.
2. The orchestrator adds the Azurite service to `ci.yml` in its own commit on the branch.
3. The same service goes into `deploy-staging.yml` and `deploy-prod.yml` (no test filter).
4. Local Azurite is upgraded to 3.37.0 (same as CI).

## Research

Researcher report, 2026-11-03 (official sources: Azurite repo, MCR, azure-sdk-for-net,
GitHub Actions docs).

- **CI image:** pin `mcr.microsoft.com/azure-storage/azurite:3.37.0` (latest stable,
  npm `latest`). Its default CMD starts Blob, Queue, and Table bound to `0.0.0.0`, so a
  `services:` container with `ports: - 10002:10002` and no `command` serves Table at
  `127.0.0.1:10002`. Confirmed (Dockerfile at tag v3.37.0).
- **API version:** `Azure.Data.Tables` 12.10.0 sends `2020-12-06` (confirmed, SDK
  source). Azurite 3.37.0 accepts it with no flags (confirmed, `constants.ts`). Local
  3.14.2 has baseline `2020-10-02`, so it likely rejects `2020-12-06` with 400 unless
  started with `--skipApiVersionCheck` (likely, from README rule and ChangeLog).
- **Local 3.14.2:** many Table fixes since (merge/upsert 3.16 and 3.19, query parser
  rewritten in 3.21 and 3.24, empty-string filters 3.25). Behavior for the tests'
  operations is unconfirmed on 3.14.2. 3.37.0 requires Node 22+ (Node 24 likely fine).
  Recommendation: `npm install -g azurite@3.37.0` with a fresh data folder.
- **Services container command:** GitHub Actions does support
  `services.<id>.command` and `entrypoint` (the planner's assumption was wrong). Not
  needed here.
- **`--loose`:** not needed as far as known (unconfirmed). Start without it; add it only
  if a real test fails on an unsupported header, and record a lesson.
- **Limits:** the Azurite README lists Table "Batch Transaction" and "Query with complex
  conditions" as not supported or limited. If a test fails on an `or` filter in Azurite
  but the code is right for Azure, report it; do not change production code to fit the
  emulator.
- Optional CI wait step (curl loop on port 10002) suggested, untested. The fixture's
  fast probe already gives a clear failure.

Sources:
- https://mcr.microsoft.com/v2/azure-storage/azurite/tags/list
- https://raw.githubusercontent.com/Azure/Azurite/v3.37.0/Dockerfile
- https://raw.githubusercontent.com/Azure/Azurite/main/src/table/utils/constants.ts
- https://github.com/Azure/Azurite/blob/main/ChangeLog.md
- https://raw.githubusercontent.com/Azure/azure-sdk-for-net/Azure.Data.Tables_12.10.0/sdk/tables/Azure.Data.Tables/src/TableClientOptions.cs
- https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax
- https://learn.microsoft.com/en-us/azure/storage/common/storage-install-azurite

## Implementation

Round 1, 2026-11-03. Implementer commit `ea6db3d`; orchestrator commit `04be29a`.

- New, tests only: `tests/RNM.Platform.IntegrationTests/Crm/AzuriteTableFixture.cs`,
  `AzuriteCollection.cs`, `AzureTableCrmAdapterAzuriteTests.cs` (three tests,
  `Category=Azurite`). No production code changed.
- Orchestrator: `azurite:3.37.0` service container on port 10002 in `ci.yml`,
  `deploy-staging.yml`, `deploy-prod.yml`. No test filter.
- Local runs, with Azurite on 127.0.0.1:10012 (port 10002 on this machine is taken by
  another project's Azurite):
  - Build: 0 warnings.
  - Full solution: integration 4/4 (3 Azurite + health), unit 520/520.
  - Unit tests alone: 520/520.
  - Preflight `--all --environment repository`: exit 0.
- Fail fast: with the connection pointed at an unused port, the 3 tests fail in 23 ms
  (2.4 s for the whole `dotnet test`) with "Azurite Table service is not reachable.
  Start Azurite (...) or set RNM_TEST_AZURITE_CONNECTION_STRING ...".
- Azurite 3.37.0 handled the adapter's `or` filter; no emulator limitation was hit.
- Not tested: the 3 s timeout path (a host that drops packets), and the default port
  10002 locally (CI uses it).

## Verification

Round 1, 2026-11-03, at `04be29a`: **PASS** (first pass).

- Build 0 warnings; tests 524/524 (integration 4/4 with 3 Azurite, unit 520/520; unit
  project alone 520/520); preflight passes.
- Only `tests/RNM.Platform.IntegrationTests/Crm/` and `.github/workflows/` changed.
- Fail fast: unreachable Azurite fails the 3 tests in 26 ms with the start message.
- Guard: four bad connection strings (real `*.table.core.windows.net` endpoint,
  `UseDevelopmentStorage=true`, `devstoreaccount1` with `EndpointSuffix`, foreign host)
  all rejected before any network call.
- `RNM_CRM_*` variables restored after building the adapter; `AzureWebJobsStorage` is
  never read.
- CI (run 41777031293, push of `feature/m1-p0-azurite`): Azurite 3.37.0 service
  container healthy; integration tests 4 passed, 0 skipped. Job succeeded.
- Optional hardening, not done: assert tenant B's contact ID differs from tenant A's;
  duplicate keys in the connection string give a less clear rejection message.

## Lessons

None added: the item passed on the first round and nothing was corrected. Two facts went
into the platform brief instead: port 10002 can be taken by another project's Azurite on
this machine, and test fixtures that create or delete tables must reject non-emulator
connection strings and fail instead of skip.
