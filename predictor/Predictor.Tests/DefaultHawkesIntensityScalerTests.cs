using System;
using ClaudeUsageProjector.Predictor.Hawkes;
using ClaudeUsageProjector.Predictor.State;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class DefaultHawkesIntensityScalerTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryEvent Event(DateTimeOffset at, string source = "jsonl") => new()
    {
        CapturedAtUtc = at,
        SourceId = source,
        EventType = "assistant_message",
        SessionId = "s1"
    };

    [Fact]
    public void NoEvents_RatioIsOne_AtBaseline()
    {
        var scaler = new DefaultHawkesIntensityScaler();
        var result = scaler.ComputeRatio(Array.Empty<TelemetryEvent>(), Now);

        result.IntensityRatio.Should().BeApproximately(1.0, 1e-9);
        result.EventsConsidered.Should().Be(0);
    }

    [Fact]
    public void RecentBurst_ProducesRatioGreaterThanOne()
    {
        var scaler = new DefaultHawkesIntensityScaler();
        scaler.Reset();
        var events = new[]
        {
            Event(Now.AddMinutes(-2)),
            Event(Now.AddMinutes(-1.5)),
            Event(Now.AddMinutes(-1)),
            Event(Now.AddMinutes(-0.5)),
            Event(Now.AddMinutes(-0.1))
        };

        var result = scaler.ComputeRatio(events, Now);

        result.IntensityRatio.Should().BeGreaterThan(1.0);
        result.EventsConsidered.Should().Be(5);
    }

    [Fact]
    public void OldEvents_OutsideWindow_AreIgnored()
    {
        var scaler = new DefaultHawkesIntensityScaler();
        scaler.Reset();
        var events = new[]
        {
            Event(Now.AddMinutes(-200)),
            Event(Now.AddMinutes(-150)),
            Event(Now.AddMinutes(-120))
        };

        var result = scaler.ComputeRatio(events, Now);

        result.IntensityRatio.Should().BeApproximately(1.0, 1e-9);
        result.EventsConsidered.Should().Be(0);
    }

    [Fact]
    public void NonJsonlEvents_AreIgnored()
    {
        var scaler = new DefaultHawkesIntensityScaler();
        scaler.Reset();
        var events = new[]
        {
            Event(Now.AddMinutes(-1), source: "hook"),
            Event(Now.AddMinutes(-0.5), source: "otlp")
        };

        var result = scaler.ComputeRatio(events, Now);

        result.EventsConsidered.Should().Be(0);
        result.IntensityRatio.Should().BeApproximately(1.0, 1e-9);
    }
}
