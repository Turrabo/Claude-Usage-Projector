using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Activity;

/// <summary>
/// Derives an <see cref="ActivitySignal"/> from JSONL telemetry events.
/// Mode = Active when last event is within ActiveThresholdMinutes,
/// Idle within IdleThresholdMinutes, Unknown beyond that. Concurrency =
/// distinct SessionId values within ConcurrencyWindowMinutes. Stateless.
/// </summary>
public sealed class JsonlActivityDetector : IActivityDetector
{
    public const double ActiveThresholdMinutes = 5.0;
    public const double IdleThresholdMinutes = 30.0;
    public const double ConcurrencyWindowMinutes = 10.0;
    public const string JsonlSourceId = "jsonl";

    public ActivitySignal Detect(IReadOnlyList<TelemetryEvent> recentTelemetry, DateTimeOffset now)
    {
        if (recentTelemetry is null || recentTelemetry.Count == 0)
            return ActivitySignal.Empty;

        var jsonl = recentTelemetry
            .Where(e => string.Equals(e.SourceId, JsonlSourceId, StringComparison.Ordinal))
            .OrderByDescending(e => e.CapturedAtUtc)
            .ToList();

        if (jsonl.Count == 0) return ActivitySignal.Empty;

        var lastEvent = jsonl[0];
        var minutesSince = (now - lastEvent.CapturedAtUtc).TotalMinutes;

        var mode = minutesSince < 0
            ? ActivityMode.Active // event timestamp slightly in the future (clock skew) — treat as just-happened
            : minutesSince <= ActiveThresholdMinutes ? ActivityMode.Active
            : minutesSince <= IdleThresholdMinutes ? ActivityMode.Idle
            : ActivityMode.Unknown;

        var concurrencyCutoff = now - TimeSpan.FromMinutes(ConcurrencyWindowMinutes);
        var withinConcurrencyWindow = jsonl
            .Where(e => e.CapturedAtUtc >= concurrencyCutoff)
            .ToList();

        var sessionCount = withinConcurrencyWindow
            .Where(e => !string.IsNullOrEmpty(e.SessionId))
            .Select(e => e.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new ActivitySignal(
            Mode: mode,
            LastActivityAtUtc: lastEvent.CapturedAtUtc,
            MinutesSinceLastActivity: minutesSince,
            ActiveSessionCount: sessionCount,
            RecentEventCount: withinConcurrencyWindow.Count);
    }
}
