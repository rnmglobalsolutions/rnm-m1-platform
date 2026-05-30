using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Booking;
using RNM.Platform.Application.Qualification;

namespace RNM.Platform.Application.Booking;

public sealed class BookingApplicationService
{
    private readonly IBookingAdapter bookingAdapter;
    private readonly IEventLogger eventLogger;

    public BookingApplicationService(
        IBookingAdapter bookingAdapter,
        IEventLogger eventLogger)
    {
        this.bookingAdapter = bookingAdapter;
        this.eventLogger = eventLogger;
    }

    public async Task<BookingDecisionResult> ProcessAsync(
        BookingRequest request,
        CancellationToken cancellationToken)
    {
        var refusalReason = GetRefusalReason(request.QualificationResult.State);
        if (refusalReason is not null)
        {
            var refused = new BookingDecisionResult(
                BookingDecisionState.Refused,
                refusalReason,
                [],
                SelectedSlot: null,
                ProviderBookingId: null);

            await LogAsync(TelemetryEventNames.BookingRefused, request, refused, cancellationToken)
                .ConfigureAwait(false);
            return refused;
        }

        await LogAsync(TelemetryEventNames.BookingAvailabilityRequested, request, null, cancellationToken)
            .ConfigureAwait(false);

        BookingAvailabilityResult availabilityResult;
        try
        {
            availabilityResult = await bookingAdapter
                .CheckAvailabilityAsync(CreateAvailabilityRequest(request), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            var failed = Failed(BookingFailureReason.AdapterFailure);
            await LogAsync(TelemetryEventNames.BookingFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        if (!availabilityResult.Succeeded)
        {
            var failed = Failed(availabilityResult.FailureReason ?? BookingFailureReason.AdapterFailure);
            await LogAsync(TelemetryEventNames.BookingFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        if (!availabilityResult.HasAvailability || availabilityResult.Slots.Count == 0)
        {
            var noAvailability = new BookingDecisionResult(
                BookingDecisionState.NoAvailability,
                BookingFailureReason.NoAvailability,
                availabilityResult.Slots,
                SelectedSlot: null,
                ProviderBookingId: null);

            await LogAsync(TelemetryEventNames.BookingNoAvailability, request, noAvailability, cancellationToken)
                .ConfigureAwait(false);
            return noAvailability;
        }

        var selectedSlot = request.SelectedSlot
            ?? (request.AutoSelectFirstAvailableAfterConfirmation ? availabilityResult.Slots.First() : null);
        if (selectedSlot is null)
        {
            var availabilityFound = new BookingDecisionResult(
                BookingDecisionState.AvailabilityFound,
                FailureReason: null,
                availabilityResult.Slots,
                SelectedSlot: null,
                ProviderBookingId: null);

            await LogAsync(TelemetryEventNames.BookingAvailabilityFound, request, availabilityFound, cancellationToken)
                .ConfigureAwait(false);
            return availabilityFound;
        }

        var availableSelectedSlot = FindAvailableSlot(selectedSlot, availabilityResult.Slots);
        if (availableSelectedSlot is null)
        {
            var failed = new BookingDecisionResult(
                BookingDecisionState.Failed,
                BookingFailureReason.SlotUnavailable,
                availabilityResult.Slots,
                SelectedSlot: null,
                ProviderBookingId: null);

            await LogAsync(TelemetryEventNames.BookingFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        await LogAsync(TelemetryEventNames.BookingAvailabilityFound, request, null, cancellationToken)
            .ConfigureAwait(false);
        await LogAsync(TelemetryEventNames.BookingCreateRequested, request, null, cancellationToken)
            .ConfigureAwait(false);

        CreateBookingResult bookingResult;
        try
        {
            bookingResult = await bookingAdapter
                .CreateBookingAsync(CreateBookingRequest(request, availableSelectedSlot), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            var failed = Failed(BookingFailureReason.AdapterFailure);
            await LogAsync(TelemetryEventNames.BookingFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        if (!bookingResult.Succeeded)
        {
            var failed = Failed(bookingResult.FailureReason ?? BookingFailureReason.AdapterFailure);
            await LogAsync(TelemetryEventNames.BookingFailed, request, failed, cancellationToken)
                .ConfigureAwait(false);
            return failed;
        }

        var booked = new BookingDecisionResult(
            BookingDecisionState.Booked,
            FailureReason: null,
            availabilityResult.Slots,
            availableSelectedSlot,
            bookingResult.ProviderBookingId);

        await LogAsync(TelemetryEventNames.BookingCreated, request, booked, cancellationToken)
            .ConfigureAwait(false);
        return booked;

        static BookingDecisionResult Failed(BookingFailureReason reason) =>
            new(
                BookingDecisionState.Failed,
                reason,
                [],
                SelectedSlot: null,
                ProviderBookingId: null);
    }

    private static BookingAvailabilityRequest CreateAvailabilityRequest(BookingRequest request)
    {
        return new BookingAvailabilityRequest(
            request.TenantId,
            request.VerticalId,
            request.CorrelationId,
            request.ServiceType,
            request.SelectedSlot is null ? request.PreferredWindow : null,
            request.TimeZone,
            GetFieldValue(request.QualificationResult, "urgency"),
            request.SelectedSlot);
    }

    private static CreateBookingRequest CreateBookingRequest(
        BookingRequest request,
        AvailableSlot selectedSlot)
    {
        return new CreateBookingRequest(
            request.TenantId,
            request.VerticalId,
            request.CorrelationId,
            request.QualificationResult.LeadData,
            selectedSlot,
            request.ServiceType,
            request.PreferredWindow,
            request.ProviderContactId);
    }

    private static string? GetFieldValue(
        QualificationResult qualificationResult,
        string fieldName)
    {
        return qualificationResult.LeadData.Fields.TryGetValue(fieldName, out var value)
            ? value
            : null;
    }

    private static AvailableSlot? FindAvailableSlot(
        AvailableSlot selectedSlot,
        IReadOnlyCollection<AvailableSlot> availableSlots)
    {
        var selectedSlotId = selectedSlot.SlotId?.Trim();
        if (!string.IsNullOrWhiteSpace(selectedSlotId))
        {
            var slotById = availableSlots.FirstOrDefault(slot =>
                string.Equals(slot.SlotId?.Trim(), selectedSlotId, StringComparison.Ordinal));
            if (slotById is not null)
            {
                return slotById;
            }
        }

        return availableSlots.FirstOrDefault(slot =>
            slot.StartsAt.ToUniversalTime() == selectedSlot.StartsAt.ToUniversalTime()
            && slot.EndsAt.ToUniversalTime() == selectedSlot.EndsAt.ToUniversalTime());
    }

    private static BookingFailureReason? GetRefusalReason(QualificationResultState state)
    {
        return state switch
        {
            QualificationResultState.Qualified => null,
            QualificationResultState.MissingRequiredFields => BookingFailureReason.MissingRequiredFields,
            QualificationResultState.OutOfServiceArea => BookingFailureReason.OutOfServiceArea,
            QualificationResultState.InvalidInput => BookingFailureReason.InvalidInput,
            QualificationResultState.NeedsEscalation => BookingFailureReason.NeedsEscalation,
            _ => BookingFailureReason.QualificationNotQualified
        };
    }

    private async Task LogAsync(
        string eventName,
        BookingRequest request,
        BookingDecisionResult? result,
        CancellationToken cancellationToken)
    {
        var properties = new SafeTelemetryProperties()
            .Add("correlationId", request.CorrelationId)
            .Add("tenantId", request.TenantId)
            .Add("verticalId", request.VerticalId)
            .Add("qualificationState", request.QualificationResult.State.ToString())
            .AddIf(result is not null, "bookingState", result?.State.ToString())
            .AddIf(result?.FailureReason is not null, "failureReason", result?.FailureReason.ToString())
            .AddIf(result is not null, "availableSlotCount", result?.AvailableSlots.Count.ToString())
            .AddIf(request.SelectedSlot is not null, "selectedSlotProvided", "true")
            .ToDictionary();

        try
        {
            await eventLogger.LogEventAsync(eventName, properties, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Booking telemetry is best-effort.
        }
    }
}
