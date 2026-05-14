using System;
using ClaudeUsageProjector.Predictor.Projection;
using FluentAssertions;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.Random;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class MonteCarloProjectionEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ZeroSigma_AllPercentilesNearMean()
    {
        // With σ=0 (truncated to MinimumStdDev=0.01), the simulations should converge tightly.
        // P50 should land within ~2 minutes of (100-50)/2 = 25 min.
        var e = new MonteCarloProjectionEngine(numSimulations: 1000, seed: 42);
        var p = e.Project(new ProjectionInputs(Now, Now.AddHours(2), 50, MeanRatePerMinute: 2.0, RateStdDevPerMinute: 0));

        p.P50EmptyAtUtc.Should().NotBeNull();
        var p50Min = (p.P50EmptyAtUtc!.Value - Now).TotalMinutes;
        p50Min.Should().BeApproximately(25.0, 2.0);
        p.ProbabilityEmptyBeforeRefresh.Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void HighSigma_P90LaterThanP50()
    {
        // High σ widens the distribution. P90 (90% of runs ran out by this time) should be
        // later than P50, because more runs took longer to hit 100%.
        var e = new MonteCarloProjectionEngine(numSimulations: 1000, seed: 123);
        var p = e.Project(new ProjectionInputs(Now, Now.AddHours(4), 50, MeanRatePerMinute: 1.0, RateStdDevPerMinute: 0.5));

        p.P50EmptyAtUtc.Should().NotBeNull();
        p.P75EmptyAtUtc.Should().NotBeNull();
        p.P90EmptyAtUtc.Should().NotBeNull();
        p.P75EmptyAtUtc!.Value.Should().BeOnOrAfter(p.P50EmptyAtUtc!.Value);
        p.P90EmptyAtUtc!.Value.Should().BeOnOrAfter(p.P75EmptyAtUtc!.Value);
    }

    [Fact]
    public void ProbabilityZero_WhenRateTooLowToFinish()
    {
        // 50% at 0.1%/min for 1 hour = +6% → finishes at 56%, never empty.
        var e = new MonteCarloProjectionEngine(numSimulations: 1000, seed: 7);
        var p = e.Project(new ProjectionInputs(Now, Now.AddHours(1), 50, MeanRatePerMinute: 0.1, RateStdDevPerMinute: 0.05));

        p.ProbabilityEmptyBeforeRefresh.Should().Be(0);
        p.P50EmptyAtUtc.Should().BeNull();
        p.ExpectedFinalPercent.Should().BeApproximately(56.0, 2.0);
    }

    [Fact]
    public void AlreadyAtCapacity_ImmediatelyEmpty()
    {
        var e = new MonteCarloProjectionEngine(numSimulations: 100, seed: 1);
        var p = e.Project(new ProjectionInputs(Now, Now.AddHours(2), 100, 1.0, 0.5));

        p.ProbabilityEmptyBeforeRefresh.Should().Be(1.0);
        p.P50EmptyAtUtc.Should().Be(Now);
    }

    [Fact]
    public void DeterministicWithSameSeed()
    {
        var e1 = new MonteCarloProjectionEngine(numSimulations: 500, seed: 999);
        var e2 = new MonteCarloProjectionEngine(numSimulations: 500, seed: 999);
        var inputs = new ProjectionInputs(Now, Now.AddHours(3), 60, 0.8, 0.3);

        var p1 = e1.Project(inputs);
        var p2 = e2.Project(inputs);

        p1.P50EmptyAtUtc.Should().Be(p2.P50EmptyAtUtc);
        p1.P75EmptyAtUtc.Should().Be(p2.P75EmptyAtUtc);
        p1.P90EmptyAtUtc.Should().Be(p2.P90EmptyAtUtc);
        p1.ProbabilityEmptyBeforeRefresh.Should().Be(p2.ProbabilityEmptyBeforeRefresh);
    }

    [Fact]
    public void ReferenceValidation_NormalDistributionMean()
    {
        // Sanity check: MathNet's Normal distribution behaves as documented. Anchors
        // the engine's correctness on the underlying RNG, which is what matters.
        var rng = new MersenneTwister(2026);
        var dist = new Normal(2.0, 0.5, rng);
        double sum = 0;
        const int n = 10000;
        for (int i = 0; i < n; i++) sum += dist.Sample();
        var empiricalMean = sum / n;
        empiricalMean.Should().BeApproximately(2.0, 0.05);
    }

    [Fact]
    public void ProbabilityRoughlyMatchesAnalyticalExpectation()
    {
        // Constant 0.5%/min, starting 70% → minutes-to-empty = 60.
        //   30-min horizon: P(empty before refresh) ≈ 0.
        //   90-min horizon: P(empty before refresh) ≈ 1.
        var e = new MonteCarloProjectionEngine(numSimulations: 500, seed: 5);

        var pShort = e.Project(new ProjectionInputs(Now, Now.AddMinutes(30), 70, 0.5, 0.05));
        var pLong  = e.Project(new ProjectionInputs(Now, Now.AddMinutes(90), 70, 0.5, 0.05));

        pShort.ProbabilityEmptyBeforeRefresh.Should().BeLessThan(0.05);
        pLong.ProbabilityEmptyBeforeRefresh.Should().BeGreaterThan(0.95);
    }
}
