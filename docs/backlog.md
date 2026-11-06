# M1 Backlog

Single source of priority for M1. See the platform goal in `AGENTS.md`.

Flow: Lead (call, chat, or form) → Qualification and service area → Availability →
Booking (1:1 or master class) → CRM → SMS/email confirmation and reminders →
Follow-up / nurturing when not booked → Logs → Monthly report. Works for any tenant.

When all items below are done, M1 does what the flow describes.

## Definition of done (every item)

- A test reproduces the problem or describes the behavior before the code changes.
- Build has 0 warnings and all tests pass.
- Tenant preflight passes.
- New behavior logs a telemetry event with `correlationId` and `tenantId`.
- Runbook updated if operational behavior changes.

## Items

Numbers are IDs, not build order. The build order and phases are in
`docs/implementation-plan.md`. Status: `todo`, `doing`, `done`.

| # | Item | Flow step | Status |
|---|---|---|---|
| 1 | One qualification for every channel | Qualification | todo |
| 2 | Configurable service area | Qualification | done |
| 3 | 1:1 booking from ManyChat and the web form | Booking | todo |
| 4 | Appointment type per tenant: onsite, Google Meet, or Zoom | Booking | todo |
| 5 | SMS replies reach the business and stop follow-ups | Follow-up | todo |
| 6 | Follow-up starts and stops at the right moments | Follow-up | todo |
| 7a | Per-call tracing | Logs | done |
| 7b | Hang-ups captured | Lead | todo |
| 8 | Master class registration from every channel | Booking | todo |
| 9 | Zoom created automatically for master classes | Booking | todo |
| 10 | Master class attendance and post-class follow-up | Follow-up | todo |
| 11 | Automatic monthly report by email | Monthly report | todo |
| 12 | New tenant setup script | Onboarding | todo |
| 13 | Alert when a tenant's calendar or Zoom connection stops working | Booking, Logs | todo |
| 14 | Tenant events to the tenant's own systems | Customization | todo |
| 15 | Voice prompt additions per tenant | Customization | todo |
| 16 | Language per lead (English or Spanish) | Confirmations, Follow-up | todo |
| 17 | Contact attribute limit guard | CRM | done |
| 18 | Appointment outcome by email (attended, closed) | CRM, Monthly report | todo |
| 19 | Date-range queries and history archiving (only when measured) | CRM, Monthly report | deferred |
| 20 | Unsubscribe links without the email address in the URL | Follow-up, Logs | todo |
| 21a | Google client ID and secret at app level | Booking, Onboarding | todo |
| 21b | Self-service Google Calendar connection link | Booking, Onboarding | todo |
| 21c | Google disconnection and deletion within 30 days | Booking, Privacy | todo |

Storage decision (Table Storage now, Azure SQL together with the UI in Platform RNM
2.0): ADR 0004.

Items 14 and 15 follow the customization levels in `AGENTS.md` (TENANT
CUSTOMIZATION). Move item 14 up if a signed tenant needs M1 connected to its own
system.

### 1. One qualification for every channel

Today voice leads skip `LeadClassifier`; only ManyChat and web leads are classified.
Voice also stores the phone as received, while ManyChat normalizes to E.164.

Done when:
- A voice lead and a ManyChat lead with the same data get the same tier, route, and
  `Attr_leadStatus`, and end up as one CRM contact.
- Phone numbers from every channel are stored in E.164.
- Every lead records `channel` (`voice`, `messenger`, `instagram`, `whatsapp`,
  `web_form`) and `entryPoint` (for example `website`, `ad`, `reel_comment`, taken from
  the ManyChat ref when present). ManyChat's existing `metaChannel` is still accepted.
  The monthly report (item 11) depends on these two fields.
- The lead's route (`consultation` = 1:1, `master_class`, `follow_up`, `none`) decides
  what the channel offers next.
