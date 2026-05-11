using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Booking;
using RNM.Platform.Application.Ports.Crm;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Inbound;

public sealed class InboundBookingWorkflowTests
{
    [Fact]
    public async Task ProcessAsync_CompletesFullSuccessFlow()
    {
        var harness = CreateHarness();

        var result = await harness.Workflow.ProcessAsync(CreateWorkflowRequest(), CancellationToken.None);

        Assert.True(result.WorkflowCompleted);
        Assert.True(result.BookingSucceeded);
        Assert.True(result.CrmSucceeded);
        Assert.True(result.ConfirmationSucceeded);
        Assert.Equal(InboundBookingWorkflowOutcome.Completed, result.Outcome);
        Assert.Equal(QualificationResultState.Qualified, result.QualificationState);
        Assert.Equal(ServiceAreaDecisionState.InServiceArea, result.ServiceAreaState);
        Assert.Equal(BookingDecisionState.Booked, result.BookingState);
        Assert.Equal(CrmSyncState.Succeeded, result.CrmState);
        Assert.Equal(ConfirmationWorkflowState.Completed, result.ConfirmationState);
        Assert.Equal(1, harness.BookingAdapter.CreateBookingCallCount);
        Assert.Equal(1, harness.CrmAdapter.UpsertCallCount);
        Assert.Equal(1, harness.CrmAdapter.LinkBookingCallCount);
        Assert.Equal("contact-123", harness.BookingAdapter.LastCreateBookingRequest?.ProviderContactId);
        Assert.Equal(1, harness.SmsSender.SendCallCount);
        Assert.Equal(1, harness.EmailSender.SendCallCount);
        Assert.Contains(harness.EventLogger.Events, EventNamed(TelemetryEventNames.WorkflowCompleted));
    }

