using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.Domain.Configuration;
using Xunit;

namespace RNM.Platform.UnitTests.Confirmations;

public sealed class ConfirmationApplicationServiceTests
{
    [Fact]
    public async Task SendBookingConfirmationAsync_SendsSms_WhenBookingSucceeded()
    {
        var smsSender = new FakeSmsSender();
        var service = CreateService(smsSender: smsSender);

        var result = await service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.SmsSent);
        Assert.Equal(1, smsSender.SendCallCount);
        Assert.Equal("+15551234567", smsSender.LastRequest?.ToPhoneNumber);
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_DoesNotSendSms_WhenBookingFailed()
    {
        var smsSender = new FakeSmsSender();
        var emailSender = new FakeEmailSender();
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(smsSender, emailSender, eventLogger);

        var result = await service.SendBookingConfirmationAsync(
            CreateRequest(bookingDecision: CreateFailedBookingDecision()),
            CancellationToken.None);

        Assert.Equal(ConfirmationChannelStatus.Skipped, result.Sms.Status);
        Assert.Equal(ConfirmationFailureReason.BookingNotCompleted, result.Sms.FailureReason);
        Assert.Equal(0, smsSender.SendCallCount);
        Assert.Equal(0, emailSender.SendCallCount);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.ConfirmationRequested));
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_LogsSmsFailureSafely()
    {
        var smsSender = new FakeSmsSender
        {
            SendResult = new SmsSendResult(Succeeded: false)
        };
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(smsSender: smsSender, eventLogger: eventLogger);

        var result = await service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(ConfirmationChannelStatus.Failed, result.Sms.Status);
        Assert.Equal(ConfirmationFailureReason.SmsSendFailed, result.Sms.FailureReason);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.SmsConfirmationFailed));
        AssertNoSensitiveTelemetry(eventLogger);
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_PropagatesCancellation_WhenSmsSenderIsCanceled()
    {
        var smsSender = new FakeSmsSender
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var service = CreateService(smsSender: smsSender);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_SendsEmail_WhenEmailExists()
    {
        var emailSender = new FakeEmailSender();
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(emailSender: emailSender, eventLogger: eventLogger);

        var result = await service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.EmailSent);
        Assert.Equal(1, emailSender.SendCallCount);
        Assert.Equal("lead@example.com", emailSender.LastRequest?.ToEmail);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.EmailConfirmationSent));
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_SkipsEmail_WhenEmailIsMissing()
    {
        var emailSender = new FakeEmailSender();
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(emailSender: emailSender, eventLogger: eventLogger);

        var result = await service.SendBookingConfirmationAsync(CreateRequest(customerEmail: null), CancellationToken.None);

        Assert.Equal(ConfirmationChannelStatus.Skipped, result.Email.Status);
        Assert.Equal(ConfirmationFailureReason.MissingEmail, result.Email.FailureReason);
        Assert.Equal(0, emailSender.SendCallCount);
        Assert.True(result.SmsSent);
        Assert.NotEqual(ConfirmationChannelStatus.Failed, result.Email.Status);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.EmailConfirmationSkipped));
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_LogsEmailFailureSafely()
    {
        var emailSender = new FakeEmailSender
        {
            SendResult = new EmailSendResult(Succeeded: false)
        };
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(emailSender: emailSender, eventLogger: eventLogger);

        var result = await service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(ConfirmationChannelStatus.Failed, result.Email.Status);
        Assert.Equal(ConfirmationFailureReason.EmailSendFailed, result.Email.FailureReason);
        Assert.True(result.SmsSent);
        Assert.Contains(eventLogger.Events, EventNamed(TelemetryEventNames.EmailConfirmationFailed));
        AssertNoSensitiveTelemetry(eventLogger);
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_PropagatesCancellation_WhenEmailSenderIsCanceled()
    {
        var emailSender = new FakeEmailSender
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var service = CreateService(emailSender: emailSender);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_AllowsSms_WhenCrmSyncFailedButBookingSucceeded()
    {
        var smsSender = new FakeSmsSender();
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(smsSender: smsSender, eventLogger: eventLogger);

        var result = await service.SendBookingConfirmationAsync(
            CreateRequest(crmSyncResult: new CrmSyncResult(CrmSyncState.Failed, "contact-123", CrmFailureReason.AdapterFailure)),
            CancellationToken.None);

        Assert.True(result.SmsSent);
        Assert.Equal(1, smsSender.SendCallCount);
        Assert.Contains(eventLogger.Events, recordedEvent =>
            recordedEvent.Properties.TryGetValue("crmState", out var crmState)
            && crmState == CrmSyncState.Failed.ToString());
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_UsesConfiguredTemplates()
    {
        var smsSender = new FakeSmsSender();
        var emailSender = new FakeEmailSender();
        var service = CreateService(smsSender, emailSender);
        var configuredTemplates = new ConfirmationTemplateConfiguration(
            "configured sms {{bookingDate}} {{bookingTime}} {{serviceType}}",
            "configured email {{bookingDate}}",
            "configured body {{bookingStart}}");

        var result = await service.SendBookingConfirmationAsync(
            CreateRequest(
                templates: ConfirmationTemplateSet.FromConfiguration(configuredTemplates),
                serviceType: "maintenance"),
            CancellationToken.None);

        Assert.True(result.SmsSent);
        Assert.Equal("configured sms 2026-05-01 14:00 maintenance", smsSender.LastRequest?.Body);
        Assert.Equal("configured email 2026-05-01", emailSender.LastRequest?.Subject);
        Assert.Equal("configured body 2026-05-01T14:00:00.0000000+00:00", emailSender.LastRequest?.Body);
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_DoesNotLogSensitiveData()
    {
        var eventLogger = new RecordingConfirmationEventLogger();
        var service = CreateService(eventLogger: eventLogger);

        await service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None);

        AssertNoSensitiveTelemetry(eventLogger);
    }

    [Fact]
    public async Task SendBookingConfirmationAsync_DoesNotLeakProviderDetailsIntoApplication()
    {
        var service = CreateService();

        var result = await service.SendBookingConfirmationAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.SmsSent);
        Assert.DoesNotContain("Twilio", typeof(ConfirmationApplicationService).FullName);
        Assert.DoesNotContain("AzureCommunication", typeof(ConfirmationApplicationService).FullName);
        Assert.DoesNotContain("Twilio", typeof(ISmsSender).FullName);
        Assert.DoesNotContain("AzureCommunication", typeof(IEmailSender).FullName);
        Assert.DoesNotContain("Twilio", typeof(BookingConfirmationRequest).FullName);
    }

    private static ConfirmationApplicationService CreateService(
        FakeSmsSender? smsSender = null,
        FakeEmailSender? emailSender = null,
        RecordingConfirmationEventLogger? eventLogger = null)
    {
        return new ConfirmationApplicationService(
            smsSender ?? new FakeSmsSender(),
            emailSender ?? new FakeEmailSender(),
            eventLogger ?? new RecordingConfirmationEventLogger());
    }

    private static BookingConfirmationRequest CreateRequest(
        BookingDecisionResult? bookingDecision = null,
        CrmSyncResult? crmSyncResult = null,
        string? customerPhoneNumber = "+15551234567",
        string? customerEmail = "lead@example.com",
        string? serviceType = "Repair",
        ConfirmationTemplateSet? templates = null)
    {
        return new BookingConfirmationRequest(
            "tenant-a",
            "vertical-a",
            "corr-123",
            bookingDecision ?? CreateBookedDecision(),
            crmSyncResult ?? new CrmSyncResult(CrmSyncState.Succeeded, "contact-123"),
            customerPhoneNumber,
            customerEmail,
            serviceType,
            templates ?? new ConfirmationTemplateSet(
                "SMS template for {{bookingDate}} {{bookingTime}}",
                "Email subject {{bookingDate}}",
                "Email body {{bookingTime}}"));
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

    private static BookingDecisionResult CreateFailedBookingDecision()
    {
        return new BookingDecisionResult(
            BookingDecisionState.Failed,
            BookingFailureReason.AdapterFailure,
            [],
            SelectedSlot: null,
            ProviderBookingId: null);
    }

    private static Predicate<RecordedConfirmationEvent> EventNamed(string eventName)
    {
        return recordedEvent => recordedEvent.EventName == eventName;
    }

    private static void AssertNoSensitiveTelemetry(RecordingConfirmationEventLogger eventLogger)
    {
        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("+15551234567", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("lead@example.com", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("SMS template", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("Email body", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("123 Secret St", StringComparison.OrdinalIgnoreCase));
        });
    }

    private sealed class FakeSmsSender : ISmsSender
    {
        public SmsSendResult SendResult { get; init; } =
            new(Succeeded: true, ProviderMessageId: "sms-123");

        public Exception? ExceptionToThrow { get; init; }

        public int SendCallCount { get; private set; }

        public SmsMessageRequest? LastRequest { get; private set; }

        public Task<SmsSendResult> SendSmsAsync(
            SmsMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            LastRequest = request;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(SendResult);
        }
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public EmailSendResult SendResult { get; init; } =
            new(Succeeded: true, ProviderMessageId: "email-123");

        public Exception? ExceptionToThrow { get; init; }

        public int SendCallCount { get; private set; }

        public EmailMessageRequest? LastRequest { get; private set; }

        public Task<EmailSendResult> SendEmailAsync(
            EmailMessageRequest request,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            LastRequest = request;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(SendResult);
        }
    }

    private sealed class RecordingConfirmationEventLogger : IEventLogger
    {
        public List<RecordedConfirmationEvent> Events { get; } = [];

        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken)
        {
            Events.Add(new RecordedConfirmationEvent(eventName, properties));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordedConfirmationEvent(
        string EventName,
        IReadOnlyDictionary<string, string> Properties);
}
