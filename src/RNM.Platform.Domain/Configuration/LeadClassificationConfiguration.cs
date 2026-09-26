namespace RNM.Platform.Domain.Configuration;

/// <summary>
/// Lead classification rules. The vertical supplies defaults; a tenant block overrides tiers one by one
/// and replaces whole funnels. Nullable members exist so validation can report missing configuration.
/// </summary>
public sealed record LeadClassificationConfiguration(
    string? FunnelAttribute,
    IReadOnlyDictionary<string, LeadTierProfile> Tiers,
    LeadClassificationOutcome? MissingFunnel,
    LeadClassificationOutcome? UnknownFunnel,
    IReadOnlyDictionary<string, LeadFunnelRuleSet> Funnels);

/// <summary>
/// What a tier means for one vertical or tenant: the classification label stored on the lead and the route it takes.
/// </summary>
public sealed record LeadTierProfile(string Classification, string Route);

/// <summary>
/// Ordered rules for one funnel; the first matching rule wins, otherwise the fallback applies.
/// </summary>
public sealed record LeadFunnelRuleSet(
    IReadOnlyList<LeadClassificationRule> Rules,
    LeadClassificationOutcome? Fallback);

public sealed record LeadClassificationRule(
    string Id,
    LeadRuleCondition When,
    LeadClassificationOutcome? Then);

/// <summary>
/// Matches when every <see cref="All"/> predicate holds and, if <see cref="Any"/> is not empty, at least one of those holds.
/// </summary>
public sealed record LeadRuleCondition(
    IReadOnlyList<LeadRulePredicate> All,
    IReadOnlyList<LeadRulePredicate> Any);

/// <summary>
/// Exactly one form is valid: <c>attribute</c> + <c>in</c>, <c>attribute</c> + <c>notIn</c>, or <c>present</c>.
/// </summary>
public sealed record LeadRulePredicate(
    string? Attribute,
    IReadOnlyList<string>? In,
    IReadOnlyList<string>? NotIn,
    string? Present);

public sealed record LeadClassificationOutcome(
    string Tier,
    IReadOnlyList<string> Reasons);

public static class LeadTiers
{
    public const string Hot = "hot";
    public const string Warm = "warm";
    public const string Cold = "cold";
    public const string Disqualified = "disqualified";

    public static readonly IReadOnlyList<string> All = [Hot, Warm, Cold, Disqualified];
}

public static class LeadRoutes
{
    public const string Consultation = "consultation";
    public const string MasterClass = "master_class";
    public const string FollowUp = "follow_up";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Consultation, MasterClass, FollowUp, None];
}
