# 21: Self-service Google Calendar connection (21a, 21b, 21c, 21d; extends #13)

Status: research and planning
Branches: feature/m1-21a, feature/m1-21b, feature/m1-21c, feature/m1-21d (and feature/m1-13)
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

## Decisions (Raisel, 2026-11-07)

Source: Raisel's onboarding walkthrough ("Cool Air HVAC", owner María) and a critical
review of manual versus automatic link delivery.

5. **M1 sends the link (new item 21d).** The tenant configuration marks the tenant as
   `googleConnect: "required"` with the owner's email. One hourly rule sends a link when
   the tenant must be connected, is not connected, has no valid link, and was not
   disconnected by an admin. The 21b admin endpoint stays as the manual and emergency
   path. Order: 21a → 21b → #13 → 21d → 21c.
6. **One Google account may serve several tenants (option B).** Revoke the old account
   only when no other tenant uses it (match by `sub`). Applies to account switch (21b)
   and disconnection (21c).
7. **Pages follow the browser language** (`Accept-Language`), English by default.
8. **Added to 21b:** schedule settings come from the tenant configuration and are copied
   into the secret on connect; operator email on every connection; admin status
   endpoint; optional expected account or domain; primary calendar only.
9. **Added to #13:** `invalid_grant` on a live call marks `disconnected` at once (lead
   goes to follow-up).
