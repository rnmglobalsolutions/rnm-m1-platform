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

        Assert.Equal(LeadTiers.Hot, decision.Tier);
        Assert.False(decision.ScheduleFollowUp);
        Assert.Equal("opted_out", decision.Classification);
        Assert.Equal(LeadRoutes.None, decision.Route);
        Assert.Equal(LeadClassificationPolicy.OptOutRuleId, decision.RuleId);
    }

    [Theory]
    [InlineData("  30-90 Days ", "30_90_days")]
    [InlineData("Master Class", "master_class")]
    [InlineData("under__30--days", "under_30_days")]
    [InlineData("consultation", "consultation")]
    public void Normalize_CanonicalizesCaseAndSeparators(string value, string expected) =>
        Assert.Equal(expected, LeadClassificationPolicy.Normalize(value));

    [Fact]
    public async Task Evaluate_NormalizedValueMatchesRule()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null).Policy;
        var lead = new Dictionary<string, string>(NearTermFinancialLead) { ["funnelType"] = "Financial Education", ["timeline"] = "30-90 days" };

        var decision = LeadClassificationPolicy.Evaluate(policy, lead, CrmConsentStatuses.OptIn);

        Assert.Equal("fe-near-term-with-goal", decision.RuleId);
        Assert.Equal(LeadTiers.Hot, decision.Tier);
        Assert.Empty(decision.UnexpectedAttributes);
    }

    [Fact]
    public async Task Evaluate_LifeInsuranceColdLead_GoesToMasterClass()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null).Policy;
        var lead = new Dictionary<string, string> { ["funnelType"] = "financial_education" };

        var decision = LeadClassificationPolicy.Evaluate(policy, lead, CrmConsentStatuses.OptIn);

        Assert.Equal(LeadTiers.Cold, decision.Tier);
        Assert.Equal(LeadRoutes.MasterClass, decision.Route);
        Assert.True(decision.ScheduleFollowUp);
        Assert.Equal("life-insurance-2026-09-26", decision.RulesetVersion);
    }

    [Fact]
    public async Task Evaluate_DisqualifiedLead_DoesNotScheduleFollowUp()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null).Policy;
        var lead = new Dictionary<string, string> { ["funnelType"] = "business_opportunity", ["incomeExpectation"] = "guaranteed_income" };

        var decision = LeadClassificationPolicy.Evaluate(policy, lead, CrmConsentStatuses.OptIn);

        Assert.Equal(LeadTiers.Disqualified, decision.Tier);
        Assert.Equal(LeadRoutes.None, decision.Route);
        Assert.False(decision.ScheduleFollowUp);
    }

    [Theory]
    [InlineData("requestedNextStep", "no_contact")]
    [InlineData("communicationOptOut", "Yes")]
    public async Task Evaluate_OptOutSignal_KeepsTierAndStopsFollowUp(string attribute, string value)
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null).Policy;
        var lead = new Dictionary<string, string>(NearTermFinancialLead) { [attribute] = value };

        var decision = LeadClassificationPolicy.Evaluate(policy, lead, CrmConsentStatuses.OptIn);

        Assert.Equal(LeadTiers.Hot, decision.Tier);
        Assert.Equal(LeadRoutes.None, decision.Route);
        Assert.Equal(LeadClassificationPolicy.OptOutRuleId, decision.RuleId);
        Assert.False(decision.ScheduleFollowUp);
    }

    [Fact]
    public async Task Classifier_ValueOutsideDeclaredCatalog_IsReportedByAttributeName()
    {
        var logger = new FakeEventLogger();
        var classifier = RepositoryConfiguration.Classifier(new FakeTenantConfigurationProvider(), logger);
        var lead = new Dictionary<string, string>(NearTermFinancialLead) { ["timeline"] = "someday-maybe" };

        var decision = await classifier.ClassifyAsync("tenant-a", "corr-1", lead, CrmConsentStatuses.OptIn, CancellationToken.None);

        Assert.Equal(["timeline"], decision.UnexpectedAttributes);
        Assert.Contains(logger.Events, item =>
            item.EventName == "lead_intake.classification.unexpected_value"
            && item.Properties.GetValueOrDefault("attributes") == "timeline"
            && !item.Properties.Values.Contains("someday-maybe"));
    }

    [Fact]
    public void Validate_CatalogMissingARuleValue_IsReported()
    {
        var configuration = Config(funnels: new()
        {
            ["financial_education"] = new(
                [new LeadClassificationRule(
                    "near-term",
                    new LeadRuleCondition([new LeadRulePredicate("timeline", ["this_week", "next_year"], null, null)], []),
                    new LeadClassificationOutcome(LeadTiers.Cold, ["near_term"]))],
                new LeadClassificationOutcome(LeadTiers.Cold, ["fallback"]),
                new Dictionary<string, IReadOnlyList<string>> { ["timeline"] = ["This Week"] })
        });
        var errors = new List<string>();

        LeadClassificationPolicy.Validate(configuration, "leadClassification", errors);

        Assert.Equal(["leadClassification.funnels.financial_education.fields.timeline does not list 'next_year', which a rule compares against."], errors);
    }

    [Fact]
    public void Resolve_FunnelsThatNormalizeToSameKey_AreReported()
    {
        var fallback = new LeadClassificationOutcome(LeadTiers.Cold, ["fallback"]);
        var tenant = Config(funnels: new()
        {
            ["financial_education"] = new([], fallback),
            ["Financial-Education"] = new([], fallback)
        });

        var resolution = LeadClassificationPolicy.Resolve(vertical: null, tenant);

        Assert.Contains(resolution.Errors, error => error.Contains("normalizes to 'financial_education'", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_WithoutConfiguration_UsesPlatformDefault()
    {
        var resolution = LeadClassificationPolicy.Resolve(vertical: null, tenant: null);
        var decision = LeadClassificationPolicy.Evaluate(resolution.Policy, NearTermFinancialLead, CrmConsentStatuses.OptIn);

        Assert.True(resolution.IsValid);
        Assert.Equal(LeadTiers.Cold, decision.Tier);
        Assert.Equal(LeadRoutes.Nurture, decision.Route);
        Assert.Equal(["unknown_funnel_type"], decision.Reasons);
        Assert.Equal(LeadClassificationPolicy.PlatformDefaultVersion, decision.RulesetVersion);
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
            tiers: new() { ["lukewarm"] = new("Bad Label", "Call Now") },
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
        Assert.Contains(errors, error => error.Contains("route must be a lowercase token", StringComparison.Ordinal));
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
            var routingErrors = LeadClassificationPolicy.ValidateRouting(resolution.Policy, tenant);

            Assert.True(resolution.IsValid, $"{tenantId}: {string.Join(" ", resolution.Errors)}");
            Assert.True(routingErrors.Count == 0, $"{tenantId}: {string.Join(" ", routingErrors)}");
        }
    }

    [Fact]
    public async Task ReachableRoutes_ListsOnlyRoutesTheLifeInsurancePolicyCanProduce()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null).Policy;

        Assert.Equal(
            [LeadRoutes.Consultation, LeadRoutes.MasterClass, LeadRoutes.None],
            LeadClassificationPolicy.ReachableRoutes(policy));
    }

    [Fact]
    public void ValidateRouting_CustomRouteWithoutManyChatAction_IsReported()
    {
        var tenant = Config(tiers: new() { [LeadTiers.Cold] = new("stay_in_touch", LeadRoutes.Nurture) });
        var policy = LeadClassificationPolicy.Resolve(vertical: null, tenant).Policy;

        var errors = LeadClassificationPolicy.ValidateRouting(policy, TenantWith(manyChatRoutes: [LeadRoutes.FollowUp]));

        Assert.Contains(errors, error => error.Contains("Route 'nurture' has no integrations.manyChat.routingActions entry", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRouting_CustomRouteWithManyChatAction_IsValid()
    {
        var tenant = Config(tiers: new() { [LeadTiers.Cold] = new("stay_in_touch", LeadRoutes.Nurture) });
        var policy = LeadClassificationPolicy.Resolve(vertical: null, tenant).Policy;

        var errors = LeadClassificationPolicy.ValidateRouting(policy, TenantWith(manyChatRoutes: [LeadRoutes.Nurture]));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ValidateRouting_MasterClassRouteWithoutClasses_IsReported()
    {
        var vertical = await RepositoryConfiguration.LoadVerticalAsync("life-insurance");
        var policy = LeadClassificationPolicy.Resolve(vertical.LeadClassification, tenant: null).Policy;

        var errors = LeadClassificationPolicy.ValidateRouting(policy, TenantWith(manyChatRoutes: null) with { Classes = null });

        Assert.Contains(errors, error => error.Contains("Route 'master_class' requires the tenant 'classes' configuration", StringComparison.Ordinal));
    }

    private static TenantConfiguration TenantWith(string[]? manyChatRoutes)
    {
        var tenant = new FakeTenantConfigurationProvider().GetTenantConfigurationAsync("tenant-a", CancellationToken.None).Result;
        return tenant with
        {
            Integrations = manyChatRoutes is null
                ? null
                : new IntegrationConfiguration(new ManyChatIntegrationConfiguration(
                    Enabled: true,
                    RoutingActions: new ManyChatRoutingActionsConfiguration(
                        manyChatRoutes.ToDictionary(route => route, _ => new ManyChatRoutingActionConfiguration("message", Message: "Hi")))))
        };
    }

    [Fact]
    public async Task Classifier_WhenConfigurationUnavailable_FallsBackAndStillHonorsOptOut()
    {
        var logger = new FakeEventLogger();
        var classifier = new LeadClassifier(new ThrowingTenantProvider(), RepositoryConfiguration.Verticals(), logger);

        var optedOut = await classifier.ClassifyAsync("tenant-a", "corr-1", NearTermFinancialLead, CrmConsentStatuses.OptedOut, CancellationToken.None);
        var optedIn = await classifier.ClassifyAsync("tenant-a", "corr-2", NearTermFinancialLead, CrmConsentStatuses.OptIn, CancellationToken.None);

        Assert.Equal(LeadRoutes.None, optedOut.Route);
        Assert.False(optedOut.ScheduleFollowUp);
        Assert.Equal(LeadTiers.Cold, optedIn.Tier);
        Assert.Equal(LeadRoutes.Nurture, optedIn.Route);
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
