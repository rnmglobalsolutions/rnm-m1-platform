# ADR 0003: Zoom And Google Calendar Connections Per Tenant

## Status

Accepted (2026-11-02)

Builds on ADR 0002 (appointment mode and online meetings).

## Context

M1 books 1:1 appointments and runs master classes for any tenant. Online meetings can
use Google Meet or Zoom. Each tenant connects its own Google Calendar today through
RNM's Google OAuth app, and Zoom is not integrated yet (master classes store a Zoom
link pasted by hand).

## Decisions

### Appointment type per tenant

Each tenant chooses `onsite`, `google_meet`, or `zoom`. Google Meet needs no extra
setup because the meeting is created with the calendar event. Zoom is used when the
tenant asks for it.

### Zoom always runs in the tenant's own account

- Meetings for 1:1s and master classes are created in the tenant's Zoom account, never
  in RNM's.
- The tenant's Zoom admin creates a Server-to-Server OAuth app in that account and
  shares the account ID, client ID, and client secret.
- RNM stores them in Key Vault as `rnm-tenant-{tenant}-zoom-credentials`.
- Creating the app is free. The tenant needs Zoom Pro or higher: the free plan limits
  meeting length and does not allow reading attendance reports.

Why: the lead sees the tenant's brand, recordings and attendee data stay with the
tenant, RNM pays no Zoom licenses, and nothing has to be exported when a tenant leaves.

Rejected alternative: hosting all meetings in RNM's Zoom account. It needs no setup
from the tenant, but RNM would pay one licensed host per advisor, meetings show RNM's
brand, and recordings with tenant client data would live in RNM's account. Because each
tenant points to its own credentials secret, this can still be enabled for one tenant
later without code changes.

### Zoom hosts

One Zoom user can run only one meeting at a time. Each tenant configures the Zoom host
user; a tenant with several advisors maps each advisor to their own Zoom user.

### Master classes block the host's calendar

A master class session must also block the host's calendar, so no 1:1 can be booked
during it. Today class sessions never touch the calendar.

### Attendance

After a session, M1 reads attendance from the tenant's Zoom (past meeting
participants) or Google Meet and matches it to registrations by email. People who join
without signing in have no email, so they cannot always be matched; those registrations
stay `unknown`. Optional later step: enable Zoom meeting registration and register
each lead through the API so every attendee has a unique join link.

### Google Calendar connection must not expire

- In Google's "Testing" publishing status, refresh tokens with calendar scopes expire
  after 7 days. RNM's Google OAuth app must be published "In production".
- Until Google verifies the app, tenants see an "unverified app" warning when
  connecting and there is a cap of 100 new users. Verification is requested in
  parallel.
- Calendars authorized while the app was in Testing must be reconnected.
- Production tokens expire after 6 months without use; a daily health check keeps
  them in use.

### Health of every connection

A daily job tests each active tenant's Google and Zoom credentials. A rejected
credential raises an operations alert naming the tenant and shows in `/readiness`.
Google access tokens are cached for about an hour instead of being requested on every
call.

## Before signing a tenant that uses Zoom

Confirm:

- Zoom Pro or higher.
- How many advisors take appointments (one Zoom user each).
- Expected master class size (the plan's participant limit may need an add-on).

## Consequences

- Zoom onboarding is a short guided call with the tenant's admin; see
  `docs/runbooks/zoom-tenant-setup.md`.
- The work is tracked in `docs/backlog.md` items 4, 9, 10, 12, and 13, and in the
  "Not code" section.
- Some facts here come from forums and third-party sources, not Zoom's or Google's
  official docs: exact scope names, the Pro participant limit, and one meeting per host
  at a time. Confirm them when building items 4 and 10.
