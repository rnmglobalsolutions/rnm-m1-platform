# P0-5: Full Google OAuth scope list for M1

Status: decided
Branch: none (documentation only)

Source: `docs/implementation-plan.md`, Phase 0, step 5; `docs/backlog.md`, Google OAuth
app section. Raisel requests Google verification once, with every scope M1 will need.

## Research

Researcher report, 2026-11-03. Sources are Google's official docs unless marked third-party.

Recommended scopes to request at verification:

| Scope | Tier | Why M1 needs it |
|---|---|---|
| `https://www.googleapis.com/auth/calendar.events` | Sensitive (third-party quoting Google) | Create, update, delete 1:1 and master-class events; Meet link through `conferenceData.createRequest` with `conferenceDataVersion=1`. In use today. |
| `https://www.googleapis.com/auth/calendar.freebusy` | Unconfirmed, plan as sensitive | `freebusy.query` for availability and the daily health check (item 13). `calendar.events` does not cover `freebusy.query` (official scope list). In use today. |
| `https://www.googleapis.com/auth/meetings.space.readonly` | Sensitive (official) | Meet REST API `conferenceRecords.participants` for attendance (item 10). Covers conferences created by Calendar. `meetings.space.created` is not enough (only spaces the app created). |
| `openid` | Unconfirmed, likely non-sensitive | Pairs with the email scope. |
| `https://www.googleapis.com/auth/userinfo.email` | Unconfirmed, likely non-sensitive | Show which Google account is connected in readiness and logs. |

No restricted scope is needed, so no CASA security assessment.

Do not request: `calendar`, `calendar.readonly`, `calendar.events.readonly`,
`meetings.space.created`, `userinfo.profile`, Drive, Gmail.

Alternative: `calendar.events.owned` is narrower than `calendar.events`, but only works
if the booking calendar is owned by the authorizing user, and its coverage of
list/patch/delete is unconfirmed. Not recommended for now.

Facts that affect later items:

- **Item 10 (attendance on Google Meet): participants have no email.** The official
  Participant resource gives `signedinUser` (`users/{user}` and display name),
  `anonymousUser` (display name), `phoneUser` (partly hidden number). Matching
  registrations by email, as item 10 says for Zoom, does not work on Meet as is.
  Participant data is available for up to 30 days after the conference. Whether consumer
  Gmail accounts can use the Meet REST API is unconfirmed.
- **Refresh tokens:** in Testing status they expire after 7 days (Calendar scopes are
  not exempt). In production they also stop working when: unused for 6 months, the user
  revokes access, a Workspace admin policy blocks it, or the account has more than 100
  refresh tokens for the same client (the oldest one is invalidated silently). M1
  should keep one token per tenant and treat `invalid_grant` as "reconnect required"
  (item 13).
- **Verification for sensitive scopes:** a homepage and a privacy policy on the same
  domain, the domain verified in Search Console, an unlisted YouTube demo video of the
  OAuth flow (English consent screen, client ID visible, each scope in use), and a
  justification per scope. Google says up to 10 days; third-party guides say weeks.

Conflicts: review time (10 days official vs weeks third-party); freebusy tier
(unconfirmed); participant email (official: none; one third-party site claims
`primaryEmail` through the Admin SDK, which needs admin scopes).

Sources:
- https://developers.google.com/workspace/calendar/api/auth
- https://developers.google.com/workspace/calendar/api/v3/reference/freebusy/query
- https://developers.google.com/workspace/calendar/api/v3/reference/events/insert
- https://developers.google.com/workspace/meet/api/guides/authenticate-authorize
- https://developers.google.com/workspace/meet/api/guides/participants
- https://developers.google.com/workspace/meet/api/reference/rest/v2/conferenceRecords.participants
- https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification
- https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification
- https://developers.google.com/identity/protocols/oauth2#expiration
- https://developer.nylas.com/docs/provider-guides/google/google-verification-security-assessment-guide/ (third-party)

## Decision

Approved by Raisel on 2026-11-03: request these five scopes at verification, exactly as
in the table above.

```text
https://www.googleapis.com/auth/calendar.events
https://www.googleapis.com/auth/calendar.freebusy
https://www.googleapis.com/auth/meetings.space.readonly
openid
https://www.googleapis.com/auth/userinfo.email
```

Follow-up done in the same change: the manual connect script and runbook request all
five, so tenants connected from now on do not need to reconnect when items 10 and 13
start using the new scopes. Open for item 10: Meet participants have no email (see
Research).
