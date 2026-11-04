# ADR 0004: CRM Storage, Table Storage Now, Azure SQL Later

## Status

Accepted (2026-11-02)

## Context

M1 stores contacts, bookings, timeline events, follow-ups, class sessions, and lead
receipts in Azure Table Storage, one partition per tenant.

Table Storage has no secondary indexes, joins, aggregates, or text search, and a row
can hold at most 255 properties. Today:

- the monthly report reads a tenant's whole history and filters dates in memory;
- the follow-up runner scans all of a tenant's follow-up rows every 5 minutes;
- each incoming lead field becomes its own contact column, so a lead with many fields
  can fail to save.

The number of tenants is not the limit (one partition each). The limit is how much
history each tenant accumulates. Estimate, not measured: 20 to 50 small or normal
tenants (up to about 1,000 leads per month each) through their first year with the
current code; hundreds with date-range queries and archiving.

## Decision

1. Keep Table Storage for M1.
2. Fix lead loss now: backlog item 17 (attribute limit guard).
3. Fix scaling only when measured: backlog item 19, triggered by a monthly report over
   30 seconds or a follow-up run over 60 seconds for one tenant.
4. Move to **Azure SQL Database** together with the UI, in **Platform RNM 2.0**, or
   earlier if one of these
   appears:
   - screens with search and filters (dashboard, inbox, lead list)
   - custom reports, BI exports, or cross-tenant analytics
   - a tenant compliance requirement (export, deletion, audit) that Table Storage makes
     hard

Azure SQL, not Cosmos DB: the CRM is relational (contact, bookings, timeline, class
registrations) and the reports are counts and sums. Cosmos DB fits massive writes,
global distribution, or very loose schemas, which M1 does not need.

## Cost (Azure retail prices, East US, checked 2026-11-02)

Table Storage, LRS (what M1 uses):

| Meter | Price |
|---|---|
| Data stored | $0.045 per GB per month |
| Writes | $0.025 per 10,000 |
| Reads | $0.005 per 10,000 |
| Scans and lists | $0.09 per 10,000 |

Estimate per normal tenant (500 leads per month): about $0.50 to $1 per month, most of
it the follow-up runner's scans every 5 minutes. About $25 to $50 per month for 50
tenants.

Azure SQL Database:

| Option | Price | Fit for M1 |
|---|---|---|
| Free offer (serverless) | $0. Per database per month: 100,000 vCore-seconds (about 28 vCore-hours), 32 GB data, 32 GB backup | Tests only. M1's 5-minute timer keeps the database awake, so the free compute runs out in about 2 days |
| Standard S0 (10 DTU, provisioned) | $0.4839 per day, about $14.70 per month | Enough to start with all tenants in one database; scale to S1 or S2 as load grows |
| General Purpose serverless | $0.5218 per vCore-hour, minimum 0.5 vCore; storage $0.115 per GB per month | The 5-minute timer prevents auto-pause, so about $190 per month minimum. Not worth it for M1's load |

## Consequences

- The Azure bill is not the reason to wait: S0 costs about the same order as Table
  Storage at M1's scale. The real cost of moving is engineering: five Table Storage
  adapters (CRM, reporting, follow-ups, classes, lead receipts) plus a data migration.
- That cost is worth paying when the UI arrives, because the UI needs SQL anyway.
- Prices are retail list prices and change; check the Azure pricing API before deciding.
