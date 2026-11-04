# Brief: channels and lead capture

Vapi voice, ManyChat (Facebook, Instagram, WhatsApp), the web form, qualification, and
service area.

## Key files

| Area | Files |
|---|---|
| Vapi webhook | `src/RNM.Platform.Api/Functions/VapiInboundWebhookFunction.cs`, `src/RNM.Platform.Api/Voice/VapiWebhookPayloadParser.cs`, `VapiWebhookMapper.cs` |
| Voice workflow | `src/RNM.Platform.Application/Inbound/InboundBookingWorkflow.cs`, `InboundCallEventProcessor.cs` (does nothing today) |
| ManyChat | `src/RNM.Platform.Api/Functions/ManyChatLeadWebhookFunction.cs` |
| Web form | `src/RNM.Platform.Api/Functions/PublicFunnelFunction.cs` |
| Intake (chat and form) | `src/RNM.Platform.Application/LeadIntake/InboundLeadIntakeService.cs`, `LeadClassifier.cs`, `LeadClassificationPolicy.cs` |
| Qualification | `src/RNM.Platform.Application/Qualification/QualificationService.cs`, `ServiceAreaValidator.cs` |
| Config | `config/verticals/*.json`, `config/tenants/*.json`, `TenantConfiguration.cs`, `ConfigurationValidator.cs` |

## Invariants

- Every webhook is verified before processing (Vapi bearer, HMAC, or legacy secret;
  ManyChat secret header; public form: allowed origin plus honeypot).
- A lead is never lost silently. Failures return a retryable status (503) or create a
  follow-up.
- Every log has `correlationId` and `tenantId`; no personal data in telemetry
  (`SafeTelemetryProperties`).
- Vertical differences live in config, never in code.

## Known traps

- **Two pipelines.** Voice goes through `InboundBookingWorkflow`; ManyChat and the form go
  through `InboundLeadIntakeService`. They differ: voice does not classify, does not
  normalize the phone to E.164, and has no idempotency. Item 1 unifies the capture step;
  do not add a third path.
- **`check_availability` runs the whole workflow** and ends in `MarkFollowUpRequired`
  (item 6).
- **Each Vapi webhook gets a new correlation ID**, and `callId` is not logged (item 7a).
- **Service area**: an empty `zipCodes` list returns `NeedsEscalation`; a missing ZIP
  returns `MissingRequiredFields`. `cities` is never read (item 2).
- **HVAC words are hardcoded** in `VapiInboundWebhookFunction` (urgency signals). Move
  them to config when you touch that code; do not add more.
- Vertical `qualificationFields` are all required for voice, including fields a caller
  cannot know (`leadSource`, `campaignId`) (item 2).
- ManyChat no longer offers a website chat widget; websites link to Messenger,
  Instagram, or WhatsApp with a ref parameter.
- Rate limiting is in memory, per instance, per tenant, and copied in three functions.
