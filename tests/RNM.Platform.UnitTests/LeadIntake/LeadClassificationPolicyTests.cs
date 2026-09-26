using RNM.Platform.Application.Configuration;
using RNM.Platform.Application.Crm;
using RNM.Platform.Application.LeadIntake;
using RNM.Platform.Domain.Configuration;
using RNM.Platform.UnitTests.Classes;
using Xunit;

namespace RNM.Platform.UnitTests.LeadIntake;

public sealed class LeadClassificationPolicyTests
{
    private static readonly Dictionary<string, string> NearTermFinancialLead = new(StringComparer.OrdinalIgnoreCase)
    {
        ["funnelType"] = "financial_education",
        ["timeline"] = "under_30_days",
        ["primaryGoal"] = "family_protection"
    };

    [Fact]
    public async Task Resolve_TenantTierOverride_ChangesOnlyThatTier()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var tenant = Config(tiers: new() { [LeadTiers.Hot] = new("priority_call", LeadRoutes.FollowUp) });

        var resolution = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant);
        var decision = LeadClassificationPolicy.Evaluate(resolution.Policy, NearTermFinancialLead, CrmConsentStatuses.OptIn);

        Assert.True(resolution.IsValid, string.Join(" ", resolution.Errors));
        Assert.Equal(LeadTiers.Hot, decision.Tier);
        Assert.Equal("priority_call", decision.Classification);
        Assert.Equal(LeadRoutes.FollowUp, decision.Route);
        Assert.Equal("education_needed", resolution.Policy.Tiers[LeadTiers.Warm].Classification);
    }

    [Fact]
    public async Task Resolve_TenantFunnel_ReplacesVerticalFunnelEntirely()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var tenant = Config(funnels: new()
        {
            ["financial_education"] = new(
                [Rule("tenant-any-goal", all: [Present("primaryGoal")], tier: LeadTiers.Warm, "tenant_goal")],
                new LeadClassificationOutcome(LeadTiers.Cold, ["tenant_fallback"]))
        });

        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant).Policy;
        var decision = LeadClassificationPolicy.Evaluate(policy, NearTermFinancialLead, CrmConsentStatuses.OptIn);

        Assert.Equal("tenant-any-goal", decision.RuleId);
        Assert.Equal(["tenant_goal"], decision.Reasons);
        Assert.True(policy.Funnels.ContainsKey("business_opportunity"));
    }

    [Fact]
    public void Evaluate_OptOutWinsOverEveryRule()
    {
        var vertical = Config(
            tiers: new() { [LeadTiers.Hot] = new("hot_lead", LeadRoutes.Consultation) },
            funnels: new()
            {
                ["financial_education"] = new(
                    [Rule("always-hot", all: [Present("funnelType")], tier: LeadTiers.Hot, "always")],
                    new LeadClassificationOutcome(LeadTiers.Cold, ["fallback"]))
            });
        var policy = LeadClassificationPolicy.Resolve(vertical, tenant: null).Policy;

        var decision = LeadClassificationPolicy.Evaluate(policy, NearTermFinancialLead, CrmConsentStatuses.OptedOut);

        Assert.Null(decision.Tier);
        Assert.Equal("opted_out", decision.Classification);
        Assert.Equal(LeadRoutes.None, decision.Route);
        Assert.Equal(LeadClassificationPolicy.OptOutRuleId, decision.RuleId);
    }

    [Fact]
    public void Resolve_WithoutConfiguration_UsesPlatformDefault()
    {
        var resolution = LeadClassificationPolicy.Resolve(vertical: null, tenant: null);
        var decision = LeadClassificationPolicy.Evaluate(resolution.Policy, NearTermFinancialLead, CrmConsentStatuses.OptIn);

        Assert.True(resolution.IsValid);
        Assert.Equal(LeadTiers.Cold, decision.Tier);
        Assert.Equal(LeadRoutes.FollowUp, decision.Route);
        Assert.Equal(["unknown_funnel_type"], decision.Reasons);
    }

    [Fact]
    public void Resolve_RuleUsingTierWithoutProfile_IsInvalid()
    {
        var tenant = Config(funnels: new()
        {
            ["financial_education"] = new(
                [Rule("needs-hot", all: [Present("primaryGoal")], tier: LeadTiers.Hot, "goal")],
                new LeadClassificationOutcome(LeadTiers.Cold, ["fallback"]))
        });

        var resolution = LeadClassificationPolicy.Resolve(vertical: null, tenant);

        Assert.False(resolution.IsValid);
        Assert.Contains(resolution.Errors, error => error.Contains("'hot'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReportsStructuralErrors()
    {
        var configuration = Config(
            tiers: new() { ["lukewarm"] = new("Bad Label", "call_now") },
            funnels: new()
            {
                ["financial_education"] = new(
                    [
                        new LeadClassificationRule("dup", new LeadRuleCondition([], []), new LeadClassificationOutcome(LeadTiers.Hot, ["ok"])),
                        new LeadClassificationRule(
                            "dup",
                            new LeadRuleCondition([new LeadRulePredicate("timeline", ["a"], ["b"], null)], []),
                            null)
                    ],
                    Fallback: null)
            });
        var errors = new List<string>();

        LeadClassificationPolicy.Validate(configuration, "leadClassification", errors);

        Assert.Contains(errors, error => error.Contains("tiers.lukewarm is not a supported tier", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("classification must be a lowercase token", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("route must be one of", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("at least one predicate", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("is duplicated", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("exactly one of 'in', 'notIn' or 'present'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("rules[1].then is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("fallback is required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckedInConfiguration_IsValidForEveryTenant()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(RepositoryConfiguration.ConfigRoot, "tenants"), "*.json"))
        {
            var tenantId = Path.GetFileNameWithoutExtension(path);
            var tenant = await new RNM.Platform.Infrastructure.Configuration.JsonTenantConfigurationProvider(
                    RepositoryConfiguration.ConfigRoot,
                    new ConfigurationValidator(),
                    allowWildcardServiceArea: true)
                .GetTenantConfigurationAsync(tenantId, CancellationToken.None);
            var vertical = await RepositoryConfiguration.LoadVerticalAsync(tenant.VerticalId.Value);

            var resolution = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant.LeadClassification);

            Assert.True(resolution.IsValid, $"{tenantId}: {string.Join(" ", resolution.Errors)}");
        }
    }

    [Fact]
    public async Task Classifier_WhenConfigurationUnavailable_FallsBackAndStillHonorsOptOut()
    {
        var logger = new FakeEventLogger();
        var classifier = new LeadClassifier(new ThrowingTenantProvider(), RepositoryConfiguration.Verticals(), logger);

        var optedOut = await classifier.ClassifyAsync("tenant-a", "corr-1", NearTermFinancialLead, CrmConsentStatuses.OptedOut, CancellationToken.None);
        var optedIn = await classifier.ClassifyAsync("tenant-a", "corr-2", NearTermFinancialLead, CrmConsentStatuses.OptIn, CancellationToken.None);

        Assert.Equal(LeadRoutes.None, optedOut.Route);
        Assert.Equal(LeadTiers.Cold, optedIn.Tier);
        Assert.Equal(LeadRoutes.FollowUp, optedIn.Route);
        Assert.Contains(logger.Events, item =>
            item.EventName == "lead_intake.classification.fallback"
            && item.Properties.GetValueOrDefault("reason") == "config_unavailable");
    }

    private static LeadClassificationConfiguration Config(
        Dictionary<string, LeadTierProfile>? tiers = null,
        Dictionary<string, LeadFunnelRuleSet>? funnels = null) =>
        new(
            FunnelAttribute: null,
            new Dictionary<string, LeadTierProfile>(tiers ?? [], StringComparer.OrdinalIgnoreCase),
            MissingFunnel: null,
            UnknownFunnel: null,
            new Dictionary<string, LeadFunnelRuleSet>(funnels ?? [], StringComparer.OrdinalIgnoreCase));

    private static LeadClassificationRule Rule(string id, LeadRulePredicate[] all, string tier, params string[] reasons) =>
        new(id, new LeadRuleCondition(all, []), new LeadClassificationOutcome(tier, reasons));

    private static LeadRulePredicate Present(string attribute) => new(null, null, null, attribute);

    private sealed class ThrowingTenantProvider : ITenantConfigurationProvider
    {
        public Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId, CancellationToken cancellationToken) =>
            throw new ConfigurationException("Tenant configuration was not found.");
    }
}