- A voice lead stopped at qualification (out of service area, escalation) is saved as a CRM
  contact and gets follow-up. Today it is lost (found in #2). Must be done before Yartex
  goes live: its voice prompt promises follow-up to out-of-state callers.
- The `OutOfServiceArea` tool result carries the tenant's `serviceArea.referralMessage`, so
  the assistant speaks it from M1 instead of a copy in the prompt.

### 2. Configurable service area

Today a tenant with no ZIP codes can never book by voice: `ServiceAreaValidator`
returns `NeedsEscalation` when `zipCodes` is empty, and `MissingRequiredFields`
when the caller gives no ZIP.

Done when:
- A vertical declares the service area mode: `zip`, `state` (with allowed states),
  or `none`, and a tenant can override it.
- HVAC still requires a ZIP; life insurance qualifies with a state and no ZIP.
- Fields a caller cannot know (`leadSource`, `campaignId`) are filled by M1, not by
  the Vapi tool.

### 3. 1:1 booking from ManyChat and the web form

Today only voice can book. ManyChat and the web consultation form create the contact
and stop.

Done when:
- ManyChat and the web form can get available slots and book one, for any tenant,
  authenticated the same way their intake is today.
- Booking reuses the same booking, CRM, and confirmation path as voice.
- A booked lead receives the same confirmations and reminders on every channel.

### 4. Appointment type per tenant: onsite, Google Meet, or Zoom

Today `GoogleCalendarBookingAdapter` always requests a Google Meet link and fails the
booking when it is missing, after the event was already created (see ADR 0002 and
`docs/analysis/zoom-conferencing-integration.md`).

Done when:
- The tenant configures the appointment type: `onsite`, `google_meet`, or `zoom`.
- Onsite bookings succeed with no meeting link.
- Zoom bookings create a Zoom meeting in the **tenant's own Zoom account** through a
  small conferencing adapter (create and cancel only) and send its join link in
  confirmations and reminders.
- Zoom credentials come from the tenant secret `zoomCredentials` (Server-to-Server
  OAuth: account ID, client ID, client secret), named with the `rnm-tenant-` prefix and
  checked by the configuration validator and tenant preflight.
- If any step fails after the calendar event or the Zoom meeting is created, both are
  cleaned up.
- The meeting link and provider meeting ID are stored in the CRM.

### 5. SMS replies reach the business and stop follow-ups

Today an inbound SMS that is not STOP is accepted and dropped, while follow-up
templates ask leads to reply.

Done when:
- A reply creates an `SmsReplyReceived` timeline event.
- The business is notified by SMS or email with the reply text.
- Pending follow-ups for that contact are skipped with reason `contact_replied`.

### 6. Follow-up starts and stops at the right moments

Today `check_availability` schedules a follow-up even when the caller books a minute
later, and booking writes the `LeadStatus` column while the follow-up stop condition
reads `Attr_leadStatus`.

Done when:
- Checking availability does not schedule a follow-up.
- A lead that books a 1:1 or registers for a master class gets no follow-up meant for
  unbooked leads. Tested against the real `AzureTableCrmAdapter` on Azurite, not only
  an in-memory double (the in-memory doubles hid this bug).
- A lead that ends the conversation without booking enters the follow-up / nurturing
  sequence for its tier.

### 7a. Per-call tracing

Today each Vapi webhook gets a new correlation ID and `callId` is not logged, so the
availability check and the booking of the same call cannot be linked.

Done when:
- Every voice telemetry event includes `callId`, and one KQL query (saved in a runbook)
  returns the full chain for a call.

### 7b. Hang-ups captured

Today `end-of-call-report` is ignored, so a caller who hangs up before a tool call
leaves no lead. Depends on item 1: hang-ups must go through the shared lead capture,
not a third path.

Done when:
- A call that ends without booking creates or updates the contact through the shared
  lead capture (item 1) and starts follow-up.
- A call that already booked does not start follow-up.

### 8. Master class registration from every channel

Today registration works from the web form (`PublicFunnelMasterClassRegistration`)
and the class registration webhook. ManyChat returns only a link, and voice has no
registration tool.

Done when:
- ManyChat can list upcoming sessions and register the lead in the conversation.
- Voice has a tool to offer upcoming sessions and register the caller.
- Every channel uses `ClassRegistrationService`, with the same capacity checks,
  confirmations, and reminders.

### 9. Zoom created automatically for master classes

Today a master class session stores a Zoom link pasted by hand (`ZoomUrl`).

Done when:
- Creating a session can create the Zoom meeting automatically with the tenant's Zoom
  credentials (same conferencing adapter as item 4).
- A pasted link still works as a fallback.
- Changing or cancelling the session updates or cancels the Zoom meeting.
- The session also blocks the host's calendar, so no 1:1 can be booked during a master
  class. Today class sessions never touch the calendar.
- Each tenant configures the Zoom host user; one host can only run one meeting at a
  time, so a tenant with several advisors maps each advisor to their own Zoom user.

### 10. Master class attendance and post-class follow-up

Today M1 does not know who attended.

Done when:
- After a session, M1 reads attendance from the tenant's Zoom (past meeting
  participants) and marks each registration as `attended` or `no_show`. Participants
  are matched to registrations by email.
