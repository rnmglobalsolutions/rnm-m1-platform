using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Domain.Configuration;

public sealed record TenantConfiguration(
    TenantId TenantId,
    VerticalId VerticalId,
    string BusinessName,
    string TimeZone,
    ServiceAreaConfiguration ServiceArea,
    ProviderConfiguration Providers,
    SecretNameConfiguration SecretNames,
    CommunicationConfiguration Communication,
    ReportingConfiguration? Reporting = null,
    VoiceConfiguration? Voice = null,
    ClassAutomationConfiguration? Classes = null,
    FollowUpAutomationConfiguration? FollowUps = null,
    IntegrationConfiguration? Integrations = null);

public sealed record ProviderConfiguration(
    string CrmProvider,
    string BookingProvider,
    string SmsProvider,
    string EmailProvider);

public sealed record SecretNameConfiguration(
    string CrmApiKey,
    string BookingApiKey,
    string VoiceWebhookSecret,
    string TwilioAccountSid,
    string TwilioAuthToken,
    string EmailConnectionString,
    string? CrmCredentials = null,
    string? BookingCredentials = null,
    string? ManyChatWebhookSecret = null,
    string? ClassRegistrationWebhookSecret = null);

public sealed record IntegrationConfiguration(
    ManyChatIntegrationConfiguration? ManyChat = null);

public sealed record ManyChatIntegrationConfiguration(
    bool? Enabled = null,
    bool? ScheduleFollowUp = null,
    int? MaxRequestsPerMinute = null)
{
    public bool EffectiveEnabled => Enabled ?? false;

    public bool EffectiveScheduleFollowUp => ScheduleFollowUp ?? true;

    public int EffectiveMaxRequestsPerMinute => MaxRequestsPerMinute ?? 120;
}

public sealed record CommunicationConfiguration(
    string SmsFromPhoneNumber,
    string? EmailFromAddress,
    ConfirmationTemplateConfiguration ConfirmationTemplates,
    string? BusinessNotificationEmail = null,
    string? BusinessNotificationPhoneNumber = null,
    bool NotifyBusinessBySmsForUrgentOnly = false,
    BusinessSmsNotificationConfiguration? BusinessSmsNotification = null,
    AppointmentReminderConfiguration? AppointmentReminders = null)
{
    public BusinessSmsNotificationConfiguration EffectiveBusinessSmsNotification =>
        BusinessSmsNotification
        ?? (NotifyBusinessBySmsForUrgentOnly
            ? BusinessSmsNotificationConfiguration.Conditional(
                "urgency",
                ["urgent", "emergency", "asap", "same-day", "today"])
            : BusinessSmsNotificationConfiguration.Always());

    public AppointmentReminderConfiguration EffectiveAppointmentReminders =>
        AppointmentReminders ?? new AppointmentReminderConfiguration();
}

public sealed record BusinessSmsNotificationConfiguration(
    string Mode,
    BusinessSmsNotificationCondition? Condition = null)
{
    public const string AlwaysMode = "always";

    public const string ConditionalMode = "conditional";

    public static BusinessSmsNotificationConfiguration Always() => new(AlwaysMode);

    public static BusinessSmsNotificationConfiguration Conditional(
        string attribute,
        IReadOnlyCollection<string> equalsAny) =>
        new(ConditionalMode, new BusinessSmsNotificationCondition(attribute, equalsAny));
}

public sealed record BusinessSmsNotificationCondition(
    string Attribute,
    IReadOnlyCollection<string> EqualsAny);

public sealed record ConfirmationTemplateConfiguration(
    string SmsBodyTemplate,
    string? EmailSubjectTemplate = null,
    string? EmailBodyTemplate = null,
    string? BusinessSmsBodyTemplate = null,
    string? BusinessEmailSubjectTemplate = null,
    string? BusinessEmailBodyTemplate = null);

public sealed record AppointmentReminderConfiguration(
    ConfirmationTemplateConfiguration? Templates = null,
    IReadOnlyCollection<int>? ReminderOffsetsMinutes = null,
    int? ReminderStalenessCutoffMinutes = null)
{
    public IReadOnlyCollection<int> EffectiveReminderOffsetsMinutes =>
        ReminderOffsetsMinutes is { Count: > 0 } ? ReminderOffsetsMinutes : [1440, 60];

    public int EffectiveReminderStalenessCutoffMinutes =>
        ReminderStalenessCutoffMinutes ?? 60;
}

public sealed record ReportingConfiguration(
    decimal? CloseRate = null,
    decimal? AvgCommissionValue = null,
    ReportingBaselineConfiguration? Baseline = null);

