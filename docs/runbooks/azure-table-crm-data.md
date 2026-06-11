# Azure Table CRM Data

The Azure Table CRM separates the current customer profile from immutable booking history.

## Tables

### `RnmContacts`

Partition key: `tenantId`

Stores the customer's current identity and latest operational context:

- name
- phone
- email
- ZIP code
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
