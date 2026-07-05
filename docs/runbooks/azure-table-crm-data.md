# RNM Native CRM v0.5 Data

The RNM Native CRM v0.5 uses Azure Table Storage as the internal CRM ledger for M1.
It is intentionally small: contacts, bookings, notes, and timeline events. It is not
a HubSpot, GoHighLevel, ServiceTitan, or Jobber replacement.

The native CRM separates the current customer profile from immutable booking history
and a lightweight event timeline.

This data model is persisted only when the tenant CRM provider is `AzureTable`. External
CRM adapters may sync contacts, notes, tags, or appointments to their own systems, but they
must not be treated as storage for RNM Native CRM timeline or follow-up state unless that
adapter explicitly implements those operations.

## Tables

### `RnmContacts`

Partition key: `tenantId`

Stores the customer's current identity and latest operational context:

- name
- phone
- email
- ZIP code
- lead status
- follow-up flag and reason
- follow-up date when available
- last interaction date
- latest service need
- latest property type
- latest service address
- latest urgency
- latest preferred time
- lead source
- campaign ID
- outbound lead status
- outbound attempt count
- last contacted date
- next follow-up date
- intent
- target property address
- assigned agent
- consent status
- tags
- last provider booking ID
- last booking start and end
- last booking state
- correlation ID

Raw transcripts and provider payloads are not stored.

Supported lead statuses are intentionally simple:

- `New`
- `Qualified`
- `AppointmentScheduled`
- `NeedsFollowUp`
- `Booked`
- `Lost`

Outbound lead attributes are stored as generic contact attributes, not as
real-estate-specific columns. Supported outbound statuses are:

- `new`
- `contacted`
- `qualified`
- `appointment_booked`
- `reactivated`
- `nurturing`
- `not_contacted`
- `dead`

Supported intent values are:

- `buyer`
- `seller`
- `renter`
- `unknown`

Supported consent values are:

- `opt_in`
- `unknown`
- `opted_out`

`opted_out` is a hard stop for outbound interaction recording and next-lead
selection. Opt-out can be recorded by provider contact ID, phone number, or email.
If a STOP/opt-out arrives before a contact exists, the native CRM creates a minimal
contact row with `consentStatus=opted_out` so the signal is not lost.

For Twilio SMS opt-out capture, configure the number's inbound message webhook to:

`/api/tenants/{tenantId}/webhooks/twilio/sms-inbound`

The endpoint validates the Twilio signature and marks opt-out for standard STOP words:
`STOP`, `STOPALL`, `UNSUBSCRIBE`, `CANCEL`, `END`, and `QUIT`.

Outbound call-list access patterns are tenant scoped and query only within
`PartitionKey = tenantId`:

- leads by outbound status
- leads by campaign ID
- next lead to call for a campaign

`getNextLeadToCall` excludes `opted_out` contacts, respects the max outbound
attempt limit, and waits until `nextFollowUpAt` when that value is present.

The phone/email secondary index table is intentionally deferred for the first pilot.
Current lookup and outbound list sizes are expected to be small enough to query within
the tenant partition. Add `RnmContactPhoneIndex` only when volume makes phone/email
deduplication a measured bottleneck.

### `RnmBookings`

Partition key: `tenantId`

Row keys are derived from the booking provider and provider booking ID. Replaying the same
successful CRM synchronization updates the existing booking instead of creating a duplicate.

Each booking stores:

- provider contact and booking IDs
- booking provider and source
- customer name, phone, and email snapshot
- service type
- property type
- service address and ZIP code
- urgency
- preferred time or window
- selected booking label
- appointment start and end
- timezone
- booking, qualification, and service-area states
- tenant, vertical, and correlation identifiers
- creation and update timestamps

Set `RNM_CRM_BOOKINGS_TABLE_NAME` to override the default table name. The legacy
`RNM_CRM_BOOKING_LINKS_TABLE_NAME` setting remains supported as a fallback.

### `RnmContactNotes`

Stores interaction outcomes linked to the CRM contact. Notes must remain concise and must not
contain raw transcripts or unnecessary sensitive data.

### `RnmTimelineEvents`

Partition key: `tenantId`

Stores a lightweight timeline for CRM events. The timeline is best-effort and must not block
booking or confirmation delivery.

Events currently include:

- `lead.qualified`
- `booking.created`
- `followup.required`
- `outbound.attempt_recorded`
- `lead.reactivated`
- `consent.opted_out`
- `sms.sent`
- `email.sent`

Each event stores:

- provider contact ID when available
- provider booking ID when available
- event type
- source
- summary
- metadata JSON
- created timestamp
- correlation ID

Set `RNM_CRM_TIMELINE_TABLE_NAME` to override the default table name.

## Pilot Reporting v0.5

The pilot reporting endpoint reads the native CRM tables directly and computes metrics on
request. It does not pre-aggregate, cache, send scheduled reports, or generate PDFs.

Endpoint:

`GET /api/tenants/{tenantId}/reports/pilot?from={isoDate}&to={isoDate}`

Authentication:

- `x-rnm-api-key`

The report is tenant-scoped and reads only rows where `PartitionKey = tenantId`.

Required tenant configuration for projected revenue and baseline comparison is optional:

- `reporting.closeRate`
- `reporting.avgCommissionValue`
- `reporting.baseline.leadsContactedPerWeek`
- `reporting.baseline.avgContactTimeSeconds`
- `reporting.baseline.appointmentsPerWeek`

If baseline values are absent, the endpoint returns safe zero metrics and labels the
baseline section as `baseline_not_set`. Revenue values are always projected, never actual.
