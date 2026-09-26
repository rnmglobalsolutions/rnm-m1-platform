# Zoom Conferencing Integration Analysis

## 1. Executive Summary

Do not delay the first pilot for Zoom if Google Meet is acceptable, because the current platform already creates Google Calendar events with Google Meet links and propagates the link into confirmations. Zoom should be added behind a small conferencing adapter only when a signed client requires Zoom specifically. The right seam is a conferencing port outside the Google Calendar booking adapter, but the first production slice should expose only create plus cancel-for-compensation, not a full meeting lifecycle. The highest-risk gap is not meeting creation; it is failure ordering, idempotency, and persisting the meeting URL/provider ID into CRM. A Zoom implementation is reasonable after the pilot path is stable, but it is not a launch blocker unless the client demands Zoom.

## 2. Current State

### Vapi booking path to calendar event

Inbound Vapi calls enter through `VapiInboundWebhookFunction.Handle`, an anonymous Azure Function route at `tenants/{tenantId}/webhooks/vapi/inbound`; security is enforced inside the handler via tenant-resolved Vapi secrets before processing continues. Evidence: `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:102`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:104`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:121`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:140`.

The handler validates Bearer, legacy `X-Vapi-Secret`, or HMAC SHA256 signatures and rejects invalid requests. Evidence: `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:140`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:142`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:144`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:153`.

The current tool names are vertical-agnostic primary names with HVAC legacy aliases: `check_availability`, `book_appointment`, `check_hvac_availability`, and `book_hvac_appointment`. Evidence: `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:24`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:25`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:26`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:27`.

The parser accepts both structured Vapi tool call formats and direct API request formats. Evidence: `src/RNM.Platform.Api/Voice/VapiWebhookPayloadParser.cs:79`, `src/RNM.Platform.Api/Voice/VapiWebhookPayloadParser.cs:140`, `src/RNM.Platform.Api/Voice/VapiWebhookPayloadParser.cs:146`, `src/RNM.Platform.Api/Voice/VapiWebhookPayloadParser.cs:169`.

Tool dispatch routes availability and booking tool calls into `InboundBookingWorkflow.ProcessAsync`. Evidence: `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:284`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:373`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:375`, `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs:397`.

The inbound workflow qualifies the lead, ensures a CRM contact, processes booking, syncs CRM after booking, and then sends confirmations. Evidence: `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:120`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:134`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:172`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:232`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:266`.

`BookingApplicationService.ProcessAsync` checks availability, validates the selected slot against returned slots, creates the booking, and returns the provider booking ID plus online meeting URL. Evidence: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:45`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:98`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:144`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:150`.

### Booking abstraction and DI

The booking abstraction currently exposes only `CheckAvailabilityAsync` and `CreateBookingAsync`; there is no update, cancel, or reschedule method on the port. Evidence: `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:5`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:7`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:11`.

The DTOs already include tenant, vertical, correlation ID, selected slot, provider contact ID, business name, provider booking ID, and `OnlineMeetingUrl`. Evidence: `src/RNM.Platform.Application/Booking/BookingModels.cs:5`, `src/RNM.Platform.Application/Booking/BookingModels.cs:30`, `src/RNM.Platform.Application/Booking/BookingModels.cs:41`, `src/RNM.Platform.Application/Booking/BookingModels.cs:46`, `src/RNM.Platform.Application/Booking/BookingModels.cs:73`, `src/RNM.Platform.Application/Booking/BookingModels.cs:80`.

Provider dispatch is already adapter-based: `ConfiguredBookingAdapter` resolves the tenant's `BookingProvider` and delegates to the matching provider adapter. Evidence: `src/RNM.Platform.Infrastructure/Booking/ConfiguredBookingAdapter.cs:9`, `src/RNM.Platform.Infrastructure/Booking/ConfiguredBookingAdapter.cs:43`, `src/RNM.Platform.Infrastructure/Booking/ConfiguredBookingAdapter.cs:49`, `src/RNM.Platform.Infrastructure/Booking/ConfiguredBookingAdapter.cs:50`.

Google Calendar and GoHighLevel booking adapters are registered as booking provider adapters; the dispatcher itself is registered as `IBookingAdapter`. Evidence: `src/RNM.Platform.Api/Program.cs:97`, `src/RNM.Platform.Api/Program.cs:102`, `src/RNM.Platform.Api/Program.cs:104`, `src/RNM.Platform.Api/Program.cs:111`, `src/RNM.Platform.Api/Program.cs:122`.

### Conferencing today

Google Meet is already created inside `GoogleCalendarBookingAdapter.CreateBookingAsync`. The adapter creates a conference request ID, sends `conferenceDataVersion=1` on `events.insert`, and includes `conferenceData.createRequest.conferenceSolutionKey.type = "hangoutsMeet"` in the event body. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:153`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:157`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:410`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:414`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:417`.