- If the tenant's Zoom plan or scopes do not allow reading participants, M1 logs it and
  the class still runs; attendance is marked `unknown`.
- Attendees enter a sequence that offers a 1:1; no-shows enter a sequence that offers
  the next session.
- Attendance appears in the class report and the monthly report.

### 11. Automatic monthly report by email

Today `PilotReport` exists but runs only on request.

Done when:
- On the first day of each month, each active tenant receives an email report for the
  previous month.
- The report answers, in this order:
  1. **Revenue**: closed deals and their value from item 18; projected revenue only
     where outcomes are missing, labeled as projected.
  2. **Leads**: total by channel and entry point, compared with the previous month.
  3. **Speed**: average time to first contact; % within 1 and 5 minutes.
  4. **Appointments**: qualified leads, 1:1s booked, booking rate, show rate (item 18).
  5. **Master classes**: registrations, attendance, attendees who booked a 1:1.
  6. **Follow-up**: leads in follow-up / nurturing, leads that booked after a
     follow-up, opt-outs.
  7. **What M1 saved**: leads captured outside business hours, hang-ups recovered,
     SMS replies routed to the business.

### 12. New tenant setup script

Done when:
- One script takes a tenant JSON and creates or updates the tenant's Vapi assistant
  from the vertical prompt, and lists the Key Vault secrets still missing (including
  Zoom credentials when the tenant uses Zoom).
- Running it twice changes nothing the second time.

- The script tests the tenant's Google Calendar and Zoom credentials on the spot and
  reports which ones fail.

### 13. Calendar and Zoom connection health

Today, if Google rejects a tenant's refresh token (`invalid_grant`), availability fails
silently and the voice agent offers human follow-up on every call. Every availability
check and booking also requests a new Google access token (no caching).

Done when:
- A daily job refreshes each active tenant's Google token (and tests Zoom credentials
  when configured). This catches a broken connection before a lead does, and keeps
  tokens in use so the 6-month inactivity expiry never applies.
- A rejected Google or Zoom credential raises an operations alert naming the tenant.
- `/readiness` reports the connection as broken for that tenant.
- Google access tokens are cached until shortly before they expire (about 1 hour),
  instead of being requested on every call.
