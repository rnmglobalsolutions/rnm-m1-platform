using System.Collections;
using System.Collections.Specialized;
using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using RNM.Platform.Application.Booking;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Confirmations;
using RNM.Platform.Application.Inbound;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Qualification;
using RNM.Platform.Application.Tenancy;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using RNM.Platform.Infrastructure.Secrets;

namespace RNM.Platform.UnitTests.Functions;

internal sealed class TestHttpRequestData : HttpRequestData
{
    public TestHttpRequestData(
        string method,
        string url,
        string body = "")
        : base(new TestFunctionContext())
    {
        Method = method;
        Url = new Uri(url);
        Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    }

    public override Stream Body { get; }

    public override HttpHeadersCollection Headers { get; } = new();

    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];

    public override Uri Url { get; }

    public override IEnumerable<ClaimsIdentity> Identities { get; } = [];

    public override string Method { get; }

    public override NameValueCollection Query => [];

    public override HttpResponseData CreateResponse()
    {
        return new TestHttpResponseData(FunctionContext);
    }
}

internal sealed class TestHttpResponseData : HttpResponseData
{
    public TestHttpResponseData(FunctionContext functionContext)
        : base(functionContext)
    {
    }

    public override HttpStatusCode StatusCode { get; set; }

    public override HttpHeadersCollection Headers { get; set; } = new();

    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpCookies Cookies { get; } = new TestHttpCookies();

    public string ReadBody()
    {
        Body.Position = 0;
        using var reader = new StreamReader(Body, leaveOpen: true);
        return reader.ReadToEnd();
    }
}

internal sealed class TestHttpCookies : HttpCookies
{
    private readonly List<IHttpCookie> cookies = [];

    public override void Append(string name, string value)
    {
        cookies.Add(CreateNew(name, value));
    }

    public override void Append(IHttpCookie cookie)
    {
        cookies.Add(cookie);
    }

    public override IHttpCookie CreateNew()
    {
        return CreateNew(string.Empty, string.Empty);
    }

    private static IHttpCookie CreateNew(string name, string value)
    {
        var cookie = new HttpCookie(name, value);
        return cookie;
    }
}

internal sealed class TestFunctionContext : FunctionContext
{
    private readonly IInvocationFeatures features = new TestInvocationFeatures();

    public override string InvocationId { get; } = Guid.NewGuid().ToString("N");

    public override string FunctionId { get; } = "test-function";

    public override TraceContext TraceContext { get; } = new TestTraceContext();

    public override BindingContext BindingContext { get; } = new TestBindingContext();

    public override RetryContext RetryContext => null!;

    public override IServiceProvider InstanceServices { get; set; } = new ServiceCollection().BuildServiceProvider();

    public override FunctionDefinition FunctionDefinition { get; } = new TestFunctionDefinition();

    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

    public override IInvocationFeatures Features => features;

    public override CancellationToken CancellationToken => CancellationToken.None;
}

internal sealed class TestInvocationFeatures : IInvocationFeatures
{
    private readonly Dictionary<Type, object> features = new();

    public T Get<T>()
    {
        return features.TryGetValue(typeof(T), out var feature)
            ? (T)feature
            : default!;
    }

    public void Set<T>(T instance)
    {
        if (instance is null)
        {
            features.Remove(typeof(T));
            return;
        }

        features[typeof(T)] = instance;
    }