The Google adapter reads the returned Meet URL from `hangoutLink` first and then from a video entry point. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:174`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:761`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:767`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:805`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:810`.

The current Google adapter fails booking if conference creation does not return a successful meeting link. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:174`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:175`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:177`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:180`.

The Meet URL is propagated from the provider result to booking decision result and then into confirmations. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:180`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:150`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:272`, `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`.

Confirmation templates can render `{{onlineMeetingUrl}}`. Evidence: `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:432`, `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`, `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:455`.

Kenny's tenant templates already include `{{onlineMeetingUrl}}` in customer SMS, customer email, business SMS, and business email. Evidence: `config/tenants/kenny-commercial-real-estate.json:36`, `config/tenants/kenny-commercial-real-estate.json:38`, `config/tenants/kenny-commercial-real-estate.json:39`, `config/tenants/kenny-commercial-real-estate.json:41`.

### Persistence gap

`CrmBookingLinkRequest` does not currently carry `OnlineMeetingUrl`, conferencing provider, provider meeting ID, host URL, or passcode. Evidence: `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Application/Crm/CrmModels.cs:66`, `src/RNM.Platform.Application/Crm/CrmModels.cs:92`, `src/RNM.Platform.Application/Crm/CrmModels.cs:105`.

The Azure Table CRM booking record writes provider booking ID, customer fields, appointment fields, and booking state, but not the online meeting URL or a conferencing provider meeting ID. Evidence: `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:267`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:280`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:288`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:289`.

This means the meeting link can reach the caller through SMS/email today, but M1 does not persist it as part of the CRM booking record. Evidence for confirmation propagation: `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`; evidence for missing CRM booking fields: `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Application/Crm/CrmModels.cs:105`.

### Existing adapter conventions

CRM is already adapter-based through `ICrmAdapter`, with methods for contact upsert, timeline events, opt-out, outbound lead selection, outbound attempt recording, CSV import, and phone index backfill. Evidence: `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:5`, `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:10`, `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:22`, `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:34`, `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:46`, `src/RNM.Platform.Application/Ports/Crm/ICrmAdapter.cs:53`.

GoHighLevel CRM exists but does not fully support the outbound CRM v0.5 surface; several outbound/consent methods return unsupported results. Evidence: `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:220`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:226`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:233`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:244`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:253`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:262`.

Outbound calling already has a separate provider-agnostic port, `IOutboundCallAdapter`, and a Vapi implementation. Evidence: `src/RNM.Platform.Application/Ports/Outbound/IOutboundCallAdapter.cs:5`, `src/RNM.Platform.Application/Ports/Outbound/IOutboundCallAdapter.cs:7`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:12`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:44`.

The Vapi outbound adapter follows useful provider-adapter patterns: tenant config resolution, Key Vault secret retrieval, safe missing config failure, consent check, timeout, retry, and circuit state. Evidence: `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:48`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:58`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:63`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:73`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:81`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:86`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:122`.

### Tenant config and secrets

Tenant configuration includes provider names and secret names as first-class configuration. Evidence: `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:11`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:17`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:23`.

Tenant config already supports voice outbound configuration but has no conferencing section today. Evidence: `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:14`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:15`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:95`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:98`.

Tenant JSON config is loaded from `config/tenants/{tenantId}.json`, then validated. Evidence: `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:36`, `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:42`, `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:46`, `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:53`.

Booking credentials are resolved through `GetBookingCredentialsSecretName`, which prefers `SecretNames.BookingCredentials` and falls back to `SecretNames.BookingApiKey`. Evidence: `src/RNM.Platform.Infrastructure/Configuration/TenantSecretNameExtensions.cs:14`, `src/RNM.Platform.Infrastructure/Configuration/TenantSecretNameExtensions.cs:16`, `src/RNM.Platform.Infrastructure/Configuration/TenantSecretNameExtensions.cs:17`.

Secrets are read from Azure Key Vault using `DefaultAzureCredential`; the secret provider is read-only. Evidence: `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:7`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:11`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:26`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:37`.

