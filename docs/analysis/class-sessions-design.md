# Live Class Sessions and Registration Design

## Executive Summary

The design is sound only if `ClassSession` and `ClassRegistration` are treated as new CRM-adjacent entities, not as `book_appointment`; the existing booking model is one lead to one appointment, while this funnel is one session to many registrants. The platform already has strong reusable pieces: tenant-scoped CRM storage, contact dedup, consent states, Twilio/SendGrid senders, confirmation templates, reporting reads, and safe telemetry. The single biggest risk is the public registration endpoint, because the current internal API-key model cannot be exposed to a browser and the repository has no reusable rate-limiting mechanism. Reminders also need new scheduling infrastructure because the only deferred pattern present is the confirmation retry queue, not a timer-based due-work runner. No Zoom API should be built in v1; storing a manually created Zoom join URL on `ClassSession` is consistent with the current adapter discipline and avoids burning time on plumbing.

## Reuse Inventory

- CRM port: `ICrmAdapter` already supports contact lookup, upsert, timeline events, booking links, opt-out, outbound attempts, and lead queries in `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:5`.
- Native CRM contact storage: `AzureTableCrmAdapter` uses tenant-scoped tables named `RnmContacts`, `RnmContactNotes`, `RnmBookings`, `RnmTimelineEvents`, and `RnmContactPhoneIndex` in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:16`.
- Contact dedup: phone lookup uses `RnmContactPhoneIndex` first and falls back to tenant-partition contact scanning in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:781`.
- Contact upsert: contacts are stored with `PartitionKey = tenantId`, dynamic `Attr_` fields, and a phone index update after the contact write in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:127` and `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:147`.
- Timeline model: timeline events are already tenant-partitioned and include contact, booking, event type, source, metadata JSON, created time, and correlation ID in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:340`.
- Consent states: the code defines `opt_in`, `unknown`, and `opted_out` in `src/RNM.Platform.Application/Crm/CrmModels.cs:409`.
- STOP handling: inbound Twilio SMS validates the Twilio signature, detects opt-out keywords, and calls `MarkOptOutAsync` in `src/RNM.Platform.Api/Functions/TwilioSmsStatusWebhookFunction.cs:205` and `src/RNM.Platform.Api/Functions/TwilioSmsStatusWebhookFunction.cs:234`.
- Customer notification senders: SMS and email are behind `ISmsSender` and `IEmailSender` in `src/RNM.Platform.Application/Ports/Messaging/ISmsSender.cs:5` and `src/RNM.Platform.Application/Ports/Messaging/IEmailSender.cs:5`.
- Confirmation templating: the confirmation service renders universal tokens and dynamic `{{attr.*}}` contact attribute tokens in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:432` and `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:455`.
- Confirmation send gating: customer SMS and customer email are skipped for `opted_out` contacts in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:67` and `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:159`.
- Business notifications: business SMS and email are already separate sends after customer sends in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:57`.
- Reporting read pattern: pilot reporting reads Contacts, Bookings, and TimelineEvents per tenant through `IReportingReadAdapter` in `src/RNM.Platform.Application/Ports/Reporting/IReportingReadAdapter.cs:5`.
- Reporting computation: pilot reporting computes on read from tenant-scoped records without pre-aggregation in `src/RNM.Platform.Application/Reporting/PilotReportingService.cs:35`.
- Tenant config: tenant configuration already includes providers, secrets, communication templates, reporting, and outbound voice config in `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`.
- Runtime tenant config binding: tenant JSON files are loaded from `config/tenants/{tenantId}.json` in `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:36`.

## Current-State Findings

### CRM Contact Model

`ICrmAdapter` exposes contact lookup, contact upsert, notes, tags, booking links, timeline events, follow-up, outbound lead selection, outbound attempt recording, reactivation, and opt-out methods in `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:5`. The contact upsert request stores tenant, vertical, correlation, provider contact ID, phone, email, name, ZIP, and a generic attribute map in `src/RNM.Platform.Application/Crm/CrmModels.cs:19`. `CrmContactRecord` exposes phone, email, name, ZIP, attributes, consent, campaign ID, attempt count, last contacted time, and next follow-up time in `src/RNM.Platform.Application/Crm/CrmModels.cs:124`.

Native CRM table names are defined in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:16`. The native adapter stores contacts with `PartitionKey = tenantId` and the contact RowKey as the provider contact ID in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:127`. It stores arbitrary contact attributes as `Attr_` properties in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:147`. Contact lookup uses the phone index first, then falls back to phone/email lookup inside the tenant partition in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:57` and `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:86`.

Bookings are persisted through `LinkBookingToContactAsync`, which writes to `RnmBookings` with `PartitionKey = tenantId` and a generated booking RowKey in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:245` and `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`. Booking fields include provider contact ID, provider booking ID, vertical, provider, source, customer name, phone, email, service type, property type, address, ZIP, urgency, preferred window, label, timezone, states, correlation ID, starts at, and ends at in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:266`. The current booking link request has no `SourceSessionId` or `SourceRegistrationId` field in `src/RNM.Platform.Application/Crm/CrmModels.cs:62`.

### Consent State Machine

The consent states are `opt_in`, `unknown`, and `opted_out` in `src/RNM.Platform.Application/Crm/CrmModels.cs:409`. `RecordInboundMarketingConsentAsync` requires phone or email, validates the consent scope, looks up or creates the contact, writes a consent timeline event, and only updates the contact to `opt_in` when the request is granted and not blocked by an existing opt-out in `src/RNM.Platform.Application/Crm/CrmApplicationService.cs:315`. An already `opted_out` contact can only be reversed when `Granted` is true and `IsPersonInitiatedInbound` is true in `src/RNM.Platform.Application/Crm/CrmApplicationService.cs:433`. If that strict reversal condition is not met, the contact remains opted out and the method returns without updating the contact in `src/RNM.Platform.Application/Crm/CrmApplicationService.cs:488`.

`MarkOptOutAsync` sets `Attr_consentStatus = opted_out`, persists `Attr_consentOptedOutAt`, and logs a `consent.opted_out` timeline event in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:607` and `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:627`. Twilio inbound SMS validates the request signature before processing in `src/RNM.Platform.Api/Functions/TwilioSmsStatusWebhookFunction.cs:205`. When the incoming message is an opt-out keyword, the function calls `MarkOptOutAsync` with source `TwilioSmsInbound` in `src/RNM.Platform.Api/Functions/TwilioSmsStatusWebhookFunction.cs:234`.