public sealed record ReportingBaselineConfiguration(
    int? LeadsContactedPerWeek = null,
    int? AvgContactTimeSeconds = null,
    int? AppointmentsPerWeek = null)
{
    public bool IsSet =>
        LeadsContactedPerWeek.HasValue
        || AvgContactTimeSeconds.HasValue
        || AppointmentsPerWeek.HasValue;
}

public sealed record VoiceConfiguration(
    OutboundVoiceConfiguration? Outbound = null);

public sealed record OutboundVoiceConfiguration(
    string? VapiApiKeySecretName = null,
    string? VapiBaseUrl = null,
    string? OutboundAssistantId = null,
    string? OutboundPhoneNumberId = null,
    string? CallbackWebhookBaseUrl = null,
    OutboundPacingConfiguration? Pacing = null,
    int? MaxAttemptsPerLead = null,
    TcpaWindowConfiguration? TcpaWindow = null);

public sealed record OutboundPacingConfiguration(
    int? MaxConcurrentCalls = null,
    int? MinSecondsBetweenCalls = null)
{
    public int EffectiveMaxConcurrentCalls => Math.Max(1, MaxConcurrentCalls ?? 1);

    public int EffectiveMinSecondsBetweenCalls => Math.Max(0, MinSecondsBetweenCalls ?? 0);
}

public sealed record TcpaWindowConfiguration(
    int? StartHour = null,
    int? EndHour = null)
{
    public int EffectiveStartHour => StartHour ?? 8;

    public int EffectiveEndHour => EndHour ?? 21;
}

public sealed record ClassAutomationConfiguration(
    ClassNotificationTemplateConfiguration? RegistrationTemplates = null,
    ClassNotificationTemplateConfiguration? ReminderTemplates = null,
    IReadOnlyCollection<int>? ReminderOffsetsMinutes = null,
    IReadOnlyCollection<string>? AllowedRegistrationOrigins = null,
    int? ReminderStalenessCutoffMinutes = null,
    int? MaxRegistrationsPerMinute = null)
{
    public IReadOnlyCollection<int> EffectiveReminderOffsetsMinutes =>
        ReminderOffsetsMinutes is { Count: > 0 } ? ReminderOffsetsMinutes : [1440, 60];

    public int EffectiveReminderStalenessCutoffMinutes =>
        ReminderStalenessCutoffMinutes ?? 60;

    public int EffectiveMaxRegistrationsPerMinute =>
        Math.Clamp(MaxRegistrationsPerMinute ?? 60, 1, 1000);
}

public sealed record ClassNotificationTemplateConfiguration(
    string? SmsBodyTemplate = null,
    string? EmailSubjectTemplate = null,
    string? EmailBodyTemplate = null);

public sealed record FollowUpAutomationConfiguration(
    bool? Enabled = null,
    int? StalenessCutoffMinutes = null,
    int? MaxFollowUpsPerContactPerDay = null,
    IReadOnlyCollection<FollowUpSequenceConfiguration>? Sequences = null)
{
    public bool EffectiveEnabled => Enabled ?? false;

    public int EffectiveStalenessCutoffMinutes => StalenessCutoffMinutes ?? 120;

    public int EffectiveMaxFollowUpsPerContactPerDay => MaxFollowUpsPerContactPerDay ?? 2;

    public IReadOnlyCollection<FollowUpSequenceConfiguration> EffectiveSequences =>
        Sequences is { Count: > 0 } ? Sequences : [];
}

public sealed record FollowUpSequenceConfiguration(
    string Id,
    string Trigger,
    IReadOnlyCollection<FollowUpStepConfiguration>? Steps = null,
    IReadOnlyCollection<FollowUpStopConditionConfiguration>? StopWhen = null)
{
    public IReadOnlyCollection<FollowUpStepConfiguration> EffectiveSteps =>
        Steps is { Count: > 0 } ? Steps : [];

    public IReadOnlyCollection<FollowUpStopConditionConfiguration> EffectiveStopWhen =>
        StopWhen is { Count: > 0 } ? StopWhen : [];
}

public sealed record FollowUpStepConfiguration(
    int DelayMinutes,
    string Channel,
    string? SmsBodyTemplate = null,
    string? EmailSubjectTemplate = null,
    string? EmailBodyTemplate = null,
    string? RequiresConsent = null);

public sealed record FollowUpStopConditionConfiguration(
    string? Attribute = null,
    IReadOnlyCollection<string>? EqualsAny = null,
    string? ConsentStatus = null);
