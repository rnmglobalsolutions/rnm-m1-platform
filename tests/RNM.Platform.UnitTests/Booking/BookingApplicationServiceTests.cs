using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Booking;
using RNM.Platform.Application.Qualification;
using Xunit;

namespace RNM.Platform.UnitTests.Booking;

public sealed class BookingApplicationServiceTests
{
    [Fact]
    public async Task ProcessAsync_RefusesBooking_WhenQualificationHasMissingRequiredFields()
    {
        var service = CreateService();
        var request = CreateRequest(QualificationResultState.MissingRequiredFields);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Refused, result.State);
        Assert.Equal(BookingFailureReason.MissingRequiredFields, result.FailureReason);
    }

    [Fact]
    public async Task ProcessAsync_RefusesBooking_WhenQualificationIsOutOfServiceArea()
    {
        var service = CreateService();
        var request = CreateRequest(QualificationResultState.OutOfServiceArea);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Refused, result.State);
        Assert.Equal(BookingFailureReason.OutOfServiceArea, result.FailureReason);
    }

    [Fact]
    public async Task ProcessAsync_RefusesBooking_WhenQualificationHasInvalidInput()
    {
        var service = CreateService();
        var request = CreateRequest(QualificationResultState.InvalidInput);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Refused, result.State);
        Assert.Equal(BookingFailureReason.InvalidInput, result.FailureReason);
    }

    [Fact]
    public async Task ProcessAsync_RefusesBooking_WhenQualificationNeedsEscalation()
    {
        var service = CreateService();
        var request = CreateRequest(QualificationResultState.NeedsEscalation);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Refused, result.State);
        Assert.Equal(BookingFailureReason.NeedsEscalation, result.FailureReason);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsAvailabilityFound_ByDefault_WhenNoSlotIsSelected()
    {
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [CreateSlot()])
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.AvailabilityFound, result.State);
        Assert.Single(result.AvailableSlots);
        Assert.Equal(1, adapter.CheckAvailabilityCallCount);
        Assert.Equal(0, adapter.CreateBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingAvailabilityFound));
    }

    [Fact]
    public async Task ProcessAsync_CreatesBooking_WhenAutoSelectAfterConfirmationIsExplicit()
    {
        var slot = CreateSlot();
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [slot])
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            AutoSelectFirstAvailableAfterConfirmation = true
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.IsBooked);
        Assert.Equal(slot, result.SelectedSlot);
        Assert.Equal(1, adapter.CreateBookingCallCount);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsNoAvailability_WhenAdapterHasNoSlots()
    {
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: false,
                [])
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            AutoSelectFirstAvailableAfterConfirmation = true
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.NoAvailability, result.State);
        Assert.Equal(BookingFailureReason.NoAvailability, result.FailureReason);
        Assert.Equal(1, adapter.CheckAvailabilityCallCount);
        Assert.Equal(0, adapter.CreateBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingNoAvailability));
    }

    [Fact]
    public async Task ProcessAsync_CreatesBooking_WhenSelectedSlotMatchesBySlotId()
    {
        var availableSlot = CreateSlot(
            slotId: "slot-available",
            startsAt: new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2026, 5, 1, 15, 0, 0, TimeSpan.Zero));
        var selectedSlot = CreateSlot(
            slotId: "slot-available",
            startsAt: new DateTimeOffset(2026, 5, 2, 14, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2026, 5, 2, 15, 0, 0, TimeSpan.Zero));
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [availableSlot])
        };
        var service = CreateService(adapter);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            SelectedSlot = selectedSlot
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.IsBooked);
        Assert.Equal(availableSlot, result.SelectedSlot);
        Assert.Equal(availableSlot, adapter.LastCreateBookingRequest?.Slot);
    }

    [Fact]
    public async Task ProcessAsync_CreatesBooking_WhenSelectedSlotMatchesByStartAndEndTimeWithoutSlotId()
    {
        var availableSlot = CreateSlot(slotId: null);
        var selectedSlot = CreateSlot(slotId: null, label: "Caller selected");
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [availableSlot])
        };
        var service = CreateService(adapter);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            SelectedSlot = selectedSlot
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.IsBooked);
        Assert.Equal(availableSlot, result.SelectedSlot);
        Assert.Equal(availableSlot, adapter.LastCreateBookingRequest?.Slot);
    }

    [Fact]
    public async Task ProcessAsync_FailsSafely_WhenSelectedSlotIsNotAvailable()
    {
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [CreateSlot(slotId: "slot-available")])
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            SelectedSlot = CreateSlot(slotId: "slot-missing")
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Failed, result.State);
        Assert.Equal(BookingFailureReason.SlotUnavailable, result.FailureReason);
        Assert.Equal(0, adapter.CreateBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingFailed));
    }

    [Fact]
    public async Task ProcessAsync_FailsSafely_WhenSelectedSlotIsStale()
    {
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [CreateSlot(slotId: "slot-current")])
        };
        var service = CreateService(adapter);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            SelectedSlot = CreateSlot(slotId: "slot-stale")
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Failed, result.State);
        Assert.Equal(BookingFailureReason.SlotUnavailable, result.FailureReason);
        Assert.Equal(0, adapter.CreateBookingCallCount);
    }

    [Fact]
    public async Task ProcessAsync_CreatesBooking_WhenQualifiedAndSlotIsAvailable()
    {
        var slot = CreateSlot();
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [slot]),
            CreateBookingResult = new CreateBookingResult(
                Succeeded: true,
                ProviderBookingId: "booking-123")
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            AutoSelectFirstAvailableAfterConfirmation = true
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.True(result.IsBooked);
        Assert.Equal("booking-123", result.ProviderBookingId);
        Assert.Equal(slot, result.SelectedSlot);
        Assert.Equal(1, adapter.CheckAvailabilityCallCount);
        Assert.Equal(1, adapter.CreateBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingCreated));
    }

    [Fact]
    public async Task ProcessAsync_HandlesAvailabilityAdapterExceptionSafely()
    {
        var adapter = new FakeBookingAdapter
        {
            ThrowOnAvailability = true
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Failed, result.State);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
        Assert.Equal(0, adapter.CreateBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingFailed));
    }

    [Fact]
    public async Task ProcessAsync_HandlesUnsuccessfulAvailabilityResultSafely()
    {
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: false,
                [],
                BookingFailureReason.AdapterFailure)
            {
                Succeeded = false
            }
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Failed, result.State);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
        Assert.Equal(0, adapter.CreateBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingFailed));
    }

    [Fact]
    public async Task ProcessAsync_EmitsAvailabilityRequestedTelemetry()
    {
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(eventLogger: eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified);

        await service.ProcessAsync(request, CancellationToken.None);

        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingAvailabilityRequested));
    }

    [Fact]
    public async Task ProcessAsync_EmitsCreateRequestedTelemetry()
    {
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(eventLogger: eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            AutoSelectFirstAvailableAfterConfirmation = true
        };

        await service.ProcessAsync(request, CancellationToken.None);

        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingCreateRequested));
    }

    [Fact]
    public async Task ProcessAsync_EmitsRefusedTelemetry_WhenLeadIsNotEligible()
    {
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(eventLogger: eventLogger);
        var request = CreateRequest(QualificationResultState.MissingRequiredFields);

        await service.ProcessAsync(request, CancellationToken.None);

        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingRefused));
        Assert.DoesNotContain(eventLogger.Events, EventNamed(TelemetryEventNames.BookingFailed));
    }

    [Fact]
    public async Task ProcessAsync_HandlesBookingAdapterFailureSafely()
    {
        var adapter = new FakeBookingAdapter
        {
            ThrowOnCreateBooking = true,
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [CreateSlot()])
        };
        var eventLogger = new RecordingBookingEventLogger();
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified) with
        {
            AutoSelectFirstAvailableAfterConfirmation = true
        };

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Failed, result.State);
        Assert.Equal(BookingFailureReason.AdapterFailure, result.FailureReason);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.BookingFailed));
    }

    [Fact]
    public async Task ProcessAsync_EmitsSafeBookingTelemetry()
    {
        var eventLogger = new RecordingBookingEventLogger();
        var adapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(
                HasAvailability: true,
                [CreateSlot()])
        };
        var service = CreateService(adapter, eventLogger);
        var request = CreateRequest(QualificationResultState.Qualified);

        await service.ProcessAsync(request, CancellationToken.None);

        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("123 Secret St", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("+15551234567", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("Repair", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ProcessAsync_ApplicationBookingContractsAreProviderNeutral()
    {
        var service = CreateService();
        var request = CreateRequest(QualificationResultState.MissingRequiredFields);

        var result = await service.ProcessAsync(request, CancellationToken.None);

        Assert.Equal(BookingDecisionState.Refused, result.State);
        Assert.DoesNotContain("GoHighLevel", typeof(BookingApplicationService).FullName);
        Assert.DoesNotContain("GoHighLevel", typeof(IBookingAdapter).FullName);
        Assert.DoesNotContain("GoHighLevel", typeof(BookingRequest).FullName);
    }

    private static BookingApplicationService CreateService(
        FakeBookingAdapter? adapter = null,
        RecordingBookingEventLogger? eventLogger = null)
    {
        return new BookingApplicationService(
            adapter ?? new FakeBookingAdapter(),
            eventLogger ?? new RecordingBookingEventLogger());
    }

    private static BookingRequest CreateRequest(QualificationResultState qualificationState)
    {
        return new BookingRequest(
            "tenant-a",
            "vertical-a",
            "corr-123",
            "America/Chicago",
            CreateQualificationResult(qualificationState),
            ServiceType: "service",
            PreferredWindow: "afternoon");
    }

    private static QualificationResult CreateQualificationResult(QualificationResultState state)
    {
        var serviceAreaDecision = state switch
        {
            QualificationResultState.Qualified => ServiceAreaDecision.InServiceArea(),
            QualificationResultState.OutOfServiceArea => ServiceAreaDecision.OutOfServiceArea(),
            QualificationResultState.InvalidInput => ServiceAreaDecision.InvalidZipCode(),
            QualificationResultState.NeedsEscalation => ServiceAreaDecision.NeedsEscalation(),
            _ => ServiceAreaDecision.MissingZipCode()
        };
        var leadData = new QualifiedLeadData(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["serviceNeed"] = "Repair",
                ["serviceAddress"] = "123 Secret St, Addison, TX 75001"
            },
            "75001",
            "+15551234567");

        return new QualificationResult(
            state,
            leadData,
            [new RequiredFieldStatus("serviceNeed", IsPresent: true)],
            state is QualificationResultState.MissingRequiredFields ? ["propertyType"] : [],
            serviceAreaDecision);
    }

    private static AvailableSlot CreateSlot(
        string? slotId = "slot-1",
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        string? label = "Afternoon")
    {
        return new AvailableSlot(
            slotId,
            startsAt ?? new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero),
            endsAt ?? new DateTimeOffset(2026, 5, 1, 15, 0, 0, TimeSpan.Zero),
            label);
    }

    private static Predicate<RecordedBookingEvent> EventNamed(string eventName)
    {
        return recordedEvent => recordedEvent.EventName == eventName;
    }

    private sealed class FakeBookingAdapter : IBookingAdapter
    {
        public BookingAvailabilityResult AvailabilityResult { get; init; } =
            new(HasAvailability: true, [CreateSlot()]);

        public CreateBookingResult CreateBookingResult { get; init; } =
            new(Succeeded: true, ProviderBookingId: "booking-123");

        public bool ThrowOnAvailability { get; init; }

        public bool ThrowOnCreateBooking { get; init; }

        public int CheckAvailabilityCallCount { get; private set; }

        public int CreateBookingCallCount { get; private set; }

        public CreateBookingRequest? LastCreateBookingRequest { get; private set; }

        public Task<BookingAvailabilityResult> CheckAvailabilityAsync(
            BookingAvailabilityRequest request,
            CancellationToken cancellationToken)
        {
            CheckAvailabilityCallCount++;
            return ThrowOnAvailability
                ? throw new InvalidOperationException("Availability failed.")
                : Task.FromResult(AvailabilityResult);
        }

        public Task<CreateBookingResult> CreateBookingAsync(
            CreateBookingRequest request,
            CancellationToken cancellationToken)
        {
            CreateBookingCallCount++;
            LastCreateBookingRequest = request;
            return ThrowOnCreateBooking
                ? throw new InvalidOperationException("Booking failed.")
                : Task.FromResult(CreateBookingResult);
        }
    }

    private sealed class RecordingBookingEventLogger : IEventLogger
    {
        public List<RecordedBookingEvent> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add(new RecordedBookingEvent(eventName, properties));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedBookingEvent(
        string EventName,
        IReadOnlyDictionary<string, string> Properties);
}
