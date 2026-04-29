namespace RNM.Platform.Application.Observability;

public static class TelemetryEventNames
{
    public const string WebhookReceived = "webhook.received";
    public const string WebhookValidationSucceeded = "webhook.validation_succeeded";
    public const string WebhookValidationFailed = "webhook.validation_failed";
    public const string TenantResolved = "tenant.resolved";
    public const string TenantResolutionFailed = "tenant.resolution_failed";
    public const string SecurityAuthFailed = "security.auth_failed";
    public const string ApiRequestCompleted = "api.request_completed";
    public const string ApiRequestFailed = "api.request_failed";
    public const string VoiceEventProcessed = "voice.event_processed";
    public const string VoiceEventUnsupported = "voice.event_unsupported";
    public const string QualificationCompleted = "qualification.completed";
    public const string ServiceAreaValidated = "service_area.validated";
    public const string QualificationMissingFields = "qualification.missing_fields";
    public const string QualificationOutOfServiceArea = "qualification.out_of_service_area";
    public const string QualificationInvalidInput = "qualification.invalid_input";
    public const string BookingAvailabilityRequested = "booking.availability_requested";
    public const string BookingAvailabilityFound = "booking.availability_found";
    public const string BookingNoAvailability = "booking.no_availability";
    public const string BookingCreateRequested = "booking.create_requested";
    public const string BookingCreated = "booking.created";
    public const string BookingRefused = "booking.refused";
    public const string BookingFailed = "booking.failed";
    public const string CrmUpsertRequested = "crm.upsert_requested";
    public const string CrmContactCreated = "crm.contact_created";
    public const string CrmContactUpdated = "crm.contact_updated";
    public const string CrmNoteAdded = "crm.note_added";
    public const string CrmTagsApplied = "crm.tags_applied";
    public const string CrmBookingLinked = "crm.booking_linked";
    public const string CrmSkipped = "crm.skipped";
    public const string CrmFailed = "crm.failed";
    public const string ConfirmationRequested = "confirmation.requested";
    public const string SmsConfirmationSent = "sms.confirmation.sent";
    public const string SmsConfirmationFailed = "sms.confirmation.failed";
    public const string EmailConfirmationSent = "email.confirmation.sent";
    public const string EmailConfirmationFailed = "email.confirmation.failed";
    public const string EmailConfirmationSkipped = "email.confirmation.skipped";
    public const string WorkflowStarted = "workflow.started";
    public const string WorkflowQualificationCompleted = "workflow.qualification_completed";
    public const string WorkflowBookingCompleted = "workflow.booking_completed";
    public const string WorkflowCrmCompleted = "workflow.crm_completed";
    public const string WorkflowConfirmationCompleted = "workflow.confirmation_completed";
    public const string WorkflowCompleted = "workflow.completed";
    // Runtime/system failure only; expected business stops use workflow.completed with stopped outcomes.
    public const string WorkflowFailed = "workflow.failed";
}
