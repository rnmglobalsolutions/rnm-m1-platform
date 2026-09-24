using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Application.Ports.LeadIntake;

namespace RNM.Platform.Infrastructure.LeadIntake;

public sealed class AzureTableInboundLeadReceiptStore : IInboundLeadReceiptStore
{
    private const string DefaultTableName = "RnmInboundWebhookReceipts";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly string? connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
    private readonly string tableName = Environment.GetEnvironmentVariable("RNM_INBOUND_WEBHOOK_RECEIPTS_TABLE_NAME")
        ?? DefaultTableName;

    public async Task<InboundLeadReceiptClaimResult> TryBeginAsync(
        InboundLeadReceiptClaimRequest request,
        CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken).ConfigureAwait(false);
        var rowKey = CreateRowKey(request.Provider, request.ExternalEventId);
        var leaseId = Guid.NewGuid().ToString("N");
        var entity = NewProcessingEntity(request, rowKey, leaseId, attemptCount: 1);

        try
        {
            await table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);
            return new InboundLeadReceiptClaimResult(InboundLeadReceiptClaimState.Acquired, rowKey, leaseId);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // Existing receipts are evaluated below with an ETag-protected takeover for stale/failed work.
        }

        var existing = await table.GetEntityAsync<TableEntity>(request.TenantId, rowKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var status = ReadString(existing.Value, "Status");
        if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return new InboundLeadReceiptClaimResult(
                InboundLeadReceiptClaimState.Completed,
                rowKey,
                ProviderContactId: ReadString(existing.Value, "ProviderContactId"));
        }

        var leaseExpiresAt = ReadDateTimeOffset(existing.Value, "LeaseExpiresAt");
        if (string.Equals(status, "Processing", StringComparison.OrdinalIgnoreCase)
            && leaseExpiresAt is { } expiry
            && expiry > request.Now)
        {
            return new InboundLeadReceiptClaimResult(InboundLeadReceiptClaimState.InProgress, rowKey);
        }

        existing.Value["Status"] = "Processing";
        existing.Value["LeaseId"] = leaseId;
        existing.Value["LeaseExpiresAt"] = request.Now.Add(LeaseDuration);
        existing.Value["CorrelationId"] = request.CorrelationId;
        existing.Value["UpdatedAt"] = request.Now;
        existing.Value["AttemptCount"] = (ReadInt(existing.Value, "AttemptCount") ?? 0) + 1;
        existing.Value.Remove("FailureCode");
        try
        {
            await table.UpdateEntityAsync(existing.Value, existing.Value.ETag, TableUpdateMode.Replace, cancellationToken)
                .ConfigureAwait(false);
            return new InboundLeadReceiptClaimResult(InboundLeadReceiptClaimState.Acquired, rowKey, leaseId);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            return new InboundLeadReceiptClaimResult(InboundLeadReceiptClaimState.InProgress, rowKey);
        }
    }

    public Task CompleteAsync(InboundLeadReceiptCompletionRequest request, CancellationToken cancellationToken) =>
        UpdateOwnedReceiptAsync(
            request.TenantId,
            request.RowKey,
            request.LeaseId,
            "Completed",
            request.CorrelationId,
            request.CompletedAt,
            request.ProviderContactId,
            failureCode: null,
            cancellationToken);

    public Task FailAsync(InboundLeadReceiptFailureRequest request, CancellationToken cancellationToken) =>
        UpdateOwnedReceiptAsync(
            request.TenantId,
            request.RowKey,
            request.LeaseId,
            "Failed",
            request.CorrelationId,
            request.FailedAt,
            providerContactId: null,
            request.FailureCode,
            cancellationToken);

    internal static string CreateRowKey(string provider, string externalEventId)
    {
        var value = $"{provider.Trim().ToLowerInvariant()}|{externalEventId.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private async Task UpdateOwnedReceiptAsync(
        string tenantId,
        string rowKey,
        string leaseId,
        string status,
        string correlationId,
        DateTimeOffset timestamp,
        string? providerContactId,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        var table = await GetTableAsync(cancellationToken).ConfigureAwait(false);
        var existing = await table.GetEntityAsync<TableEntity>(tenantId, rowKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(ReadString(existing.Value, "LeaseId"), leaseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Inbound lead receipt lease is no longer owned by this operation.");
        }

        existing.Value["Status"] = status;
        existing.Value["CorrelationId"] = correlationId;
        existing.Value["UpdatedAt"] = timestamp;
        existing.Value[status == "Completed" ? "CompletedAt" : "FailedAt"] = timestamp;
        if (!string.IsNullOrWhiteSpace(providerContactId))
        {
            existing.Value["ProviderContactId"] = providerContactId;
        }

        if (!string.IsNullOrWhiteSpace(failureCode))
        {
            existing.Value["FailureCode"] = failureCode;
        }

        await table.UpdateEntityAsync(existing.Value, existing.Value.ETag, TableUpdateMode.Replace, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TableClient> GetTableAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure Table connection string is missing.");
        }

        var table = new TableClient(connectionString, tableName);
        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        return table;
    }

    private static TableEntity NewProcessingEntity(
        InboundLeadReceiptClaimRequest request,
        string rowKey,
        string leaseId,
        int attemptCount) =>
        new(request.TenantId, rowKey)
        {
            ["Provider"] = request.Provider,
            ["Status"] = "Processing",
            ["LeaseId"] = leaseId,
            ["LeaseExpiresAt"] = request.Now.Add(LeaseDuration),
            ["CorrelationId"] = request.CorrelationId,
            ["AttemptCount"] = attemptCount,
            ["CreatedAt"] = request.Now,
            ["UpdatedAt"] = request.Now
        };

    private static string? ReadString(TableEntity entity, string name) =>
        entity.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static int? ReadInt(TableEntity entity, string name) =>
        entity.TryGetValue(name, out var value) && value is int intValue ? intValue : null;

    private static DateTimeOffset? ReadDateTimeOffset(TableEntity entity, string name) =>
        entity.TryGetValue(name, out var value) && value is DateTimeOffset dateTimeOffset ? dateTimeOffset : null;
}