    public IEnumerator<KeyValuePair<Type, object>> GetEnumerator()
    {
        return features.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

internal sealed class TestTraceContext : TraceContext
{
    public override string TraceParent => string.Empty;

    public override string TraceState => string.Empty;
}

internal sealed class TestBindingContext : BindingContext
{
    public override IReadOnlyDictionary<string, object?> BindingData { get; } =
        new Dictionary<string, object?>();
}

internal sealed class TestFunctionDefinition : FunctionDefinition
{
    public override string PathToAssembly => string.Empty;

    public override string EntryPoint => string.Empty;

    public override string Id => "test-function";

    public override string Name => "test-function";

    public override IImmutableDictionary<string, BindingMetadata> InputBindings { get; } =
        ImmutableDictionary<string, BindingMetadata>.Empty;

    public override IImmutableDictionary<string, BindingMetadata> OutputBindings { get; } =
        ImmutableDictionary<string, BindingMetadata>.Empty;

    public override ImmutableArray<FunctionParameter> Parameters { get; } =
        ImmutableArray<FunctionParameter>.Empty;
}

internal sealed class RecordingEventLogger : IEventLogger
{
    private readonly bool throwOnLog;

    public RecordingEventLogger(bool throwOnLog = false)
    {
        this.throwOnLog = throwOnLog;
    }

    public List<RecordedTelemetryEvent> Events { get; } = [];

    public Task LogEventAsync(
        string eventName,
        IReadOnlyDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        if (throwOnLog)
        {
            throw new InvalidOperationException("Telemetry sink unavailable.");
        }

        Events.Add(new RecordedTelemetryEvent(eventName, properties));
        return Task.CompletedTask;
    }
}

internal sealed record RecordedTelemetryEvent(
    string EventName,
    IReadOnlyDictionary<string, string> Properties);

internal sealed class RecordingInboundBookingWorkflow : IInboundBookingWorkflow
{
    private readonly bool throwOnProcess;
    private readonly InboundBookingWorkflowResult result;

    public RecordingInboundBookingWorkflow(
        bool throwOnProcess = false,
        InboundBookingWorkflowResult? result = null)
    {
        this.throwOnProcess = throwOnProcess;
        this.result = result ?? new InboundBookingWorkflowResult(
            InboundBookingWorkflowOutcome.Completed,
            QualificationResultState.Qualified,
            ServiceAreaDecisionState.InServiceArea,
            BookingDecisionState.Booked,
            CrmSyncState.Succeeded,
            ConfirmationWorkflowState.Completed);
    }

    public List<InboundCallEvent> Events { get; } = [];

    public List<InboundBookingWorkflowRequest> Requests { get; } = [];

    public Task<InboundBookingWorkflowResult> ProcessAsync(
        InboundCallEvent inboundCallEvent,
        CancellationToken cancellationToken)
    {
        return ProcessAsync(new InboundBookingWorkflowRequest(inboundCallEvent), cancellationToken);
    }

    public Task<InboundBookingWorkflowResult> ProcessAsync(
        InboundBookingWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (throwOnProcess)
        {
            throw new InvalidOperationException("Workflow failed.");
        }

        Requests.Add(request);
        Events.Add(request.InboundCallEvent);
        return Task.FromResult(result);
    }
}

internal sealed class RecordingInboundCallEventProcessor : IInboundCallEventProcessor
{
    private readonly bool throwOnProcess;

    public RecordingInboundCallEventProcessor(bool throwOnProcess = false)
    {
        this.throwOnProcess = throwOnProcess;
    }

    public List<InboundCallEvent> Events { get; } = [];

    public Task<InboundCallEventProcessingResult> ProcessAsync(
        InboundCallEvent inboundCallEvent,
        CancellationToken cancellationToken)
    {
        if (throwOnProcess)
        {
            throw new InvalidOperationException("Processor failed.");
        }

        Events.Add(inboundCallEvent);
        var result = inboundCallEvent.EventType is InboundCallEventType.Unsupported
            ? InboundCallEventProcessingResult.IgnoredUnsupported()
            : InboundCallEventProcessingResult.ProcessedResult();

        return Task.FromResult(result);
    }
}

internal sealed class StubSecretProvider : ISecretProvider
{
    private readonly string secretValue;
    private readonly bool throwOnRead;

    public StubSecretProvider(string secretValue, bool throwOnRead = false)
    {
        this.secretValue = secretValue;
        this.throwOnRead = throwOnRead;
    }

    public Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        return throwOnRead
            ? throw new SecretRetrievalException("Secret unavailable.")
            : Task.FromResult(secretValue);
    }
}

internal sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
{
    private readonly bool throwTenantResolutionException;

    public StubTenantConfigurationProvider(bool throwTenantResolutionException = false)
    {
        this.throwTenantResolutionException = throwTenantResolutionException;
    }

    public Task<TenantConfiguration> GetTenantConfigurationAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (throwTenantResolutionException)
        {
            throw new TenantResolutionException("Tenant not found.");
        }

        return Task.FromResult(new TenantConfiguration(
            new TenantId(tenantId),
            new VerticalId("example"),
            "Example Business",
            "America/Chicago",
            new ServiceAreaConfiguration(["12345"], ["Austin"], null),
            new ProviderConfiguration("ExampleCrm", "ExampleBooking", "Twilio", "ExampleEmail"),
            new SecretNameConfiguration(
                "crm-secret",
                "booking-secret",
                "vapi-secret",
                "twilio-account-sid",
                "twilio-auth-token",
                "email-secret"),
            new CommunicationConfiguration(
                "+15550001000",
                "booking@example.com",
                new ConfirmationTemplateConfiguration(
                    "SMS template {{bookingDate}}",
                    "Email subject {{bookingDate}}",
                    "Email body {{bookingStart}}"))));
    }
}

internal sealed class StubVerticalConfigurationProvider : IVerticalConfigurationProvider
{
    public Task<VerticalConfiguration> GetVerticalConfigurationAsync(
        string verticalId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new VerticalConfiguration(
            new VerticalId(verticalId),
            "Example",
            ["name", "phone"],
            ["inbound"],
            ServiceAreaFieldAliasConfiguration.Defaults()));
    }
}