Send gating exists in four places. Customer SMS is skipped for opted-out contacts in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:67`. Customer email is also skipped for opted-out contacts in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:159`. Outbound campaign execution skips opted-out leads before starting a call in `src/RNM.Platform.Application/Outbound/OutboundCampaignRunService.cs:124`. The Vapi outbound adapter also rejects opted-out contacts if a caller passes one directly to the adapter in `src/RNM.Platform.Infrastructure/Voice/VapiOutboundCallAdapter.cs:58`.

### Messaging

SMS is sent through `ISmsSender` in `src/RNM.Platform.Application/Ports/Messaging/ISmsSender.cs:5`. Email is sent through `IEmailSender` in `src/RNM.Platform.Application/Ports/Messaging/IEmailSender.cs:5`. Booking confirmation requests carry tenant, vertical, correlation ID, customer contact data, service/address fields, timezone, booking decision, templates, and contact attributes in `src/RNM.Platform.Application/Confirmations/ConfirmationModels.cs:7`.

The confirmation service loads the contact, merges CRM attributes into the render context, checks opt-out state, and then sends customer SMS, customer email, business email, and business SMS in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:49`. The template renderer includes universal tokens such as tenant, business name, customer info, booking info, provider booking ID, and online meeting URL in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:432`. It also supports dynamic `{{attr.<name>}}` tokens from contact attributes in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:455`.

Twilio uses the tenant communication config for the from number in `src/RNM.Platform.Infrastructure/Messaging/TwilioSmsSender.cs:33` and reads the Twilio Account SID/Auth Token secrets by tenant secret names in `src/RNM.Platform.Infrastructure/Messaging/TwilioSmsSender.cs:43`. SendGrid uses tenant communication config for the from address in `src/RNM.Platform.Infrastructure/Messaging/SendGridEmailSender.cs:45`. Confirmation failures schedule retry messages in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:106`. The queue retry function processes `confirmation-retries` with an Azure Storage Queue trigger in `src/RNM.Platform.Api/Functions/ConfirmationRetryFunction.cs:29`.

### Scheduling