Kenny's current tenant config uses Google Calendar as booking provider and points booking credentials at `rnm-tenant-kenny-google-calendar-credentials`. Evidence: `config/tenants/kenny-commercial-real-estate.json:11`, `config/tenants/kenny-commercial-real-estate.json:13`, `config/tenants/kenny-commercial-real-estate.json:17`, `config/tenants/kenny-commercial-real-estate.json:21`.

### Appointment/contact model

The domain appointment model is minimal: provider appointment ID, appointment slot, and status. Evidence: `src/RNM.Platform.Domain/Appointments/Appointment.cs:3`, `src/RNM.Platform.Domain/Appointments/Appointment.cs:4`, `src/RNM.Platform.Domain/Appointments/Appointment.cs:5`, `src/RNM.Platform.Domain/Appointments/Appointment.cs:6`.

The domain appointment slot contains start, end, time zone, and technician name, with no conferencing fields. Evidence: `src/RNM.Platform.Domain/Appointments/AppointmentSlot.cs:3`, `src/RNM.Platform.Domain/Appointments/AppointmentSlot.cs:4`, `src/RNM.Platform.Domain/Appointments/AppointmentSlot.cs:5`, `src/RNM.Platform.Domain/Appointments/AppointmentSlot.cs:6`.

Appointment status includes `Cancelled`, but the booking adapter port has no cancel method. Evidence: `src/RNM.Platform.Domain/Appointments/AppointmentStatus.cs:3`, `src/RNM.Platform.Domain/Appointments/AppointmentStatus.cs:7`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:5`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:13`.

Contact records support an attribute map, consent status, campaign ID, outbound status, outbound attempt count, and follow-up timestamps. Evidence: `src/RNM.Platform.Application/Crm/CrmModels.cs:19`, `src/RNM.Platform.Application/Crm/CrmModels.cs:27`, `src/RNM.Platform.Application/Crm/CrmModels.cs:124`, `src/RNM.Platform.Application/Crm/CrmModels.cs:135`, `src/RNM.Platform.Application/Crm/CrmModels.cs:140`, `src/RNM.Platform.Application/Crm/CrmModels.cs:146`.

### Cancel/reschedule

There is no booking cancel/update/reschedule operation on the current booking port. Evidence: `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:5`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:7`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:11`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:13`.

Because there is no booking cancel/reschedule operation, adding a full `UpdateMeetingAsync` to Zoom in the first PR would be unused by the current application flow. Evidence for current booking operation shape: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:20`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:45`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`.

### Time zone handling

Booking requests carry a tenant time zone string into availability and booking operations. Evidence: `src/RNM.Platform.Application/Booking/BookingModels.cs:11`, `src/RNM.Platform.Application/Booking/BookingModels.cs:64`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:178`.

Google event payload converts slot start/end to the configured time zone and sends the time zone in both start and end objects. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:345`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:346`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:407`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:408`.

Kenny is configured for `America/Chicago`. Evidence: `config/tenants/kenny-commercial-real-estate.json:5`.

### Test seams

Google Calendar adapter tests already verify Meet conference payload, `conferenceDataVersion=1`, returned Meet URL, and video entry point fallback. Evidence: `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:338`, `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:357`, `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:369`, `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:388`, `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:393`, `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:422`.

Booking application tests verify the online meeting URL is propagated by the booking service. Evidence: `tests/RNM.Platform.UnitTests/Booking/BookingApplicationServiceTests.cs:288`, `tests/RNM.Platform.UnitTests/Booking/BookingApplicationServiceTests.cs:318`.

Confirmation tests verify the online meeting URL token renders in confirmation output. Evidence: `tests/RNM.Platform.UnitTests/Confirmations/ConfirmationApplicationServiceTests.cs:366`, `tests/RNM.Platform.UnitTests/Confirmations/ConfirmationApplicationServiceTests.cs:388`.

Provider dispatch tests already exercise configured adapter routing. Evidence: `tests/RNM.Platform.UnitTests/Infrastructure/ProviderDispatcherTests.cs:102`, `tests/RNM.Platform.UnitTests/Infrastructure/ProviderDispatcherTests.cs:135`.

## 3. Design Assessment

### Should Zoom live behind `IConferencingAdapter`?

Yes, but not as an overgrown lifecycle abstraction. The current booking provider abstraction owns calendar availability and event creation. Evidence: `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:7`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:11`.

