using System;
using System.Collections.Generic;
using ClaudeUsageProjector.Predictor.Hawkes;
using FluentAssertions;
using MathNet.Numerics.Random;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class HawkesParameterFitterTests
{
    [Fact]
    public void Fit_TooFewEvents_ReturnsNull()
    {
        var fitter = new HawkesParameterFitter();
        fitter.Fit(new[] { 1.0, 2.0 }, T: 10).Should().BeNull();
    }

    [Fact]
    public void Fit_ZeroDuration_ReturnsNull()
    {
        var fitter = new HawkesParameterFitter();
        fitter.Fit(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }, T: 0).Should().BeNull();
    }

    [Fact]
    public void Fit_RecoversParametersFromSyntheticData_WithinTolerance()
    {
        var truth = new HawkesParameters(Mu: 0.8, Alpha: 0.4, Beta: 1.2);
        var events = new List<double>();
        var rng = new MersenneTwister(2025);
        new HawkesProcess(truth).Simulate(0, 2000, Array.Empty<double>(), rng, events);

        events.Count.Should().BeGreaterThan(500);

        var fitter = new HawkesParameterFitter(maxIterations: 1000, tolerance: 1e-6);
        var fitted = fitter.Fit(events, T: 2000, initialGuess: new HawkesParameters(0.5, 0.3, 1.0));

        fitted.Should().NotBeNull();
        fitted!.Mu.Should().BeApproximately(truth.Mu, truth.Mu * 0.30);
        fitted.BranchingRatio.Should().BeApproximately(truth.BranchingRatio, 0.15);
    }

    [Fact]
    public void Fit_ReturnsStationaryParameters()
    {
        var truth = new HawkesParameters(Mu: 0.5, Alpha: 0.3, Beta: 1.0);
        var events = new List<double>();
        var rng = new MersenneTwister(11);
        new HawkesProcess(truth).Simulate(0, 1500, Array.Empty<double>(), rng, events);

        var fitter = new HawkesParameterFitter(maxIterations: 2000, tolerance: 1e-6);
        var fitted = fitter.Fit(events, T: 1500, initialGuess: new HawkesParameters(0.5, 0.3, 1.0));

        fitted.Should().NotBeNull();
        fitted!.IsStationary.Should().BeTrue();
        fitted.BranchingRatio.Should().BeLessThan(1.0);
    }
}
