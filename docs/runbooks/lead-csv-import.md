# Lead CSV Import

Use this import for pilot clients that do not have a CRM. The import writes leads
into RNM Native CRM v0.5 for one tenant and one campaign, then the existing
outbound flow can pick eligible `opt_in` leads by `campaignId`.

Endpoint:

```text
POST /api/tenants/{tenantId}/crm/campaigns/{campaignId}/leads/import-csv
```

Headers:

```text
Content-Type: text/csv
x-rnm-api-key: <internal-api-key>
x-correlation-id: <optional-correlation-id>
```

## CSV Columns

Required:

| Column | Notes |
| --- | --- |
| `firstName` | Lead first name |
| `lastName` | Lead last name |
| `phone` | E.164 or US phone number. US 10-digit numbers are normalized to `+1...` |

Consent:

| Column | Notes |
| --- | --- |
| `consentStatus` | Allowed values: `opt_in`, `unknown`, `opted_out` |
| `consentBasis` | Optional documentation column. If `consentStatus` is missing or unclear, imported consent is `unknown` |

Optional:

| Column | Notes |
| --- | --- |
| `email` | May be empty |
| `leadSource` | Defaults to `csv_import` |
| `intent` | `buyer`, `seller`, `renter`, or `unknown` |
| `targetPropertyAddress` | Property of interest |
| `assignedAgent` | Agent/user assignment |
| `estimatedValue` | Stored as contact attribute |
| `timeZone` | Used by outbound TCPA checks when present |

Example:

```csv
firstName,lastName,phone,email,leadSource,intent,targetPropertyAddress,assignedAgent,estimatedValue,timeZone,consentStatus
Jane,Seller,3052445176,jane@example.com,zillow,seller,"123 Main St, Miami, FL",Alex,450000,America/New_York,opt_in
Bob,Buyer,+13055550123,,old_database,buyer,,Alex,,America/New_York,unknown
Pat,Owner,3055550188,pat@example.com,expired_listing,seller,"900 Ocean Dr, Miami, FL",Alex,725000,America/New_York,opted_out
```

## Behavior

- Import is scoped to `tenantId` and `campaignId`.
- One bad row does not abort the import.
- Duplicate detection uses existing CRM phone/email lookup within the tenant.
- Existing contacts are updated instead of duplicated.
- `consentStatus` is never defaulted to `opt_in`.
- `unknown` imports are stored but are not eligible for outbound calls/SMS.
- Existing `opted_out` contacts remain `opted_out` even if re-imported as `opt_in`.
- `opted_out` rows are imported, not dropped, so suppression is explicit.
- A `lead.imported` timeline event is written for each imported/updated lead.

Response includes:

- total rows
- created count
- updated count
- skipped count
- consent breakdown: `opt_in`, `unknown`, `opted_out`
- row-level skipped errors
- `tenantId`
- `campaignId`
- `correlationId`

Current pilot limit: request body is capped at 2 MB. Larger imports should be
split into smaller CSV files.
