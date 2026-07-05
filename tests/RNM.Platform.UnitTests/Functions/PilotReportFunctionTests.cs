using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Observability;
using RNM.Platform.Application.Ports.Reporting;
using RNM.Platform.Application.Reporting;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class PilotReportFunctionTests
{
    [Fact]
    public async Task HandleAsync_RejectsMissingApiKey()
    {
        var function = CreateFunction();
        var request = new TestHttpRequestData(
            "GET",
            "https://platform.example.com/api/tenants/tenant-a/reports/pilot?from=2026-07-01T00%3A00%3A00Z&to=2026-07-07T00%3A00%3A00Z");

        var response = (TestHttpResponseData)await function.HandleAsync(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_ReturnsPilotReport_WhenAuthorized()
    {
        var function = CreateFunction();
        var request = new TestHttpRequestData(
            "GET",
            "https://platform.example.com/api/tenants/tenant-a/reports/pilot?from=2026-07-01T00%3A00%3A00Z&to=2026-07-07T00%3A00%3A00Z");
        request.Headers.Add("x-rnm-api-key", "secret");

        var response = (TestHttpResponseData)await function.HandleAsync(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = response.ReadBody();
        Assert.Contains("\"tenantId\":\"tenant-a\"", body);
        Assert.Contains("\"projectedRevenue\"", body);
    }

    [Fact]
    public async Task HandleAsync_ReturnsBadRequest_WhenTenantConfigurationFails()
    {
        var function = CreateFunction(tenantConfigurationProvider: new ThrowingTenantConfigurationProvider());
        var request = new TestHttpRequestData(
            "GET",
            "https://platform.example.com/api/tenants/tenant-a/reports/pilot?from=2026-07-01T00%3A00%3A00Z&to=2026-07-07T00%3A00%3A00Z");
        request.Headers.Add("x-rnm-api-key", "secret");

        var response = (TestHttpResponseData)await function.HandleAsync(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", response.ReadBody());
    }

    [Fact]
    public async Task HandleAsync_ReturnsSafeInternalError_WhenReportingReadFails()
    {
        var function = CreateFunction(reportingReadAdapter: new ThrowingReportingReadAdapter());
        var request = new TestHttpRequestData(
            "GET",
            "https://platform.example.com/api/tenants/tenant-a/reports/pilot?from=2026-07-01T00%3A00%3A00Z&to=2026-07-07T00%3A00%3A00Z");
        request.Headers.Add("x-rnm-api-key", "secret");

        var response = (TestHttpResponseData)await function.HandleAsync(request, "tenant-a", CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = response.ReadBody();
        Assert.Contains("\"code\":\"internal_error\"", body);
        Assert.DoesNotContain("storage unavailable", body, StringComparison.OrdinalIgnoreCase);
    }

    private static PilotReportFunction CreateFunction(
        IReportingReadAdapter? reportingReadAdapter = null,
        ITenantConfigurationProvider? tenantConfigurationProvider = null)
    {
        var reportingService = new PilotReportingService(
            reportingReadAdapter ?? new StubReportingReadAdapter(),
            tenantConfigurationProvider ?? new StubTenantConfigurationProvider(),
            new StubEventLogger());

        return new PilotReportFunction(
            reportingService,
            new ApiKeyRequestValidator(),
            new RnmRuntimeConfiguration(
                "Development",
                "../../../config",
                "secret",
                KeyVaultUri: null,
                AllowEnvironmentSecretFallback: true,
                RequireInternalApiKey: true),
            new SafeErrorResponseFactory(),
            new SafeHttpResponseWriter(),
            new CorrelationContextFactory());
    }

    private sealed class StubReportingReadAdapter : IReportingReadAdapter
    {
        public Task<ReportingDataSet> GetPilotReportingDataAsync(
            PilotReportRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReportingDataSet([], [], []));
    }

    private sealed class ThrowingReportingReadAdapter : IReportingReadAdapter
    {
        public Task<ReportingDataSet> GetPilotReportingDataAsync(
            PilotReportRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("storage unavailable");
    }

    private sealed class StubTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TenantConfiguration(
                new TenantId(tenantId),
                new VerticalId("real-estate"),
                "Tenant",
                "America/Chicago",
                new ServiceAreaConfiguration(["*"], [], null),
                new ProviderConfiguration("AzureTable", "GoogleCalendar", "Twilio", "SendGrid"),
                new SecretNameConfiguration("crm", "booking", "voice", "sid", "token", "email"),
                new CommunicationConfiguration("+15550001000", null, new ConfirmationTemplateConfiguration("sms")),
                new ReportingConfiguration()));
    }

    private sealed class ThrowingTenantConfigurationProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            throw new ConfigurationException("Tenant configuration is invalid.");
    }

    private sealed class StubEventLogger : IEventLogger
    {
        public Task LogEventAsync(
            string eventName,
            IReadOnlyDictionary<string, string> properties,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
