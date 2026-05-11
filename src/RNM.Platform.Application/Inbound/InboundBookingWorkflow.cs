using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.Inbound;

public interface IInboundBookingWorkflow
{
    Task<InboundBookingWorkflowResult> ProcessAsync(
        InboundCallEvent inboundCallEvent,
        CancellationToken cancellationToken);

    Task<InboundBookingWorkflowResult> ProcessAsync(
        InboundBookingWorkflowRequest request,
        CancellationToken cancellationToken);
}

public sealed class InboundBookingWorkflow : IInboundBookingWorkflow
{
    private readonly ITenantConfigurationProvider tenantConfigurationProvider;
    private readonly IVerticalConfigurationProvider verticalConfigurationProvider;
    private readonly QualificationService qualificationService;
    private readonly BookingApplicationService bookingApplicationService;
    private readonly CrmApplicationService crmApplicationService;
    private readonly ConfirmationApplicationService confirmationApplicationService;
    private readonly IEventLogger eventLogger;

    public InboundBookingWorkflow(
        ITenantConfigurationProvider tenantConfigurationProvider,
        IVerticalConfigurationProvider verticalConfigurationProvider,
        QualificationService qualificationService,
        BookingApplicationService bookingApplicationService,
        CrmApplicationService crmApplicationService,
        ConfirmationApplicationService confirmationApplicationService,
        IEventLogger eventLogger)
    {
        this.tenantConfigurationProvider = tenantConfigurationProvider;
        this.verticalConfigurationProvider = verticalConfigurationProvider;
        this.qualificationService = qualificationService;
        this.bookingApplicationService = bookingApplicationService;
        this.crmApplicationService = crmApplicationService;
        this.confirmationApplicationService = confirmationApplicationService;
        this.eventLogger = eventLogger;
    }

    public Task<InboundBookingWorkflowResult> ProcessAsync(
        InboundCallEvent inboundCallEvent,
        CancellationToken cancellationToken)
    {
        return ProcessAsync(new InboundBookingWorkflowRequest(inboundCallEvent), cancellationToken);
    }

