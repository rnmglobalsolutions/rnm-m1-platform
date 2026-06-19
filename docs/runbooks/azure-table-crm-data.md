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