There is exactly one scheduled/deferred work mechanism in the code found for this review: `ConfirmationRetryFunction` uses an Azure Storage Queue trigger in `src/RNM.Platform.Api/Functions/ConfirmationRetryFunction.cs:29`. Timer-triggered Azure Functions are not present in this repository. Durable Functions are not present in this repository. Service Bus triggers are not present in this repository.

The existing retry queue scheduler writes confirmation retry messages to the `confirmation-retries` queue in `src/RNM.Platform.Infrastructure/Messaging/AzureQueueConfirmationRetryScheduler.cs:11`. That scheduler currently sends messages with a zero visibility delay in `src/RNM.Platform.Infrastructure/Messaging/AzureQueueConfirmationRetryScheduler.cs:43`. This is not enough for T-24h and T-1h class reminders without extending or adding a due-work runner.

### TCPA / Send Window Enforcement

Outbound campaign runs resolve a lead timezone from contact attributes named `timeZone`, `timezone`, `leadTimeZone`, or `leadTimezone`, and fall back to the tenant timezone in `src/RNM.Platform.Application/Outbound/OutboundCampaignRunService.cs:312`. TCPA window checks convert `utcNow` into that timezone and require the local hour to be greater than or equal to the configured start hour and less than the configured end hour in `src/RNM.Platform.Application/Outbound/OutboundCampaignRunService.cs:335`. The default TCPA window comes from tenant voice config and defaults to 8 through 21 in `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:117`.

This logic is currently private static logic inside `OutboundCampaignRunService`, not a shared policy service, in `src/RNM.Platform.Application/Outbound/OutboundCampaignRunService.cs:312` and `src/RNM.Platform.Application/Outbound/OutboundCampaignRunService.cs:335`. Class reminder SMS must either reuse this logic by extracting a small shared policy or duplicate it carefully; duplicating it would increase compliance risk.

### Booking Entity

The booking application model supports checking availability and creating a booking through `BookingAvailabilityRequest` and `CreateBookingRequest` in `src/RNM.Platform.Application/Booking/BookingModels.cs:5` and `src/RNM.Platform.Application/Booking/BookingModels.cs:30`. `CreateBookingResult` and `BookingDecisionResult` already have `OnlineMeetingUrl` in `src/RNM.Platform.Application/Booking/BookingModels.cs:41` and `src/RNM.Platform.Application/Booking/BookingModels.cs:73`.

CRM booking persistence is driven by `CrmBookingLinkRequest` in `src/RNM.Platform.Application/Crm/CrmModels.cs:62`. The request is created from a booking decision in `src/RNM.Platform.Application/Crm/CrmApplicationService.cs:617`. The Azure Table write stores one booking row in `RnmBookings` and then updates summary fields on the contact in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264` and `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:297`. `SourceSessionId` belongs on `CrmBookingLinkRequest`, the `RnmBookings` entity, and probably a future class attribution read model; adding it is a small schema-additive change because Azure Table entities tolerate new properties.

### Public HTTP Surface

Most internal endpoints are anonymous at the Azure Functions binding level but enforce `x-rnm-api-key` in code. Lead CSV import checks `x-rnm-api-key` in `src/RNM.Platform.Api/Functions/LeadCsvImportFunction.cs:53`. Pilot reporting checks `x-rnm-api-key` in `src/RNM.Platform.Api/Functions/PilotReportFunction.cs:49`. Outbound campaign run checks `x-rnm-api-key` in `src/RNM.Platform.Api/Functions/OutboundCampaignRunFunction.cs:49`.

The existing public browser-facing contact form endpoint is anonymous and cannot use an internal API key; it instead checks the Origin header against an allowlist in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:60`. It also enforces a 16 KB body limit in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:18`, rejects oversized payloads in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:73`, validates required fields in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:183`, and uses a honeypot field in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:97`. CORS origins are configured for a separate contact Function App in `infra/main.bicep:29`, `infra/main.bicep:138`, and `infra/modules/functionApp.bicep:134`.

Reusable rate limiting is not present in this repository. A public registration endpoint needs rate limiting or equivalent abuse protection before paid traffic is sent to it.

### Reporting

