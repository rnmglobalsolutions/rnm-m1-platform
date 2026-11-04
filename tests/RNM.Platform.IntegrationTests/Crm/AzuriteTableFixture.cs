using Azure.Core;
using Azure.Data.Tables;
using RNM.Platform.Infrastructure.Crm;
using Xunit;

namespace RNM.Platform.IntegrationTests.Crm;

/// <summary>
/// Shared Azurite connection for tests that need real Table Storage behavior.
/// Fails (never skips) when Azurite is not reachable, so a CI gate cannot go green without running the tests.
/// Start Azurite locally with: azurite --silent --location &lt;dir&gt; --tableHost 127.0.0.1 --tablePort 10002
/// </summary>
public sealed class AzuriteTableFixture : IAsyncLifetime
{
    public const string ConnectionStringVariable = "RNM_TEST_AZURITE_CONNECTION_STRING";

    private const string DefaultConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";

    private static readonly string[] AdapterVariables =
    [
        "RNM_CRM_TABLE_STORAGE_CONNECTION_STRING",
        "RNM_CRM_CONTACTS_TABLE_NAME",
        "RNM_CRM_CONTACT_NOTES_TABLE_NAME",
        "RNM_CRM_BOOKINGS_TABLE_NAME",
        "RNM_CRM_BOOKING_LINKS_TABLE_NAME",
        "RNM_CRM_TIMELINE_TABLE_NAME",
        "RNM_CRM_CONTACT_PHONE_INDEX_TABLE_NAME"
    ];

    private readonly string runId = Guid.NewGuid().ToString("N")[..12];
    private readonly Dictionary<string, string> tableNames = new();
    private TableServiceClient? serviceClient;

    public string ConnectionString { get; private set; } = DefaultConnectionString;

    public string NewTenantId(string label) => $"azurite-{label}-{runId}-{Guid.NewGuid():N}"[..48];

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        ConnectionString = string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured;
        EnsureEmulatorOnly(ConnectionString);

        tableNames["RNM_CRM_CONTACTS_TABLE_NAME"] = $"TestContacts{runId}";
        tableNames["RNM_CRM_CONTACT_NOTES_TABLE_NAME"] = $"TestNotes{runId}";
        tableNames["RNM_CRM_BOOKINGS_TABLE_NAME"] = $"TestBookings{runId}";
        tableNames["RNM_CRM_TIMELINE_TABLE_NAME"] = $"TestTimeline{runId}";
        tableNames["RNM_CRM_CONTACT_PHONE_INDEX_TABLE_NAME"] = $"TestPhoneIndex{runId}";

        var options = new TableClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(3);
        options.Retry.Mode = RetryMode.Fixed;
        serviceClient = new TableServiceClient(ConnectionString, options);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await serviceClient.GetPropertiesAsync(cts.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Azurite Table service is not reachable. Start Azurite " +
                "(azurite --silent --location <dir> --tableHost 127.0.0.1 --tablePort 10002) " +
                $"or set {ConnectionStringVariable} to a local Azurite connection string. " +
                $"Reason: {exception.GetType().Name}.",
                exception);
        }
    }

    public async Task DisposeAsync()
    {
        if (serviceClient is null)
        {
            return;
        }

        foreach (var name in tableNames.Values)
        {
            try
            {
                await serviceClient.DeleteTableAsync(name);
            }
            catch
            {
                // Best-effort cleanup of this run's own uniquely named tables.
            }
        }
    }

    /// <summary>
    /// Builds a real <see cref="AzureTableCrmAdapter"/> against Azurite. The adapter reads its settings from
    /// environment variables in its constructor, so they are set only for the construction and then restored.
    /// </summary>
    public AzureTableCrmAdapter CreateAdapter()
    {
        var saved = AdapterVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in AdapterVariables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Environment.SetEnvironmentVariable("RNM_CRM_TABLE_STORAGE_CONNECTION_STRING", ConnectionString);
            foreach (var (name, value) in tableNames)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            return new AzureTableCrmAdapter();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static void EnsureEmulatorOnly(string connectionString)
    {
        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);

        var accountOk = parts.TryGetValue("AccountName", out var account)
            && string.Equals(account, "devstoreaccount1", StringComparison.Ordinal);
        var hostOk = parts.TryGetValue("TableEndpoint", out var endpoint)
            && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Host == "localhost");

        if (!accountOk || !hostOk)
        {
            throw new InvalidOperationException(
                $"{ConnectionStringVariable} must point to a local Azurite account (devstoreaccount1 on localhost). " +
                "Refusing to run tests against any other storage account.");
        }
    }
}
