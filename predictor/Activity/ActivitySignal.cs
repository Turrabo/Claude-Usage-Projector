using System;

namespace ClaudeUsageProjector.Predictor.Activity;

/// <summary>
/// Pure value object describing the user's current Claude Code activity state.
/// Derived from JSONL telemetry; consumed by the predictor and surfaced in the
/// prediction message.
/// </summary>
public sealed record ActivitySignal(
    ActivityMode Mode,
    DateTimeOffset? LastActivityAtUtc,
    double? MinutesSinceLastActivity,
    int ActiveSessionCount,
    int RecentEventCount)
{
    public static ActivitySignal Empty { get; } = new(ActivityMode.Unknown, null, null, 0, 0);
}
