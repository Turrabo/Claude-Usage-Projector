using System;
using System.Collections.Generic;
using ClaudeUsageProjector.Predictor.Projection;
using ClaudeUsageProjector.Predictor.State;
using ClaudeUsageProjector.Predictor.Tiers;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class Tier1WeightedBurnRateTests
{
    private static Tier1WeightedBurnRate Predictor() =>
        new(new PredictorOptions(), new DeterministicProjectionEngine());

    private static UsageSnapshot Snap(DateTimeOffset at, double percent, DateTimeOffset? refreshAt = null) => new()
    {
        CapturedAtUtc = at,
        UsedPercent = percent,
        RefreshAtUtc = refreshAt,
    };

    [Fact]
    public void NoSnapshots_ReturnsUnknown()
    {
        var p = Predictor();
        var now = DateTimeOffset.UtcNow;
        var result = p.Compute(Array.Empty<UsageSnapshot>(), now);

        result.Risk.Should().Be(RiskLevel.Unknown);
        result.WeightedBurnRate.Should().BeNull();
        result.ProjectedEmptyP50AtUtc.Should().BeNull();
        result.Stale.Should().BeTrue();
    }

    [Fact]
    public void SingleSnapshot_HasPercentButNoRate_RiskFromPercent()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var snapshots = new[] { Snap(now, 80, now.AddHours(2)) };

        var result = p.Compute(snapshots, now);

        result.UsedPercent.Should().Be(80);
        result.WeightedBurnRate.Should().BeNull();
        result.Risk.Should().Be(RiskLevel.Medium); // 80 >= MediumRiskPercent (75)
        result.Reason.Should().Contain("Insufficient");
    }

    [Fact]
    public void TwoSnapshots_OverFiveMinutes_ComputesRateAndProjection()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(3);
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-5), 50, refreshAt),
            Snap(now,                  60, refreshAt)
        };

        var result = p.Compute(snapshots, now);

        result.WeightedBurnRate.Should().BeApproximately(2.0, 1e-6);
        result.ProjectedEmptyP50AtUtc.Should().NotBeNull();
        // 100-60 = 40, 40/2 = 20 minutes
        (result.ProjectedEmptyP50AtUtc!.Value - now).TotalMinutes.Should().BeApproximately(20, 1e-6);
        result.Risk.Should().Be(RiskLevel.High); // minutes-until-empty (20) < HighRiskMinutes (30)
    }

    [Fact]
    public void SessionReset_NoSpuriousRate_WhenOnlyOnePostResetPoint()
    {
        // A large drop (60→10) is detected as a reset. With only one post-reset point
        // and one pre-reset point, neither segment can produce a WLS rate — no
        // spurious cross-reset burn rate should appear.
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(2);
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-5), 60, refreshAt),
            Snap(now,                10, refreshAt)
        };

        var result = p.Compute(snapshots, now);

        result.WeightedBurnRate.Should().BeNull();
        result.ProjectedEmptyP50AtUtc.Should().BeNull();
    }

    [Fact]
    public void HighPercent_ForcesHighRisk_EvenIfNoRate()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var snapshots = new[] { Snap(now, 95, now.AddHours(2)) };

        var result = p.Compute(snapshots, now);

        result.Risk.Should().Be(RiskLevel.High);
    }

    [Fact]
    public void StaleSnapshot_DowngradesRisk()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 10, 0, TimeSpan.Zero);
        // Latest snapshot is 7 minutes old (between DowngradeAfter=5 and UnknownAfter=15)
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-12), 88, now.AddHours(1)),
            Snap(now.AddMinutes(-7),  92, now.AddHours(1))
        };

        var result = p.Compute(snapshots, now);

        result.Stale.Should().BeTrue();
        // Without staleness it would be High (>=90); downgraded once -> Medium
        result.Risk.Should().Be(RiskLevel.Medium);
    }

    [Fact]
    public void VeryStaleSnapshot_ForcesUnknown()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 30, 0, TimeSpan.Zero);
        var snapshots = new[] { Snap(now.AddMinutes(-20), 80, now.AddHours(2)) };

        var result = p.Compute(snapshots, now);

        result.Risk.Should().Be(RiskLevel.Unknown);
        result.Stale.Should().BeTrue();
    }

    [Fact]
    public void WeightedBlend_OnlyUsesPresentRates()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(2);
        // Two snapshots 30 minutes apart — WLS span is 30min, exceeds MinSpanMinutes (5).
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-30), 10, refreshAt),
            Snap(now,                  40, refreshAt)
        };

        var result = p.Compute(snapshots, now);

        // WLS rate over the full 30-min span = 1.0 %/min
        result.WeightedBurnRate.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void WlsRate_DampensBurstSpike_TowardSteadyRate()
    {
        // 20 steady points then a +4% burst at the endpoint. WLS should produce a rate
        // much closer to the steady 0.2%/min than to the burst's 2%/min.
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(4);

        var snaps = new List<UsageSnapshot>();
        for (int i = 0; i <= 20; i++)
        {
            var t = -120.0 + i * 6.0;
            snaps.Add(Snap(now.AddMinutes(t), 10 + 0.2 * (t + 120), refreshAt));
        }
        snaps[^1] = Snap(now, snaps[^2].UsedPercent!.Value + 4.0, refreshAt);

        var result = p.Compute(snaps, now);

        result.WeightedBurnRate.Should().NotBeNull();
        result.WeightedBurnRate!.Value.Should().BeLessThan(0.6);
        result.WeightedBurnRate!.Value.Should().BeGreaterThan(0.1);
    }

    [Fact]
    public void SessionReset_OnlyUsesPostResetSnapshots()
    {
        // Pre-reset: heavy burn to 85%. Reset: drops to 2%. Post-reset: 3 minutes of
        // light usage. The rate must come from the post-reset segment (with a
        // bootstrap from prior when post is too short), not a cross-reset slope.
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(4);

        var snapshots = new[]
        {
            Snap(now.AddMinutes(-35), 50, refreshAt),
            Snap(now.AddMinutes(-25), 60, refreshAt),
            Snap(now.AddMinutes(-15), 72, refreshAt),
            Snap(now.AddMinutes(-5),  85, refreshAt),
            Snap(now.AddMinutes(-3),   2, refreshAt),
            Snap(now.AddMinutes(-1),   3, refreshAt),
            Snap(now,                  4, refreshAt),
        };

        var result = p.Compute(snapshots, now);

        result.UsedPercent.Should().Be(4);
        result.WeightedBurnRate.Should().NotBeNull();
        result.WeightedBurnRate!.Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SessionReset_TooLittlePostResetData_UsesPriorRateAsBootstrap()
    {
        // Only one post-reset snapshot — WLS can't fit a line. Should fall back to
        // the pre-reset session's WLS rate as a prior.
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(4);

        var snapshots = new[]
        {
            Snap(now.AddMinutes(-32), 10, refreshAt),
            Snap(now.AddMinutes(-22), 15, refreshAt),
            Snap(now.AddMinutes(-12), 20, refreshAt),
            Snap(now.AddMinutes(-2),  25, refreshAt),
            Snap(now,                  1, refreshAt),
        };

        var result = p.Compute(snapshots, now);

        result.UsedPercent.Should().Be(1);
        result.WeightedBurnRate.Should().NotBeNull();
        result.WeightedBurnRate!.Value.Should().BeApproximately(0.5, 0.15);
        result.Reason.Should().Contain("prior session");
    }

    [Fact]
    public void ProjectedBeforeRefresh_ClassifiesHigh()
    {
        var p = Predictor();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(4);
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-5), 50, refreshAt),
            Snap(now,                  60, refreshAt)
        };

        var result = p.Compute(snapshots, now);

        result.Risk.Should().Be(RiskLevel.High);
        result.ProjectedEmptyBeforeRefresh.Should().BeTrue();
    }

    [Fact]
    public void Tier_IsOneForDeterministic_TwoForMonteCarlo()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(4);
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-5), 50, refreshAt),
            Snap(now,                  60, refreshAt)
        };

        var t1 = new Tier1WeightedBurnRate(new PredictorOptions(), new DeterministicProjectionEngine())
            .Compute(snapshots, now);
        var t2 = new Tier1WeightedBurnRate(new PredictorOptions(), new MonteCarloProjectionEngine(numSimulations: 100, seed: 1))
            .Compute(snapshots, now);

        t1.Tier.Should().Be(1);
        t2.Tier.Should().Be(2);
    }
}
