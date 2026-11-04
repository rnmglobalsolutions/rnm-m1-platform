# 17: Contact attribute limit guard

Status: verified (PR pending)
Branch: feature/m1-17
Hot files: `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs` (exclusive); others per plan

Source: `docs/backlog.md`, item 17; `docs/implementation-plan.md`, Phase 1, order 1.

Done when (from the backlog):
- Known attributes stay as columns; once a row nears the limit, extra attributes are
  stored together in one JSON column and are still readable as `{{attr.name}}`.
- A lead with 300 attributes saves and reads back correctly in a test on Azurite.
- An event is logged when a tenant's leads overflow into the JSON column.

## Plan

Planner report, 2026-11-04 (at db9a97c).

Goal: a contact row never exceeds Azure Table Storage's per-row property limit. Platform
attributes always stay as `Attr_*` columns; extra attributes go into one capped JSON column;
every reader sees both transparently.

### Where `Attr_*` is written and read

Writers (all in `AzureTableCrmAdapter.cs`):
- `UpsertContactAsync` maps every request attribute to `Attr_<sanitized>`. Only writer of
  arbitrary names; the only one that needs the guard.
- `RecordOutboundAttemptAsync`, `MarkLeadReactivatedAsync`, `MarkOptOutAsync` write only
  `CrmContactAttributeNames` constants.

Readers:
- `AzureTableCrmAdapter.MaterializeContact` builds `CrmContactRecord.Attributes`; every
  application reader goes through it (`FollowUpRunService` stop conditions and `{{attr.x}}`,
  `ConfirmationApplicationService`, `ClassReminderService`, `SendWindowPolicy`,
  `CrmApplicationService.TryGetContactAttribute`, consent properties). Merging the overflow
  there covers them all.
- In the adapter, `GetAttribute`/`ReadIntAttribute` read columns only.
- `QueryContactsByAttributeAsync` filters server-side (`Attr_leadStatus`, `Attr_campaignId`),
  so those must stay real columns.
- `AzureTableReportingReadAdapter.ReadAttributeString` (line 159) reads columns only.

### Design

1. **Known attributes (always columns):** existing `CrmContactAttributeNames.PlatformManaged`
   (39 names, by reflection). No tenant or vertical names; includes every server-filtered
   attribute.
2. **Budget:** 255 max − 48 reserved (3 system, 22 fixed columns, 1 overflow, ~22 spare) =
   `MaxAttributeColumns` 207. `UnknownAttributeColumnLimit = 207 − PlatformManaged.Count`
   (168 today).
3. **Stable placement** (pure function of existing row + incoming values): known names and
   names that already have a column are written to their column (and removed from overflow
   if present there); a name already in overflow stays in overflow; a new unknown name gets a
   column while unknown columns < 168, else overflow. Never column → overflow. On read, column
   wins.
4. **Overflow column** `AttributesOverflowJson` (not `Attr_`-prefixed): JSON object of
   strings, `UnicodeRanges.All` encoder, capped at 32,000 chars; entries that do not fit are
   dropped and counted. Rewritten in full; written as "" when empty. Unparseable JSON is
   ignored by readers and replaced on next overflow write.
5. **Per-value cap:** values over 32,000 chars are truncated and counted (today they make the
   whole upsert fail and the lead is lost).
6. **Concurrency (optional):** when the overflow changes, write with ETag
   (`UpdateEntityAsync` with `ifMatch`, or `AddEntityAsync`), retry up to 3 times on 412/409.

### Telemetry

`CrmContactAttributesOverflowed = "crm.contact_attributes.overflowed"`, best effort, after a
successful write when new overflow, drops, or truncations > 0. Properties (counts only):
`tenantId`, `correlationId`, `contactCreated`, `attributeColumnCount`,
`overflowAttributeCount`, `newOverflowAttributeCount`, `droppedAttributeCount`,
`truncatedValueCount`, `overflowJsonLength`. No names, values, or `providerContactId`.

### Files

- New `src/RNM.Platform.Infrastructure/Crm/ContactAttributeStorage.cs` (internal static):
  constants, `PropertyName`, `IsAlwaysColumn`, `Place`, `ReadAll`, `ReadAttribute`.
- `AzureTableCrmAdapter.cs`: `UpsertContactAsync` uses `Place` (after
  `ShouldPreserveExistingOptOut`), event logging; `MaterializeContact` uses `ReadAll`;
  sanitizer moves to the helper.
