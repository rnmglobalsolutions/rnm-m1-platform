# Master Classes Minimal Launch Summary

> Historical launch proposal. The implemented behavior and current limitations
> are documented in `docs/runbooks/masterclass-automation.md`.

## Decision

For financial education master classes, Zoom should not be treated as a normal
one-to-one appointment provider.

The current `book_appointment` flow creates one appointment per lead. A master
class is a one-to-many event: many leads register for the same shared session.

For the first launch, use Zoom manually and store the Zoom link in M1. Do not
automate Zoom meeting creation yet.

## Recommended First-Launch Flow

1. Create the master class manually in Zoom.
2. Store the class session in M1 with:
   - title
   - date
   - time
   - timezone
   - Zoom link
   - capacity
   - campaignId
3. The funnel, Vapi, or form qualifies the lead.
4. M1 creates or updates the contact in CRM.
5. M1 captures and persists consent.
6. M1 registers the lead for the class session.
7. M1 sends SMS and email confirmation with the class details and Zoom link.
8. M1 can later send reminders before the class.
9. Attendance can be imported after the class if needed.
10. Reporting measures leads, registrations, confirmations, attendance, booked
    calls, and projected revenue.

## Why This Is The Right MVP

The platform's moat is not creating Zoom meetings. Zoom meeting creation is
commoditized plumbing.

M1's value is:

- capturing leads
- qualifying leads
- storing consent safely
- registering people into revenue events
- sending confirmations and reminders
- tracking attendance and conversion
- proving campaign value through reporting

This keeps the product aligned with the platform principle: rent the plumbing,
build the moat.

## What Not To Build Yet

Do not build these for the first launch:

- Zoom API meeting creation
- Zoom OAuth
- automatic Zoom webinar provisioning
- advanced attendance sync
- multi-session class catalogs
- class dashboard UI
- long-term nurture automation

These can be added later if manual Zoom setup becomes a real operational
bottleneck.

## Implementation Direction

Add a separate class registration path instead of reusing `book_appointment`.

Recommended concepts:

- `ClassSession`: the shared master class event.
- `ClassRegistration`: one lead registered for one class session.
- `ClassNotificationService`: sends class confirmations and reminders.
- Reporting additions for registrations, attendance, booked calls, and projected
  revenue.

The first production slice should be:

1. Store class sessions.
2. Register contacts into sessions.
3. Send confirmation SMS/email.
4. Preserve attribution with `campaignId` and `sessionId`.
5. Report registrations and downstream bookings.

## Verdict

This design is correct for M1 because it avoids premature Zoom automation while
still enabling the revenue workflow:

Lead capture -> qualification -> consent -> class registration -> confirmation
-> reminder -> attendance/reporting -> booked call/revenue attribution.
