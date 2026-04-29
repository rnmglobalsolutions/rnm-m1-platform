using System.Text.Json;
using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.Domain.Tenancy;

namespace RNM.Platform.Infrastructure.Configuration;

public sealed class JsonVerticalConfigurationProvider : IVerticalConfigurationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string configRoot;
    private readonly IConfigurationValidator configurationValidator;

    public JsonVerticalConfigurationProvider(
        string configRoot,
        IConfigurationValidator configurationValidator)
    {
        this.configRoot = string.IsNullOrWhiteSpace(configRoot)
            ? throw new ArgumentException("Config root is required.", nameof(configRoot))
            : configRoot;
        this.configurationValidator = configurationValidator;
    }

    public async Task<VerticalConfiguration> GetVerticalConfigurationAsync(
        string verticalId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(verticalId))
        {
            throw new ConfigurationException("Vertical id is required.");
        }

        var path = Path.Combine(configRoot, "verticals", $"{verticalId}.json");
        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Vertical configuration '{verticalId}' was not found.");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<VerticalConfigurationDto>(json, JsonOptions)
            ?? throw new ConfigurationException($"Vertical configuration '{verticalId}' is empty or invalid JSON.");

        var configuration = dto.ToDomain();
        var validation = configurationValidator.ValidateVertical(configuration);
        if (!validation.IsValid)
        {
            throw new ConfigurationException(
                $"Vertical configuration '{verticalId}' is invalid: {string.Join(" ", validation.Errors)}");
        }

        return configuration;
    }

    private sealed record VerticalConfigurationDto(
        string? VerticalId,
        string? DisplayName,
        IReadOnlyCollection<string>? QualificationFields,
        IReadOnlyCollection<string>? SupportedCallTypes,
        ServiceAreaFieldAliasesDto? ServiceAreaFieldAliases)
    {
        public VerticalConfiguration ToDomain()
        {
            var defaultAliases = ServiceAreaFieldAliasConfiguration.Defaults();
            return new VerticalConfiguration(
                new VerticalId(VerticalId ?? string.Empty),
                DisplayName ?? string.Empty,
                QualificationFields ?? [],
                SupportedCallTypes ?? [],
                new ServiceAreaFieldAliasConfiguration(
                    ServiceAreaFieldAliases?.ZipCodeFields ?? defaultAliases.ZipCodeFields,
                    ServiceAreaFieldAliases?.AddressFields ?? defaultAliases.AddressFields));
        }
    }

    private sealed record ServiceAreaFieldAliasesDto(
        IReadOnlyCollection<string>? ZipCodeFields,
        IReadOnlyCollection<string>? AddressFields);
}
