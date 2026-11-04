# Brief: booking, calendars, and meetings

Availability, booking, Google Calendar, GoHighLevel, Google Meet, Zoom, cleanup, and
connection health.

## Key files

| Area | Files |
|---|---|
| Booking logic | `src/RNM.Platform.Application/Booking/BookingApplicationService.cs`, `BookingModels.cs` |
| Provider routing | `src/RNM.Platform.Infrastructure/Booking/ConfiguredBookingAdapter.cs` |
| Google Calendar | `src/RNM.Platform.Infrastructure/Booking/GoogleCalendarBookingAdapter.cs` |
| GoHighLevel | `src/RNM.Platform.Infrastructure/Booking/GoHighLevelBookingAdapter.cs` |
| Decisions | `docs/adr/0002-appointment-mode-and-online-meetings.md`, `docs/adr/0003-zoom-and-google-calendar-connections.md` |
| Runbooks | `docs/runbooks/google-calendar-manual-client-connect.md`, `docs/runbooks/zoom-tenant-setup.md` |

## Invariants

- A booking is only made on a slot that availability returned.
- Never tell the caller a meeting link exists unless M1 created one.
- If any step fails after the calendar event or meeting is created, clean both up
  (item 4).
- Zoom always runs in the tenant's own Zoom account (ADR 0003). Credentials come from
  `rnm-tenant-{tenant}-zoom-credentials`.
- Provider calls have timeouts (HttpClient 10 seconds today).

## Known traps

- **Google Meet is always requested and required.** If the link is missing, the booking
  fails after the event was already created, leaving an orphan event (item 4).
- **No token cache.** Every availability check and booking reads Key Vault and refreshes
  the Google token (item 13). `GoogleCalendarBookingAdapter.cs` is an exclusive hot file:
  item 13's cache waits for item 4.
- **Business hours live inside the booking credentials secret**, not in tenant config.
- Google OAuth app in "Testing" means refresh tokens expire after 7 days (ADR 0003).
  Production tokens expire after 6 months without use.
- One Zoom user can run one meeting at a time (not officially confirmed); map each
  advisor to their own Zoom user.
- Master class sessions do not block the calendar today (item 9).
- `ConfiguredBookingAdapter` strips `ProviderContactId` for non-GoHighLevel providers.
