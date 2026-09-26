using System.Text.RegularExpressions;
using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;

namespace RNM.Platform.Application.LeadIntake;

/// <summary>
/// Effective classification policy for one tenant: platform defaults, then vertical, then tenant overrides.
/// </summary>
public sealed record ResolvedLeadClassification(
    string FunnelAttribute,
    IReadOnlyDictionary<string, LeadTierProfile> Tiers,
    LeadClassificationOutcome MissingFunnel,
    LeadClassificationOutcome UnknownFunnel,
    IReadOnlyDictionary<string, LeadFunnelRuleSet> Funnels,
    string Version);

public sealed record LeadClassificationResolution(
    ResolvedLeadClassification Policy,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Result of classifying one lead. On opt-out the tier the rules assigned is kept for reporting, while
/// classification, route and follow-up are overridden by the platform invariant.
/// </summary>
public sealed record LeadClassificationDecision(
    string Tier,
    string Classification,
    string Route,
    IReadOnlyList<string> Reasons,
    string RuleId,
    bool ScheduleFollowUp,
    string RulesetVersion)
{
    /// <summary>
    /// Declared funnel fields whose value is outside the declared catalog (names only, never values).
    /// </summary>
    public IReadOnlyList<string> UnexpectedAttributes { get; init; } = [];

    /// <summary>
    /// Writes this decision onto CRM contact attributes.
    /// </summary>
    public void WriteTo(IDictionary<string, string> attributes)
    {
        attributes[CrmContactAttributeNames.LeadClassification] = Classification;
        attributes[CrmContactAttributeNames.ClassificationReasons] = string.Join(",", Reasons);
        attributes[CrmContactAttributeNames.RecommendedRoute] = Route;
        attributes[CrmContactAttributeNames.LeadTemperature] = Tier;
        attributes[CrmContactAttributeNames.ClassificationRuleId] = RuleId;
        attributes[CrmContactAttributeNames.ClassificationRulesetVersion] = RulesetVersion;
    }
}

public static class LeadClassificationPolicy
{
    public const string OptOutRuleId = "platform.opt_out";
    public const string MissingFunnelRuleId = "platform.missing_funnel";
    public const string UnknownFunnelRuleId = "platform.unknown_funnel";
    public const int MaxFunnels = 20;
    public const int MaxRulesPerFunnel = 50;
    public const int MaxPredicatesPerCondition = 20;
    public const int MaxValuesPerPredicate = 50;
    public const int MaxValueLength = 128;
    public const int MaxTokenLength = 64;
    public const int MaxFieldsPerFunnel = 30;
    public const string PlatformDefaultVersion = "platform-default";
    private static readonly Regex SeparatorRuns = new(@"[\s\-_]+", RegexOptions.Compiled);

    /// <summary>
    /// Used when neither vertical nor tenant configures classification, and as the safe fallback
    /// when configuration cannot be loaded. It never routes to a vertical-specific destination.
    /// </summary>
    public static readonly ResolvedLeadClassification PlatformDefault = new(
        "funnelType",
        new Dictionary<string, LeadTierProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [LeadTiers.Cold] = new("follow_up", LeadRoutes.Nurture)
        },
        new LeadClassificationOutcome(LeadTiers.Cold, ["missing_funnel_type"]),
        new LeadClassificationOutcome(LeadTiers.Cold, ["unknown_funnel_type"]),
        new Dictionary<string, LeadFunnelRuleSet>(StringComparer.Ordinal),
        PlatformDefaultVersion);

    /// <summary>
    /// Canonical form used for every comparison: trimmed, lowercase, and runs of spaces, '-' or '_' collapsed to '_'
    /// (so "30-90 Days" matches "30_90_days"). It only widens matches; stored values are never rewritten.
    /// </summary>
    public static string Normalize(string value) =>
        SeparatorRuns.Replace(value.Trim().ToLowerInvariant(), "_");

    public static LeadClassificationResolution Resolve(
        LeadClassificationConfiguration? vertical,
        LeadClassificationConfiguration? tenant)
    {
        var errors = new List<string>();
        Validate(vertical, "vertical.leadClassification", errors);
        Validate(tenant, "tenant.leadClassification", errors);

        var tiers = new Dictionary<string, LeadTierProfile>(PlatformDefault.Tiers, StringComparer.OrdinalIgnoreCase);
        var funnels = new Dictionary<string, LeadFunnelRuleSet>(StringComparer.Ordinal);
        foreach (var (layer, path) in new[] { (vertical, "vertical"), (tenant, "tenant") })
        {
            foreach (var (tier, profile) in layer?.Tiers ?? new Dictionary<string, LeadTierProfile>())
            {
                tiers[tier] = profile;
            }

            // A tenant funnel replaces the vertical funnel entirely so its rule order stays predictable.
            var layerKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (funnel, ruleSet) in layer?.Funnels ?? new Dictionary<string, LeadFunnelRuleSet>())
            {
                var key = Normalize(funnel);
                if (!layerKeys.Add(key))
                {
                    errors.Add($"{path}.leadClassification.funnels has more than one funnel that normalizes to '{key}'.");
                }

                funnels[key] = ruleSet;
            }
        }

        var policy = new ResolvedLeadClassification(
            FirstNonEmpty(tenant?.FunnelAttribute, vertical?.FunnelAttribute) ?? PlatformDefault.FunnelAttribute,
            tiers,
            tenant?.MissingFunnel ?? vertical?.MissingFunnel ?? PlatformDefault.MissingFunnel,
            tenant?.UnknownFunnel ?? vertical?.UnknownFunnel ?? PlatformDefault.UnknownFunnel,
            funnels,
            ComposeVersion(vertical?.Version, tenant?.Version));

        foreach (var (path, outcome, ruleSet) in EnumerateOutcomes(policy))
        {
            if (outcome is not null && FindProfile(policy, ruleSet, outcome.Tier) is null)
            {
                errors.Add($"{path} uses tier '{outcome.Tier}', which has no classification/route profile.");
            }
        }

        return new LeadClassificationResolution(policy, errors);
    }

    public static LeadClassificationDecision Evaluate(
        ResolvedLeadClassification policy,
        IReadOnlyDictionary<string, string> attributes,
        string consentStatus)
    {
        var decision = EvaluateRules(policy, attributes);

        // Platform invariant, applied last so the tier survives for reporting: a person who opted out
        // gets no promotional route and never enters follow-up automation.
        if (string.Equals(consentStatus, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
            || Matches(attributes, "requestedNextStep", ["opted_out", "no_contact"])
            || Matches(attributes, "communicationOptOut", ["true", "yes"]))
        {
            return decision with
            {
                Classification = "opted_out",
                Route = LeadRoutes.None,
                Reasons = ["consent_not_available"],
                RuleId = OptOutRuleId,
                ScheduleFollowUp = false
            };
        }

        return decision;
    }

    private static LeadClassificationDecision EvaluateRules(
        ResolvedLeadClassification policy,
        IReadOnlyDictionary<string, string> attributes)
    {
        var funnel = GetAttribute(attributes, policy.FunnelAttribute);
        if (funnel is null)
        {
            return Decide(policy, policy.MissingFunnel, MissingFunnelRuleId);
        }

        var funnelKey = Normalize(funnel);
        if (!policy.Funnels.TryGetValue(funnelKey, out var ruleSet))
        {
            return Decide(policy, policy.UnknownFunnel, UnknownFunnelRuleId);
        }

        var matched = ruleSet.Rules.FirstOrDefault(rule => IsMatch(rule.When, attributes));
        var decision = matched is null
            ? Decide(policy, ruleSet.Fallback!, $"{funnelKey}.fallback", ruleSet)
            : Decide(policy, matched.Then!, matched.Id, ruleSet);
        return decision with { UnexpectedAttributes = FindUnexpectedAttributes(ruleSet, attributes) };
    }

    private static IReadOnlyList<string> FindUnexpectedAttributes(
        LeadFunnelRuleSet ruleSet,
        IReadOnlyDictionary<string, string> attributes)
    {
        if (ruleSet.Fields is null)
        {
            return [];
        }

        return ruleSet.Fields
            .Where(field => GetAttribute(attributes, field.Key) is not null && !Matches(attributes, field.Key, field.Value))
            .Select(field => field.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Checks that every route the policy can produce is actionable for this tenant:
    /// a ManyChat routing action when ManyChat is enabled, and class automation for <c>master_class</c>.
    /// </summary>
    public static IReadOnlyList<string> ValidateRouting(ResolvedLeadClassification policy, TenantConfiguration tenant)
    {
        var errors = new List<string>();
        var manyChat = tenant.Integrations?.ManyChat;
        foreach (var route in ReachableRoutes(policy))
        {
            if (route == LeadRoutes.None)
            {
                continue;
            }

            if (manyChat?.EffectiveEnabled is true && manyChat.RoutingActions?.For(route) is null)
            {
                errors.Add($"Route '{route}' has no integrations.manyChat.routingActions entry.");
            }

            if (route == LeadRoutes.MasterClass && tenant.Classes is null)
            {
                errors.Add($"Route '{route}' requires the tenant 'classes' configuration.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Routes the policy can actually produce: those of tiers used by an outcome, plus the opt-out route.
    /// </summary>
    public static IReadOnlyCollection<string> ReachableRoutes(ResolvedLeadClassification policy)
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal) { LeadRoutes.None };
        foreach (var (_, outcome, ruleSet) in EnumerateOutcomes(policy))
        {
            if (outcome is not null && FindProfile(policy, ruleSet, outcome.Tier) is { } profile)
            {
                routes.Add(profile.Route);
            }
        }

        return routes;
    }

    /// <summary>
    /// Structural validation of one configuration block. Cross-block checks (tier coverage) run in <see cref="Resolve"/>.
    /// </summary>
    public static void Validate(LeadClassificationConfiguration? configuration, string path, ICollection<string> errors)
    {
        if (configuration is null)
        {
            return;
        }

        if (configuration.FunnelAttribute is not null && !IsValidAttributeName(configuration.FunnelAttribute))
        {
            errors.Add($"{path}.funnelAttribute must be a valid attribute name.");
        }

        if (configuration.Version is not null && !IsValidRuleId(configuration.Version))
        {
            errors.Add($"{path}.version must be a token of letters, digits, '-', '_' or '.'.");
        }

        ValidateTierProfiles(configuration.Tiers, $"{path}.tiers", errors);

        ValidateOutcome(configuration.MissingFunnel, $"{path}.missingFunnel", errors, required: false);
        ValidateOutcome(configuration.UnknownFunnel, $"{path}.unknownFunnel", errors, required: false);

        if (configuration.Funnels.Count > MaxFunnels)
        {
            errors.Add($"{path}.funnels must not exceed {MaxFunnels} funnels.");
        }

        foreach (var (funnel, ruleSet) in configuration.Funnels)
        {
            ValidateFunnel(funnel, ruleSet, $"{path}.funnels.{funnel}", errors);
        }
    }

    private static void ValidateTierProfiles(
        IReadOnlyDictionary<string, LeadTierProfile> tiers,
        string path,
        ICollection<string> errors)
    {
        foreach (var (tier, profile) in tiers)
        {
            var tierPath = $"{path}.{tier}";
            if (!IsKnownTier(tier))
            {
                errors.Add($"{tierPath} is not a supported tier ({string.Join(", ", LeadTiers.All)}).");
            }

            if (!IsValidToken(profile.Classification))
            {
                errors.Add($"{tierPath}.classification must be a lowercase token.");
            }

            if (!LeadRoutes.IsValid(profile.Route))
            {
                errors.Add($"{tierPath}.route must be a lowercase token of letters, digits or '_'.");
            }
        }
    }

    private static void ValidateFunnel(string funnel, LeadFunnelRuleSet ruleSet, string path, ICollection<string> errors)
    {
        if (ruleSet.Tiers is not null)
        {
            ValidateTierProfiles(ruleSet.Tiers, $"{path}.tiers", errors);
        }

        if (string.IsNullOrWhiteSpace(funnel) || funnel.Length > MaxValueLength)
        {
            errors.Add($"{path} must have a funnel value of 1 to {MaxValueLength} characters.");
        }

        if (ruleSet.Rules.Count > MaxRulesPerFunnel)
        {
            errors.Add($"{path}.rules must not exceed {MaxRulesPerFunnel} rules.");
        }

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ruleSet.Rules.Count; index++)
        {
            var rule = ruleSet.Rules[index];
            var rulePath = $"{path}.rules[{index}]";
            if (!IsValidRuleId(rule.Id))
            {
                errors.Add($"{rulePath}.id must be a token of letters, digits, '-', '_' or '.'.");
            }
            else if (!ruleIds.Add(rule.Id))
            {
                errors.Add($"{rulePath}.id '{rule.Id}' is duplicated.");
            }

            ValidateCondition(rule.When, $"{rulePath}.when", errors);
            ValidateOutcome(rule.Then, $"{rulePath}.then", errors, required: true);
        }

        ValidateOutcome(ruleSet.Fallback, $"{path}.fallback", errors, required: true);
        ValidateFields(ruleSet, path, errors);
    }

    /// <summary>
    /// A declared field catalog must list every value its funnel's rules compare against,
    /// otherwise a rule could never match a value the catalog calls valid (or vice versa).
    /// </summary>
    private static void ValidateFields(LeadFunnelRuleSet ruleSet, string path, ICollection<string> errors)
    {
        if (ruleSet.Fields is null)
        {
            return;
        }

        if (ruleSet.Fields.Count > MaxFieldsPerFunnel)
        {
            errors.Add($"{path}.fields must not exceed {MaxFieldsPerFunnel} attributes.");
        }

        foreach (var (attribute, values) in ruleSet.Fields)
        {
            var fieldPath = $"{path}.fields.{attribute}";
            if (!IsValidAttributeName(attribute))
            {
                errors.Add($"{fieldPath} must be keyed by a valid attribute name.");
            }

            if (values.Count == 0
                || values.Count > MaxValuesPerPredicate
                || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength))
            {
                errors.Add($"{fieldPath} must list 1 to {MaxValuesPerPredicate} values of 1 to {MaxValueLength} characters.");
                continue;
            }

            var catalog = values.Select(Normalize).ToHashSet(StringComparer.Ordinal);
            var predicates = ruleSet.Rules.SelectMany(rule => rule.When.All.Concat(rule.When.Any));
            foreach (var predicate in predicates.Where(predicate =>
                         string.Equals(predicate.Attribute, attribute, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var value in (predicate.In ?? predicate.NotIn ?? []).Where(value => !catalog.Contains(Normalize(value))))
                {
                    errors.Add($"{fieldPath} does not list '{value}', which a rule compares against.");
                }
            }
        }
    }

    private static void ValidateCondition(LeadRuleCondition condition, string path, ICollection<string> errors)
    {
        if (condition.All.Count == 0 && condition.Any.Count == 0)
        {
            errors.Add($"{path} must include at least one predicate in 'all' or 'any'.");
        }

        if (condition.All.Count > MaxPredicatesPerCondition || condition.Any.Count > MaxPredicatesPerCondition)
        {
            errors.Add($"{path} must not exceed {MaxPredicatesPerCondition} predicates per group.");
        }

        for (var index = 0; index < condition.All.Count; index++)
        {
            ValidatePredicate(condition.All[index], $"{path}.all[{index}]", errors);
        }

        for (var index = 0; index < condition.Any.Count; index++)
        {
            ValidatePredicate(condition.Any[index], $"{path}.any[{index}]", errors);
        }
    }

    private static void ValidatePredicate(LeadRulePredicate predicate, string path, ICollection<string> errors)
    {
        var forms = (predicate.In is not null ? 1 : 0)
            + (predicate.NotIn is not null ? 1 : 0)
            + (predicate.Present is not null ? 1 : 0);
        if (forms != 1)
        {
            errors.Add($"{path} must use exactly one of 'in', 'notIn' or 'present'.");
            return;
        }

        if (predicate.Present is not null)
        {
            if (predicate.Attribute is not null)
            {
                errors.Add($"{path} must not combine 'present' with 'attribute'.");
            }

            if (!IsValidAttributeName(predicate.Present))
            {
                errors.Add($"{path}.present must be a valid attribute name.");
            }

            return;
        }

        if (predicate.Attribute is null || !IsValidAttributeName(predicate.Attribute))
        {
            errors.Add($"{path}.attribute must be a valid attribute name.");
        }

        var values = predicate.In ?? predicate.NotIn!;
        var valuesPath = predicate.In is not null ? $"{path}.in" : $"{path}.notIn";
        if (values.Count == 0 || values.Count > MaxValuesPerPredicate)
        {
            errors.Add($"{valuesPath} must include 1 to {MaxValuesPerPredicate} values.");
        }

        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength))
        {
            errors.Add($"{valuesPath} values must be 1 to {MaxValueLength} characters.");
        }
    }

    private static void ValidateOutcome(
        LeadClassificationOutcome? outcome,
        string path,
        ICollection<string> errors,
        bool required)
    {
        if (outcome is null)
        {
            if (required)
            {
                errors.Add($"{path} is required.");
            }

            return;
        }

        if (!IsKnownTier(outcome.Tier))
        {
            errors.Add($"{path}.tier must be one of: {string.Join(", ", LeadTiers.All)}.");
        }

        if (outcome.Reasons.Count == 0 || outcome.Reasons.Any(reason => !IsValidToken(reason)))
        {
            errors.Add($"{path}.reasons must include at least one lowercase token.");
        }
    }

    private static IEnumerable<(string Path, LeadClassificationOutcome? Outcome, LeadFunnelRuleSet? RuleSet)> EnumerateOutcomes(
        ResolvedLeadClassification policy)
    {
        yield return ("missingFunnel", policy.MissingFunnel, null);
        yield return ("unknownFunnel", policy.UnknownFunnel, null);
        foreach (var (funnel, ruleSet) in policy.Funnels)
        {
            foreach (var rule in ruleSet.Rules)
            {
                yield return ($"funnels.{funnel}.{rule.Id}", rule.Then, ruleSet);
            }

            yield return ($"funnels.{funnel}.fallback", ruleSet.Fallback, ruleSet);
        }
    }

    private static LeadTierProfile? FindProfile(ResolvedLeadClassification policy, LeadFunnelRuleSet? ruleSet, string tier) =>
        ruleSet?.Tiers is not null && ruleSet.Tiers.TryGetValue(tier, out var funnelProfile)
            ? funnelProfile
            : policy.Tiers.TryGetValue(tier, out var profile) ? profile : null;

    private static LeadClassificationDecision Decide(
        ResolvedLeadClassification policy,
        LeadClassificationOutcome outcome,
        string ruleId,
        LeadFunnelRuleSet? ruleSet = null)
    {
        var profile = FindProfile(policy, ruleSet, outcome.Tier)!;
        return new LeadClassificationDecision(
            outcome.Tier,
            profile.Classification,
            profile.Route,
            outcome.Reasons,
            ruleId,
            profile.EffectiveScheduleFollowUp,
            policy.Version);
    }

    private static bool IsMatch(LeadRuleCondition condition, IReadOnlyDictionary<string, string> attributes) =>
        condition.All.All(predicate => IsMatch(predicate, attributes))
        && (condition.Any.Count == 0 || condition.Any.Any(predicate => IsMatch(predicate, attributes)));

    private static bool IsMatch(LeadRulePredicate predicate, IReadOnlyDictionary<string, string> attributes)
    {
        if (predicate.Present is not null)
        {
            return GetAttribute(attributes, predicate.Present) is not null;
        }

        return predicate.In is not null
            ? Matches(attributes, predicate.Attribute!, predicate.In)
            : !Matches(attributes, predicate.Attribute!, predicate.NotIn!);
    }

    private static bool Matches(IReadOnlyDictionary<string, string> attributes, string name, IReadOnlyList<string> values)
    {
        var current = GetAttribute(attributes, name);
        if (current is null)
        {
            return false;
        }

        var normalized = Normalize(current);
        return values.Any(value => string.Equals(normalized, Normalize(value), StringComparison.Ordinal));
    }

    private static string ComposeVersion(string? verticalVersion, string? tenantVersion)
    {
        var parts = new[] { verticalVersion, tenantVersion }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();
        return parts.Length == 0 ? PlatformDefaultVersion : string.Join("+", parts);
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsKnownTier(string tier) => LeadTiers.All.Contains(tier, StringComparer.Ordinal);

    // Mirrors the attribute-name rule enforced on inbound lead attributes.
    private static bool IsValidAttributeName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxTokenLength
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');

    private static bool IsValidRuleId(string id) => IsValidAttributeName(id);

    private static bool IsValidToken(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxTokenLength
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_');
}