- `AzureTableReportingReadAdapter.cs`: `ReadAttributeString` uses the helper.
- `TelemetryEventNames.cs`: one constant.
- Tests: `AzuriteTableFixture.cs` (`CreateAdapter(IEventLogger?)`, `GetContactsTableClient()`),
  new `AzureTableCrmAdapterAttributeLimitAzuriteTests.cs`, new `ContactAttributeStorageTests.cs`.
- `Program.cs`: no change.

Hot files: exclusive `AzureTableCrmAdapter.cs`; append-only `TelemetryEventNames.cs`.
Size: S, close to M with the concurrency step.

### Tests first

Unit (`ContactAttributeStorageTests`): under limit no overflow; 300 unknown on new row → 168
columns + rest overflow; known attribute last in request still a column; overflow key update
stays in overflow; existing column update stays column when full; known name found in overflow
moves to column; overflow over cap drops and counts; value > 32,000 truncated; sanitized-name
collision last wins; `ReadAll` column wins; invalid JSON ignored; budget invariants.

Azurite: 300 attributes save and read back by phone and email, raw row ≤ 255 properties, other
tenant cannot see it; update of overflowed attribute without duplicate column; `leadStatus`
stays a column and `GetLeadsByStatusAsync`, `OutboundLeadStatus`, `RecordOutboundAttemptAsync`,
`MarkLeadReactivatedAsync` work; worst-case row with all writers stays ≤ 255; overflow logs
exactly one event with counts only; (if concurrency step) concurrent upserts keep both keys.

### Risks

- Tenant isolation: overflow is in the same row (PartitionKey = tenantId); no new queries.
- Security: names sanitized (letters/digits, ≤ 48) before use as column or JSON key; values
  JSON-escaped; event has counts only.
- 1 MiB per row not guarded (upstream caps keep rows far below); rows written before #17 with
  >168 unknown columns could still exceed (no live tenant); overflow beyond cap drops (logged).

### Open external questions

- Confirm Azure Table limits (255 properties incl. system, 1 MiB entity, 64 KiB string,
  property-name rules incl. non-ASCII letters).
- Does Azurite 3.37.0 enforce them?
- Azure.Data.Tables: 412 on ETag mismatch with Merge, 409 on Add of existing entity; metadata
  keys `TableEntity` exposes after `GetEntityAsync`.

### Notes

- Sanitizing drops `_`, `-`, `.` (`first_name` → `firstname`), so `{{attr.first_name}}`
  finds nothing. Not fixed here; candidate for #1.
- Runbook section (`docs/runbooks/azure-table-crm-data.md`) goes to config-author; brief
  updates go to the orchestrator after merge.
- #1 must rebase on #17; new fixed columns come out of the 22 spare slots.

Approved by Raisel on 2026-11-04, with two decisions:
1. Overflow JSON capped at 32,000 characters; attributes beyond the cap are dropped and
   counted in the event (no multi-column overflow).
2. Concurrency step (design point 6, ETag retry) is **out**. Known limitation: two
   simultaneous upserts of the same overflowed contact can lose each other's overflow
   entries. The concurrent-upserts Azurite test is not added.
Out of scope, for #1: sanitizer drops `_`, `-`, `.`; non-ASCII letters in property names
(Azure support unconfirmed).

## Research

Researcher report, 2026-11-04.

1. Azure Table limits (official, confirmed):
   - 255 properties per entity **including** PartitionKey, RowKey, Timestamp (252 custom).
   - 1 MiB per entity; Edm.String up to 64 KiB ("about 32K characters or less").
   - Property names: case-sensitive, up to 255 chars, C# identifier rules, no dashes.
     Non-ASCII letters: **unconfirmed** (researcher suggests ASCII letters and digits only).
   - Errors: TooManyProperties 400, PropertyValueTooLarge 400, EntityTooLarge 400,
     EntityAlreadyExists 409, UpdateConditionNotSatisfied 412.
