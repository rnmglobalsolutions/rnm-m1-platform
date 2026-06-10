using RNM.Platform.Api.Functions;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;
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
            new RecordingEventLogger());

        await function.RunAsync(
            """
            {
              "tenantId": "tenant-a",
              "correlationId": "corr-123",
              "kind": 0,
              "destination": "+15551234567",
              "body": "Appointment confirmed"
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
            new RecordingEventLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => function.RunAsync(
                """
                {
                  "tenantId": "tenant-a",
                  "correlationId": "corr-123",
                  "kind": 0,
                  "destination": "+15551234567",
                  "body": "Appointment confirmed"
                }
                """,
                CancellationToken.None));
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
