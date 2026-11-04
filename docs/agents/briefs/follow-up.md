# Brief: CRM, confirmations, follow-up, consent, and language

## Key files

| Area | Files |
|---|---|
| CRM logic | `src/RNM.Platform.Application/Crm/CrmApplicationService.cs`, `CrmModels.cs`, `ChannelConsent.cs` |
| CRM storage | `src/RNM.Platform.Infrastructure/Crm/AzureTableCrmAdapter.cs`, `ConfiguredCrmAdapter.cs`, `GoHighLevelCrmAdapter.cs` |
| Confirmations | `src/RNM.Platform.Application/Confirmations/ConfirmationApplicationService.cs`, `src/RNM.Platform.Api/Functions/ConfirmationRetryFunction.cs` |
| SMS gate | `src/RNM.Platform.Application/Compliance/SmsEligibilityGate.cs`, `SendWindowPolicy.cs` |
| Follow-up | `src/RNM.Platform.Application/FollowUps/FollowUpSchedulingService.cs`, `FollowUpRunService.cs`, `src/RNM.Platform.Infrastructure/FollowUps/AzureTableFollowUpStore.cs` |
| Inbound SMS | `src/RNM.Platform.Api/Functions/TwilioSmsStatusWebhookFunction.cs` |
| Email | `src/RNM.Platform.Infrastructure/Messaging/SendGridEmailSender.cs`, `src/RNM.Platform.Application/EmailPreferences/` |

## Invariants

- Every SMS goes through `SmsEligibilityGate`; `TwilioSmsSender` requires its
  `SmsEligibilityProof`. Never bypass it.
- Opt-outs are never reversed automatically. STOP stops SMS and outbound calls; email
  unsubscribe stops email only.
- Marketing email needs the tenant postal address and a signed one-click unsubscribe
  link, or it is not sent.
- Booking confirmations are transactional: only an explicit opt-out on that number
  blocks them.

## Known traps

- **Two "lead status" fields.** Booking writes the top-level `LeadStatus` column; the
  follow-up stop condition reads `Attr_leadStatus` (`CrmContactRecord.OutboundLeadStatus`).
  Booking never sets the attribute (item 6).
- **In-memory CRM doubles hid that bug.** Test CRM behavior against the real
  `AzureTableCrmAdapter` on Azurite.
- **Inbound SMS that is not STOP is accepted and dropped**, while follow-up templates
  ask leads to reply (item 5).
- **255 properties per row** (including PartitionKey, RowKey, Timestamp). Since #17, attributes
  past the column limit go into `AttributesOverflowJson`; read them only through
  `CrmContactRecord.Attributes` (or `ContactAttributeStorage`), never as raw `Attr_*` columns.
- `FindContactByPhoneOrEmailAsync` swallows exceptions and returns "not found".
- The follow-up row key starts with the due time, so the "dedupe" hash does not stop a
  second schedule for the same contact and sequence.
- The follow-up runner scans every follow-up row of a tenant every 5 minutes.
- Templates have one language per tenant today (item 16).
- Unsubscribe links carry the email address base64-encoded in the URL (item 20).
