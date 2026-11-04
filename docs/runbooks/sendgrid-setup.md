# SendGrid Setup (Shared Account)

M1 sends all tenant email through one RNM SendGrid account. Each tenant has its
own sender identity, reply-to address and unsubscribe handling. This runbook
covers the account, DNS, platform secrets, tenant configuration and the Event
Webhook. Moving to one SendGrid subuser per tenant later reuses every step; only
where the API key is read changes.

## How email works in M1

| Email | Category | Sender | Extras |
| --- | --- | --- | --- |
| Booking confirmations, class confirmations, reminders, business notifications | Transactional | `communication.emailFromAddress` | `Reply-To` |
| Follow-up sequence steps (`followUps`, channel `email`) | Marketing | `communication.marketingEmailFromAddress` (falls back to `emailFromAddress`) | `Reply-To`, one-click unsubscribe (`List-Unsubscribe` + `List-Unsubscribe-Post`), footer with business name, `postalAddress` and unsubscribe link |

Every message carries SendGrid `custom_args` `rnmTenantId`, `rnmCorrelationId`
and `rnmEmailCategory`, and the categories `transactional`/`marketing` and
`tenant-<tenantId>` for per-tenant statistics. Click, open and subscription
tracking are disabled per message.

Marketing email is **never sent** without a postal address and a working
unsubscribe link; the send fails with `marketing_compliance_unavailable` and the
follow-up step is recorded as failed.

Unsubscribes are handled by M1, per tenant, at
`/api/tenants/{tenantId}/email/unsubscribe`. SendGrid's account-wide
subscription tracking and unsubscribe groups are not used, so a lead who
unsubscribes from one tenant keeps receiving another tenant's email.

## 1. Authenticate sending subdomains (DNS)

SendGrid: Settings -> Sender Authentication -> Authenticate Your Domain.

- `notify.rnmglobalsolutions.com` for transactional email.
- `news.rnmglobalsolutions.com` for marketing email.
- A tenant's own domain (for example `mail.clientdomain.com`) when the client
  can add DNS records; this gives the best trust and deliverability.

Add the CNAME records SendGrid shows (`em####`, `s1._domainkey`,
`s2._domainkey`) and press **Verify**. Do not point a tenant sender at a domain
before it is verified; SendGrid rejects unverified senders.

Never send automated email from the root domain (`@rnmglobalsolutions.com`),
which carries RNM's own mailbox reputation.

## 2. DMARC

If `_dmarc.rnmglobalsolutions.com` does not exist yet (check first; the
mailbox provider may have created it), add a TXT record:

```text
v=DMARC1; p=none; rua=mailto:dmarc@rnmglobalsolutions.com
```

After 2-4 weeks of clean reports, move to `p=quarantine`. Subdomains inherit
the policy.

## 3. Platform secrets (per environment Key Vault)

| Secret | Value |
| --- | --- |
| `rnm-sendgrid-api-key` | Restricted API key with **Mail Send** only; one key per environment |
| `rnm-email-unsubscribe-signing-key` | Random value that signs unsubscribe links (`gen` in the seed script). Rotating it invalidates links already sent, so rotate only if it leaks |
| `rnm-sendgrid-webhook-verification-key` | The Event Webhook verification public key (step 6) |

```bash
scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --platform
```

The unsubscribe link host is the Function App (`WEBSITE_HOSTNAME`). Set
`RNM_PUBLIC_BASE_URL` (https) on the Function App only if links must use a
custom domain.

## 4. Tenant configuration

```json
"communication": {
  "emailFromAddress": "yartex@notify.rnmglobalsolutions.com",
  "marketingEmailFromAddress": "yartex@news.rnmglobalsolutions.com",
  "replyToAddress": "<business inbox>",
  "postalAddress": "<street, city, state ZIP>",
  ...
}
```

The tenant preflight reports:

- `communication.replyToAddress`: warning when missing;
- `communication.postalAddress`: required in production when any follow-up
  sequence has an email step;
- `communication.marketingEmailFromAddress`: syntax.

## 5. Account settings

- Settings -> Tracking: turn Click Tracking off (M1 also disables it per
  message; the account setting protects any other sender).
- Unsubscribe groups: not needed.

## 6. Event Webhook (after M1 is deployed)

Settings -> Mail Settings -> Event Webhook:

- URL: `https://<main Function App host>/api/webhooks/sendgrid/events`
  (the main Function App; the contact Function App disables this function).
- Events: **Bounced**, **Dropped**, **Spam Reports**, **Unsubscribe**,
  **Group Unsubscribe**. Delivery and engagement events are not needed.
- Enable **Signed Event Webhook**, copy the verification key and store it:

```bash
scripts/seed-tenant-secrets.sh --vault <KEY_VAULT_NAME> --platform
# answer the rnm-sendgrid-webhook-verification-key prompt with the key
```

What M1 does with each event (only events carrying `rnmTenantId` of a known
tenant):

- spam report, unsubscribe, group unsubscribe -> contact
  `emailConsentStatus=opted_out` (SMS and calls unaffected) and a
  `consent.opted_out` timeline event with `channel=email`;
- hard bounce (`bounce` with type `bounce`) -> `email.bounced` timeline event;
  SendGrid itself suppresses the address;
- everything else is ignored.

Requests with a missing or invalid signature, or a timestamp older than 10
minutes, get `401`. If the CRM cannot record an event, M1 returns `503` and
SendGrid retries the batch.

## 7. Verify

1. Readiness is `ready` for the tenant.
2. Send a test email to a Gmail address; "Show original" must show SPF, DKIM and
   DMARC `PASS` and the expected `Reply-To`.
3. Trigger a follow-up email step to a test contact: the footer shows the
   postal address and unsubscribe link; Gmail shows its own "Unsubscribe"
   button (one-click header).
4. Open the unsubscribe link: a confirmation page appears; confirm, and the
   contact gets `emailConsentStatus=opted_out`; the next follow-up email step is
   skipped.
5. Send to a non-existent address: an `email.bounced` event appears on the
   contact timeline.

## Moving to one subuser per tenant

Create the subuser, move the tenant's authenticated domain to it, create its
Mail Send key and Event Webhook (same URL, its own verification key). M1 then
needs to read the API key and verification key per tenant; until that change,
keep all tenants on the shared account.
