using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Qualification;
using Xunit;

namespace RNM.Platform.UnitTests.Crm;

public sealed class CrmApplicationServiceTests
{
    [Fact]
    public async Task SyncBookedLeadAsync_CreatesContact_WhenNoExistingContactIsFound()
    {
        var adapter = new FakeCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(Found: false, ProviderContactId: null),
            UpsertResult = new CrmContactUpsertResult(
                Succeeded: true,
                Created: true,
                ProviderContactId: "contact-123")
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("contact-123", result.ProviderContactId);
        Assert.Equal(1, adapter.LookupCallCount);
        Assert.Equal(1, adapter.UpsertCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmContactCreated));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_UpdatesContact_WhenExistingContactIsFound()
    {
        var adapter = new FakeCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(Found: true, ProviderContactId: "contact-existing"),
            UpsertResult = new CrmContactUpsertResult(
                Succeeded: true,
                Created: false,
                ProviderContactId: "contact-existing")
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("contact-existing", result.ProviderContactId);
        Assert.Equal("contact-existing", adapter.LastUpsertRequest?.ProviderContactId);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmContactUpdated));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_LinksBookingToContact()
    {
        var adapter = new FakeCrmAdapter();
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var request = CreateRequest() with
        {
            PreferredWindow = "Wednesday afternoon",
            TimeZone = "America/Chicago",
            BookingProvider = "GoogleCalendar"
        };
        var result = await service.SyncBookedLeadAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, adapter.LinkBookingCallCount);
        var booking = Assert.IsType<CrmBookingLinkRequest>(adapter.LastBookingLinkRequest);
        Assert.Equal("booking-123", booking.ProviderBookingId);
        Assert.Equal("contact-123", booking.ProviderContactId);
        Assert.Equal("vertical-a", booking.VerticalId);
        Assert.Equal("GoogleCalendar", booking.BookingProvider);
        Assert.Equal("InboundVoice", booking.Source);
        Assert.Equal("Jane Lead", booking.CustomerName);
        Assert.Equal("+15551234567", booking.PhoneNumber);
        Assert.Equal("lead@example.com", booking.Email);
        Assert.Equal("service", booking.ServiceType);
        Assert.Equal("residential", booking.PropertyType);
        Assert.Equal("123 Secret St, Addison, TX 75001", booking.ServiceAddress);
        Assert.Equal("75001", booking.ZipCode);
        Assert.Equal("non_urgent", booking.Urgency);
        Assert.Equal("Wednesday afternoon", booking.PreferredWindow);
        Assert.Equal("Afternoon", booking.BookingLabel);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero), booking.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 15, 0, 0, TimeSpan.Zero), booking.EndsAt);
        Assert.Equal("America/Chicago", booking.TimeZone);
        Assert.Equal("Booked", booking.BookingState);
        Assert.Equal("Qualified", booking.QualificationState);
        Assert.Equal("InServiceArea", booking.ServiceAreaState);
        Assert.Equal(2, adapter.TimelineEventCallCount);
        Assert.Equal(CrmTimelineEventTypes.BookingCreated, adapter.LastTimelineEventRequest?.EventType);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmBookingLinked));
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmTimelineEventAdded));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_DoesNotFailBookingSync_WhenTimelineWriteFails()
    {
        var adapter = new FakeCrmAdapter
        {
            TimelineEventResult = new CrmOperationResult(false, CrmFailureReason.AdapterFailure)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, adapter.TimelineEventCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmTimelineEventFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_PropagatesCancellation_WhenTimelineWriteIsCanceled()
    {
        var adapter = new FakeCrmAdapter
        {
            CancelOnTimeline = true
        };
        var service = CreateService(adapter);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task EnsureContactAsync_SavesOperationalAttributes_WithoutTranscript()
    {
        var adapter = new FakeCrmAdapter();
        var service = CreateService(adapter);
        var request = new CrmContactEnsureRequest(
            "tenant-a",
            "vertical-a",
            "corr-123",
            CreateQualificationResult("+15551234567", "lead@example.com"));

        var result = await service.EnsureContactAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(CrmLeadStatuses.Qualified, adapter.LastUpsertRequest?.LeadStatus);
        Assert.False(adapter.LastUpsertRequest?.NeedsFollowUp ?? true);
        Assert.Equal(1, adapter.TimelineEventCallCount);
        Assert.Equal(CrmTimelineEventTypes.LeadQualified, adapter.LastTimelineEventRequest?.EventType);
        var attributes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            adapter.LastUpsertRequest?.Attributes);
        Assert.Equal("Repair", attributes["serviceNeed"]);
        Assert.Equal("residential", attributes["propertyType"]);
        Assert.Equal("123 Secret St, Addison, TX 75001", attributes["serviceAddress"]);
        Assert.Equal("non_urgent", attributes["urgency"]);
        Assert.Equal("Wednesday afternoon", attributes["preferredTime"]);
        Assert.DoesNotContain("transcript", attributes.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncBookedLeadAsync_AppliesExpectedTags()
    {
        var adapter = new FakeCrmAdapter();
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(serviceType: "Repair"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, adapter.ApplyTagsCallCount);
        Assert.Contains("Inbound Call", adapter.LastTagRequest?.Tags ?? []);
        Assert.Contains("AI Booked", adapter.LastTagRequest?.Tags ?? []);
        Assert.Contains("Service Repair", adapter.LastTagRequest?.Tags ?? []);
        Assert.Contains("Booking Booked", adapter.LastTagRequest?.Tags ?? []);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmTagsApplied));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_AddsSafeInteractionNote()
    {
        var adapter = new FakeCrmAdapter();
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, adapter.AddNoteCallCount);
        Assert.Contains("AI booked appointment.", adapter.LastNoteRequest?.Note);
        Assert.Contains("Booking state: Booked", adapter.LastNoteRequest?.Note);
        Assert.Contains("Service: service", adapter.LastNoteRequest?.Note);
        Assert.Contains("Urgency: non_urgent", adapter.LastNoteRequest?.Note);
        Assert.Contains("ZIP: 75001", adapter.LastNoteRequest?.Note);
        Assert.Contains("Reference: corr-123", adapter.LastNoteRequest?.Note);
        Assert.DoesNotContain("+15551234567", adapter.LastNoteRequest?.Note);
        Assert.DoesNotContain("lead@example.com", adapter.LastNoteRequest?.Note);
        Assert.DoesNotContain("123 Secret St", adapter.LastNoteRequest?.Note);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmNoteAdded));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_HandlesCrmAdapterFailureSafely()
    {
        var adapter = new FakeCrmAdapter
        {
            ThrowOnUpsert = true
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.AdapterFailure, result.FailureReason);
        Assert.Equal(0, adapter.AddNoteCallCount);
        Assert.Equal(0, adapter.ApplyTagsCallCount);
        Assert.Equal(0, adapter.LinkBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_HandlesLookupExceptionSafely()
    {
        var adapter = new FakeCrmAdapter
        {
            ThrowOnLookup = true
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.AdapterFailure, result.FailureReason);
        Assert.Equal(0, adapter.UpsertCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_PropagatesCancellation_WhenLookupIsCanceled()
    {
        var adapter = new FakeCrmAdapter
        {
            CancelOnLookup = true
        };
        var service = CreateService(adapter);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_FailsSafely_WhenUpsertIsUnsuccessful()
    {
        var adapter = new FakeCrmAdapter
        {
            UpsertResult = new CrmContactUpsertResult(
                Succeeded: false,
                Created: false,
                ProviderContactId: null,
                CrmFailureReason.ContactUpsertFailed)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.ContactUpsertFailed, result.FailureReason);
        Assert.Equal(0, adapter.AddNoteCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_FailsSafely_WhenUpsertSucceedsWithoutContactId()
    {
        var adapter = new FakeCrmAdapter
        {
            UpsertResult = new CrmContactUpsertResult(
                Succeeded: true,
                Created: true,
                ProviderContactId: null)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.MissingProviderContactId, result.FailureReason);
        Assert.Equal(0, adapter.AddNoteCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_FailsSafely_WhenNoteFails()
    {
        var adapter = new FakeCrmAdapter
        {
            NoteResult = new CrmOperationResult(false, CrmFailureReason.NoteFailed)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.NoteFailed, result.FailureReason);
        Assert.Equal(0, adapter.ApplyTagsCallCount);
        Assert.Equal(0, adapter.LinkBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_FailsSafely_WhenTagsFail()
    {
        var adapter = new FakeCrmAdapter
        {
            TagResult = new CrmOperationResult(false, CrmFailureReason.TagsFailed)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.TagsFailed, result.FailureReason);
        Assert.Equal(0, adapter.LinkBookingCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_FailsSafely_WhenBookingLinkFails()
    {
        var adapter = new FakeCrmAdapter
        {
            BookingLinkResult = new CrmOperationResult(false, CrmFailureReason.BookingLinkFailed)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.BookingLinkFailed, result.FailureReason);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_Skips_WhenPhoneAndEmailAreMissing()
    {
        var adapter = new FakeCrmAdapter();
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(
            CreateRequest(phoneNumber: null, email: null),
            CancellationToken.None);

        Assert.Equal(CrmSyncState.Skipped, result.State);
        Assert.Equal(CrmFailureReason.MissingContactIdentifier, result.FailureReason);
        Assert.Equal(0, adapter.LookupCallCount);
        Assert.Equal(0, adapter.UpsertCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmSkipped));
        Assert.DoesNotContain(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_EmitsSkippedTelemetry_WhenBookingIsNotCompleted()
    {
        var adapter = new FakeCrmAdapter();
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(
            CreateRequest(bookingDecision: CreateNonBookedDecision()),
            CancellationToken.None);

        Assert.Equal(CrmSyncState.Skipped, result.State);
        Assert.Equal(CrmFailureReason.BookingNotCompleted, result.FailureReason);
        Assert.Equal(0, adapter.LookupCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmSkipped));
        Assert.DoesNotContain(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_FailsSafely_WhenLookupFoundWithoutContactId()
    {
        var adapter = new FakeCrmAdapter
        {
            LookupResult = new CrmContactLookupResult(Found: true, ProviderContactId: null)
        };
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(adapter, eventLogger);

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(CrmSyncState.Failed, result.State);
        Assert.Equal(CrmFailureReason.AdapterFailure, result.FailureReason);
        Assert.Equal(0, adapter.UpsertCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmFailed));
    }

    [Fact]
    public async Task SyncBookedLeadAsync_SanitizesAndLimitsDynamicServiceTypeTag()
    {
        var adapter = new FakeCrmAdapter();
        var service = CreateService(adapter);
        var unsafeServiceType = "  Emergency\nRepair<script>with an extremely long suffix that should be trimmed  ";

        var result = await service.SyncBookedLeadAsync(CreateRequest(serviceType: unsafeServiceType), CancellationToken.None);

        Assert.True(result.Succeeded);
        var serviceTag = Assert.Single(adapter.LastTagRequest?.Tags ?? [], tag => tag.StartsWith("Service ", StringComparison.Ordinal));
        var tagValue = serviceTag["Service ".Length..];
        Assert.DoesNotContain('\n', serviceTag);
        Assert.DoesNotContain('<', serviceTag);
        Assert.DoesNotContain('>', serviceTag);
        Assert.True(tagValue.Length <= 48);
    }

    [Fact]
    public async Task SyncBookedLeadAsync_EmitsSafeCrmTelemetry()
    {
        var eventLogger = new RecordingCrmEventLogger();
        var service = CreateService(eventLogger: eventLogger);

        await service.SyncBookedLeadAsync(CreateRequest(serviceType: "Repair"), CancellationToken.None);

        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.CrmUpsertRequested));
        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("+15551234567", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("lead@example.com", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("123 Secret St", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("Repair", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("raw transcript", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task SyncBookedLeadAsync_DoesNotLeakGoHighLevelDetailsIntoApplication()
    {
        var service = CreateService();

        var result = await service.SyncBookedLeadAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("GoHighLevel", typeof(CrmApplicationService).FullName);
        Assert.DoesNotContain("GoHighLevel", typeof(ICrmAdapter).FullName);
        Assert.DoesNotContain("GoHighLevel", typeof(CrmSyncRequest).FullName);
        Assert.DoesNotContain("GoHighLevel", typeof(CrmContactUpsertRequest).FullName);
    }

    private static CrmApplicationService CreateService(
        FakeCrmAdapter? adapter = null,
        RecordingCrmEventLogger? eventLogger = null)
    {
        return new CrmApplicationService(
            adapter ?? new FakeCrmAdapter(),
            eventLogger ?? new RecordingCrmEventLogger());
    }

    private static CrmSyncRequest CreateRequest(
        string? serviceType = "service",
        string? phoneNumber = "+15551234567",
        string? email = "lead@example.com",
        BookingDecisionResult? bookingDecision = null)
    {
        return new CrmSyncRequest(
            "tenant-a",
            "vertical-a",
            "corr-123",
            CreateQualificationResult(phoneNumber, email),
            bookingDecision ?? CreateBookedDecision(),
            serviceType);
    }

    private static QualificationResult CreateQualificationResult(
        string? phoneNumber,
        string? email)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "Jane Lead",
            ["serviceNeed"] = "Repair",
            ["propertyType"] = "residential",
            ["serviceAddress"] = "123 Secret St, Addison, TX 75001",
            ["urgency"] = "non_urgent",
            ["preferredTime"] = "Wednesday afternoon",
            ["transcript"] = "raw transcript should never be logged"
        };

        if (!string.IsNullOrWhiteSpace(email))
        {
            fields["email"] = email;
        }

        var leadData = new QualifiedLeadData(
            fields,
            "75001",
            phoneNumber);

        return new QualificationResult(
            QualificationResultState.Qualified,
            leadData,
            [new RequiredFieldStatus("serviceNeed", IsPresent: true)],
            [],
            ServiceAreaDecision.InServiceArea());
    }

    private static BookingDecisionResult CreateBookedDecision()
    {
        var slot = new AvailableSlot(
            "slot-1",
            new DateTimeOffset(2026, 5, 1, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 15, 0, 0, TimeSpan.Zero),
            "Afternoon");

        return new BookingDecisionResult(
            BookingDecisionState.Booked,
            FailureReason: null,
            [slot],
            slot,
            "booking-123");
    }

    private static BookingDecisionResult CreateNonBookedDecision()
    {
        return new BookingDecisionResult(
            BookingDecisionState.Refused,
            BookingFailureReason.MissingRequiredFields,
            [],
            SelectedSlot: null,
            ProviderBookingId: null);
    }

    private static Predicate<RecordedCrmEvent> EventNamed(string eventName)
    {
        return recordedEvent => recordedEvent.EventName == eventName;
    }

    private sealed class FakeCrmAdapter : ICrmAdapter
    {
        public CrmContactLookupResult LookupResult { get; init; } =
            new(Found: false, ProviderContactId: null);

        public CrmContactUpsertResult UpsertResult { get; init; } =
            new(Succeeded: true, Created: true, ProviderContactId: "contact-123");

        public CrmOperationResult NoteResult { get; init; } =
            new(Succeeded: true);

        public CrmOperationResult TagResult { get; init; } =
            new(Succeeded: true);

        public CrmOperationResult BookingLinkResult { get; init; } =
            new(Succeeded: true);

        public CrmOperationResult TimelineEventResult { get; init; } =
            new(Succeeded: true);

        public bool ThrowOnUpsert { get; init; }

        public bool ThrowOnLookup { get; init; }

        public bool CancelOnLookup { get; init; }

        public bool CancelOnTimeline { get; init; }

        public int LookupCallCount { get; private set; }

        public int UpsertCallCount { get; private set; }

        public int AddNoteCallCount { get; private set; }

        public int ApplyTagsCallCount { get; private set; }

        public int LinkBookingCallCount { get; private set; }

        public int TimelineEventCallCount { get; private set; }

        public int FollowUpCallCount { get; private set; }

        public CrmContactUpsertRequest? LastUpsertRequest { get; private set; }

        public CrmInteractionNoteRequest? LastNoteRequest { get; private set; }

        public CrmTagRequest? LastTagRequest { get; private set; }

        public CrmBookingLinkRequest? LastBookingLinkRequest { get; private set; }

        public CrmTimelineEventRequest? LastTimelineEventRequest { get; private set; }

        public CrmFollowUpRequest? LastFollowUpRequest { get; private set; }

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
            CrmContactLookupRequest request,
            CancellationToken cancellationToken)
        {
            LookupCallCount++;
            if (CancelOnLookup)
            {
                throw new OperationCanceledException();
            }

            return ThrowOnLookup
                ? throw new InvalidOperationException("CRM lookup failed.")
                : Task.FromResult(LookupResult);
        }

        public Task<CrmContactUpsertResult> UpsertContactAsync(
            CrmContactUpsertRequest request,
            CancellationToken cancellationToken)
        {
            UpsertCallCount++;
            LastUpsertRequest = request;
            return ThrowOnUpsert
                ? throw new InvalidOperationException("CRM upsert failed.")
                : Task.FromResult(UpsertResult);
        }

        public Task<CrmOperationResult> AddInteractionNoteAsync(
            CrmInteractionNoteRequest request,
            CancellationToken cancellationToken)
        {
            AddNoteCallCount++;
            LastNoteRequest = request;
            return Task.FromResult(NoteResult);
        }

        public Task<CrmOperationResult> ApplyTagsAsync(
            CrmTagRequest request,
            CancellationToken cancellationToken)
        {
            ApplyTagsCallCount++;
            LastTagRequest = request;
            return Task.FromResult(TagResult);
        }

        public Task<CrmOperationResult> LinkBookingToContactAsync(
            CrmBookingLinkRequest request,
            CancellationToken cancellationToken)
        {
            LinkBookingCallCount++;
            LastBookingLinkRequest = request;
            return Task.FromResult(BookingLinkResult);
        }

        public Task<CrmOperationResult> AddTimelineEventAsync(
            CrmTimelineEventRequest request,
            CancellationToken cancellationToken)
        {
            TimelineEventCallCount++;
            LastTimelineEventRequest = request;
            if (CancelOnTimeline)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(TimelineEventResult);
        }

        public Task<CrmOperationResult> MarkFollowUpRequiredAsync(
            CrmFollowUpRequest request,
            CancellationToken cancellationToken)
        {
            FollowUpCallCount++;
            LastFollowUpRequest = request;
            return Task.FromResult(new CrmOperationResult(true));
        }
    }

    private sealed class RecordingCrmEventLogger : IEventLogger
    {
        public List<RecordedCrmEvent> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add(new RecordedCrmEvent(eventName, properties));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedCrmEvent(
        string EventName,
        IReadOnlyDictionary<string, string> Properties);
}
