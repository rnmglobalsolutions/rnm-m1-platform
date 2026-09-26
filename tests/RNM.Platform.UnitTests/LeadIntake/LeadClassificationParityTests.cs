using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadIntake;
using Xunit;

namespace RNM.Platform.UnitTests.LeadIntake;

/// <summary>
/// Proves config/verticals/life-insurance.json reproduces the hardcoded rules it replaced, for every
/// combination of the known attribute values (plus casing, blanks and unknown values).
/// </summary>
public sealed class LeadClassificationParityTests
{
    private static readonly string?[] Consents = [CrmConsentStatuses.OptIn, CrmConsentStatuses.Unknown, CrmConsentStatuses.OptedOut];
    private static readonly string?[] RequestedNextSteps = [null, " ", "consultation", "CONSULTATION", "master_class", "intro_call", "follow_up", "opted_out", "no_contact", "other"];
    private static readonly string?[] Timelines = [null, "this_week", "under_30_days", "30_90_days", "over_90_days", "just_learning", "learning_only", "Under_30_Days", "later"];

    [Fact]
    public async Task FinancialEducation_MatchesLegacyRules()
    {
        var policy = await ResolveLifeInsuranceAsync();
        var cases = 0;
        foreach (var funnel in new[] { "financial_education", "FINANCIAL_EDUCATION" })
        foreach (var consent in Consents)
        foreach (var nextStep in RequestedNextSteps)
        foreach (var timeline in Timelines)
        foreach (var goal in new string?[] { null, "family_protection", "not_sure", "education", "" })
        foreach (var protection in new string?[] { null, "employer_only" })
        foreach (var optOut in new string?[] { null, "true", "yes", "no" })
        {
            AssertParity(policy, consent!, new()
            {
                ["funnelType"] = funnel,
                ["requestedNextStep"] = nextStep,
                ["timeline"] = timeline,
                ["primaryGoal"] = goal,
                ["currentProtection"] = protection,
                ["communicationOptOut"] = optOut
            });
            cases++;
        }

        Assert.True(cases > 10_000);
    }