    public async Task<InboundBookingWorkflowResult> ProcessAsync(
        InboundBookingWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var inboundCallEvent = request.InboundCallEvent;
        var tenantId = inboundCallEvent.TenantId;
        var verticalId = inboundCallEvent.VerticalId;
        var correlationId = inboundCallEvent.CorrelationId;
        QualificationResultState? latestQualificationState = null;
        ServiceAreaDecisionState? latestServiceAreaState = null;
        BookingDecisionState? latestBookingState = null;
        CrmSyncState? latestCrmState = null;
        ConfirmationWorkflowState? latestConfirmationState = null;

        await LogAsync(
                TelemetryEventNames.WorkflowStarted,
                correlationId,
                tenantId,
                verticalId,
                "started",
                null,
                null,
                null,
                null,
                null,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var tenantConfiguration = await tenantConfigurationProvider
                .GetTenantConfigurationAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            var verticalConfiguration = await verticalConfigurationProvider
                .GetVerticalConfigurationAsync(verticalId, cancellationToken)
                .ConfigureAwait(false);

            var qualificationResult = await qualificationService
                .QualifyAsync(CreateQualificationRequest(inboundCallEvent, tenantConfiguration, verticalConfiguration), cancellationToken)
                .ConfigureAwait(false);
            latestQualificationState = qualificationResult.State;
            latestServiceAreaState = qualificationResult.ServiceAreaDecision.State;

            await LogAsync(
                    TelemetryEventNames.WorkflowQualificationCompleted,
                    correlationId,
                    tenantId,
                    verticalId,
                    "completed",
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State,
                    null,
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!qualificationResult.IsQualified)
            {
                var stopped = InboundBookingWorkflowResult.Stopped(
                    InboundBookingWorkflowOutcome.QualificationStopped,
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State);
                await LogCompletedAsync(correlationId, tenantId, verticalId, stopped, cancellationToken).ConfigureAwait(false);
                return stopped;
            }

            var serviceType = request.ServiceType ?? GetFieldValue(qualificationResult, "serviceNeed");
            var preferredWindow = request.PreferredWindow ?? GetFieldValue(qualificationResult, "preferredTime");

            var contactResult = await crmApplicationService
                .EnsureContactAsync(
                    new CrmContactEnsureRequest(
                        tenantId,
                        verticalId,
                        correlationId,
                        qualificationResult),
                    cancellationToken)
                .ConfigureAwait(false);
            latestCrmState = contactResult.State;

            await LogAsync(
                    TelemetryEventNames.WorkflowCrmCompleted,
                    correlationId,
                    tenantId,
                    verticalId,
                    "contact_ensured",
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State,
                    null,
                    contactResult.State,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!contactResult.Succeeded || string.IsNullOrWhiteSpace(contactResult.ProviderContactId))
            {
                var stopped = new InboundBookingWorkflowResult(
                    InboundBookingWorkflowOutcome.CrmStopped,
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State,
                    null,
                    contactResult.State,
                    null);
                await LogCompletedAsync(correlationId, tenantId, verticalId, stopped, cancellationToken).ConfigureAwait(false);
                return stopped;
            }

            var bookingResult = await bookingApplicationService
                .ProcessAsync(
                    new BookingRequest(
                        tenantId,
                        verticalId,
                        correlationId,
                        tenantConfiguration.TimeZone,
                        qualificationResult,
                        serviceType,
                        preferredWindow,
                        request.SelectedSlot,
                        request.AutoSelectFirstAvailableSlot,
                        contactResult.ProviderContactId),
                    cancellationToken)
                .ConfigureAwait(false);
            latestBookingState = bookingResult.State;

            await LogAsync(
                    TelemetryEventNames.WorkflowBookingCompleted,
                    correlationId,
                    tenantId,
                    verticalId,
                    "completed",
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State,
                    bookingResult.State,
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!bookingResult.IsBooked)
            {
                var stopped = new InboundBookingWorkflowResult(
                    InboundBookingWorkflowOutcome.BookingStopped,
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State,
                    bookingResult.State,
                    latestCrmState,
                    ConfirmationState: null);
                await LogCompletedAsync(correlationId, tenantId, verticalId, stopped, cancellationToken).ConfigureAwait(false);
                return stopped;
            }

            var crmResult = await crmApplicationService
                .CompleteBookedLeadSyncAsync(
                    new CrmPostBookingSyncRequest(
                        tenantId,
                        verticalId,
                        correlationId,
                        qualificationResult,
                        bookingResult,
                        contactResult.ProviderContactId,
                        serviceType),
                    cancellationToken)
                .ConfigureAwait(false);
            latestCrmState = crmResult.State;

            await LogAsync(
                    TelemetryEventNames.WorkflowCrmCompleted,
                    correlationId,
                    tenantId,
                    verticalId,
                    "completed",
                    qualificationResult.State,
                    qualificationResult.ServiceAreaDecision.State,
                    bookingResult.State,
                    crmResult.State,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            var confirmationResult = await confirmationApplicationService
                .SendBookingConfirmationAsync(
                    new BookingConfirmationRequest(
                        tenantId,
                        verticalId,
                        correlationId,
                        bookingResult,
                        crmResult,
                        qualificationResult.LeadData.CallerPhoneNumber,
                        GetFieldValue(qualificationResult, "email"),
                        serviceType,
                        ConfirmationTemplateSet.FromConfiguration(tenantConfiguration.Communication.ConfirmationTemplates)),
                    cancellationToken)
                .ConfigureAwait(false);
            var confirmationState = GetConfirmationState(confirmationResult);
            latestConfirmationState = confirmationState;

            var completed = new InboundBookingWorkflowResult(
                InboundBookingWorkflowOutcome.Completed,
                qualificationResult.State,
                qualificationResult.ServiceAreaDecision.State,
                bookingResult.State,
                crmResult.State,
                confirmationState);

            await LogAsync(
                    TelemetryEventNames.WorkflowConfirmationCompleted,
                    correlationId,
                    tenantId,
                    verticalId,
                    "completed",
                    completed.QualificationState,
                    completed.ServiceAreaState,
                    completed.BookingState,
                    completed.CrmState,
                    completed.ConfirmationState,
                    cancellationToken)
                .ConfigureAwait(false);
            await LogCompletedAsync(correlationId, tenantId, verticalId, completed, cancellationToken).ConfigureAwait(false);
            return completed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = new InboundBookingWorkflowResult(
                InboundBookingWorkflowOutcome.Failed,
                latestQualificationState,
                latestServiceAreaState,
                latestBookingState,
                latestCrmState,
                latestConfirmationState);

            await LogAsync(
                    TelemetryEventNames.WorkflowFailed,
                    correlationId,
                    tenantId,
                    verticalId,
                    "failed",
                    failed.QualificationState,
                    failed.ServiceAreaState,
                    failed.BookingState,
                    failed.CrmState,
                    failed.ConfirmationState,
                    cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }
    }

    private static QualificationRequest CreateQualificationRequest(
        InboundCallEvent inboundCallEvent,
        TenantConfiguration tenantConfiguration,
        VerticalConfiguration verticalConfiguration)
    {
        return new QualificationRequest(
            inboundCallEvent.TenantId,
            inboundCallEvent.VerticalId,
            inboundCallEvent.CorrelationId,
            inboundCallEvent,
            verticalConfiguration.QualificationFields,
            tenantConfiguration.ServiceArea.ZipCodes,
            StructuredFields: null,
            new ServiceAreaFieldAliases(
                verticalConfiguration.ServiceAreaFieldAliases.ZipCodeFields,
                verticalConfiguration.ServiceAreaFieldAliases.AddressFields));
    }

    private static string? GetFieldValue(
        QualificationResult qualificationResult,
        string fieldName)
    {
        return qualificationResult.LeadData.Fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static ConfirmationWorkflowState GetConfirmationState(
        BookingConfirmationResult confirmationResult)
    {
        if (confirmationResult.Sms.Status is ConfirmationChannelStatus.Sent
            && confirmationResult.Email.Status is ConfirmationChannelStatus.Sent or ConfirmationChannelStatus.Skipped)
        {
            return ConfirmationWorkflowState.Completed;
        }

        if (confirmationResult.Sms.Status is ConfirmationChannelStatus.Sent)
        {
            return ConfirmationWorkflowState.PartialFailure;
        }

        return ConfirmationWorkflowState.Failed;
    }

    private Task LogCompletedAsync(
        string correlationId,
        string tenantId,
        string verticalId,
        InboundBookingWorkflowResult result,
        CancellationToken cancellationToken)
    {
        return LogAsync(
            TelemetryEventNames.WorkflowCompleted,
            correlationId,
            tenantId,
            verticalId,
            result.Outcome.ToString(),
            result.QualificationState,
            result.ServiceAreaState,
            result.BookingState,
            result.CrmState,
            result.ConfirmationState,
            cancellationToken);
    }

    private async Task LogAsync(
        string eventName,
        string correlationId,
        string tenantId,
        string verticalId,
        string outcome,
        QualificationResultState? qualificationState,
        ServiceAreaDecisionState? serviceAreaState,
        BookingDecisionState? bookingState,
        CrmSyncState? crmState,
        ConfirmationWorkflowState? confirmationState,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", correlationId)
            .Add("tenantId", tenantId)
            .Add("verticalId", verticalId)
            .Add("outcome", outcome)
            .AddIf(qualificationState is not null, "qualificationState", qualificationState?.ToString())
            .AddIf(serviceAreaState is not null, "serviceAreaState", serviceAreaState?.ToString())
            .AddIf(bookingState is not null, "bookingState", bookingState?.ToString())
            .AddIf(crmState is not null, "crmState", crmState?.ToString())
            .AddIf(confirmationState is not null, "confirmationState", confirmationState?.ToString())
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Workflow telemetry is best-effort.
        }
    }
}

public sealed record InboundBookingWorkflowRequest(
    InboundCallEvent InboundCallEvent,
    string? ServiceType = null,
    string? PreferredWindow = null,
    AvailableSlot? SelectedSlot = null,
    bool AutoSelectFirstAvailableSlot = true);

public sealed record InboundBookingWorkflowResult(
    InboundBookingWorkflowOutcome Outcome,
    QualificationResultState? QualificationState,
    ServiceAreaDecisionState? ServiceAreaState,
    BookingDecisionState? BookingState,
    CrmSyncState? CrmState,
    ConfirmationWorkflowState? ConfirmationState)
{
    public bool WorkflowCompleted => Outcome is not InboundBookingWorkflowOutcome.Failed;

    public bool BookingSucceeded => BookingState is BookingDecisionState.Booked;

    public bool CrmSucceeded => CrmState is CrmSyncState.Succeeded;

    public bool ConfirmationSucceeded => ConfirmationState is ConfirmationWorkflowState.Completed;

    public static InboundBookingWorkflowResult Stopped(
        InboundBookingWorkflowOutcome outcome,
        QualificationResultState qualificationState,
        ServiceAreaDecisionState serviceAreaState,
        BookingDecisionState? bookingState = null) =>
        new(
            outcome,
            qualificationState,
            serviceAreaState,
            bookingState,
            CrmState: null,
            ConfirmationState: null);
}

public enum InboundBookingWorkflowOutcome
{
    Completed = 0,
    QualificationStopped = 1,
    BookingStopped = 2,
    Failed = 3,
    CrmStopped = 4
}

public enum ConfirmationWorkflowState
{
    Completed = 0,
    PartialFailure = 1,
    Failed = 2
}
