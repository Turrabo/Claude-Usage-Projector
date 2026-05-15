using System;
using ClaudeUsageProjector.Predictor.Activity;
using ClaudeUsageProjector.Predictor.State;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class JsonlActivityDetectorTests
{
    private static TelemetryEvent Event(DateTimeOffset at, string? sessionId = "s1", string source = "jsonl") => new()
    {
        CapturedAtUtc = at,
        SourceId = source,
        EventType = "assistant_message",
        SessionId = sessionId
    };

    [Fact]
    public void NoTelemetry_ReturnsEmpty()
    {
        var d = new JsonlActivityDetector();
        var now = DateTimeOffset.UtcNow;

        var sig = d.Detect(Array.Empty<TelemetryEvent>(), now);

        sig.Mode.Should().Be(ActivityMode.Unknown);
        sig.LastActivityAtUtc.Should().BeNull();
        sig.ActiveSessionCount.Should().Be(0);
        sig.RecentEventCount.Should().Be(0);
    }

    [Fact]
    public void RecentEvent_WithinFiveMinutes_IsActive()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[] { Event(now.AddMinutes(-2)) };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Active);
        sig.MinutesSinceLastActivity.Should().BeApproximately(2.0, 1e-6);
        sig.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public void EventBetweenFiveAndThirtyMinutes_IsIdle()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[] { Event(now.AddMinutes(-15)) };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Idle);
        sig.ActiveSessionCount.Should().Be(0); // outside 10-min concurrency window
        sig.RecentEventCount.Should().Be(0);
    }

    [Fact]
    public void EventOlderThanIdleHorizon_IsUnknown()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[] { Event(now.AddMinutes(-45)) };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Unknown);
        sig.LastActivityAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void IgnoresNonJsonlSources()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Event(now.AddMinutes(-1), source: "hook"),
            Event(now.AddMinutes(-1), source: "otlp"),
        };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Unknown);
        sig.ActiveSessionCount.Should().Be(0);
    }

    [Fact]
    public void CountsDistinctSessionsWithinConcurrencyWindow()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Event(now.AddMinutes(-1), sessionId: "session-A"),
            Event(now.AddMinutes(-2), sessionId: "session-B"),
            Event(now.AddMinutes(-3), sessionId: "session-A"),
            Event(now.AddMinutes(-4), sessionId: "session-C"),
            Event(now.AddMinutes(-12), sessionId: "session-D"),
        };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Active);
        sig.ActiveSessionCount.Should().Be(3);
        sig.RecentEventCount.Should().Be(4);
    }

    [Fact]
    public void HandlesNullSessionIdGracefully()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Event(now.AddMinutes(-1), sessionId: null),
            Event(now.AddMinutes(-2), sessionId: "real-session"),
        };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Active);
        sig.ActiveSessionCount.Should().Be(1);
    }

    [Fact]
    public void FutureTimestamp_ClockSkew_TreatedAsActive()
    {
        var d = new JsonlActivityDetector();
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var events = new[] { Event(now.AddSeconds(1)) };

        var sig = d.Detect(events, now);

        sig.Mode.Should().Be(ActivityMode.Active);
    }
}
