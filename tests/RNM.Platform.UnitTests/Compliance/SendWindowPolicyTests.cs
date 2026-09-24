using RNM.Platform.Application.Compliance;
using RNM.Platform.Application.Crm;
using RNM.Platform.Domain.Configuration;
using Xunit;

namespace RNM.Platform.UnitTests.Compliance;

public sealed class SendWindowPolicyTests
{
    private readonly SendWindowPolicy policy = new();

    [Fact]
    public void Evaluate_ReturnsAllowedInsideWindow()
    {
        var result = policy.Evaluate(
            CreateContact(),
            "America/Chicago",
            new TcpaWindowConfiguration(8, 21),
            new DateTimeOffset(2026, 7, 5, 15, 0, 0, TimeSpan.Zero));

        Assert.True(result.IsAllowed);
        Assert.Equal("America/Chicago", result.TimeZoneId);
        Assert.Equal("tenant", result.TimeZoneBasis);
        Assert.Equal(10, result.LocalHour);
        Assert.Null(result.NextAllowedAt);
    }

    [Fact]
    public void Evaluate_ReturnsBlockedBeforeWindowWithNextAllowedTime()
    {
        var result = policy.Evaluate(
            CreateContact(),
            "America/Chicago",
            new TcpaWindowConfiguration(8, 21),
            new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.False(result.IsAllowed);
        Assert.Equal(7, result.LocalHour);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 13, 0, 0, TimeSpan.Zero), result.NextAllowedAt);
    }

    [Fact]
    public void Evaluate_ReturnsBlockedAfterWindowWithNextMorning()
    {
        var result = policy.Evaluate(
            CreateContact(),
            "America/Chicago",
            new TcpaWindowConfiguration(8, 21),
            new DateTimeOffset(2026, 7, 6, 3, 0, 0, TimeSpan.Zero));

        Assert.False(result.IsAllowed);
        Assert.Equal(22, result.LocalHour);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 13, 0, 0, TimeSpan.Zero), result.NextAllowedAt);
    }

    [Fact]
    public void Evaluate_UsesTenantTimezoneWhenLeadTimezoneAttributeIsMissing()
    {
        var result = policy.Evaluate(
            CreateContact(),
            "America/Chicago",
            null,
            new DateTimeOffset(2026, 7, 5, 15, 0, 0, TimeSpan.Zero));

        Assert.True(result.IsAllowed);
        Assert.Equal("tenant", result.TimeZoneBasis);
        Assert.Equal("America/Chicago", result.TimeZoneId);
    }

    [Fact]
    public void Evaluate_UsesLeadTimezoneWhenProvidedBySupportedAttribute()
    {
        var result = policy.Evaluate(
            CreateContact(new Dictionary<string, string>
            {
                ["leadTimezone"] = "America/New_York"
            }),
            "America/Chicago",
            null,
            new DateTimeOffset(2026, 7, 5, 12, 30, 0, TimeSpan.Zero));

        Assert.True(result.IsAllowed);
        Assert.Equal("lead", result.TimeZoneBasis);
        Assert.Equal("America/New_York", result.TimeZoneId);
        Assert.Equal(8, result.LocalHour);
    }

    [Fact]
    public void Evaluate_HandlesDstTransitionDay()
    {
        var result = policy.Evaluate(
            CreateContact(),
            "America/Chicago",
            new TcpaWindowConfiguration(8, 21),
            new DateTimeOffset(2026, 3, 8, 13, 30, 0, TimeSpan.Zero));

        Assert.True(result.IsAllowed);
        Assert.Equal(8, result.LocalHour);
    }

    private static CrmContactRecord CreateContact(IReadOnlyDictionary<string, string>? attributes = null) =>
        new(
            "tenant-a",
            "contact-a",
            "+15551234567",
            "lead@example.com",
            "Lead One",
            null,
            attributes ?? new Dictionary<string, string>());
}