10. **Safeguards from the critical review (binding for 21b, #13, 21d, 21c):**
    1. Per-environment switch `GoogleConnect:CustomerEmailsEnabled`, on only in prod and
       only after Google verification; when off, emails go to the operator. Tenant JSON
       is shared by every environment, so without it dev would email real customers.
    2. `invalid_client` never contacts a tenant. Safety brake: more than 2 tenants failing
       in one run → no customer email, operator alert.
    3. No reminder replaces a valid link (only the hash is stored, so a reminder would
       need a new link, which kills the one the owner may be opening). A new link is sent
       only after the old one expires unused. At most 2 automatic sends, then the operator.
    4. Optional expected account or domain; operator notified with the connected email.
    5. The link is consumed only on a successful connection, never on open (email
       scanners such as Outlook Safe Links open links).
    6. A failed send retries and then alerts; the admin endpoint can return the link for
       manual delivery.
    7. Status separates "disconnected by error" from "disconnected by admin"; admin-
       disconnected tenants are never contacted.
    8. Expired or used link page: "Send me a new link", only to the configured owner
       email, rate limited.
    9. Customer emails need Google verification (no "unsafe" warning; each test account
       counts toward the lifetime 100-user cap) and an authenticated sender.

Checked 2026-11-07 (read-only):
- SendGrid: domain authentication verified for `rnmglobalsolutions.com`, `news.` and
  `notify.`; link branding verified; single sender `info@rnmglobalsolutions.com`. DMARC
  `p=quarantine`, relaxed alignment. Email API on the paid Essentials 50K plan; the "Trial" label is Marketing Campaigns, unused by M1.
  `SendGridEmailSender.cs:279` already turns click tracking off, so link tokens are not
  rewritten through the tracking domain.
- Azure: dev Function App `rnm-m1-dev-wwptb6ha7riuc-func` (Flex Consumption, FC1) created
  2026-05-02, before Flex supported custom domains with managed certificates (research
  finding 12). Staging and prod resource groups are empty, so the prod app will be new.
  `api.rnmglobalsolutions.com` has no DNS record yet. The 21b plan decides the dev
  redirect (new dev app, or test-only redirect).

Open decisions: none.

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

Researcher report, 2026-11-06 (official docs; no API calls).

1. **Revocation:** revoking an access token also revokes its refresh token (confirmed).
   Google says revocation removes the scopes granted "to a project ... for all clients
   registered under that project" (broader than "this app"). Whether it also kills a token
   from a later re-consent: likely, not explicit. Revocation may take time to apply.
2. **Re-consent:** `prompt=consent` issues a new refresh token (confirmed). The old one
   likely stays valid. Limit: 100 refresh tokens per Google account per client; the 101st
   silently invalidates the oldest. Tokens die after 6 months unused or on user revoke.
   **Implication:** after a same-account reconnect, disabling the old Key Vault version
   does not end the old token's access at Google; only revoke would, and revoke risks the
   new token. Accepted risk: the old token is never used again and ages out.
3. **id_token:** signature validation is not required when received directly from the token
   endpoint over HTTPS. Check `iss` (`https://accounts.google.com` or `accounts.google.com`),
   `aud` = client ID, `exp`. Use `sub` as the stable account ID, `email` for display. No JWT
   library needed. Alternative: userinfo endpoint (from the discovery document).
4. **PKCE on a Web client:** official web-server doc is silent (unconfirmed). Recommended:
   send `code_challenge` + `S256` and also `client_secret` + `code_verifier` at exchange;
   do not rely on Google enforcing PKCE.
5. **Granular consent:** users can uncheck individual scopes (confirmed). The token
   response `scope` field (space-delimited) is the record; with `include_granted_scopes`
   it may include earlier grants.
6. **Errors:** callback `access_denied` (confirmed); `admin_policy_enforced`, `org_internal`,
   `disallowed_useragent` (Google Help Center, likely). Token endpoint: `invalid_grant`,
   `invalid_client`, `redirect_uri_mismatch` (confirmed). Show a generic message for any
   other value.
7. **Redirects:** HTTPS required except localhost (allowed; keep it behind config). Once
   `rnmglobalsolutions.com` is an authorized domain, any subdomain (for example
   `api.rnmglobalsolutions.com`) is covered. Search Console verification of the authorized
   domain is required for sensitive scopes.
8. **Verification:** requesting only scopes in use is fine; scopes can be added later.
   **The 100-user cap applies over the project's lifetime and cannot be reset.** Up to 10
   days official. Adding a scope later means tenants reconnect (likely).
9. **Quotas:** 600 requests/min per user, 10,000/min and 1,000,000/day per project. One
   free/busy per tenant per day is negligible.
10. **Key Vault:** retention 7–90 days, set only at creation. Purge protection cannot be
    disabled once on. **A soft-deleted secret keeps its name reserved:** recreating it
    fails until purged or recovered. "Key Vault Secrets Officer" at vault scope covers
    set, update, delete (purge likely). Per-secret role scope is not recommended.
    **Implication:** dedicated vault with 7-day retention and purge protection OFF, so
    disconnect can delete and purge immediately and a later reconnect reuses the name.
11. **.NET:** `GetPropertiesOfSecretVersions` lists versions; set `Enabled = false` and
    `UpdateSecretProperties` to disable one; `SetSecretAsync` creates a new version.
12. **Flex Consumption:** custom domains and free managed certificates are supported
    (official, 2026-09-15). **Apps created before the feature have no migration path:**
    may need a new Flex app. Check the M1 app's creation date. Subdomain CNAME must point
    directly at `<app>.azurewebsites.net`.
13. **Table Storage:** no TTL. Store `expiresAt`, check on read, clean with a timer.

Conflicts: revocation scope (project-wide wording); PKCE on Web clients; old third-party
guides on Flex certificates (outdated).

**New decision needed (ties to "one account, several tenants"):** revocation is
project-wide for that Google account. If one account may serve several tenants, the
"different account → revoke the old one" step must skip the revoke while another tenant
still uses the old account.

Sources: developers.google.com/identity/protocols/oauth2 (web-server, native-app,
resources/granular-permissions, production-readiness/sensitive-scope-verification),
developers.google.com/identity/openid-connect/openid-connect,
developers.google.com/workspace/calendar/api/guides/quota,
support.google.com/cloud/answer/10311615 and 13463817,
support.google.com/accounts/answer/16668185,
learn.microsoft.com (key-vault soft-delete-overview, key-vault-recovery, rbac-guide,
secrets quick-create-net, SecretClient API; azure-functions flex-consumption-plan and
how-to; app-service configure-ssl-certificate; storage tables overview).

## Plan (21a)

Planner report, 2026-11-06. Precondition: Raisel confirms the pending live test call;
no other branch is changing `GoogleCalendarBookingAdapter.cs`.

Goal: the token refresh takes the OAuth client from two platform Key Vault secrets read
once per process; a tenant secret that still has `clientId`/`clientSecret` keeps working
and wins when both are present.

Repo facts (verified): adapter reads the tenant secret every call (192-209); refresh needs
`refreshToken`+`clientId`+`clientSecret` (211-243); adapter is a typed HttpClient
(transient), so the cache must be a separate singleton; readiness requires the tenant pair
(371-389); preflight never reads secret values; existing `@Microsoft.KeyVault` references
in `functionApp.bicep` (lines 72, 75) are **not pinned** to a version.

Decisions:
1. Platform secrets `rnm-google-oauth-client-id` and `rnm-google-oauth-client-secret`,
   read through the existing `ISecretProvider` (same pattern as
   `rnm-email-unsubscribe-signing-key`). No Bicep change; works locally through
   `EnvironmentSecretProvider`.
2. Singleton `GoogleOAuthClientProvider`: first successful read cached for the process
   (`SemaphoreSlim`), failures never cached, values trimmed. Rotation = new version +
   restart.
3. Precedence: tenant pair wins when both values are non-empty; otherwise the app client;
   never mixed. Legacy tenants send identical requests with no extra reads. A refresh
   token is likely bound to the client that issued it.
4. Does not block #21b's separate vault (the client stays in the platform vault).

Files: new `Infrastructure/Booking/GoogleOAuthClientProvider.cs` (`IGoogleOAuthClientProvider`,
`GoogleOAuthClient` sealed class whose `ToString` never prints the secret);
`GoogleCalendarBookingAdapter.cs` (constructor + `GetAccessTokenAsync` only);
`ReadinessFunction.cs` (`googleOAuthClient` check with fixed detail strings, no values;
`bookingCredentialsShape` = calendarId and accessToken or refreshToken); `Program.cs` (+1
singleton); `TelemetryEventNames.cs` (+`booking.google_oauth_client_unavailable`);
`local.settings.json.example` (2 placeholders). Size S.

Hot files: exclusive `GoogleCalendarBookingAdapter.cs`; append-only `Program.cs`,
`TelemetryEventNames.cs`.

Tests first (21, unit only): provider (reads both names once across concurrent calls,
failure not cached, blanks → null, trimming, cancellation, `ToString` hides secret);
adapter (tenant pair used and provider not called; app client used; tenant wins when both;
never mixed; no client → same failure message, no HTTP call, event with tenantId and
correlationId and no secrets; same slots legacy vs app-level; booking path; access token
skips provider); readiness (ready with app client; blocked when missing; legacy match;
legacy mismatch still ready; body never contains values; non-Google tenants untouched).

Orchestrator / Raisel (not agents): `scripts/seed-tenant-secrets.sh` adds the two platform
secrets; briefs (`platform.md` secret names, `booking.md` trap and #13/#4 order note).
config-author: manual-connect runbook (client fields legacy/optional, store the app client
once, pass it to the script without printing), seed and local-development runbooks.

Migration of `sample-hvac-tenant` (per environment, Raisel runs, values piped never printed):
1. Merge 21a (nothing changes: tenant pair wins).
2. Copy `clientId`/`clientSecret` from the tenant secret into the two platform secrets
   (only if it is the "M1 Calendar Connect" client).
3. Readiness → "legacy; matches app configuration" (stop if "differs").
4. Write a new tenant secret version without the client fields (note the old version ID).
5. Readiness → "from app configuration", ready.
6. Smoke test availability (Postman) and compare slots.
7. Rollback: write a new version from the old version ID (do not disable the latest).
8. After a week green, disable the old version.

Risks: client secret held in a singleton (masked `ToString`, fixed strings in telemetry
and readiness, tests assert no values); a wrongly copied client breaks a migrated tenant
(caught by steps 3 and 5, rollback in 7).

Open question (researcher, non-blocking): is a refresh token bound to its OAuth client, and
which error does a refresh with another client return? Safe either way (tenant pair wins).

Note: `docs/agents/briefs/booking.md` says #13's cache waits for #4; Track B order (21a,
21b, #13; #4 one at a time on the adapter) takes priority. Orchestrator updates the brief.

## Implementation

## Verification

## Lessons
