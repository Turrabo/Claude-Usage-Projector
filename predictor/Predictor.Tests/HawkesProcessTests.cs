using System;
using System.Collections.Generic;
using ClaudeUsageProjector.Predictor.Hawkes;
using FluentAssertions;
using MathNet.Numerics.Random;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class HawkesProcessTests
{
    [Fact]
    public void Intensity_AtBaseline_WithNoEvents_EqualsMu()
    {
        var p = new HawkesParameters(Mu: 0.5, Alpha: 0.3, Beta: 1.0);
        new HawkesProcess(p).Intensity(t: 5.0, pastEvents: Array.Empty<double>()).Should().BeApproximately(0.5, 1e-12);
    }

    [Fact]
    public void Intensity_ImmediatelyAfterOneEvent_EqualsMuPlusAlpha()
    {
        var p = new HawkesParameters(Mu: 0.1, Alpha: 0.5, Beta: 1.0);
        var lambda = new HawkesProcess(p).Intensity(t: 1e-9, pastEvents: new[] { 0.0 });
        lambda.Should().BeApproximately(0.1 + 0.5, 1e-6);
    }

    [Fact]
    public void Intensity_DecaysExponentially()
    {
        var p = new HawkesParameters(Mu: 0.0, Alpha: 1.0, Beta: 1.0);
        var proc = new HawkesProcess(p);
        proc.Intensity(t: 1.0, pastEvents: new[] { 0.0 }).Should().BeApproximately(Math.Exp(-1), 1e-12);
        proc.Intensity(t: 2.0, pastEvents: new[] { 0.0 }).Should().BeApproximately(Math.Exp(-2), 1e-12);
    }

    [Fact]
    public void BranchingRatio_BoundsStationarity()
    {
        new HawkesParameters(0.1, 0.3, 1.0).IsStationary.Should().BeTrue();
        new HawkesParameters(0.1, 1.5, 1.0).IsStationary.Should().BeFalse();
        new HawkesParameters(0.1, 1.0, 1.0).IsStationary.Should().BeFalse();
    }

    [Fact]
    public void ExpectedEventCount_MatchesClosedForm()
    {
        var p = new HawkesParameters(Mu: 0.5, Alpha: 0.3, Beta: 1.0);
        p.ExpectedEventCount(10).Should().BeApproximately(0.5 * 10.0 / 0.7, 1e-9);
    }

    [Fact]
    public void Simulate_BaselinePoissonProcess_HasExpectedRate()
    {
        var p = new HawkesParameters(Mu: 0.2, Alpha: 0.0, Beta: 1.0);
        var proc = new HawkesProcess(p);
        var rng = new MersenneTwister(42);
        var output = new List<double>();
        proc.Simulate(0, 1000, Array.Empty<double>(), rng, output);
        output.Count.Should().BeInRange(170, 230);
        output.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Simulate_StationaryHawkes_HasCorrectMeanEventCount()
    {
        var p = new HawkesParameters(Mu: 0.5, Alpha: 0.3, Beta: 1.0);
        var proc = new HawkesProcess(p);
        var totalCount = 0;
        const int reps = 10;
        for (int seed = 0; seed < reps; seed++)
        {
            var rng = new MersenneTwister(seed);
            var output = new List<double>();
            proc.Simulate(0, 200, Array.Empty<double>(), rng, output);
            totalCount += output.Count;
        }
        var meanCount = totalCount / (double)reps;
        var expected = p.ExpectedEventCount(200);
        meanCount.Should().BeApproximately(expected, expected * 0.20);
    }

    [Fact]
    public void LogLikelihood_NoEvents_EqualsMinusMuT()
    {
        var p = new HawkesParameters(Mu: 0.5, Alpha: 0.3, Beta: 1.0);
        new HawkesProcess(p).LogLikelihood(Array.Empty<double>(), T: 10).Should().BeApproximately(-5.0, 1e-9);
    }

    [Fact]
    public void LogLikelihood_HigherForBetterFitParams()
    {
        var truth = new HawkesParameters(Mu: 0.5, Alpha: 0.3, Beta: 1.0);
        var rng = new MersenneTwister(7);
        var events = new List<double>();
        new HawkesProcess(truth).Simulate(0, 500, Array.Empty<double>(), rng, events);

        var llTruth = new HawkesProcess(truth).LogLikelihood(events, 500);
        var llBad = new HawkesProcess(new HawkesParameters(Mu: 5.0, Alpha: 0.0, Beta: 1.0)).LogLikelihood(events, 500);

        llTruth.Should().BeGreaterThan(llBad);
    }
}