Zoom would not replace Google Calendar for the stated use case; it would only create the meeting room while Google Calendar continues to own availability and calendar event creation. Evidence that Google Calendar owns availability and event creation today: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:45`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:114`.

Keeping Zoom inside `GoogleCalendarBookingAdapter` would couple a calendar provider to a conferencing provider. That conflicts with the accepted ADR that booking adapters should own booking provider behavior and meeting adapters should own meeting-link behavior. Evidence: `docs/adr/0002-appointment-mode-and-online-meetings.md:37`, `docs/adr/0002-appointment-mode-and-online-meetings.md:49`.

The counterargument is operational simplicity: the Google adapter already creates Google Meet successfully in one place and returns one `OnlineMeetingUrl`. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:153`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:180`.

Recommendation: keep Google Meet in the Google Calendar adapter for now as the default Google behavior, but introduce a separate conferencing adapter only for external meeting providers such as Zoom. This avoids reworking working Meet code while still preventing Zoom logic from leaking into the calendar adapter. Evidence that current Meet behavior is working and tested: `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:338`, `tests/RNM.Platform.UnitTests/Booking/BookingApplicationServiceTests.cs:288`, `tests/RNM.Platform.UnitTests/Confirmations/ConfirmationApplicationServiceTests.cs:366`.

### Minimal viable interface

Do not start with `Create`, `Update`, and `Cancel` as equal operations. The current product has no booking update or reschedule flow, so `UpdateMeetingAsync` would be speculative. Evidence: `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:5`, `src/RNM.Platform.Application/Ports/Booking/IBookingAdapter.cs:13`.

Do include `CancelMeetingAsync` if Zoom meetings are created before calendar events, because it is needed for compensation if the Google Calendar event creation fails after Zoom succeeds. Evidence that Google event creation can fail after request payload is built and sent: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:153`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:161`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:162`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:164`.

Recommended v0.5 shape:

```csharp
public interface IConferencingAdapter
{
    Task<CreateConferenceResult> CreateConferenceAsync(
        CreateConferenceRequest request,
        CancellationToken cancellationToken);

    Task<CancelConferenceResult> CancelConferenceAsync(
        CancelConferenceRequest request,
        CancellationToken cancellationToken);
}
```

This is illustrative only, not implemented in this analysis.

### Failure ordering and compensation

For Zoom plus Google Calendar, create the Zoom meeting before creating the calendar event so the Google event can include the Zoom join URL in the description and/or location. Evidence that the Google adapter currently builds event description and location before posting the event: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:355`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:402`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:405`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:406`.

If Zoom creation fails, do not create the calendar event and return a safe booking failure or human follow-up. This follows the existing application pattern where provider booking failure becomes a failed booking decision. Evidence: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:134`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:136`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:139`.

If Zoom succeeds and Google Calendar creation fails, call `CancelConferenceAsync` to remove the orphan Zoom meeting, log the compensation result, and return a safe booking failure. Evidence that Google Calendar creation currently returns safe failed booking results on provider failure: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:162`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:164`.

If Zoom succeeds, Google succeeds, and CRM persistence fails, do not hide the booking from the caller. The current workflow sends confirmations after CRM sync result is obtained, and does not branch out before confirmation solely because CRM sync failed. Evidence: `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:232`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:249`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:265`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:266`.

The current CRM booking persistence should be extended to store conferencing fields before Zoom becomes production-critical. Evidence that CRM booking link model and table write omit those fields: `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Application/Crm/CrmModels.cs:105`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:289`.

### Idempotency

Current Google Meet conference request IDs are unique per create attempt because they append a new GUID. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:434`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:439`.

That uniqueness prevents Google conference deduplication, but it does not make booking creation idempotent across retries. Evidence that the booking service retries are not keyed by a stored booking intent before provider create: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:116`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:144`.

Zoom should use a deterministic idempotency key built from tenant ID, provider contact ID, selected slot start/end, and correlation/tool call ID. Evidence that those data points exist in the booking request: `src/RNM.Platform.Application/Booking/BookingModels.cs:31`, `src/RNM.Platform.Application/Booking/BookingModels.cs:33`, `src/RNM.Platform.Application/Booking/BookingModels.cs:35`, `src/RNM.Platform.Application/Booking/BookingModels.cs:38`.

### Zoom token lifecycle

ASSUMPTION from the task prompt: the first Zoom path would use Server-to-Server OAuth, with tenant secrets containing account/client credentials and short-lived access tokens fetched from Zoom.

There is no generic provider access-token cache in the current codebase. Evidence: `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:26`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:46`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:73`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:81`.