    [Fact]
    public async Task BusinessOpportunity_MatchesLegacyRules()
    {
        var policy = await ResolveLifeInsuranceAsync();
        var cases = 0;
        foreach (var consent in Consents)
        foreach (var nextStep in RequestedNextSteps)
        foreach (var timeline in Timelines)
        foreach (var income in new string?[] { null, "guaranteed_income", "money_fast", "commission" })
        foreach (var license in new string?[] { null, "false", "no", "yes", "NO" })
        foreach (var availability in new string?[] { null, "less_than_5", "none", "5_10_hours" })
        foreach (var experience in new string?[] { null, "new_to_industry", "some_interest", "experienced" })
        {
            AssertParity(policy, consent!, new()
            {
                ["funnelType"] = "business_opportunity",
                ["requestedNextStep"] = nextStep,
                ["timeline"] = timeline,
                ["incomeExpectation"] = income,
                ["willingToLicense"] = license,
                ["weeklyAvailability"] = availability,
                ["experienceLevel"] = experience
            });
            cases++;
        }

        Assert.True(cases > 50_000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("real_estate")]
    [InlineData("financial education")]
    public async Task MissingOrUnknownFunnel_MatchesLegacyRules(string? funnel)
    {
        var policy = await ResolveLifeInsuranceAsync();
        foreach (var consent in Consents)
        foreach (var nextStep in RequestedNextSteps)
        {
            AssertParity(policy, consent!, new() { ["funnelType"] = funnel, ["requestedNextStep"] = nextStep });
        }
    }

    private static async Task<ResolvedLeadClassification> ResolveLifeInsuranceAsync()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var resolution = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null);
        Assert.True(resolution.IsValid, string.Join(" ", resolution.Errors));
        return resolution.Policy;
    }

    private static void AssertParity(
        ResolvedLeadClassification policy,
        string consent,
        Dictionary<string, string?> raw)
    {
        var attributes = raw
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);

        var expected = LegacyClassifier.Classify(attributes, consent);
        var actual = LeadClassificationPolicy.Evaluate(policy, attributes, consent);

        var context = $"consent={consent}; {string.Join("; ", attributes.Select(pair => $"{pair.Key}={pair.Value}"))}";
        Assert.True(expected.Classification == actual.Classification, $"classification {expected.Classification} != {actual.Classification} for {context}");
        Assert.True(expected.Route == actual.Route, $"route {expected.Route} != {actual.Route} for {context}");
        Assert.True(expected.Reasons.SequenceEqual(actual.Reasons), $"reasons [{string.Join(",", expected.Reasons)}] != [{string.Join(",", actual.Reasons)}] for {context}");
    }

    /// <summary>
    /// Verbatim copy of the hardcoded ClassifyLead logic removed from InboundLeadIntakeService (commit 571852f).
    /// It is the parity oracle only; do not extend it.
    /// </summary>
    private static class LegacyClassifier
    {
        public static (string Classification, string Route, IReadOnlyList<string> Reasons) Classify(
            IReadOnlyDictionary<string, string> attributes,
            string consent)
        {
            var reasons = new List<string>();
            if (string.Equals(consent, CrmConsentStatuses.OptedOut, StringComparison.OrdinalIgnoreCase)
                || IsAny(attributes, "requestedNextStep", "opted_out", "no_contact")
                || IsAny(attributes, "communicationOptOut", "true", "yes"))
            {
                return ("opted_out", "none", ["consent_not_available"]);
            }

            var funnelType = GetAttribute(attributes, "funnelType");
            if (string.IsNullOrWhiteSpace(funnelType))
            {
                return ("follow_up", "follow_up", ["missing_funnel_type"]);
            }

            if (string.Equals(funnelType, "financial_education", StringComparison.OrdinalIgnoreCase))
            {
                return FinancialEducation(attributes, reasons);
            }

            if (string.Equals(funnelType, "business_opportunity", StringComparison.OrdinalIgnoreCase))
            {
                return BusinessOpportunity(attributes, reasons);
            }

            return ("follow_up", "follow_up", ["unknown_funnel_type"]);
        }

        private static (string, string, IReadOnlyList<string>) FinancialEducation(
            IReadOnlyDictionary<string, string> attributes,
            List<string> reasons)
        {
            if (IsAny(attributes, "requestedNextStep", "consultation"))
            {
                reasons.Add("requested_consultation");
                return ("ready_for_consultation", "consultation", reasons);
            }

            if (IsAny(attributes, "requestedNextStep", "master_class"))
            {
                reasons.Add("requested_master_class");
                return ("education_needed", "master_class", reasons);
            }

            if (IsAny(attributes, "timeline", "this_week", "under_30_days", "30_90_days")
                && HasAnyAttribute(attributes, "primaryGoal", "currentProtection"))
            {
                reasons.Add("near_term_financial_timeline");
                reasons.Add("financial_goal_present");
                return ("ready_for_consultation", "consultation", reasons);
            }

            if (IsAny(attributes, "timeline", "learning_only", "just_learning", "over_90_days")
                || IsAny(attributes, "primaryGoal", "not_sure", "education"))
            {
                reasons.Add("education_or_longer_timeline");
                return ("education_needed", "master_class", reasons);
            }

            reasons.Add("financial_interest_needs_follow_up");
            return ("follow_up", "follow_up", reasons);
        }

        private static (string, string, IReadOnlyList<string>) BusinessOpportunity(
            IReadOnlyDictionary<string, string> attributes,
            List<string> reasons)
        {
            if (IsAny(attributes, "incomeExpectation", "guaranteed_income", "money_fast")
                || IsAny(attributes, "willingToLicense", "false", "no"))
            {
                reasons.Add("incompatible_business_expectation");
                return ("not_qualified", "none", reasons);
            }

            if (IsAny(attributes, "requestedNextStep", "consultation", "intro_call")
                && !IsAny(attributes, "weeklyAvailability", "less_than_5", "none"))
            {
                reasons.Add("requested_intro_call");
                reasons.Add("availability_present");
                return ("ready_for_consultation", "consultation", reasons);
            }

            if (IsAny(attributes, "requestedNextStep", "master_class")
                || IsAny(attributes, "experienceLevel", "new_to_industry", "some_interest"))
            {
                reasons.Add("business_education_needed");
                return ("education_needed", "master_class", reasons);
            }

            if (IsAny(attributes, "timeline", "this_week", "under_30_days")
                && !IsAny(attributes, "weeklyAvailability", "less_than_5", "none"))
            {
                reasons.Add("near_term_business_timeline");
                reasons.Add("availability_present");
                return ("ready_for_consultation", "consultation", reasons);
            }

            reasons.Add("business_interest_needs_follow_up");
            return ("follow_up", "follow_up", reasons);
        }

        private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
            attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        private static bool HasAnyAttribute(IReadOnlyDictionary<string, string> attributes, params string[] names) =>
            names.Any(name => GetAttribute(attributes, name) is not null);

        private static bool IsAny(IReadOnlyDictionary<string, string> attributes, string name, params string[] values)
        {
            var current = GetAttribute(attributes, name);
            return current is not null
                && values.Any(value => string.Equals(current, value, StringComparison.OrdinalIgnoreCase));
        }
    }
}