    [Fact]
    public async Task ProcessAsync_Stops_WhenQualificationFails()
    {
        var harness = CreateHarness();

        var result = await harness.Workflow.ProcessAsync(
            CreateWorkflowRequest(argumentsJson: """{"serviceNeed":"repair"}"""),
            CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.QualificationStopped, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.False(result.BookingSucceeded);
        Assert.False(result.CrmSucceeded);
        Assert.False(result.ConfirmationSucceeded);
        Assert.Equal(QualificationResultState.MissingRequiredFields, result.QualificationState);
        Assert.Null(result.BookingState);
        Assert.Equal(0, harness.BookingAdapter.AvailabilityCallCount);
        Assert.Equal(0, harness.CrmAdapter.UpsertCallCount);
        Assert.Equal(0, harness.SmsSender.SendCallCount);
    }

    [Fact]
    public async Task ProcessAsync_Stops_WhenServiceAreaFails()
    {
        var harness = CreateHarness();

        var result = await harness.Workflow.ProcessAsync(
            CreateWorkflowRequest(argumentsJson: CreateArgumentsJson(serviceAddress: "123 Outside Rd, Dallas, TX 99999")),
            CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.QualificationStopped, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.False(result.BookingSucceeded);
        Assert.Equal(QualificationResultState.OutOfServiceArea, result.QualificationState);
        Assert.Equal(ServiceAreaDecisionState.OutOfServiceArea, result.ServiceAreaState);
        Assert.Equal(0, harness.BookingAdapter.AvailabilityCallCount);
        Assert.Equal(0, harness.SmsSender.SendCallCount);
    }

    [Fact]
    public async Task ProcessAsync_Stops_WhenBookingFails()
    {
        var bookingAdapter = new FakeBookingAdapter
        {
            AvailabilityResult = new BookingAvailabilityResult(false, [])
        };
        var harness = CreateHarness(bookingAdapter: bookingAdapter);

        var result = await harness.Workflow.ProcessAsync(CreateWorkflowRequest(), CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.BookingStopped, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.False(result.BookingSucceeded);
        Assert.Equal(BookingDecisionState.NoAvailability, result.BookingState);
        Assert.Equal(CrmSyncState.Succeeded, result.CrmState);
        Assert.Null(result.ConfirmationState);
        Assert.Equal(1, harness.CrmAdapter.UpsertCallCount);
        Assert.Equal(0, harness.SmsSender.SendCallCount);
    }

    [Fact]
    public async Task ProcessAsync_StopsBeforeBooking_WhenCrmContactCannotBeCreated()
    {
        var crmAdapter = new FakeCrmAdapter
        {
            UpsertResult = new CrmContactUpsertResult(
                Succeeded: false,
                Created: false,
                ProviderContactId: null,
                CrmFailureReason.AdapterFailure)
        };
        var harness = CreateHarness(crmAdapter: crmAdapter);

        var result = await harness.Workflow.ProcessAsync(CreateWorkflowRequest(), CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.CrmStopped, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.False(result.BookingSucceeded);
        Assert.False(result.CrmSucceeded);
        Assert.False(result.ConfirmationSucceeded);
        Assert.Null(result.BookingState);
        Assert.Equal(CrmSyncState.Failed, result.CrmState);
        Assert.Equal(0, harness.BookingAdapter.CreateBookingCallCount);
        Assert.Equal(0, harness.SmsSender.SendCallCount);
        Assert.Contains(harness.EventLogger.Events, recordedEvent =>
            recordedEvent.EventName == TelemetryEventNames.WorkflowCompleted
            && recordedEvent.Properties.TryGetValue("outcome", out var outcome)
            && outcome == InboundBookingWorkflowOutcome.CrmStopped.ToString());
    }

    [Fact]
    public async Task ProcessAsync_SendsSms_WhenPostBookingCrmSyncFails()
    {
        var crmAdapter = new FakeCrmAdapter
        {
            BookingLinkResult = new CrmOperationResult(false, CrmFailureReason.BookingLinkFailed)
        };
        var harness = CreateHarness(crmAdapter: crmAdapter);

        var result = await harness.Workflow.ProcessAsync(CreateWorkflowRequest(), CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.Completed, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.True(result.BookingSucceeded);
        Assert.False(result.CrmSucceeded);
        Assert.True(result.ConfirmationSucceeded);
        Assert.Equal(BookingDecisionState.Booked, result.BookingState);
        Assert.Equal(CrmSyncState.Failed, result.CrmState);
        Assert.Equal(1, harness.BookingAdapter.CreateBookingCallCount);
        Assert.Equal(1, harness.SmsSender.SendCallCount);
        Assert.Contains(harness.EventLogger.Events, recordedEvent =>
            recordedEvent.EventName == TelemetryEventNames.WorkflowCrmCompleted
            && recordedEvent.Properties.TryGetValue("crmState", out var crmState)
            && crmState == CrmSyncState.Failed.ToString());
    }

    [Fact]
    public async Task ProcessAsync_KeepsBookingSuccess_WhenSmsFails()
    {
        var smsSender = new FakeSmsSender
        {
            SendResult = new SmsSendResult(Succeeded: false)
        };
        var harness = CreateHarness(smsSender: smsSender);

        var result = await harness.Workflow.ProcessAsync(CreateWorkflowRequest(), CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.Completed, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.True(result.BookingSucceeded);
        Assert.True(result.CrmSucceeded);
        Assert.False(result.ConfirmationSucceeded);
        Assert.Equal(BookingDecisionState.Booked, result.BookingState);
        Assert.Equal(ConfirmationWorkflowState.Failed, result.ConfirmationState);
        Assert.Equal(1, harness.BookingAdapter.CreateBookingCallCount);
        Assert.Equal(1, harness.SmsSender.SendCallCount);
    }

    [Fact]
    public async Task ProcessAsync_SkipsEmail_WhenEmailIsMissing()
    {
        var harness = CreateHarness();

        var result = await harness.Workflow.ProcessAsync(
            CreateWorkflowRequest(argumentsJson: CreateArgumentsJson(email: null)),
            CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.Completed, result.Outcome);
        Assert.True(result.WorkflowCompleted);
        Assert.True(result.BookingSucceeded);
        Assert.True(result.ConfirmationSucceeded);
        Assert.Equal(ConfirmationWorkflowState.Completed, result.ConfirmationState);
        Assert.Equal(1, harness.SmsSender.SendCallCount);
        Assert.Equal(0, harness.EmailSender.SendCallCount);
        Assert.Contains(harness.EventLogger.Events, EventNamed(TelemetryEventNames.EmailConfirmationSkipped));
    }

    [Fact]
    public async Task ProcessAsync_DoesNotLogSensitiveData()
    {
        var harness = CreateHarness();

        await harness.Workflow.ProcessAsync(
            CreateWorkflowRequest(
                transcript: "Customer said the house is at 123 Secret St.",
                argumentsJson: CreateArgumentsJson(serviceAddress: "123 Secret St, Addison, TX 75001")),
            CancellationToken.None);

        Assert.All(harness.EventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("+15551234567", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("lead@example.com", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("123 Secret St", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("Customer said", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("raw", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task ProcessAsync_PreservesKnownBookingState_WhenUnexpectedExceptionOccursAfterBooking()
    {
        var harness = CreateHarness(
            tenantConfigurationProvider: new StubTenantConfigurationProvider(useInvalidConfirmationTemplates: true));

        var result = await harness.Workflow.ProcessAsync(CreateWorkflowRequest(), CancellationToken.None);

        Assert.Equal(InboundBookingWorkflowOutcome.Failed, result.Outcome);
        Assert.False(result.WorkflowCompleted);
        Assert.True(result.BookingSucceeded);
        Assert.True(result.CrmSucceeded);
        Assert.False(result.ConfirmationSucceeded);
        Assert.Equal(QualificationResultState.Qualified, result.QualificationState);
        Assert.Equal(ServiceAreaDecisionState.InServiceArea, result.ServiceAreaState);
        Assert.Equal(BookingDecisionState.Booked, result.BookingState);
        Assert.Equal(CrmSyncState.Succeeded, result.CrmState);
        Assert.Null(result.ConfirmationState);
        Assert.Contains(harness.EventLogger.Events, recordedEvent =>
            recordedEvent.EventName == TelemetryEventNames.WorkflowFailed
            && recordedEvent.Properties.TryGetValue("bookingState", out var bookingState)
            && bookingState == BookingDecisionState.Booked.ToString());
    }

    private static WorkflowHarness CreateHarness(
        FakeBookingAdapter? bookingAdapter = null,
        FakeCrmAdapter? crmAdapter = null,
        FakeSmsSender? smsSender = null,
        FakeEmailSender? emailSender = null,
        ITenantConfigurationProvider? tenantConfigurationProvider = null)
    {
        var eventLogger = new RecordingWorkflowEventLogger();
        var finalBookingAdapter = bookingAdapter ?? new FakeBookingAdapter();
        var finalCrmAdapter = crmAdapter ?? new FakeCrmAdapter();
        var finalSmsSender = smsSender ?? new FakeSmsSender();
        var finalEmailSender = emailSender ?? new FakeEmailSender();
        var workflow = new InboundBookingWorkflow(
            tenantConfigurationProvider ?? new StubTenantConfigurationProvider(),
            new StubVerticalConfigurationProvider(),
            new QualificationService(new ServiceAreaValidator(), eventLogger),
            new BookingApplicationService(finalBookingAdapter, eventLogger),
            new CrmApplicationService(finalCrmAdapter, eventLogger),
            new ConfirmationApplicationService(finalSmsSender, finalEmailSender, eventLogger),
            eventLogger);

        return new WorkflowHarness(
            workflow,
            finalBookingAdapter,
            finalCrmAdapter,
            finalSmsSender,
            finalEmailSender,
            eventLogger);
    }

    private static InboundBookingWorkflowRequest CreateWorkflowRequest(
        string? argumentsJson = null,
        string? transcript = null)
    {
        return new InboundBookingWorkflowRequest(
            new InboundCallEvent(
                "tenant-a",
                "hvac",
                "corr-123",
                InboundCallEventType.ActionRequested,
                new CallSession("call-123", "+15551234567"),
                "test",
                "tool-call",
                transcript,
                "assistant",
                new StructuredActionRequest("action-123", "bookAppointment", argumentsJson ?? CreateArgumentsJson()),
                new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    private static string CreateArgumentsJson(
        string serviceAddress = "123 Main St, Addison, TX 75001",
        string? email = "lead@example.com")
    {
        var emailProperty = email is null
            ? string.Empty
            : $@",""email"":""{email}""";

        return $$"""
        {
          "serviceNeed": "Repair",
          "propertyType": "Residential",
          "serviceAddress": "{{serviceAddress}}",
          "urgency": "Soon",
          "preferredTime": "Afternoon",
          "name": "Jane Customer"{{emailProperty}}
        }
        """;
    }

    private static Predicate<RecordedWorkflowEvent> EventNamed(string eventName) =>
        recordedEvent => recordedEvent.EventName == eventName;

    private sealed record WorkflowHarness(
        InboundBookingWorkflow Workflow,
        FakeBookingAdapter BookingAdapter,
        FakeCrmAdapter CrmAdapter,
        FakeSmsSender SmsSender,
        FakeEmailSender EmailSender,
        RecordingWorkflowEventLogger EventLogger);

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        private readonly bool useInvalidConfirmationTemplates;

        public StubTenantConfigurationProvider(bool useInvalidConfirmationTemplates = false)
        {
            this.useInvalidConfirmationTemplates = useInvalidConfirmationTemplates;
        }

        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("hvac"),
                "Tenant A",
                "America/Chicago",
                new ServiceAreaConfiguration(["75001"], ["Addison"], null),
                new ProviderConfiguration("Crm", "Booking", "Sms", "Email"),
                new SecretNameConfiguration("crm", "booking", "vapi", "sid", "token", "email"),
                new CommunicationConfiguration(
                    "+15550001000",
                    "booking@example.com",
                    useInvalidConfirmationTemplates
                        ? null!
                        : new ConfirmationTemplateConfiguration(
                            "SMS {{bookingDate}} {{bookingTime}} {{serviceType}}",
                            "Email {{bookingDate}}",
                            "Email {{bookingStart}}"))));
        }
    }

    private sealed class StubVerticalConfigurationProvider : IVerticalConfigurationProvider
    {
        public Task<VerticalConfiguration> GetVerticalConfigurationAsync(
            string verticalId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new VerticalConfiguration(
                new VerticalId(verticalId),
                "HVAC",
                ["serviceNeed", "propertyType", "serviceAddress", "urgency", "preferredTime"],
                ["GeneralInquiry"],
                ServiceAreaFieldAliasConfiguration.Defaults()));
        }
    }

    private sealed class FakeBookingAdapter : IBookingAdapter
    {
        private readonly AvailableSlot slot = new(
            "slot-1",
            new DateTimeOffset(2026, 5, 2, 14, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 2, 15, 0, 0, TimeSpan.Zero),
            "Afternoon");

        public BookingAvailabilityResult? AvailabilityResult { get; init; }

        public CreateBookingResult CreateResult { get; init; } =
            new(Succeeded: true, ProviderBookingId: "booking-123");

        public int AvailabilityCallCount { get; private set; }

        public int CreateBookingCallCount { get; private set; }

        public CreateBookingRequest? LastCreateBookingRequest { get; private set; }

        public Task<BookingAvailabilityResult> CheckAvailabilityAsync(
            BookingAvailabilityRequest request,
            CancellationToken cancellationToken)
        {
            AvailabilityCallCount++;
            return Task.FromResult(AvailabilityResult ?? new BookingAvailabilityResult(true, [slot]));
        }

        public Task<CreateBookingResult> CreateBookingAsync(
            CreateBookingRequest request,
            CancellationToken cancellationToken)
        {
            CreateBookingCallCount++;
            LastCreateBookingRequest = request;
            return Task.FromResult(CreateResult);
        }
    }

    private sealed class FakeCrmAdapter : ICrmAdapter
    {
        public CrmContactUpsertResult UpsertResult { get; init; } =
            new(Succeeded: true, Created: true, ProviderContactId: "contact-123");

        public CrmOperationResult BookingLinkResult { get; init; } =
            new(Succeeded: true);

        public int UpsertCallCount { get; private set; }

        public int LinkBookingCallCount { get; private set; }

        public Task<CrmContactLookupResult> FindContactByPhoneOrEmailAsync(
            CrmContactLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmContactLookupResult(false, ProviderContactId: null));

        public Task<CrmContactUpsertResult> UpsertContactAsync(
            CrmContactUpsertRequest request,
            CancellationToken cancellationToken)
        {
            UpsertCallCount++;
            return Task.FromResult(UpsertResult);
        }

        public Task<CrmOperationResult> AddInteractionNoteAsync(
            CrmInteractionNoteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmOperationResult(true));

        public Task<CrmOperationResult> ApplyTagsAsync(
            CrmTagRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CrmOperationResult(true));

        public Task<CrmOperationResult> LinkBookingToContactAsync(
            CrmBookingLinkRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(RecordBookingLink());

        private CrmOperationResult RecordBookingLink()
        {
            LinkBookingCallCount++;
            return BookingLinkResult;
        }
    }

    private sealed class FakeSmsSender : ISmsSender
    {
        public SmsSendResult SendResult { get; init; } =
            new(Succeeded: true, ProviderMessageId: "sms-123");

        public int SendCallCount { get; private set; }

        public Task<SmsSendResult> SendSmsAsync(
            SmsMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            return Task.FromResult(SendResult);
        }
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public int SendCallCount { get; private set; }

        public Task<EmailSendResult> SendEmailAsync(
            EmailMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            return Task.FromResult(new EmailSendResult(Succeeded: true, ProviderMessageId: "email-123"));
        }
    }

    private sealed class RecordingWorkflowEventLogger : IEventLogger
    {
        public List<RecordedWorkflowEvent> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add(new RecordedWorkflowEvent(eventName, properties));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedWorkflowEvent(
        string EventName,
        IReadOnlyDictionary<string, string> Properties);
}