For M1, cache Zoom access tokens in memory per tenant/secret with an expiry skew and refetch on 401. That mirrors the serverless/cost discipline and avoids turning Key Vault into a token write store. Evidence that the current secret provider is read-only: `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:26`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:46`.

### Host identity

The Zoom host identity must be tenant configuration, not hardcoded. Tenant config already carries provider and communication settings by tenant. Evidence: `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:11`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:13`.

Recommended shape is a new optional `conferencing` section with `provider`, `credentialsSecretName`, `hostUserId`, and safe meeting defaults. This would follow the existing `voice.outbound` pattern for provider-specific operational config. Evidence for existing optional voice provider config: `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:95`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:98`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:99`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:103`.

### Multi-tenant ceiling

ASSUMPTION from the task prompt: Server-to-Server OAuth would use RNM's Zoom account, while future tenant-owned Zoom accounts would require a 3-legged OAuth path.

The adapter seam survives that future because callers should pass tenant-scoped meeting intent, not Zoom OAuth details. Evidence that existing provider adapters already resolve tenant credentials internally rather than leaking secrets into application services: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:120`, `src/RNM.Platform.Infrastructure/Configuration/TenantSecretNameExtensions.cs:14`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:26`.

The rework likely appears around the third or fourth tenant if customers require their own Zoom accounts, own branding, or own host-level Zoom audit controls. ASSUMPTION based on platform stage and current single-founder constraints.

### Blast radius

Likely code areas affected by Zoom:

- Tenant domain/config binding: `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`, `src/RNM.Platform.Infrastructure/Configuration/JsonTenantConfigurationProvider.cs:71`.
- DI registration: `src/RNM.Platform.Api/Program.cs:97`, `src/RNM.Platform.Api/Program.cs:135`.
- Booking orchestration: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:20`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`.
- Booking DTOs for meeting metadata: `src/RNM.Platform.Application/Booking/BookingModels.cs:30`, `src/RNM.Platform.Application/Booking/BookingModels.cs:41`.
- Google Calendar event payload to accept external meeting links: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:340`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:405`.
- CRM booking persistence: `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`.
- Confirmation tokens are mostly ready because `onlineMeetingUrl` already exists: `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`.
- Tests around Google adapter, booking service, confirmation service, and provider dispatch: `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:338`, `tests/RNM.Platform.UnitTests/Booking/BookingApplicationServiceTests.cs:288`, `tests/RNM.Platform.UnitTests/Confirmations/ConfirmationApplicationServiceTests.cs:366`, `tests/RNM.Platform.UnitTests/Infrastructure/ProviderDispatcherTests.cs:102`.

## 4. Recommended Design

### Recommendation for the next 30 days

Use the existing Google Calendar plus Google Meet path for Kenny and the first pilot unless Zoom is explicitly required to close the deal. Evidence that the path already creates a meeting link and sends it through confirmations: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:153`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:180`, `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`.

Do not introduce Zoom before the pilot just because it is architecturally clean. The current system's launch goal is still the revenue path: Call -> Qualification -> Booking -> CRM -> SMS/Email -> Logs. Evidence that the current workflow follows that path: `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:134`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:172`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:232`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:266`.

### If Zoom becomes required

Add:

- `IConferencingAdapter` in Application ports.
- `ConfiguredConferencingAdapter` in Infrastructure if more than one provider is active.
- `ZoomConferencingAdapter` with tenant-scoped Key Vault credentials.
- Tenant `conferencing` config.
- Booking DTO fields for `OnlineMeetingUrl`, `ConferencingProvider`, `ProviderMeetingId`, and optional `MeetingPasscode`.
- CRM booking persistence fields for the same metadata.
- Google Calendar payload support for an externally supplied meeting URL.

