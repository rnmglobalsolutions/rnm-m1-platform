using RNM.Platform.Application.Configuration;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Infrastructure.Configuration;

/// <summary>
/// JSON shape of the <c>leadClassification</c> block shared by vertical and tenant configuration files.
/// </summary>
internal sealed record LeadClassificationConfigurationDto(
    string? FunnelAttribute,
    IReadOnlyDictionary<string, LeadTierProfileDto?>? Tiers,
    LeadClassificationOutcomeDto? MissingFunnel,
    LeadClassificationOutcomeDto? UnknownFunnel,
    IReadOnlyDictionary<string, LeadFunnelRuleSetDto?>? Funnels,
    string? Version)
{
    public LeadClassificationConfiguration ToDomain() =>
        new(
            FunnelAttribute,
            ToCaseInsensitive(
                Tiers,
                "tiers",
                tier => new LeadTierProfile(tier?.Classification ?? string.Empty, tier?.Route ?? string.Empty, tier?.ScheduleFollowUp)),
            MissingFunnel?.ToDomain(),
            UnknownFunnel?.ToDomain(),
            ToCaseInsensitive(
                Funnels,
                "funnels",
                funnel => new LeadFunnelRuleSet(
                    (funnel?.Rules ?? []).Select(rule => rule.ToDomain()).ToArray(),
                    funnel?.Fallback?.ToDomain(),
                    funnel?.Fields is null
                        ? null
                        : new Dictionary<string, IReadOnlyList<string>>(funnel.Fields, StringComparer.OrdinalIgnoreCase))),
            Version);

    private static IReadOnlyDictionary<string, TDomain> ToCaseInsensitive<TDto, TDomain>(
        IReadOnlyDictionary<string, TDto>? source,
        string path,
        Func<TDto, TDomain> map)
    {
        var result = new Dictionary<string, TDomain>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source ?? new Dictionary<string, TDto>())
        {
            // Keys are matched case-insensitively at runtime, so keys differing only by case are ambiguous.
            if (!result.TryAdd(key, map(value)))
            {
                throw new ConfigurationException($"leadClassification.{path} has duplicate key '{key}'.");
            }
        }

        return result;
    }
}

internal sealed record LeadTierProfileDto(string? Classification, string? Route, bool? ScheduleFollowUp);

internal sealed record LeadFunnelRuleSetDto(
    IReadOnlyList<LeadClassificationRuleDto>? Rules,
    LeadClassificationOutcomeDto? Fallback,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Fields);

internal sealed record LeadClassificationRuleDto(
    string? Id,
    LeadRuleConditionDto? When,
    LeadClassificationOutcomeDto? Then)
{
    public LeadClassificationRule ToDomain() =>
        new(
            Id ?? string.Empty,
            new LeadRuleCondition(
                (When?.All ?? []).Select(predicate => predicate.ToDomain()).ToArray(),
                (When?.Any ?? []).Select(predicate => predicate.ToDomain()).ToArray()),
            Then?.ToDomain());
}

internal sealed record LeadRuleConditionDto(
    IReadOnlyList<LeadRulePredicateDto>? All,
    IReadOnlyList<LeadRulePredicateDto>? Any);

internal sealed record LeadRulePredicateDto(
    string? Attribute,
    IReadOnlyList<string>? In,
    IReadOnlyList<string>? NotIn,
    string? Present)
{
    public LeadRulePredicate ToDomain() => new(Attribute, In, NotIn, Present);
}

internal sealed record LeadClassificationOutcomeDto(
    string? Tier,
    IReadOnlyList<string>? Reasons)
{
    public LeadClassificationOutcome ToDomain() => new(Tier ?? string.Empty, Reasons ?? []);
}
