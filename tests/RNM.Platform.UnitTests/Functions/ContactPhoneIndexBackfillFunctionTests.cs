using System.Net;
using RNM.Platform.Api.Functions;
using RNM.Platform.Api.Http;
using RNM.Platform.Api.Runtime;
using RNM.Platform.Api.Security;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.Ports.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Functions;

public sealed class ContactPhoneIndexBackfillFunctionTests
{
    [Fact]
    public async Task HandleAsync_RejectsMissingApiKey()
    {
        var function = CreateFunction(new StubBackfillAdapter());
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/crm/phone-index/backfill");

        var response = (TestHttpResponseData)await function.HandleAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_BackfillsPhoneIndex_WhenAuthorized()
    {
        var adapter = new StubBackfillAdapter();
        var function = CreateFunction(adapter);
        var request = new TestHttpRequestData(
            "POST",
            "https://platform.example.com/api/tenants/tenant-a/crm/phone-index/backfill");
        request.Headers.Add("x-rnm-api-key", "secret");

        var response = (TestHttpResponseData)await function.HandleAsync(
            request,
            "tenant-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tenant-a", adapter.LastRequest?.TenantId);
        var body = response.ReadBody();
        Assert.Contains("\"tenantId\":\"tenant-a\"", body);
        Assert.Contains("\"indexed\":2", body);
        Assert.Contains("\"skipped\":1", body);
    }

    private static ContactPhoneIndexBackfillFunction CreateFunction(IContactPhoneIndexBackfillAdapter adapter) =>
        new(
            adapter,
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

    private sealed class StubBackfillAdapter : IContactPhoneIndexBackfillAdapter
    {
        public CrmContactPhoneIndexBackfillRequest? LastRequest { get; private set; }

        public Task<CrmContactPhoneIndexBackfillResult> BackfillPhoneIndexAsync(
            CrmContactPhoneIndexBackfillRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new CrmContactPhoneIndexBackfillResult(
                request.TenantId,
                request.CorrelationId,
                ContactsScanned: 3,
                Indexed: 2,
                Skipped: 1,
                Failed: 0));
        }
    }
}
