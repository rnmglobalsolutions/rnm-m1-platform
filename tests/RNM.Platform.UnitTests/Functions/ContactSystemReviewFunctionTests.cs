using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Messaging;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class ContactSystemReviewFunctionTests
{
    [Fact]
    public async Task Handle_ValidRequest_SendsRnmNotification()
    {
        var emailSender = new FakeEmailSender();
        var function = CreateFunction(emailSender);

        var response = (TestHttpResponseData)await function.Handle(CreateValidRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"received\":true", response.ReadBody());
        Assert.True(emailSender.Requests.Count >= 1);
        var notification = emailSender.Requests[0];
        Assert.Equal("info@rnmglobalsolutions.com", notification.ToEmail);
        Assert.Contains("New system review request from Jane Founder", notification.Subject);
        Assert.Contains("Workflow Needs Improvement", notification.Body);
    }

    [Fact]
    public async Task Handle_ValidRequest_AttemptsConfirmationAfterNotificationSuccess()
    {
        var emailSender = new FakeEmailSender();
        var function = CreateFunction(emailSender);

        var response = (TestHttpResponseData)await function.Handle(CreateValidRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, emailSender.Requests.Count);
        Assert.Equal("jane@example.com", emailSender.Requests[1].ToEmail);
        Assert.Equal("We received your system review request", emailSender.Requests[1].Subject);
    }

    [Fact]
    public async Task Handle_ConfirmationFailure_DoesNotFailSubmission()
    {
        var emailSender = new FakeEmailSender
        {
            Results = new Queue<EmailSendResult>(
            [
                new EmailSendResult(true, "notification-id"),
                new EmailSendResult(false, Message: "provider_failure")
            ])
        };
        var function = CreateFunction(emailSender);

        var response = (TestHttpResponseData)await function.Handle(CreateValidRequest(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"received\":true", response.ReadBody());
        Assert.Equal(2, emailSender.Requests.Count);
    }

    [Fact]
    public async Task Handle_InvalidEmail_ReturnsBadRequest()
    {
        var response = (TestHttpResponseData)await CreateFunction().Handle(
            CreateValidRequest(email: "not-an-email"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"validationResult\":\"invalid_email\"", response.ReadBody());
    }

    [Fact]
    public async Task Handle_MissingFullName_ReturnsBadRequest()
    {
        var response = (TestHttpResponseData)await CreateFunction().Handle(
            CreateValidRequest(fullName: ""),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"validationResult\":\"missing_full_name\"", response.ReadBody());
    }

    [Fact]
    public async Task Handle_MissingWorkflowNeedsImprovement_ReturnsBadRequest()
    {
        var response = (TestHttpResponseData)await CreateFunction().Handle(
            CreateValidRequest(workflowNeedsImprovement: ""),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"validationResult\":\"missing_workflow_needs_improvement\"", response.ReadBody());
    }

    [Fact]
    public async Task Handle_MalformedJson_ReturnsBadRequest()
    {
        var request = CreateRequest("{ not-json");

        var response = (TestHttpResponseData)await CreateFunction().Handle(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"validationResult\":\"malformed_json\"", response.ReadBody());
    }

    [Fact]
    public async Task Handle_HoneypotFilled_ReturnsSafeSuccessAndDoesNotSendEmail()
    {
        var emailSender = new FakeEmailSender();
        var response = (TestHttpResponseData)await CreateFunction(emailSender).Handle(
            CreateValidRequest(companyWebsiteConfirm: "spam"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"received\":true", response.ReadBody());
        Assert.Empty(emailSender.Requests);
    }

    [Fact]
    public async Task Handle_EmailProviderFailure_ReturnsSafeFailure()
    {
        var emailSender = new FakeEmailSender
        {
            Results = new Queue<EmailSendResult>(
            [
                new EmailSendResult(false, Message: "provider_failure")
            ])
        };

        var response = (TestHttpResponseData)await CreateFunction(emailSender).Handle(
            CreateValidRequest(),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("\"code\":\"notification_failed\"", response.ReadBody());
    }

    [Fact]
    public async Task Handle_TelemetryDoesNotIncludePii()
    {
        var eventLogger = new RecordingEventLogger();
        var response = (TestHttpResponseData)await CreateFunction(eventLogger: eventLogger).Handle(
            CreateValidRequest(),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(eventLogger.Events, recordedEvent =>
        {
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("Jane Founder", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("jane@example.com", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("+15551234567", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("manual spreadsheets", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recordedEvent.Properties.Values, value =>
                value.Contains("example.com", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static ContactSystemReviewFunction CreateFunction(
        FakeEmailSender? emailSender = null,
        RecordingEventLogger? eventLogger = null)
    {
        return new ContactSystemReviewFunction(
            emailSender ?? new FakeEmailSender(),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory(),
            eventLogger ?? new RecordingEventLogger(),
            new LimitedRequestBodyReader());
    }

    private static TestHttpRequestData CreateValidRequest(
        string fullName = "Jane Founder",
        string email = "jane@example.com",
        string phone = "+15551234567",
        string preferredChannels = "Email",
        string currentTools = "CRM, spreadsheets",
        string workflowNeedsImprovement = "Too many manual spreadsheets and missed follow-ups.",
        string website = "https://example.com",
        string companyWebsiteConfirm = "")
    {
        return CreateRequest(
            $$"""
            {
              "fullName": "{{fullName}}",
              "email": "{{email}}",
              "phone": "{{phone}}",
              "preferredChannels": "{{preferredChannels}}",
              "currentTools": "{{currentTools}}",
              "workflowNeedsImprovement": "{{workflowNeedsImprovement}}",
              "website": "{{website}}",
              "companyWebsiteConfirm": "{{companyWebsiteConfirm}}"
            }
            """);
    }

    private static TestHttpRequestData CreateRequest(string body)
    {
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/contact/system-review",
            body);
        request.Headers.Add("Origin", "https://www.rnmglobalsolutions.com");
        return request;
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public Queue<EmailSendResult> Results { get; init; } = new(
        [
            new EmailSendResult(true, "email-id"),
            new EmailSendResult(true, "confirmation-id")
        ]);

        public List<EmailMessageRequest> Requests { get; } = [];

        public Task<EmailSendResult> SendEmailAsync(
            EmailMessageRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new EmailSendResult(true, "email-id"));
        }
    }
}
