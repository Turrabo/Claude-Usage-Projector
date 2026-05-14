using System;

namespace ClaudeUsageProjector.Predictor.Projection;

/// <summary>
/// Pure value object describing the inputs to a forward projection. Decoupled
/// from the rate-estimation pipeline so the engine can be tested against
/// synthetic inputs without requiring real snapshots.
/// </summary>
public sealed record ProjectionInputs(
    DateTimeOffset NowUtc,
    DateTimeOffset? RefreshAtUtc,
    double CurrentPercent,
    double MeanRatePerMinute,
    double RateStdDevPerMinute);