- Extended by #21 (2026-11-06): each tenant has a connection status (`connected`,
  `disconnected`, `error`), connected email, `connectedAt`, `lastHealthCheckAt`, and
  `lastError` with no secrets (the store from #21b). The daily job runs with a
  configurable concurrency limit. `invalid_grant` or `invalid_client` marks the tenant
  `disconnected` and emails the operator through SendGrid with the tenant ID and error.
  Temporary errors (429, 5xx, timeouts) retry with backoff and never disconnect.
- A reconnection takes effect without restarting the Function (short TTL or
  invalidation on save); any `@Microsoft.KeyVault` reference pinned to a version is
  reported.

### 16. Language per lead (English or Spanish)

Today templates have one language per tenant. Houston and Laredo tenants get callers
in both languages.

Done when:
- Every channel records the lead's language (`en` or `es`) on the contact: voice from
  the Vapi tool argument, ManyChat from a field, the web form from the page.
- Each template can have an `es` version; M1 picks it by the lead's language and falls
  back to the default with a logged event when it is missing.
- Confirmations, reminders, and follow-ups all use the lead's language.

### 17. Contact attribute limit guard

Today every incoming field becomes its own `Attr_*` column on the contact row. Azure
Table Storage allows at most 255 properties per row, so a lead with many fields fails
to save and is lost.

Done when:
- Known attributes stay as columns; once a row nears the limit, extra attributes are
  stored together in one JSON column and are still readable as `{{attr.name}}`.
- A lead with 300 attributes saves and reads back correctly in a test on Azurite.
- An event is logged when a tenant's leads overflow into the JSON column.

### 18. Appointment outcome by email (attended, closed)

Today M1 does not know whether a lead showed up or became a customer, so the report
can only show projected revenue.

Done when:
- After each 1:1 ends, the business receives an email with signed one-click links:
  attended yes / no, and closed yes / no with an optional value.
- One click records the outcome on the contact and booking in the CRM; links expire and
  cannot be reused for another lead or tenant.
- A no-show can start a reschedule sequence configured by the tenant.
- The monthly report (item 11) uses these outcomes for show rate and real revenue.

### 19. Date-range queries and history archiving (only when measured)

Today the monthly report reads a tenant's whole history and filters dates in memory,
and the follow-up runner scans every follow-up row of a tenant every 5 minutes.

Start this item only when one of these is measured in production:
- one tenant's monthly report takes more than 30 seconds, or
- one tenant's follow-up run takes more than 60 seconds.

Done when:
- Reports and the follow-up runner read only the date range they need, using row keys
  ordered by date.
- Records older than the tenant's retention period move to an archive table.

### 20. Unsubscribe links without the email address in the URL

Found in the Phase 0 security review (2026-11-03). Today the unsubscribe link carries
the lead's email base64-encoded in the query string (`?e=...`), which anyone can decode,
and request URLs are usually stored in Application Insights. AGENTS.md requires
minimizing personal data in logs. Also, when the signing key cannot be read, the
endpoint fails without logging anything.

Must be done before any tenant sends marketing email in production.

Done when:
- The link identifies the contact without the email address (for example the contact ID
  signed with the tenant ID), and the unsubscribe still works for that contact.
- No request URL M1 generates contains an email address or phone number.
- A missing or unreadable signing key logs a telemetry event with `tenantId` and
  `correlationId`.

### 21. Self-service Google Calendar connection

Today connecting a tenant's calendar is manual: an OAuth URL, copy the code, exchange it
with `scripts/google-calendar-oauth-manual.sh`, and paste the refresh token into Key
Vault. It does not scale, and Google verification needs a real HTTPS connection flow on
a verified domain for the demo video. Requirements, constraints, and decisions are in
`docs/agents/work/21-google-connect.md`. Scopes never change in this item (see "Not
code", Google OAuth app). The manual script keeps working as a fallback.

#### 21a. Google client ID and secret at app level

Done when:
- The OAuth client ID and secret are read once from app configuration (Key Vault), not
  from each tenant's secret.
- A tenant secret that still has `clientId`/`clientSecret` keeps working until migrated.
- `sample-hvac-tenant` keeps working before, during, and after the migration.
- Vapi and availability endpoints behave exactly as before.

#### 21b. Self-service Google Calendar connection link

Done when:
- The operator generates a single-use, expiring link for a tenant (admin endpoint,
  internal API key); only its hash is stored; a new link invalidates older ones.
- The customer opens it, picks a Google account, accepts, and sees a "Calendar connected"
  page with the connected email (English and Spanish).
- PKCE (S256) and an HMAC-signed `state` (tenant, expiry, single-use nonce). The callback
  is idempotent, handles cancel, unchecked permissions, and Workspace admin blocks with a
  clear message, and saves nothing broken.
- Granted scopes are checked; a free/busy test runs before the connection counts.
- A new secret version keeps the tenant's schedule settings and changes only the
  credentials; older versions are disabled. Same account: never revoke. Different
  account: revoke the old one after the new one works, and email the operator.
- Credentials go to a separate Key Vault for tenant Google credentials (7-day soft-delete
  retention; the app can write only there).
- No log contains tokens, codes, secrets, the full state, or the link token.
- Production redirect on a verified custom domain; localhost only by configuration.

#### 21c. Google disconnection and deletion within 30 days

Done when:
- An admin endpoint revokes the tenant's token at Google, deletes the credentials, and
  marks the tenant `disconnected`.
- Google-derived data M1 keeps outside Key Vault (event IDs, Meet links, and attendance
  when #10 exists) is deleted or anonymized within 30 days.
- The privacy policy text matches what M1 does (Raisel approves).

### 14. Tenant events to the tenant's own systems

Today M1 cannot notify a tenant's own systems; customization beyond configuration
needs code.

Done when:
- A tenant can configure one or more URLs and choose which events to receive:
  `lead.qualified`, `appointment.booked`, `appointment.cancelled`, `lead.not_booked`,
  `class.registered`, `class.attended`, `lead.opted_out`.
- Each event carries the tenant ID, event type, timestamp, correlation ID, and the
  lead's data and attributes, with no secrets.
- Each request is signed with a per-tenant secret (`rnm-tenant-{tenant}-events-secret`)
  so the tenant can verify it came from M1.
- Delivery runs through a queue, never blocks the lead flow, and retries with backoff.
  Failed deliveries are logged and visible in `/readiness`.
- A runbook shows how to receive events in Zapier or Make.

### 15. Voice prompt additions per tenant

Today there is one voice prompt file per vertical at most, and tenant-specific voice
instructions live only in the Vapi dashboard.

Done when:
- Each vertical has a base voice prompt in `config/prompts/`.
- A tenant can add its own instructions (tone, products, extra questions) in its
  config without changing the base prompt.
- The base prompt's safety rules (AI disclosure, no advice, no invented facts) cannot
  be overridden by tenant additions.
- The new tenant setup script (item 12) builds the final prompt for Vapi from both.

## Not code (owner: Raisel)

- **Google OAuth app (urgent).** In "Testing" status, refresh tokens with calendar
  scopes expire after 7 days, so every connected tenant calendar stops working a week
  after setup.
  1. Today: Google Cloud Console > OAuth consent screen > **Publish app** ("In
     production"). This removes the 7-day expiry. Until verified, tenants see an
     "unverified app" warning when connecting, and there is a cap of 100 new users.
  2. Reconnect every calendar authorized while the app was in Testing (those tokens
     still expire).
  3. Request Google verification with `calendar.events`, `calendar.freebusy`,
     `openid`, `userinfo.email`. Decided 2026-11-06 (replaces the 2026-11-03 list):
     **remove `meetings.space.readonly`** from the consent screen before verifying. It is
     sensitive and unused until item 10, and an unused sensitive scope likely fails
     verification. Item 10 adds it later (new verification, tenants reconnect).
     Needs a privacy
     policy page and homepage on rnmglobalsolutions.com, the domain verified in Google
     Search Console, a scope justification, and a demo video of the connection flow.
     It can take several weeks.
  4. Update `docs/runbooks/google-calendar-manual-client-connect.md`, which today says
     to keep the app in Testing.
  5. Check that a calendar connected after publishing still works on day 8.

- A2P 10DLC registration for each tenant's SMS number.
- ManyChat account connected to each tenant's Facebook, Instagram, and WhatsApp.
- Vapi phone number per tenant.
- Consent text approved per tenant.
- Google Calendar access per tenant.
- Zoom (decided 2026-11-02: each tenant uses its own Zoom account). For each tenant
  that uses Zoom:
  - The tenant's Zoom admin creates a Server-to-Server OAuth app (Develop > Build App)
    and activates it.
  - Scopes: create, update, and delete meetings; read past meeting participants
    (attendance). Confirm the exact scope names in Zoom's docs when building item 4.
  - Paid Zoom plan (Pro or higher): needed for meetings longer than the free limit and
    for reading attendance.
  - The tenant shares account ID, client ID, and client secret; RNM stores them in Key
    Vault as `rnm-tenant-{tenant}-zoom-credentials`.
