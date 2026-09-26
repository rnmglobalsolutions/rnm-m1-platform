using RNM.Platform.Api.Functions;
using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;
using RNM.Platform.UnitTests.Classes;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class ConfirmationRetryFunctionTests
{
    [Fact]
    public async Task RunAsync_RetriesSmsMessage()
    {
        var smsSender = new RecordingSmsSender();
        var function = new ConfirmationRetryFunction(
            smsSender,
            new RecordingEmailSender(),
            new RecordingEventLogger(),
            new AllowingSmsEligibilityGate());

        await function.RunAsync(
            """
            {
              "tenantId": "tenant-a",
              "correlationId": "corr-123",
              "kind": 0,
              "destination": "+15551234567",
              "body": "Appointment confirmed",
              "providerContactId": "contact-1",
              "smsCategory": 0,
              "originalRequestedAt": "2026-09-26T12:00:00Z"
            }
            """,
            CancellationToken.None);

        var request = Assert.Single(smsSender.Requests);
        Assert.Equal("+15551234567", request.ToPhoneNumber);
        Assert.Equal("Appointment confirmed", request.Body);
    }

    [Fact]
    public async Task RunAsync_Throws_WhenProviderRetryFails()
    {
        var smsSender = new RecordingSmsSender
        {
            Result = new SmsSendResult(false)
        };
        var function = new ConfirmationRetryFunction(
            smsSender,
            new RecordingEmailSender(),
            new RecordingEventLogger(),
            new AllowingSmsEligibilityGate());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => function.RunAsync(
                """
                {
                  "tenantId": "tenant-a",
                  "correlationId": "corr-123",
                  "kind": 0,
                  "destination": "+15551234567",
                  "body": "Appointment confirmed",
                  "providerContactId": "contact-1",
                  "smsCategory": 0,
                  "originalRequestedAt": "2026-09-26T12:00:00Z"
                }
                """,
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_SuccessfulClassRetryUpdatesOnlyDeliveredChannel()
    {
        var store = new FakeClassSessionStore();
        var function = new ConfirmationRetryFunction(
            new RecordingSmsSender(),
            new RecordingEmailSender(),
            new RecordingEventLogger(),
            new AllowingSmsEligibilityGate(),
            store);

        await function.RunAsync(
            """
            {
              "tenantId": "tenant-a",
              "correlationId": "corr-123",
              "kind": 0,
              "destination": "+15551234567",
              "body": "Class confirmed",
              "classRegistrationId": "registration-1",
              "providerContactId": "contact-1",
              "smsCategory": 1,
              "originalRequestedAt": "2026-09-26T12:00:00Z"
            }
            """,
            CancellationToken.None);

        var update = Assert.Single(store.NotificationStatusUpdates);
        Assert.Equal("Sent", update.SmsStatus);
        Assert.Null(update.EmailStatus);
    }

    [Fact]
    public async Task RunAsync_LegacySmsWithoutContactContext_IsSkipped()
    {
        var smsSender = new RecordingSmsSender();
        var eventLogger = new FakeEventLogger();
        var function = new ConfirmationRetryFunction(
            smsSender,
            new RecordingEmailSender(),
            eventLogger,
            new SmsEligibilityGate(
                new FakeCrmAdapter(),
                new FakeTenantConfigurationProvider(),
                new SendWindowPolicy(),
                new FakeEventLogger(),
                TimeProvider.System));

        await function.RunAsync(
            """
            {
              "tenantId": "tenant-a",
              "correlationId": "corr-legacy",
              "kind": 0,
              "destination": "+15551234567",
              "body": "Old queued message"
            }
            """,
            CancellationToken.None);

        Assert.Empty(smsSender.Requests);
        Assert.Contains(eventLogger.Events, item =>
            item.EventName == TelemetryEventNames.ConfirmationRetrySkipped
            && item.Properties.GetValueOrDefault("skipReason") == nameof(SmsEligibilitySkipReason.RetryMissingTimestamp));
    }

    [Fact]
    public async Task RunAsync_EligibilityDenied_IsSkippedWithoutSending()
    {
        var smsSender = new RecordingSmsSender();
        var function = new ConfirmationRetryFunction(
            smsSender,
            new RecordingEmailSender(),
            new RecordingEventLogger(),
            new AllowingSmsEligibilityGate { DenyWith = SmsEligibilitySkipReason.ContactOptedOut });

        await function.RunAsync(
            """
            {
              "tenantId": "tenant-a",
              "correlationId": "corr-stop",
              "kind": 0,
              "destination": "+15551234567",
              "body": "Appointment confirmed",
              "providerContactId": "contact-1",
              "smsCategory": 0,
              "originalRequestedAt": "2026-09-26T12:00:00Z"
            }
            """,
            CancellationToken.None);

        Assert.Empty(smsSender.Requests);
    }

    private sealed class RecordingSmsSender : ISmsSender
    {
        public SmsSendResult Result { get; init; } = new(true, "sms-123");

        public List<SmsMessageRequest> Requests { get; } = [];

        public Task<SmsSendResult> SendSmsAsync(
            SmsMessageRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public Task<EmailSendResult> SendEmailAsync(
            EmailMessageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmailSendResult(true, "email-123"));
    }

    private sealed class RecordingEventLogger : IEventLogger
    {
        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
