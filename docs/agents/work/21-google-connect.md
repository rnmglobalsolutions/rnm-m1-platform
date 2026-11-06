# 21: Self-service Google Calendar connection (21a, 21b, 21c; extends #13)

Status: research and planning
Branches: feature/m1-21a, feature/m1-21b, feature/m1-21c (and feature/m1-13)
Hot files: `GoogleCalendarBookingAdapter.cs` (exclusive; 21a and #13), `Program.cs`
(append-only), `TelemetryEventNames.cs` (append-only); others per plan

Source: Raisel's requirements prompt (2026-11-06), the Google audit of 2026-11-05, and
`docs/backlog.md` items 13 and 21. Track B in `docs/implementation-plan.md`.

## Goal

The customer only clicks a link, picks a Google account, accepts, and is connected.
Raisel's only manual step is generating and sending the link.

Customer experience:
1. Receives a link (email, SMS, or WhatsApp) Raisel generates for the tenant.
2. Opens it, picks the Google account, accepts the permissions.
3. Sees a "Calendar connected" page with the connected email.

## Google Cloud state (as given by Raisel)

- Project `rnm-m1-platform`. OAuth app "RNM Global Solutions", External, In production,
  not yet verified (cap of 100 users until verified).
- OAuth client "M1 Calendar Connect" (Web application). Current redirect, manual tests
  only: `http://localhost:8080/oauth2callback`.
- Site: https://www.rnmglobalsolutions.com (privacy policy at `/privacy.html`).
- Reusable: app Key Vault (managed identity), SendGrid (`rnm-sendgrid-api-key`),
  internal key (`rnm-internal-api-key`), the endpoints' correlationId pattern.

Tenant secret format today (`rnm-tenant-{tenantId}-google-calendar-credentials`):
`calendarId`, `refreshToken`, `clientId`, `clientSecret`, `timeZone`, `businessStart`,
`businessEnd`, `urgentBusinessStart`, `urgentBusinessEnd`, `appointmentMinutes`,
`slotStepMinutes`, `lookAheadDays`, `includeWeekends`, `includeWeekendsForUrgent`.

## Decisions (Raisel, 2026-11-06)

1. Added to the backlog as 21a, 21b, 21c, plus #13 extended (health check and cache).
   Runs as Track B in parallel with Phase 1.
2. **Scopes: remove `meetings.space.readonly` before verification.** The connection flow
   requests and requires exactly `calendar.events` and `calendar.freebusy`, plus
   `openid` and `userinfo.email` for the connected email. This replaces the prompt's
   "the 5 scopes" and "require meetings.space.readonly". Item 10 adds the Meet scope later.
3. **Separate Key Vault for tenant Google credentials**, with 7-day soft-delete
   retention; the app gets write access only there. The platform vault stays read-only
   for the app. This also lets deletion meet the privacy policy's 30 days.
4. Research and planning start now (read-only). No implementation until Raisel confirms
   the pending live test call (demo calendar reconnect plus #7a/#2 check) passed.

Open decisions (for the plan approval):
- Can one Google account be connected to more than one tenant?
- Pages: English only, Spanish only, or detect language (prompt says EN + ES by
  Accept-Language or a parameter)?
- Link delivery: the endpoint only returns it (Raisel sends), or M1 sends it?

## Components (from the prompt)

1. **Connection link (admin only).** `POST /api/admin/tenants/{tenantId}/google/connect-link`,
   protected with the repo's internal auth. Single-use link that expires (default 72 h,
   configurable). Stored only as a hash. A new link invalidates older ones for the tenant.
2. **Start.** `GET /api/google/connect?t={linkToken}`. Validates the link (exists, not
   expired, not used) and redirects to Google with `access_type=offline`,
   `prompt=consent`, `include_granted_scopes=true`, PKCE (S256), the approved scopes, and an
   HMAC-signed `state` (tenantId, 10–15 min expiry, single-use nonce). Links, nonces, and
   PKCE verifiers live in storage with expiry (the repo's Table Storage; expiry checked on
   read and cleaned by a timer), never in Key Vault.
3. **Callback.** `GET /api/google/oauth/callback`:
   - Validates state (constant-time signature check, expiry, unused nonce) and PKCE.
   - Idempotent: a reload or double click shows the first attempt's result.
   - `error=access_denied`: friendly page with retry (link still valid if not expired).
   - Workspace admin block or any other Google error: clear message, no technical detail.
   - Exchanges the code at `oauth2.googleapis.com/token`.
   - Checks the `scope` field; if a required scope is missing, saves nothing and asks to
     retry with all permissions checked.
   - Gets the account email (from the `id_token` or `userinfo`; see research).
   - Runs a free/busy test BEFORE accepting the connection.
   - Saves a NEW VERSION of the tenant secret, keeping schedule settings and changing only
     credentials; defaults (configurable) when no secret exists; disables older versions.
   - Same Google account reconnecting: NEVER call revoke on the old token (revoking any
     token of a grant revokes all tokens of that account for this app, including the new
     one). Only disable the old version.
   - Different account: after saving and testing the new one, revoke the old account's
     token, log the change, and email Raisel through SendGrid.
   - Marks the link used and shows the success page.
4. **Client ID/secret at app level.** Read once from configuration (Key Vault), not from
   each tenant secret. Old tenant JSON with `clientId`/`clientSecret` keeps working until
   migrated. New connections do not store them per tenant.
5. **Connection status per tenant.** `status` (connected / disconnected / error),
   connected email, `connectedAt`, `lastHealthCheckAt`, `lastError` (no secrets). Note:
   tenants are JSON files in the repo (read-only at runtime), so this needs a new table.
6. **Daily health check (timer).** One free/busy per connected tenant, configurable
   concurrency. `invalid_grant` / `invalid_client` → `disconnected` and SendGrid email with
   tenantId and error (no secrets). 429, 5xx, timeouts → retry with backoff, never
   disconnect. (Delivered in #13.)
7. **Disconnect and deletion.** `POST /api/admin/tenants/{tenantId}/google/disconnect`
   (admin). Revokes at `oauth2.googleapis.com/revoke`, deletes the secret, marks
   `disconnected`. Meets the privacy policy's 30 days, including Google data stored outside
   Key Vault (event IDs, Meet links, attendance).
8. **Cache.** A reconnection takes effect without restart (short TTL or invalidation on
   save). Flag any `@Microsoft.KeyVault` reference pinned to a version. (Delivered in #13.)
9. **Customer pages.** Simple HTML served by the Function, "RNM Global Solutions" brand, no
   external scripts, responsive, English and Spanish. (Approved exception to AGENTS.md "no
   UI": minimal pages required by OAuth.)
10. **Security and hygiene.** Never log tokens, codes, secrets, the full state, or the link
    token; correlationId in traces. HTTPS only in production. Production redirect on a
    custom domain, for example
    `https://api.rnmglobalsolutions.com/api/google/oauth/callback` (not
    `*.azurewebsites.net`). Localhost redirect only in local development, by configuration.
    Rate limiting or basic abuse protection on the public endpoints (connect, callback).
11. **Configuration.** Public base URL, redirect URI, state HMAC key, link and state
    expiry, schedule defaults, health-check concurrency, notification email. Every new
    setting documented.

## Constraints (binding for every agent)

- Do NOT add or change Google scopes (see decision 2). If something seems to need another
  scope, raise it as a question.
- Follow the repo's conventions, language, libraries, and structure. No new frameworks
  without justification (a JWT library for `id_token` validation counts as new).
- Do not change Vapi or availability behavior, except reading credentials from the new
  place.
- `scripts/google-calendar-oauth-manual.sh` keeps working as a fallback.
- No real calls to Google or Azure, no deployments, no commits outside the agent loop
  (Raisel approves each PR).
- Never print secret values in any output.

## Acceptance criteria

- A new calendar connects with only a generated link; no curl, no manual Key Vault.
- Reconnecting keeps the schedule settings, disables old credentials, and the new token
  keeps working.
- Switching Google accounts revokes the old account and Raisel gets an email.
- Cancel, unchecked permissions, or an admin block show a clear message and save nothing
  broken.
- Reloading the callback or reusing a link or state breaks nothing and creates no
  duplicates.
- A revoked token is caught by the daily check, the tenant becomes `disconnected`, and
  Raisel gets an email. A temporary Google error disconnects no one.
- Disconnection revokes at Google and deletes credentials within the privacy policy's 30
  days.
- No log contains tokens, codes, or secrets.
- Scopes are exactly the approved ones.
- Vapi and availability endpoints behave the same; `sample-hvac-tenant` keeps working
  during and after the migration.

## Delivery mapping

| Prompt block | Backlog item | Agents |
|---|---|---|
| Phase 1 research | all | researcher |
| Phase 1 plan | each item | planner (repo map from the 2026-11-05 audit) |
| a) app-level client | 21a | implementer, verifier |
| b) link, start, callback, pages, status | 21b | implementer, verifier; config-author for page text review |
| c) health, cache | #13 (extended) | implementer, verifier |
| c) disconnect, deletion | 21c | implementer, verifier; Raisel approves privacy text |
| d) tests and Postman | inside each item | implementer, verifier; orchestrator updates `docs/postman` |
| e) README | each item's runbook | config-author |
| "Show me the diff after each block" | each item's PR | Raisel reviews and merges |

## Research

## Plan (21a)

## Implementation

## Verification

## Lessons