Pilot reporting reads contacts, bookings, and timeline events per tenant through `IReportingReadAdapter` in `src/RNM.Platform.Application/Ports/Reporting/IReportingReadAdapter.cs:5`. The Azure Table reporting adapter queries contacts by tenant partition in `src/RNM.Platform.Infrastructure/Reporting/AzureTableReportingReadAdapter.cs:52`. It queries bookings by tenant partition and filters the date range in memory in `src/RNM.Platform.Infrastructure/Reporting/AzureTableReportingReadAdapter.cs:84`. It queries timeline events by tenant partition and filters the date range in memory in `src/RNM.Platform.Infrastructure/Reporting/AzureTableReportingReadAdapter.cs:120`.

The reporting service computes speed-to-contact, funnel, projected revenue, activity, baseline, and summary after reading the dataset in `src/RNM.Platform.Application/Reporting/PilotReportingService.cs:53`. This supports small pilot volumes. It is not optimized for large historical analytics because bookings and timeline events are scanned per tenant and date-filtered in code.

### Tenant Configuration

Tenant config includes tenant ID, vertical ID, business name, timezone, service area, providers, secret names, communication, reporting, and voice config in `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`. Tenant JSON binds at runtime from `config/tenants/{tenantId}.json` in `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:36`. The Kenny tenant uses `GoogleCalendar`, `Twilio`, `SendGrid`, and the native CRM provider in `config/tenants/kenny-commercial-real-estate.json:11`. The Kenny tenant currently has no class/session configuration in `config/tenants/kenny-commercial-real-estate.json:1`.

## Proposed Design

### New Entities

Add CRM-adjacent Application DTOs and an Azure Table adapter, not a formal Domain aggregate.

`ClassSession`

- TenantId
- SessionId
- CorrelationId
- VerticalId
- CampaignId
- Title
- Description
- StartsAtUtc
- EndsAtUtc
- TimeZone
- ZoomJoinUrl
- HostName
- Status: `draft`, `open`, `closed`, `completed`, `cancelled`
- Reminder24hEnabled
- Reminder1hEnabled
- AttendanceMinimumMinutes
- RegistrationSourceUrl
- CreatedAt
- UpdatedAt

Table: `RnmClassSessions`

- PartitionKey: `tenantId`
- RowKey: `session-{startsAtUtc:yyyyMMddHHmmss}-{sessionId}`

This key supports upcoming sessions for a tenant with a RowKey range query, while `SessionId` is also stored as a property for exact filtering when needed.

`ClassRegistration`

- TenantId
- RegistrationId
- SessionId
- ContactId
- CampaignId
- CorrelationId
- Status: `registered`, `confirmed`, `reminded_24h`, `reminded_1h`, `attended`, `no_show`, `cancelled`
- CustomerName
- PhoneNumber
- Email
- ConsentStatusAtRegistration
- ConsentGranted
- ConsentDisclosureText
- ConsentDisclosureVersion
- ConsentCapturedAt
- ConsentChannel: `web_registration`
- ConsentSourceUrl
- RegistrationSourceUrl
- RegistrationUserAgentHash
- RegisteredAt
- ConfirmationSmsSentAt
- ConfirmationEmailSentAt
- Reminder24hSmsSentAt
- Reminder24hEmailSentAt
- Reminder1hSmsSentAt
- Reminder1hEmailSentAt
- AttendanceImportId
- Attended
- AttendanceDurationMinutes
- FirstJoinAt
- LastLeaveAt
- ZoomDisplayName
- AttendanceMatchMethod
- AttendanceMatchConfidence
- CreatedAt
- UpdatedAt

Table: `RnmClassRegistrations`

- PartitionKey: `tenantId`
- RowKey: `session-{sessionId}|contact-{contactId}`

This RowKey is the duplicate protection for the same person registering twice for the same session. The service uses an idempotent upsert or a create-if-not-exists flow with this deterministic key.

Secondary index: `RnmClassRegistrationsByContact`

- PartitionKey: `tenantId`
- RowKey: `contact-{contactId}|session-{sessionId}`
- Value properties: `RegistrationRowKey`, `SessionId`, `RegisteredAt`, `Status`

This supports all registrations for a contact without scanning every registration for the tenant.

Reminder due table: `RnmClassReminderDue`

- PartitionKey: `tenantId`
- RowKey: `due-{dueAtUtc:yyyyMMddHHmmss}|session-{sessionId}|contact-{contactId}|{kind}`
- Properties: `SessionId`, `ContactId`, `RegistrationRowKey`, `Kind`, `DueAtUtc`, `Status`, `LockedUntilUtc`, `AttemptCount`, `LastAttemptAt`, `SentAt`, `CorrelationId`

