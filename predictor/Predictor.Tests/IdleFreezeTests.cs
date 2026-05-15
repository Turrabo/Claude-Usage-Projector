using System;
using ClaudeUsageProjector.Predictor.Activity;
using ClaudeUsageProjector.Predictor.Projection;
using ClaudeUsageProjector.Predictor.State;
using ClaudeUsageProjector.Predictor.Tiers;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class IdleFreezeTests
{
    private static UsageSnapshot Snap(DateTimeOffset at, double percent, DateTimeOffset? refreshAt = null) => new()
    {
        CapturedAtUtc = at,
        UsedPercent = percent,
        RefreshAtUtc = refreshAt,
    };

    [Fact]
    public void IdleMode_FreezesRateAtLastActiveValue()
    {
        // First call: Active mode with a clear rate. Predictor caches it.
        // Second call: Idle mode with snapshots that would otherwise compute near-zero.
        // The cached active rate should be used instead and RateFrozenFromIdle = true.
        var p = new Tier1WeightedBurnRate(new PredictorOptions(), new DeterministicProjectionEngine());
        var t0 = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = t0.AddHours(4);

        var activeSnaps = new[]
        {
            Snap(t0.AddMinutes(-10), 40, refreshAt),
            Snap(t0.AddMinutes(-5),  50, refreshAt),
            Snap(t0,                 60, refreshAt)
        };
        var active = new ActivitySignal(ActivityMode.Active, t0, 0.5, 1, 3);
        var first = p.Compute(activeSnaps, active, Array.Empty<TelemetryEvent>(), t0);
        first.WeightedBurnRate.Should().BeApproximately(2.0, 1e-6);
        first.RateFrozenFromIdle.Should().BeFalse();

        // 10 minutes later: flat snapshot (user idle, no consumption).
        var t1 = t0.AddMinutes(10);
        var idleSnaps = new[]
        {
            Snap(t0.AddMinutes(-10), 40, refreshAt),
            Snap(t0.AddMinutes(-5),  50, refreshAt),
            Snap(t0,                 60, refreshAt),
            Snap(t1.AddMinutes(-2),  60, refreshAt),
            Snap(t1,                 60, refreshAt)
        };
        var idle = new ActivitySignal(ActivityMode.Idle, t0, 10.0, 0, 0);
        var idleResult = p.Compute(idleSnaps, idle, Array.Empty<TelemetryEvent>(), t1);

        idleResult.RateFrozenFromIdle.Should().BeTrue();
        idleResult.WeightedBurnRate.Should().BeApproximately(2.0, 1e-6);
        idleResult.ActivityMode.Should().Be("Idle");
    }

    [Fact]
    public void ActiveMode_DoesNotFreeze_AlwaysUsesCurrentRate()
    {
        var p = new Tier1WeightedBurnRate(new PredictorOptions(), new DeterministicProjectionEngine());
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(4);
        var snapshots = new[]
        {
            Snap(now.AddMinutes(-5), 50, refreshAt),
            Snap(now,                52, refreshAt)
        };
        var active = new ActivitySignal(ActivityMode.Active, now, 0.5, 2, 5);

        var result = p.Compute(snapshots, active, Array.Empty<TelemetryEvent>(), now);

        result.WeightedBurnRate.Should().BeApproximately(0.4, 1e-6);
        result.RateFrozenFromIdle.Should().BeFalse();
        result.ActiveSessionCount.Should().Be(2);
    }

    [Fact]
    public void ActivitySignalExposed_InPredictionResult()
    {
        var p = new Tier1WeightedBurnRate(new PredictorOptions(), new DeterministicProjectionEngine());
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var refreshAt = now.AddHours(2);
        var snapshots = new[] { Snap(now, 50, refreshAt) };
        var activity = new ActivitySignal(ActivityMode.Active, now.AddMinutes(-1), 1.0, 3, 12);

        var result = p.Compute(snapshots, activity, Array.Empty<TelemetryEvent>(), now);

        result.ActivityMode.Should().Be("Active");
        result.ActiveSessionCount.Should().Be(3);
    }
}