Do not remove the current Google Meet behavior in this PR. It is already tested and used by confirmation templates. Evidence: `tests/RNM.Platform.UnitTests/Infrastructure/GoogleCalendarBookingAdapterTests.cs:338`, `tests/RNM.Platform.UnitTests/Confirmations/ConfirmationApplicationServiceTests.cs:366`.

### Proposed tenant config

Illustrative only:

```json
{
  "conferencing": {
    "provider": "Zoom",
    "credentialsSecretName": "rnm-tenant-kenny-zoom-credentials",
    "hostUserId": "kenny@example.com",
    "meetingDefaults": {
      "waitingRoom": true,
      "joinBeforeHost": false
    }
  }
}
```

This follows the current pattern where tenant config stores provider names and secret names, while actual credentials stay in Key Vault. Evidence: `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:17`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:23`, `src/RNM.Platform.Infrastructure/Secrets/KeyVaultSecretProvider.cs:26`.

### Proposed secret shape

ASSUMPTION from the task prompt and common Zoom S2S OAuth shape:

```json
{
  "accountId": "...",
  "clientId": "...",
  "clientSecret": "..."
}
```

The exact shape must be validated against the Zoom API at implementation time. This analysis did not implement or test Zoom API calls.

### Calendar event behavior with Zoom

When the conferencing provider is Zoom, Google Calendar should not request Google Meet for that event. Today it always requests Google Meet through `conferenceData`. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:410`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:417`.

Instead, the Google event should include the Zoom join URL in the event description and possibly location, while `OnlineMeetingUrl` remains the provider-agnostic URL returned to confirmations. Evidence that event description and location are built inside the Google adapter: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:355`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:405`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:406`.

### Group classes

The current booking model is a one-customer appointment model, not a class/session/registration model. Evidence: `src/RNM.Platform.Application/Booking/BookingModels.cs:30`, `src/RNM.Platform.Application/Booking/BookingModels.cs:34`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:398`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:400`.

Do not solve 30-100 attendee group classes in the Zoom slice. If the makeup school needs that immediately, handle it as a separate revenue slice with explicit class/session requirements.

## 5. Work Breakdown

### PR 1: Persistence and config prep, 4-6 hours

- Add optional `ConferencingConfiguration` to tenant config.
- Bind it in JSON tenant config provider.
- Add validation for provider/secret/host when conferencing provider is enabled.
- Extend booking and CRM DTOs with conferencing provider, provider meeting ID, online meeting URL, and passcode.
- Persist these fields to `RnmBookings`.

This should land before Zoom because the current persistence gap exists even with Google Meet. Evidence for the gap: `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`.

### PR 2: External meeting URL support in Google Calendar adapter, 4-6 hours

- Allow `CreateBookingRequest` to carry an existing online meeting URL.
- If external URL is present, add it to description and skip Google Meet creation.
- Preserve current Google Meet behavior when no external conferencing provider is selected.
- Add tests proving both branches.

This isolates calendar event behavior before adding Zoom.

### PR 3: Zoom adapter, 6-10 hours

- Add `IConferencingAdapter`.
- Add `ZoomConferencingAdapter`.
- Resolve credentials from Key Vault.
- Add in-memory access token cache with expiry skew.
- Add timeout, bounded retry, and safe errors following the Vapi outbound adapter pattern.
- Add unit tests with mocked HTTP.

Evidence for retry/timeout/circuit pattern to mirror: `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:14`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:15`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:81`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:86`, `src/RNM.Platform.Infrastructure/Outbound/VapiOutboundCallAdapter.cs:122`.

### PR 4: Booking orchestration with compensation, 8-12 hours

- Update `BookingApplicationService` to create external conferencing before calendar booking only when tenant config requires it.
- If Zoom fails, do not create the calendar event.
- If Zoom succeeds and calendar fails, attempt Zoom cancel compensation.
- Log all branches with tenant ID and correlation ID.
- Confirm confirmation templates still receive `onlineMeetingUrl`.

Evidence for orchestration point: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:116`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:144`.

### PR 5: Runbook and operational test, 3-5 hours

- Add Zoom setup runbook.
- Add Key Vault secret format.
- Add manual smoke test: create appointment, verify Google Calendar event has Zoom link, verify customer SMS/email includes Zoom link, verify CRM booking row stores meeting metadata.

### Deferrable until after first pilot live

- Tenant-owned Zoom OAuth onboarding.
- `UpdateMeetingAsync`.
- Self-service connection UI.
- Recurring class/session support.
- Multi-host routing.
- Zoom webhook ingestion.
- Provider migration tooling.

These are not required by the current booking flow. Evidence that current flow only creates bookings and sends confirmations: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:20`, `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs:266`.