This supports due reminder discovery per tenant and avoids scanning every registration. It also gives a place to lock/claim work before sending.

Unmatched attendance table: `RnmClassAttendanceUnmatched`

- PartitionKey: `tenantId`
- RowKey: `session-{sessionId}|import-{importId}|row-{rowNumber}`
- Properties: Zoom name, Zoom email, join time, leave time, duration, reason, created at, correlation ID

This keeps bad or ambiguous Zoom rows from disappearing silently.

### New Ports

Add a small `IClassSessionAdapter` or `IClassRegistrationAdapter` in Application Ports:

- `UpsertSessionAsync`
- `GetSessionAsync`
- `GetUpcomingSessionsAsync`
- `RegisterContactForSessionAsync`
- `GetRegistrationsForSessionAsync`
- `GetRegistrationsForContactAsync`
- `GetDueRemindersAsync`
- `ClaimReminderAsync`
- `MarkReminderSentAsync`
- `MarkReminderSkippedAsync`
- `ImportAttendanceAsync`
- `GetSessionFunnelAsync`

This should be separate from `ICrmAdapter`. CRM owns contacts, consent, bookings, and timeline. Class registration owns session attendance and reminder state.

### Endpoint Contracts

Public registration endpoint:

`POST /api/tenants/{tenantId}/classes/{sessionId}/registrations`

Request body:

```json
{
  "fullName": "Jane Doe",
  "phoneNumber": "+13055550100",
  "email": "jane@example.com",
  "zipCode": "78041",
  "answers": {
    "financialGoal": "retirement",
    "incomeRange": "optional"
  },
  "consent": {
    "granted": true,
    "disclosureText": "I agree to receive class reminders and follow-up messages from ... Reply STOP to opt out.",
    "disclosureVersion": "financial-class-2026-01",
    "sourceUrl": "https://example.com/masterclass"
  },
  "idempotencyKey": "browser-generated-guid",
  "companyWebsiteConfirm": ""
}
```

Response:

```json
{
  "registered": true,
  "tenantId": "tenant",
  "sessionId": "session",
  "registrationId": "registration",
  "correlationId": "correlation",
  "confirmation": {
    "sms": "sent|skipped|failed",
    "email": "sent|skipped|failed"
  }
}
```

Internal attendance import endpoint:

`POST /api/tenants/{tenantId}/classes/{sessionId}/attendance/import-csv`

Authentication: `x-rnm-api-key`, matching the internal endpoint pattern in `src/RNM.Platform.Api/Functions/LeadCsvImportFunction.cs:53`.

Internal class report endpoint:

`GET /api/tenants/{tenantId}/reports/classes/{sessionId}`

Authentication: `x-rnm-api-key`, matching pilot reports in `src/RNM.Platform.Api/Functions/PilotReportFunction.cs:49`.

### Config Shape

Add optional tenant config:

```json
{
  "classes": {
    "registrationAllowedOrigins": ["https://example.com"],
    "defaultAttendanceMinimumMinutes": 15,
    "reminders": {
      "enabled": true,
      "send24h": true,
      "send1h": true
    },
    "templates": {
      "confirmationSmsBodyTemplate": "...",
      "confirmationEmailSubjectTemplate": "...",
      "confirmationEmailBodyTemplate": "...",
      "reminder24hSmsBodyTemplate": "...",
      "reminder24hEmailSubjectTemplate": "...",
      "reminder24hEmailBodyTemplate": "...",
      "reminder1hSmsBodyTemplate": "...",
      "reminder1hEmailSubjectTemplate": "...",
      "reminder1hEmailBodyTemplate": "..."
    }
  }
}
```

The code currently has no `Classes` config section in `TenantConfiguration` in `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`, so this is an additive config extension.

### Booking Attribution Additions

Add these optional fields to `CrmBookingLinkRequest` and `RnmBookings`:

- `SourceSessionId`
- `SourceRegistrationId`
- `SourceCampaignId`

