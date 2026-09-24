using System.Net.Mail;
using System.Text.RegularExpressions;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadImport;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.LeadIntake;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.LeadIntake;

public sealed class InboundLeadIntakeService
{
    private static readonly Regex TemplateTokenRegex = new(@"\{\{\s*(?<token>[^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private readonly IInboundLeadReceiptStore receiptStore;
    private readonly ICrmAdapter crmAdapter;
    private readonly CrmApplicationService crmApplicationService;
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IConfirmationRetryScheduler retryScheduler;
    private readonly IEventLogger eventLogger;

    public InboundLeadIntakeService(
        IInboundLeadReceiptStore receiptStore,
        ICrmAdapter crmAdapter,
        CrmApplicationService crmApplicationService,
        ITenantConfigurationProvider tenantConfigurationProvider,
        IConfirmationRetryScheduler retryScheduler,
        IEventLogger eventLogger)
    {
        this.receiptStore = receiptStore;
        this.crmAdapter = crmAdapter;
        this.crmApplicationService = crmApplicationService;
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.retryScheduler = retryScheduler;
        this.eventLogger = eventLogger;
    }

    public async Task<InboundLeadIntakeResult> ProcessAsync(
        InboundLeadIntakeRequest request,
        CancellationToken cancellationToken)
    {
        var validationFailure = Validate(request, out var phone, out var email);
        if (validationFailure is not null)
        {
            return new InboundLeadIntakeResult(false, false, false, FailureCode: validationFailure);
        }

        InboundLeadReceiptClaimResult claim;
        try
        {
            claim = await receiptStore.TryBeginAsync(
                    new InboundLeadReceiptClaimRequest(
                        request.TenantId,
                        request.Source,
                        request.ExternalEventId,
                        request.CorrelationId,
                        DateTimeOffset.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(TelemetryEventNames.LeadIntakeFailed, request, "receipt_store_unavailable", cancellationToken)
                .ConfigureAwait(false);
            return new InboundLeadIntakeResult(false, false, false, FailureCode: "dependency_unavailable");
        }

        if (claim.State == InboundLeadReceiptClaimState.Completed)
        {
            await LogAsync(TelemetryEventNames.LeadIntakeDuplicate, request, "completed", cancellationToken).ConfigureAwait(false);
            return new InboundLeadIntakeResult(true, true, false, claim.ProviderContactId);
        }

        if (claim.State == InboundLeadReceiptClaimState.InProgress)
        {
            await LogAsync(TelemetryEventNames.LeadIntakeDuplicate, request, "processing", cancellationToken).ConfigureAwait(false);
            return new InboundLeadIntakeResult(true, true, true);
        }

        try
        {
            var lookup = await crmAdapter.FindContactByPhoneOrEmailAsync(
                    new CrmContactLookupRequest(request.TenantId, request.CorrelationId, phone, email),
                    cancellationToken)
                .ConfigureAwait(false);
            var previousConsent = lookup.Contact?.ConsentStatus ?? CrmConsentStatuses.Unknown;
            var consent = ResolveConsent(previousConsent, request.MarketingConsentGranted);
            var attributes = BuildAttributes(request, consent);

            var upsert = await crmAdapter.UpsertContactAsync(
                    new CrmContactUpsertRequest(
                        request.TenantId,
                        request.VerticalId,
                        request.CorrelationId,
                        lookup.ProviderContactId,
                        phone,
                        email,
                        Normalize(request.CustomerName),
                        GetAttribute(attributes, "zipCode"),
                        attributes)
                    {
                        LeadStatus = CrmLeadStatuses.New,
                        LastInteractionAt = DateTimeOffset.UtcNow,
                        AllowOptOutReversal = false
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!upsert.Succeeded || string.IsNullOrWhiteSpace(upsert.ProviderContactId))
            {
                return await FailAsync(request, claim, "crm_upsert_failed", cancellationToken).ConfigureAwait(false);
            }

            var timelineResult = await crmAdapter.AddTimelineEventAsync(
                    new CrmTimelineEventRequest(
                        request.TenantId,
                        request.CorrelationId,
                        upsert.ProviderContactId,
                        ProviderBookingId: null,
                        CrmTimelineEventTypes.LeadIntakeReceived,
                        request.Source,
                        "External lead intake received.",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["campaignId"] = request.CampaignId ?? string.Empty,
                            ["externalContactId"] = request.ExternalContactId
                        }),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!timelineResult.Succeeded)
            {
                return await FailAsync(request, claim, "timeline_write_failed", cancellationToken).ConfigureAwait(false);
            }

            var consentTimelineResult = await WriteConsentTimelineAsync(
                    request,
                    upsert.ProviderContactId,
                    previousConsent,
                    consent,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!consentTimelineResult.Succeeded)
            {
                return await FailAsync(request, claim, "consent_audit_failed", cancellationToken).ConfigureAwait(false);
            }

            var followUpRequested = false;
            if (request.ScheduleFollowUp)
            {
                var followUp = await crmApplicationService.MarkFollowUpRequiredAsync(
                        new CrmFollowUpRequest(
                            request.TenantId,
                            request.CorrelationId,
                            upsert.ProviderContactId,
                            "External lead requires follow-up.")
                        {
                            Source = request.Source,
                            CustomerName = Normalize(request.CustomerName),
                            CustomerPhoneNumber = phone,
                            CustomerEmail = email,
                            Attributes = attributes,
                            LastInteractionAt = DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                followUpRequested = followUp.Succeeded;
            }

            await receiptStore.CompleteAsync(
                    new InboundLeadReceiptCompletionRequest(
                        request.TenantId,
                        claim.RowKey,
                        claim.LeaseId!,
                        request.CorrelationId,
                        upsert.ProviderContactId,
                        DateTimeOffset.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);

            var businessNotificationQueued = await TryQueueBusinessNotificationsAsync(
                    request,
                    phone,
                    email,
                    attributes,
                    cancellationToken)
                .ConfigureAwait(false);
            await LogAsync(TelemetryEventNames.LeadIntakeCompleted, request, "completed", cancellationToken).ConfigureAwait(false);
            return new InboundLeadIntakeResult(
                true,
                false,
                false,
                upsert.ProviderContactId,
                followUpRequested,
                businessNotificationQueued,
                GetAttribute(attributes, "leadClassification"),
                GetAttribute(attributes, "recommendedRoute"),
                GetAttribute(attributes, "classificationReasons"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await FailAsync(request, claim, "processing_failed", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CrmOperationResult> WriteConsentTimelineAsync(
        InboundLeadIntakeRequest request,
        string providerContactId,
        string previousConsent,
        string resultConsent,
        CancellationToken cancellationToken)
    {
        var eventType = string.Equals(previousConsent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
            ? CrmTimelineEventTypes.MarketingConsentExternalBlockedOptedOut
            : request.MarketingConsentGranted
                ? CrmTimelineEventTypes.MarketingConsentExternalGranted
                : CrmTimelineEventTypes.MarketingConsentExternalDeclined;
        return await crmAdapter.AddTimelineEventAsync(
                new CrmTimelineEventRequest(
                    request.TenantId,
                    request.CorrelationId,
                    providerContactId,
                    ProviderBookingId: null,
                    eventType,
                    request.Source,
                    "External marketing consent captured.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["explicitConsent"] = request.MarketingConsentGranted.ToString(),
                        ["previousConsentStatus"] = previousConsent,
                        ["consentStatus"] = resultConsent,
                        ["consentCapturedAt"] = request.ConsentCapturedAt?.ToUniversalTime().ToString("O") ?? string.Empty,
                        ["consentTextVersion"] = request.ConsentTextVersion ?? string.Empty
                    }),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> QueueBusinessNotificationsAsync(
        InboundLeadIntakeRequest request,
        string? phone,
        string? email,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        TenantConfiguration tenant;
        try
        {
            tenant = await tenantConfigurationProvider.GetTenantConfigurationAsync(request.TenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfigurationException)
        {
            return false;
        }

        var templates = tenant.Communication.ConfirmationTemplates;
        var tokens = BuildTemplateTokens(request, tenant, phone, email, attributes);
        var attempted = 0;
        var queued = 0;
        if (!string.IsNullOrWhiteSpace(tenant.Communication.BusinessNotificationPhoneNumber)
            && !string.IsNullOrWhiteSpace(templates.BusinessSmsBodyTemplate)
            && ShouldNotifyBusinessBySms(tenant.Communication.EffectiveBusinessSmsNotification, attributes))
        {
            attempted++;
            queued += await retryScheduler.ScheduleAsync(
                    new ConfirmationRetryRequest(
                        request.TenantId,
                        request.CorrelationId,
                        ConfirmationRetryKind.BusinessSms,
                        tenant.Communication.BusinessNotificationPhoneNumber,
                        Render(templates.BusinessSmsBodyTemplate, tokens, attributes)),
                    cancellationToken)
                .ConfigureAwait(false) ? 1 : 0;
        }

        if (!string.IsNullOrWhiteSpace(tenant.Communication.BusinessNotificationEmail)
            && !string.IsNullOrWhiteSpace(templates.BusinessEmailSubjectTemplate)
            && !string.IsNullOrWhiteSpace(templates.BusinessEmailBodyTemplate))
        {
            attempted++;
            queued += await retryScheduler.ScheduleAsync(
                    new ConfirmationRetryRequest(
                        request.TenantId,
                        request.CorrelationId,
                        ConfirmationRetryKind.BusinessEmail,
                        tenant.Communication.BusinessNotificationEmail,
                        Render(templates.BusinessEmailBodyTemplate, tokens, attributes),
                        Render(templates.BusinessEmailSubjectTemplate, tokens, attributes)),
                    cancellationToken)
                .ConfigureAwait(false) ? 1 : 0;
        }

        await LogAsync(
                attempted == queued
                    ? TelemetryEventNames.LeadIntakeBusinessNotificationQueued
                    : TelemetryEventNames.LeadIntakeBusinessNotificationFailed,
                request,
                $"{queued}_of_{attempted}_queued",
                cancellationToken)
            .ConfigureAwait(false);
        return attempted > 0 && attempted == queued;
    }

    private async Task<bool> TryQueueBusinessNotificationsAsync(
        InboundLeadIntakeRequest request,
        string? phone,
        string? email,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await QueueBusinessNotificationsAsync(request, phone, email, attributes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await LogAsync(
                    TelemetryEventNames.LeadIntakeBusinessNotificationFailed,
                    request,
                    "queue_exception",
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task<InboundLeadIntakeResult> FailAsync(
        InboundLeadIntakeRequest request,
        InboundLeadReceiptClaimResult claim,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await receiptStore.FailAsync(
                    new InboundLeadReceiptFailureRequest(
                        request.TenantId,
                        claim.RowKey,
                        claim.LeaseId!,
                        request.CorrelationId,
                        failureCode,
                        DateTimeOffset.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The CRM remains the source of truth. A stale lease can be reclaimed on the provider retry.
        }

        await LogAsync(TelemetryEventNames.LeadIntakeFailed, request, failureCode, cancellationToken).ConfigureAwait(false);
        return new InboundLeadIntakeResult(false, false, false, FailureCode: failureCode);
    }

    private static string? Validate(InboundLeadIntakeRequest request, out string? phone, out string? email)
    {
        phone = null;
        email = Normalize(request.CustomerEmail);
        if (string.IsNullOrWhiteSpace(request.ExternalEventId) || request.ExternalEventId.Length > 256
            || string.IsNullOrWhiteSpace(request.ExternalContactId) || request.ExternalContactId.Length > 256)
        {
            return "invalid_external_identifier";
        }

        if (!string.IsNullOrWhiteSpace(request.CustomerPhoneNumber)
            && !LeadPhoneNormalizer.TryNormalizeToE164(request.CustomerPhoneNumber, out phone))
        {
            return "invalid_phone";
        }

        if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
        {
            return "invalid_email";
        }

        if (phone is null && email is null)
        {
            return "missing_contact_identifier";
        }

        if (request.CustomerName?.Length > 200 || request.CampaignId?.Length > 128)
        {
            return "invalid_field_length";
        }

        if (request.MarketingConsentGranted
            && (request.ConsentCapturedAt is null || string.IsNullOrWhiteSpace(request.ConsentTextVersion)))
        {
            return "missing_consent_evidence";
        }

        if (request.ConsentCapturedAt > DateTimeOffset.UtcNow.AddMinutes(5)
            || request.ConsentTextVersion?.Length > 128
            || request.Attributes.Count > 25
            || request.Attributes.Any(pair => !IsValidAttribute(pair.Key, pair.Value)))
        {
            return "invalid_attributes";
        }

        return null;
    }

    private static Dictionary<string, string> BuildAttributes(InboundLeadIntakeRequest request, string consent)
    {
        var classification = ClassifyLead(request.Attributes, consent);
        var attributes = new Dictionary<string, string>(request.Attributes, StringComparer.OrdinalIgnoreCase)
        {
            [CrmContactAttributeNames.LeadSource] = request.Source,
            [CrmContactAttributeNames.LeadStatus] = CrmOutboundLeadStatuses.New,
            [CrmContactAttributeNames.ConsentStatus] = consent,
            [CrmContactAttributeNames.ExternalSourceId] = request.ExternalContactId,
            ["leadClassification"] = classification.Classification,
            ["classificationReasons"] = string.Join(",", classification.Reasons),
            ["recommendedRoute"] = classification.RecommendedRoute
        };
        AddIfPresent(attributes, CrmContactAttributeNames.CampaignId, request.CampaignId);
        AddIfPresent(attributes, CrmContactAttributeNames.ConsentCapturedAt, request.ConsentCapturedAt?.ToUniversalTime().ToString("O"));
        AddIfPresent(attributes, CrmContactAttributeNames.ConsentTextVersion, request.ConsentTextVersion);
        return attributes;
    }

    private static LeadClassification ClassifyLead(IReadOnlyDictionary<string, string> attributes, string consent)
    {
        var reasons = new List<string>();
        if (string.Equals(consent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
            || IsAny(attributes, "requestedNextStep", "opted_out", "no_contact")
            || IsAny(attributes, "communicationOptOut", "true", "yes"))
        {
            return new LeadClassification("opted_out", "none", ["consent_not_available"]);
        }

        var funnelType = GetAttribute(attributes, "funnelType");
        if (string.IsNullOrWhiteSpace(funnelType))
        {
            return new LeadClassification("follow_up", "follow_up", ["missing_funnel_type"]);
        }

        if (string.Equals(funnelType, "financial_education", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyFinancialEducationLead(attributes, reasons);
        }

        if (string.Equals(funnelType, "business_opportunity", StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyBusinessOpportunityLead(attributes, reasons);
        }

        return new LeadClassification("follow_up", "follow_up", ["unknown_funnel_type"]);
    }

    private static LeadClassification ClassifyFinancialEducationLead(
        IReadOnlyDictionary<string, string> attributes,
        List<string> reasons)
    {
        if (IsAny(attributes, "requestedNextStep", "consultation"))
        {
            reasons.Add("requested_consultation");
            return new LeadClassification("ready_for_consultation", "consultation", reasons);
        }

        if (IsAny(attributes, "requestedNextStep", "master_class"))
        {
            reasons.Add("requested_master_class");
            return new LeadClassification("education_needed", "master_class", reasons);
        }

        if (IsAny(attributes, "timeline", "this_week", "under_30_days", "30_90_days")
            && HasAnyAttribute(attributes, "primaryGoal", "currentProtection"))
        {
            reasons.Add("near_term_financial_timeline");
            reasons.Add("financial_goal_present");
            return new LeadClassification("ready_for_consultation", "consultation", reasons);
        }

        if (IsAny(attributes, "timeline", "learning_only", "just_learning", "over_90_days")
            || IsAny(attributes, "primaryGoal", "not_sure", "education"))
        {
            reasons.Add("education_or_longer_timeline");
            return new LeadClassification("education_needed", "master_class", reasons);
        }

        reasons.Add("financial_interest_needs_follow_up");
        return new LeadClassification("follow_up", "follow_up", reasons);
    }

    private static LeadClassification ClassifyBusinessOpportunityLead(
        IReadOnlyDictionary<string, string> attributes,
        List<string> reasons)
    {
        if (IsAny(attributes, "incomeExpectation", "guaranteed_income", "money_fast")
            || IsAny(attributes, "willingToLicense", "false", "no"))
        {
            reasons.Add("incompatible_business_expectation");
            return new LeadClassification("not_qualified", "none", reasons);
        }

        if (IsAny(attributes, "requestedNextStep", "consultation", "intro_call")
            && !IsAny(attributes, "weeklyAvailability", "less_than_5", "none"))
        {
            reasons.Add("requested_intro_call");
            reasons.Add("availability_present");
            return new LeadClassification("ready_for_consultation", "consultation", reasons);
        }

        if (IsAny(attributes, "requestedNextStep", "master_class")
            || IsAny(attributes, "experienceLevel", "new_to_industry", "some_interest"))
        {
            reasons.Add("business_education_needed");
            return new LeadClassification("education_needed", "master_class", reasons);
        }

        if (IsAny(attributes, "timeline", "this_week", "under_30_days")
            && !IsAny(attributes, "weeklyAvailability", "less_than_5", "none"))
        {
            reasons.Add("near_term_business_timeline");
            reasons.Add("availability_present");
            return new LeadClassification("ready_for_consultation", "consultation", reasons);
        }

        reasons.Add("business_interest_needs_follow_up");
        return new LeadClassification("follow_up", "follow_up", reasons);
    }

    private static Dictionary<string, string> BuildTemplateTokens(
        InboundLeadIntakeRequest request,
        TenantConfiguration tenant,
        string? phone,
        string? email,
        IReadOnlyDictionary<string, string> attributes) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tenantId"] = request.TenantId,
            ["verticalId"] = request.VerticalId,
            ["businessName"] = tenant.BusinessName,
            ["correlationId"] = request.CorrelationId,
            ["customerName"] = request.CustomerName ?? string.Empty,
            ["customerPhoneNumber"] = phone ?? string.Empty,
            ["customerEmail"] = email ?? string.Empty,
            ["campaignId"] = request.CampaignId ?? string.Empty,
            ["serviceType"] = GetAttribute(attributes, "serviceNeed") ?? string.Empty,
            ["propertyType"] = GetAttribute(attributes, "propertyType") ?? string.Empty,
            ["serviceAddress"] = GetAttribute(attributes, "serviceAddress") ?? string.Empty,
            ["zipCode"] = GetAttribute(attributes, "zipCode") ?? string.Empty,
            ["urgency"] = GetAttribute(attributes, "urgency") ?? string.Empty
        };

    private static bool ShouldNotifyBusinessBySms(
        BusinessSmsNotificationConfiguration configuration,
        IReadOnlyDictionary<string, string> attributes)
    {
        if (string.Equals(configuration.Mode, BusinessSmsNotificationConfiguration.AlwaysMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var condition = configuration.Condition;
        var value = condition is null ? null : GetAttribute(attributes, condition.Attribute);
        return condition is not null
               && value is not null
               && condition.EqualsAny.Any(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
    }

    private static string Render(
        string template,
        IReadOnlyDictionary<string, string> tokens,
        IReadOnlyDictionary<string, string> attributes) =>
        TemplateTokenRegex.Replace(template, match =>
        {
            var token = match.Groups["token"].Value.Trim();
            if (token.StartsWith("attr.", StringComparison.OrdinalIgnoreCase))
            {
                return GetAttribute(attributes, token[5..]) ?? string.Empty;
            }

            return tokens.TryGetValue(token, out var value) ? value : string.Empty;
        });

    private async Task LogAsync(
        string eventName,
        InboundLeadIntakeRequest request,
        string outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await eventLogger.LogEventAsync(
                    eventName,
                    new SafeTelemetryProperties()
                        .Add("tenantId", request.TenantId)
                        .Add("correlationId", request.CorrelationId)
                        .Add("source", request.Source)
                        .Add("outcome", outcome)
                        .ToDictionary(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Telemetry is best-effort and must not change intake behavior.
        }
    }

    private static string ResolveConsent(string previousConsent, bool granted) =>
        string.Equals(previousConsent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
            ? CrmConsentStatuses.OptedOut
            : granted
                ? CrmConsentStatuses.OptIn
                : previousConsent;

    private static bool IsValidEmail(string value)
    {
        try
        {
            return string.Equals(new MailAddress(value).Address, value, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidAttribute(string key, string value) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length <= 64
        && key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
        && value.Length <= 512;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool HasAnyAttribute(IReadOnlyDictionary<string, string> attributes, params string[] names) =>
        names.Any(name => GetAttribute(attributes, name) is not null);

    private static bool IsAny(IReadOnlyDictionary<string, string> attributes, string name, params string[] values)
    {
        var current = GetAttribute(attributes, name);
        return current is not null
            && values.Any(value => string.Equals(current, value, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddIfPresent(IDictionary<string, string> attributes, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[name] = value.Trim();
        }
    }

    private sealed record LeadClassification(
        string Classification,
        string RecommendedRoute,
        IReadOnlyCollection<string> Reasons);
}
