using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Infrastructure.Configuration;

public sealed class JsonTenantConfigurationProvider : ITenantConfigurationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string configRoot;
    private readonly IConfigurationValidator configurationValidator;
    private readonly bool allowWildcardServiceArea;

    public JsonTenantConfigurationProvider(
        string configRoot,
        IConfigurationValidator configurationValidator,
        bool allowWildcardServiceArea = true)
    {
        this.configRoot = string.IsNullOrWhiteSpace(configRoot)
            ? throw new ArgumentException("Config root is required.", nameof(configRoot))
            : configRoot;
        this.configurationValidator = configurationValidator;
        this.allowWildcardServiceArea = allowWildcardServiceArea;
    }

    public async Task<TenantConfiguration> GetTenantConfigurationAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ConfigurationException("Tenant id is required.");
        }

        var path = Path.Combine(configRoot, "tenants", $"{tenantId}.json");
        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Tenant configuration '{tenantId}' was not found.");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<TenantConfigurationDto>(json, JsonOptions)
            ?? throw new ConfigurationException($"Tenant configuration '{tenantId}' is empty or invalid JSON.");

        var configuration = dto.ToDomain();
        if (!string.Equals(configuration.TenantId.Value, tenantId, StringComparison.Ordinal))
        {
            throw new ConfigurationException(
                $"Tenant configuration '{tenantId}' has a mismatched tenantId.");
        }

        var validation = configurationValidator.ValidateTenant(configuration);
        if (!validation.IsValid)
        {
            throw new ConfigurationException(
                $"Tenant configuration '{tenantId}' is invalid: {string.Join(" ", validation.Errors)}");
        }

        if (!allowWildcardServiceArea
            && configuration.ServiceArea.ZipCodes.Any(
                zipCode => string.Equals(zipCode?.Trim(), "*", StringComparison.Ordinal)))
        {
            throw new ConfigurationException(
                $"Tenant configuration '{tenantId}' cannot use wildcard service area ZIP codes in production.");
        }

        return configuration;
    }

    private sealed record TenantConfigurationDto(
        string? TenantId,
        string? VerticalId,
        string? BusinessName,
        string? TimeZone,
        ServiceAreaConfigurationDto? ServiceArea,
        ProviderConfigurationDto? Providers,
        SecretNameConfigurationDto? SecretNames,
        CommunicationConfigurationDto? Communication,
        ReportingConfigurationDto? Reporting,
        VoiceConfigurationDto? Voice,
        ClassAutomationConfigurationDto? Classes,
        FollowUpAutomationConfigurationDto? FollowUps,
        IntegrationConfigurationDto? Integrations)
    {
        public TenantConfiguration ToDomain()
        {
            return new TenantConfiguration(
                new TenantId(TenantId ?? string.Empty),
                new VerticalId(VerticalId ?? string.Empty),
                BusinessName ?? string.Empty,
                TimeZone ?? string.Empty,
                new ServiceAreaConfiguration(
                    ServiceArea?.ZipCodes ?? [],
                    ServiceArea?.Cities ?? [],
                    ServiceArea?.ReferralMessage),
                new ProviderConfiguration(
                    Providers?.CrmProvider ?? string.Empty,
                    Providers?.BookingProvider ?? string.Empty,
                    Providers?.SmsProvider ?? string.Empty,
                    Providers?.EmailProvider ?? string.Empty),
                new SecretNameConfiguration(
                    SecretNames?.CrmApiKey ?? string.Empty,
                    SecretNames?.BookingApiKey ?? string.Empty,
                    SecretNames?.VoiceWebhookSecret ?? SecretNames?.VapiWebhookSecret ?? string.Empty,
                    SecretNames?.TwilioAccountSid ?? string.Empty,
                    SecretNames?.TwilioAuthToken ?? string.Empty,
                    SecretNames?.EmailConnectionString ?? string.Empty,
                    SecretNames?.CrmCredentials,
                    SecretNames?.BookingCredentials,
                    SecretNames?.ManyChatWebhookSecret,
                    SecretNames?.ClassRegistrationWebhookSecret),
                new CommunicationConfiguration(
                    Communication?.SmsFromPhoneNumber ?? string.Empty,
                    Communication?.EmailFromAddress,
                    new ConfirmationTemplateConfiguration(
                        Communication?.ConfirmationTemplates?.SmsBodyTemplate ?? string.Empty,
                        Communication?.ConfirmationTemplates?.EmailSubjectTemplate,
                        Communication?.ConfirmationTemplates?.EmailBodyTemplate,
                        Communication?.ConfirmationTemplates?.BusinessSmsBodyTemplate,
                        Communication?.ConfirmationTemplates?.BusinessEmailSubjectTemplate,
                        Communication?.ConfirmationTemplates?.BusinessEmailBodyTemplate),
                    Communication?.BusinessNotificationEmail,
                    Communication?.BusinessNotificationPhoneNumber,
                    Communication?.NotifyBusinessBySmsForUrgentOnly ?? false,
                    CreateBusinessSmsNotificationConfiguration(Communication),
                    Communication?.AppointmentReminders is null
                        ? null
                        : new AppointmentReminderConfiguration(
                            Communication.AppointmentReminders.Templates is null
                                ? null
                                : new ConfirmationTemplateConfiguration(
                                    Communication.AppointmentReminders.Templates.SmsBodyTemplate ?? string.Empty,
                                    Communication.AppointmentReminders.Templates.EmailSubjectTemplate,
                                    Communication.AppointmentReminders.Templates.EmailBodyTemplate,
                                    Communication.AppointmentReminders.Templates.BusinessSmsBodyTemplate,
                                    Communication.AppointmentReminders.Templates.BusinessEmailSubjectTemplate,
                                    Communication.AppointmentReminders.Templates.BusinessEmailBodyTemplate),
                            Communication.AppointmentReminders.ReminderOffsetsMinutes,
                            Communication.AppointmentReminders.ReminderStalenessCutoffMinutes)),
                new ReportingConfiguration(
                    Reporting?.CloseRate,
                    Reporting?.AvgCommissionValue,
                    Reporting?.Baseline is null
                        ? null
                        : new ReportingBaselineConfiguration(
                            Reporting.Baseline.LeadsContactedPerWeek,
                            Reporting.Baseline.AvgContactTimeSeconds,
                            Reporting.Baseline.AppointmentsPerWeek)),
                Voice is null
                    ? null
                    : new VoiceConfiguration(
                        Voice.Outbound is null
                            ? null
                            : new OutboundVoiceConfiguration(
                                Voice.Outbound.VapiApiKeySecretName,
                                Voice.Outbound.VapiBaseUrl,
                                Voice.Outbound.OutboundAssistantId,
                                Voice.Outbound.OutboundPhoneNumberId,
                                Voice.Outbound.CallbackWebhookBaseUrl,
                                Voice.Outbound.Pacing is null
                                    ? null
                                    : new OutboundPacingConfiguration(
                                        Voice.Outbound.Pacing.MaxConcurrentCalls,
                                        Voice.Outbound.Pacing.MinSecondsBetweenCalls),
                                Voice.Outbound.MaxAttemptsPerLead,
                                Voice.Outbound.TcpaWindow is null
                                    ? null
                                    : new TcpaWindowConfiguration(
                                        Voice.Outbound.TcpaWindow.StartHour,
                                        Voice.Outbound.TcpaWindow.EndHour))),
                Classes is null
                    ? null
                    : new ClassAutomationConfiguration(
                        Classes.RegistrationTemplates is null
                            ? null
                            : new ClassNotificationTemplateConfiguration(
                                Classes.RegistrationTemplates.SmsBodyTemplate,
                                Classes.RegistrationTemplates.EmailSubjectTemplate,
                                Classes.RegistrationTemplates.EmailBodyTemplate),
                        Classes.ReminderTemplates is null
                            ? null
                            : new ClassNotificationTemplateConfiguration(
                                Classes.ReminderTemplates.SmsBodyTemplate,
                                Classes.ReminderTemplates.EmailSubjectTemplate,
                                Classes.ReminderTemplates.EmailBodyTemplate),
                        Classes.ReminderOffsetsMinutes,
                        Classes.AllowedRegistrationOrigins,
                        Classes.ReminderStalenessCutoffMinutes,
                        Classes.MaxRegistrationsPerMinute),
                FollowUps is null
                    ? null
                    : new FollowUpAutomationConfiguration(
                        FollowUps.Enabled,
                        FollowUps.StalenessCutoffMinutes,
                        FollowUps.MaxFollowUpsPerContactPerDay,
                        FollowUps.Sequences?.Select(sequence =>
                                new FollowUpSequenceConfiguration(
                                    sequence.Id ?? string.Empty,
                                    sequence.Trigger ?? string.Empty,
                                    sequence.Steps?.Select(step =>
                                            new FollowUpStepConfiguration(
                                                step.DelayMinutes ?? 0,
                                                step.Channel ?? string.Empty,
                                                step.SmsBodyTemplate,
                                                step.EmailSubjectTemplate,
                                                step.EmailBodyTemplate,
                                                step.RequiresConsent))
                                        .ToArray(),
                                    sequence.StopWhen?.Select(condition =>
                                            new FollowUpStopConditionConfiguration(
                                                condition.Attribute,
                                                condition.EqualsAny,
                                                condition.ConsentStatus))
                                        .ToArray()))
                            .ToArray()),
                Integrations is null
                    ? null
                    : new IntegrationConfiguration(
                        Integrations.ManyChat is null
                            ? null
                            : new ManyChatIntegrationConfiguration(
                                Integrations.ManyChat.Enabled,
                                Integrations.ManyChat.ScheduleFollowUp,
                                Integrations.ManyChat.MaxRequestsPerMinute)));
        }
    }

    private sealed record ServiceAreaConfigurationDto(
        IReadOnlyCollection<string> ZipCodes,
        IReadOnlyCollection<string> Cities,
        string? ReferralMessage);

    private sealed record ProviderConfigurationDto(
        string? CrmProvider,
        string? BookingProvider,
        string? SmsProvider,
        string? EmailProvider);

    private sealed record SecretNameConfigurationDto(
        string? CrmApiKey,
        string? BookingApiKey,
        string? VoiceWebhookSecret,
        string? VapiWebhookSecret,
        string? TwilioAccountSid,
        string? TwilioAuthToken,
        string? EmailConnectionString,
        string? CrmCredentials,
        string? BookingCredentials,
        string? ManyChatWebhookSecret,
        string? ClassRegistrationWebhookSecret);

    private sealed record IntegrationConfigurationDto(
        ManyChatIntegrationConfigurationDto? ManyChat);

    private sealed record ManyChatIntegrationConfigurationDto(
        bool? Enabled,
        bool? ScheduleFollowUp,
        int? MaxRequestsPerMinute);

    private sealed record CommunicationConfigurationDto(
        string? SmsFromPhoneNumber,
        string? EmailFromAddress,
        ConfirmationTemplateConfigurationDto? ConfirmationTemplates,
        string? BusinessNotificationEmail,
        string? BusinessNotificationPhoneNumber,
        bool? NotifyBusinessBySmsForUrgentOnly,
        BusinessSmsNotificationConfigurationDto? BusinessSmsNotification,
        AppointmentReminderConfigurationDto? AppointmentReminders);

    private static BusinessSmsNotificationConfiguration CreateBusinessSmsNotificationConfiguration(
        CommunicationConfigurationDto? communication)
    {
        if (communication?.BusinessSmsNotification is not null)
        {
            return new BusinessSmsNotificationConfiguration(
                communication.BusinessSmsNotification.Mode ?? BusinessSmsNotificationConfiguration.AlwaysMode,
                communication.BusinessSmsNotification.Condition is null
                    ? null
                    : new BusinessSmsNotificationCondition(
                        communication.BusinessSmsNotification.Condition.Attribute ?? string.Empty,
                        communication.BusinessSmsNotification.Condition.EqualsAny ?? []));
        }

        if (communication?.NotifyBusinessBySmsForUrgentOnly is true)
        {
            return BusinessSmsNotificationConfiguration.Conditional(
                "urgency",
                ["urgent", "emergency", "asap", "same-day", "today"]);
        }

        return BusinessSmsNotificationConfiguration.Always();
    }

    private sealed record BusinessSmsNotificationConfigurationDto(
        string? Mode,
        BusinessSmsNotificationConditionDto? Condition);

    private sealed record BusinessSmsNotificationConditionDto(
        string? Attribute,
        IReadOnlyCollection<string>? EqualsAny);

    private sealed record AppointmentReminderConfigurationDto(
        ConfirmationTemplateConfigurationDto? Templates,
        IReadOnlyCollection<int>? ReminderOffsetsMinutes,
        int? ReminderStalenessCutoffMinutes);

    private sealed record ConfirmationTemplateConfigurationDto(
        string? SmsBodyTemplate,
        string? EmailSubjectTemplate,
        string? EmailBodyTemplate,
        string? BusinessSmsBodyTemplate,
        string? BusinessEmailSubjectTemplate,
        string? BusinessEmailBodyTemplate);

    private sealed record ReportingConfigurationDto(
        decimal? CloseRate,
        decimal? AvgCommissionValue,
        ReportingBaselineConfigurationDto? Baseline);

    private sealed record ReportingBaselineConfigurationDto(
        int? LeadsContactedPerWeek,
        int? AvgContactTimeSeconds,
        int? AppointmentsPerWeek);

    private sealed record VoiceConfigurationDto(
        OutboundVoiceConfigurationDto? Outbound);

    private sealed record OutboundVoiceConfigurationDto(
        string? VapiApiKeySecretName,
        string? VapiBaseUrl,
        string? OutboundAssistantId,
        string? OutboundPhoneNumberId,
        string? CallbackWebhookBaseUrl,
        OutboundPacingConfigurationDto? Pacing,
        int? MaxAttemptsPerLead,
        TcpaWindowConfigurationDto? TcpaWindow);

    private sealed record OutboundPacingConfigurationDto(
        int? MaxConcurrentCalls,
        int? MinSecondsBetweenCalls);

    private sealed record TcpaWindowConfigurationDto(
        int? StartHour,
        int? EndHour);

    private sealed record ClassAutomationConfigurationDto(
        ClassNotificationTemplateConfigurationDto? RegistrationTemplates,
        ClassNotificationTemplateConfigurationDto? ReminderTemplates,
        IReadOnlyCollection<int>? ReminderOffsetsMinutes,
        IReadOnlyCollection<string>? AllowedRegistrationOrigins,
        int? ReminderStalenessCutoffMinutes,
        int? MaxRegistrationsPerMinute);

    private sealed record ClassNotificationTemplateConfigurationDto(
        string? SmsBodyTemplate,
        string? EmailSubjectTemplate,
        string? EmailBodyTemplate);

    private sealed record FollowUpAutomationConfigurationDto(
        bool? Enabled,
        int? StalenessCutoffMinutes,
        int? MaxFollowUpsPerContactPerDay,
        IReadOnlyCollection<FollowUpSequenceConfigurationDto>? Sequences);

    private sealed record FollowUpSequenceConfigurationDto(
        string? Id,
        string? Trigger,
        IReadOnlyCollection<FollowUpStepConfigurationDto>? Steps,
        IReadOnlyCollection<FollowUpStopConditionConfigurationDto>? StopWhen);

    private sealed record FollowUpStepConfigurationDto(
        int? DelayMinutes,
        string? Channel,
        string? SmsBodyTemplate,
        string? EmailSubjectTemplate,
        string? EmailBodyTemplate,
        string? RequiresConsent);

    private sealed record FollowUpStopConditionConfigurationDto(
        string? Attribute,
        IReadOnlyCollection<string>? EqualsAny,
        string? ConsentStatus);
}