Current `CrmBookingLinkRequest` has `Source` but no session attribution fields in `src/RNM.Platform.Application/Crm/CrmModels.cs:62`. Current `RnmBookings` writes booking fields but no session attribution fields in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:266`.

## Design Answers

### 1. Entity Design

Use `RnmClassSessions`, `RnmClassRegistrations`, `RnmClassRegistrationsByContact`, `RnmClassReminderDue`, and `RnmClassAttendanceUnmatched`. All tables keep `PartitionKey = tenantId`, consistent with the CRM table design in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:127`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`, and `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:340`.

Upcoming sessions for a tenant are supported by `RnmClassSessions` RowKey prefix `session-{startsAtUtc}`. All registrations for a session are supported by `RnmClassRegistrations` RowKey prefix `session-{sessionId}|`. All registrations for a contact are supported by `RnmClassRegistrationsByContact` RowKey prefix `contact-{contactId}|`. Registrations due for reminder are supported by `RnmClassReminderDue` RowKey prefix `due-{dueAtUtc}`.

### 2. Contact Relationship

The registration service should first upsert or find the contact through `ICrmAdapter.FindContactByPhoneOrEmailAsync`, which already exists in `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:7`. For phone dedup, the native adapter uses `RnmContactPhoneIndex` first in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:781`. The registration RowKey `session-{sessionId}|contact-{contactId}` makes a duplicate registration for the same session deterministic.