## 6. Cost of Delay

If Google Meet is acceptable, adding Zoom now likely costs 2-4 engineering days and adds risk to a path that is already ready enough to pilot. Evidence that the current path already returns a meeting URL: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:180`; evidence that confirmation already renders it: `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`.

The real cost is not writing the Zoom POST request. The real cost is compensating partial failures, preserving idempotency, expanding tenant config, adding secure token handling, updating Google event behavior, persisting meeting metadata, and expanding tests. Evidence for involved seams: `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:340`, `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`, `src/RNM.Platform.Domain/Configuration/TenantConfiguration.cs:5`.

If a prospect says "we only use Zoom," then Zoom becomes revenue-critical and should be built. If the prospect accepts Google Meet, ship the pilot and postpone Zoom.

## 7. Risks

### Current Google Meet behavior conflicts with earlier onsite assumptions

The accepted ADR says HVAC onsite appointments should not create online meetings, while the current Google Calendar adapter always requests and requires Google Meet. Evidence for ADR: `docs/adr/0002-appointment-mode-and-online-meetings.md:17`, `docs/adr/0002-appointment-mode-and-online-meetings.md:22`, `docs/runbooks/m1-demo-provider-setup.md:94`, `docs/runbooks/m1-demo-provider-setup.md:115`; evidence for current always-Meet behavior: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:153`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:157`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:410`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:417`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:177`.

This is not a Zoom blocker, but it is a product/config risk: appointment modality needs to become explicit before supporting multiple appointment styles.

### Meeting metadata is not persisted

The meeting URL reaches confirmations but is not stored in CRM booking records. Evidence: `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs:447`, `src/RNM.Platform.Application/Crm/CrmModels.cs:62`, `src/RNM.Platform.Application/Crm/CrmModels.cs:105`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:264`, `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs:289`.

This hurts support, auditability, reporting, and manual recovery.

### Idempotency is weak

The current conference request ID is deliberately unique per event attempt, but no persistent booking intent is checked before provider creation. Evidence: `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:434`, `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs:439`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:116`, `src/RNM.Platform.Application/Booking/BookingApplicationService.cs:122`.

Adding Zoom doubles the partial-creation surface unless a deterministic idempotency key and compensation path are added.

### GoHighLevel parity is incomplete

If a tenant uses GoHighLevel CRM, the full native CRM v0.5 outbound/consent/reporting behavior does not carry over today. Evidence: `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:220`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:226`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:233`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:244`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:253`, `src/RNM.Platform.Infrastructure/Crm/GoHighLevelCrmAdapter.cs:262`.

This matters if Zoom is being added for a GHL-backed tenant; the booking may work, but CRM/reporting parity may not.

### Zoom S2S may not match tenant-owned Zoom expectations

ASSUMPTION from the task prompt: Zoom Server-to-Server OAuth is tied to RNM-owned Zoom infrastructure, while tenant-owned Zoom requires 3-legged OAuth. If true, S2S is acceptable for the first controlled pilot but becomes operational debt when tenants want their own Zoom account connected.

## 8. Open Questions

1. Is Zoom required to close Kenny, or is Google Meet acceptable for the pilot?
2. Should online meeting creation be enabled for every Google Calendar tenant, or should appointment modality become tenant-configured before launch?
3. Does the customer need the Zoom host URL, or only the attendee join URL?
4. Should the business notification include the meeting link for every virtual appointment?
5. Do we need to store meeting passcode and provider meeting ID in CRM immediately?
6. What should happen if Zoom succeeds, Google Calendar fails, and Zoom cancellation also fails?
7. What deterministic idempotency key should M1 use for duplicate Vapi tool calls?
8. Are group sessions for the makeup school part of the first revenue slice, or a separate later slice?
9. If using RNM's Zoom account for tenant calls, who owns branding, host identity, recordings, and compliance settings?
10. When tenant-owned Zoom becomes necessary, will we accept manual OAuth token setup first, or build a self-service OAuth flow?