2. Azurite (main branch source; 3.37.0 tag match unconfirmed):
   - **Does not enforce 255 properties** (issue #1446 open). A 300-column row succeeds on
     Azurite, so tests must assert the raw property count themselves.
   - Rejects strings over 32,768 UTF-16 code units (400); approximates 1 MiB on the UTF-8
     request body; no property-name checks; key max 512 (Azure: 1024).
   - Branch on status codes, not error messages (issue #1832).
3. Azure.Data.Tables:
   - `UpdateEntityAsync(..., ifMatch, Merge)` with a stale ETag throws
     `RequestFailedException` 412 (Azure doc; Azurite source). `AddEntityAsync` on an
     existing entity: 409 (service error table; Azurite source).
   - `TableEntity` keys: `PartitionKey`, `RowKey`, `Timestamp`, `odata.etag`; possibly
     `odata.metadata` and `*@odata.type` (unconfirmed whether present after `GetEntityAsync`).
     Count raw properties excluding system keys, keys starting with `odata.`, and keys ending
     with `@odata.type`.

Sources: learn.microsoft.com/azure/storage/tables/scalability-targets;
learn.microsoft.com/rest/api/storageservices/understanding-the-table-service-data-model;
learn.microsoft.com/rest/api/storageservices/table-service-error-codes;
github.com/Azure/Azurite (TableHandler.ts, constants.ts, issues #1446, #1832);
learn.microsoft.com/dotnet/api/azure.data.tables.tableclient.updateentity and .addentity;
github.com/Azure/azure-sdk-for-net (Azure.Data.Tables TableEntity.cs, TableConstants.cs).

Effect on the plan: budget already counts the 3 system properties; the 32,000-char cap is
under both limits; the raw-count assertion in the Azurite tests is what catches a regression.

## Implementation

### Round 1 (implementer, 2026-11-04)

Commit `225e2be` on `worktree-agent-ac17cf29fc7bbc173`.

Files: new `ContactAttributeStorage.cs` (constants, sanitizer moved unchanged, `Place`,
`ReadAll`, `ReadAttribute`, `ContactAttributePlacement`); `AzureTableCrmAdapter.cs`
(`UpsertContactAsync` uses `Place` after opt-out preservation, logs the event best effort;
`MaterializeContact` uses `ReadAll`; blind Merge unchanged); `AzureTableReportingReadAdapter.cs`
(`ReadAttributeString` via helper); `TelemetryEventNames.cs` (+1 constant);
`AzuriteTableFixture.cs` (`CreateAdapter(IEventLogger?)`, `GetContactsTableClient()`).

Tests: `ContactAttributeStorageTests.cs` (19 unit cases) and
`AzureTableCrmAdapterAttributeLimitAzuriteTests.cs` (6 Azurite tests, each asserting the raw
stored property count ≤ 255). Concurrent-upserts test not added (decision 2).

Counts: before unit 520 + Azurite 4; after unit 539 + Azurite 10, 0 failed. Failed first: all
19 unit cases (stubs) and 4 Azurite tests (300 attributes, update overflowed, worst case,
event); `leadStatus` and few-attributes tests are guards that passed before.

Build 0 warnings; preflight pass.

Deviations: `Place` also returns `TruncatedValueCount` and `OverflowJsonLength` for the event.
Opt-out preservation still reads columns only (consent names are platform-managed, always
columns).

Notes: measured worst-case row ≈ 231 properties. Azurite `TableEntity` after `GetEntityAsync`
exposes `odata.etag`, `PartitionKey`, `RowKey`, `Timestamp`; no `odata.metadata` or
`@odata.type` keys. Known limitation: concurrent upserts of the same overflowed contact can
lose overflow entries.

## Verification

### Round 1 (verifier, 2026-11-04): PASS

At `225e2be`. Build 0 warnings; tests 549 passed (539 unit, 10 Azurite on its own Azurite);
preflight pass.

Criteria met: known attributes stay columns and extras go to one JSON column, readable by
every reader (only `MaterializeContact` via `ReadAll` and the reporting adapter via
`ReadAttribute` touch `Attr_*`; all direct column reads and server filters use platform-managed
constants); 300 attributes save and read back on Azurite with raw count ≤ 255; event has counts
only, best effort after a successful write. All four binding decisions respected.

Minor notes (not blocking):
1. Replaying identical attributes on an overflowed row rewrites the same JSON (harmless extra
   write; `NewOverflowAttributeCount` stays 0).
2. `LogAttributesOverflowedAsync`'s bare `catch` also swallows cancellation; safe because the
   contact is already saved.

Tests added: `157b12a` (cherry-picked as `aa63254`),
`tests/RNM.Platform.UnitTests/Crm/ContactAttributeStorageVerifierTests.cs`:
`Place_EscapeHeavyOverflowValues_NeverExceedJsonCap` and
`Place_ReplayingSameAttributesOnOverflowedRow_DoesNotRewriteOverflowOrCountNew` (name is
misleading: it asserts the replayed JSON is identical, see note 1).

First-pass verification: yes.

## Lessons

None. Facts (Azurite does not enforce 255 properties; server-filtered attributes must be
platform-managed constants) went into the briefs.