If the same person registers twice for the same session, the service returns the existing registration and may update mutable fields such as name or email. If the same person registers for three sessions, the system creates three registration rows with different session IDs and the same contact ID. There is no cross-table transaction, so the source-of-truth write order should be contact first, registration second, indexes/reminders third; failed index/reminder writes must become repairable timeline or telemetry warnings, following the existing fail-safe pattern where phone index write failure does not fail contact upsert in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:898`.

### 3. Consent at Registration

Persist the exact disclosure text shown, disclosure version, timestamp, channel `web_registration`, source URL, granted value, and consent status at registration. Use the existing consent states in `src/RNM.Platform.Application/Crm/CrmModels.cs:409`.

For a contact with `unknown`, explicit checkbox grant can set `consentStatus = opt_in` through a new registration-specific service path that uses the same CRM contact upsert model in `src/RNM.Platform.Application/Crm/CrmModels.cs:19` and writes a timeline event using the existing timeline model in `src/RNM.Platform.Application/Crm/CrmModels.cs:252`. For a contact already `opted_out`, do not reverse from a web form under the current rule, because the code only allows reversal when the signal is granted and person-initiated inbound in `src/RNM.Platform.Application/Crm/CrmApplicationService.cs:433`. Record the registration, keep the contact opted out, log a consent conflict timeline event, and skip outbound SMS/email sends unless the business explicitly changes the consent policy later.

### 4. Public Registration Endpoint

The endpoint cannot use `x-rnm-api-key` because a browser page would expose that key to every visitor. Internal endpoints can use `x-rnm-api-key` because they are not browser-exposed, as shown by lead import in `src/RNM.Platform.Api/Functions/LeadCsvImportFunction.cs:53` and pilot reports in `src/RNM.Platform.Api/Functions/PilotReportFunction.cs:49`.

The registration endpoint should copy the public contact form safety posture: Origin allowlist, body size limit, JSON validation, field length validation, honeypot, and safe errors. The public contact function already implements Origin rejection in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:60`, a body limit in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:18`, and honeypot handling in `src/RNM.Platform.Api/Functions/ContactSystemReviewFunction.cs:97`.

Required additions beyond the existing contact endpoint pattern:

- Per-session idempotency key.
- Deterministic duplicate protection using contact ID plus session ID.
- Rate limiting by tenant, source IP hash, email hash, and phone hash.
- Spam bot scoring or captcha support if paid traffic starts generating junk.
- PII-safe logging: never log full phone, email, raw answers, or full disclosure payloads.

Honest estimate: 10 to 16 hours for production-safe first version, because this is the first true public lead-ingest endpoint that writes CRM data and sends messages.

### 5. Reminders

Create reminder due rows at registration time for T-24h and T-1h if the session and tenant config enable them. Discover due reminders through `RnmClassReminderDue` by tenant and RowKey range. Prevent double sends by claiming a reminder row with an optimistic update from `pending` to `sending`, including `LockedUntilUtc`; after provider success, mark it `sent` with provider message ID and sent time.

Consent must be checked at send time, not at registration time. Customer sends already skip opted-out contacts in `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:67` and `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:159`; class reminders should apply the same rule. TCPA must also be checked at send time. The existing TCPA logic is private to outbound campaigns in `src/RNM.Platform.Application/Outbound/OutboundCampaignRunService.cs:335`, so a shared send-window policy is required before reminder SMS is production-safe.

If a T-24h SMS is outside the allowed window, delay it to the next allowed window if that still occurs before the session. If a T-1h SMS is outside the allowed window and no legal send window remains, skip SMS, log `class.reminder.skipped_outside_tcpa_window`, and still send email if consent rules allow email. The existing scheduling pattern does not support this because there is no TimerTrigger and the confirmation retry queue currently enqueues immediate retry messages in `src/RNM.Platform.Infrastructure/Messaging/AzureQueueConfirmationRetryScheduler.cs:43`.

### 6. Attendance Import

Use manual CSV import from Zoom participant reports. Matching is unreliable by design because display names are user-editable and email can be missing.

Match precedence:

1. Exact normalized email match to a registration email for that session.
2. Exact normalized email match to CRM contact email, then registration by contact ID for that session.
3. Exact normalized display name match to a single registration for that session.
4. Ambiguous or missing data goes to `RnmClassAttendanceUnmatched`.

`Attended` should require a configurable minimum duration, defaulting from tenant class config. For the first production slice, 15 minutes is a reasonable default, but it should live in config because class length can change.

Re-import must be idempotent. Use `ImportId` plus row number or a hash of session ID, normalized email, normalized display name, join time, and leave time. Existing CSV import models show the platform already accepts CSV input for lead import in `src/RNM.Platform.Application/LeadImport/LeadCsvImportModels.cs:3`, but attendance import is a separate endpoint and schema.

### 7. Attribution

With `SourceSessionId` on bookings, the concrete query is:

1. Read registrations for the session from `RnmClassRegistrations` where `PartitionKey = tenantId` and RowKey starts with `session-{sessionId}|`.
2. Count registered from those rows.
3. Count attended from those rows where `Attended = true`.
4. Read bookings from `RnmBookings` where `PartitionKey = tenantId`, then filter `SourceSessionId = sessionId` and `ProviderContactId` in the session contact set.
5. Count showed if booking status/show outcome exists.

The expensive part is step 4 because current reporting booking reads scan `RnmBookings` by tenant and filter in memory in `src/RNM.Platform.Infrastructure/Reporting/AzureTableReportingReadAdapter.cs:84`. For first pilot volumes, this is acceptable. If class volume grows, add `RnmBookingsBySourceSession` with `PartitionKey = tenantId` and RowKey `session-{sessionId}|booking-{providerBookingId}`.

Booking show-up status is UNKNOWN in the current code because `RnmBookings` stores `BookingState` and timing fields but no explicit attended/show/no-show outcome in `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:280`. To answer "how many showed up to the booking", the platform needs either a booking outcome event in timeline or a booking status update from the booking provider.

### 8. Reporting Surface

Extend the existing reporting service rather than creating a separate analytics system. The current service computes metrics on read from tenant-scoped storage in `src/RNM.Platform.Application/Reporting/PilotReportingService.cs:35`. Add class-specific read models and a method such as `GetClassSessionReportAsync`.

Per-session metrics:

- Registered
- Confirmed by SMS
- Confirmed by email
- Reminder 24h sent
- Reminder 1h sent
- Attended
- No-show
- Attendance rate
- Booked 1:1 from session
- Booking conversion rate
- Booking show-up count, only after booking outcome tracking exists

The report should return safe zeros when no data exists, matching the current pilot report empty-state posture in `src/RNM.Platform.Application/Reporting/PilotReportingService.cs:83`.

### 9. v1 Cut Line

Leave out capacity limits until the first class actually risks overfilling. Leave out waitlists until capacity limits exist. Leave out recurring class series until at least three manual sessions prove the funnel. Leave out Zoom API/OAuth until manual Zoom links become the bottleneck. Leave out self-service session admin UI until the founder is no longer the only operator. Leave out dashboards until JSON reports are not enough to close or retain clients. Leave out multi-tenant class marketplace behavior entirely for v1.

## Work Breakdown

1. PR 1: Class session and registration storage port plus Azure Table adapter. Estimate: 8 to 12 hours. Required for first class. Unblocks session creation, registration persistence, and attendance/reminder data model.

2. PR 2: Public registration endpoint with validation, CORS/origin allowlist, idempotency, honeypot, safe logging, and minimal rate limiting. Estimate: 10 to 16 hours. Required for first class. Unblocks paid traffic registration.

3. PR 3: Class confirmation SMS/email using existing Twilio, SendGrid, template rendering, and timeline events. Estimate: 6 to 10 hours. Required for first class if automated confirmations are needed. Unblocks immediate class confirmation with Zoom link.

4. PR 4: Booking attribution fields `SourceSessionId`, `SourceRegistrationId`, and `SourceCampaignId` in CRM booking link and `RnmBookings`. Estimate: 4 to 6 hours. Required before attendees book 1:1 if attribution must be real. Unblocks session-to-appointment reporting.

5. PR 5: Reminder due rows plus reminder runner. Estimate: 8 to 14 hours. Required for fully automated v1 reminders, but can follow the first class if reminders are sent manually for the first run. Unblocks T-24h and T-1h reminders.

6. PR 6: Shared send-window policy extracted from outbound TCPA logic. Estimate: 3 to 5 hours. Required before automated SMS reminders. Unblocks legal send-window enforcement outside outbound campaigns.

7. PR 7: Zoom attendance CSV import and unmatched row handling. Estimate: 8 to 12 hours. Required for v1, but can be merged after registrations are already live as long as it lands before attendance reporting is due. Unblocks attendance measurement.

8. PR 8: Class session report endpoint integrated with existing reporting style. Estimate: 6 to 10 hours. Can follow first class data capture. Unblocks funnel proof: registered to attended to booked.

9. PR 9: Operational runbook and tenant config examples. Estimate: 2 to 4 hours. Required for first class. Unblocks repeatable manual setup without guessing.

## Minimum Viable First Class

The shortest real path is:

1. Create the Zoom meeting manually in Zoom.
2. Create one `ClassSession` row manually or through an internal admin-only endpoint with the Zoom join URL.
3. Publish one landing page registration form that posts to the public registration endpoint.
4. On submit, upsert/find CRM contact, persist registration, persist consent artifact, and send confirmation SMS/email with the Zoom link.
5. For the first run only, reminders may be manual or triggered through an internal endpoint if the TimerTrigger runner is not ready.
6. After the class, import the Zoom participant CSV manually.
7. When attendees book 1:1, include `SourceSessionId` so the booking can be attributed.
8. Pull class report JSON to prove registered, attended, and booked conversion.

Manual for first class is acceptable for Zoom creation and possibly reminder triggering. Manual registration tracking in a spreadsheet is not recommended because it bypasses consent, dedup, and attribution, which are the actual value of M1 in this funnel.

## Risks

1. Public endpoint abuse. Mitigation: Origin allowlist, body limit, honeypot, idempotency, rate limiting, safe logging, and optional captcha before paid traffic.

2. Consent mistakes. Mitigation: persist exact disclosure text and source for every registration, check consent again at every send, and never reverse `opted_out` from a web form under the current inbound-only reversal rule.

3. Reminder double-send. Mitigation: due reminder table with claim/lock status, optimistic update, sent state, and provider message IDs.

4. TCPA window drift. Mitigation: extract the existing outbound TCPA logic into a shared policy and use it for reminder SMS.

5. Attendance matching false positives. Mitigation: conservative match precedence, confidence labels, unmatched table, and manual resolution.

6. Attribution scans becoming expensive. Mitigation: accept tenant scans for pilot volume, then add `RnmBookingsBySourceSession` only when volume justifies it.

7. Zoom link leakage. Mitigation: treat the join URL as customer-facing but avoid logging it in telemetry; store it only on the session and render it only in requested templates.

8. Overbuilding. Mitigation: no Zoom API, no recurring series, no dashboard, no waitlist, and no session admin UI in v1.

## Open Questions

1. What exact disclosure text do you want shown on the registration page for SMS reminders and follow-up?

2. Should a person who is already `opted_out` be allowed to receive only an on-screen Zoom link after registration, with no SMS/email?

3. Is 15 minutes the right minimum attendance threshold, or should it be based on percentage of class duration?

4. Will every class have one Zoom link shared by all registrants, or do you need per-registrant tracking links later?

5. Should the first class reminders be manual to ship faster, or do you want the automated reminder runner before the first paid traffic test?

6. Which landing page domains must be allowed in CORS for the first class?

7. What fields do you want on the registration form beyond name, phone, email, and consent?

8. How will a 1:1 booking know the `SourceSessionId`: hidden link parameter, CRM contact attribute, or manual campaign selection?
