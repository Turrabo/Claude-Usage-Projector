using System;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Tiers;

/// <summary>
/// Internal predictor output, decoupled from the IPC wire format. The IPC
/// serialiser in Program.cs translates this into a PredictionMessage on
/// emission.
/// </summary>
public sealed record PredictionResult
{
    public required DateTimeOffset ComputedAtUtc { get; init; }
    public int Tier { get; init; }
    public RiskLevel Risk { get; init; }
    public bool Stale { get; init; }
    public string? Reason { get; init; }

    public double? UsedPercent { get; init; }
    public DateTimeOffset? RefreshAtUtc { get; init; }

    public double? WeightedBurnRate { get; init; }
    public double? RateStdDev { get; init; }

    public DateTimeOffset? ProjectedEmptyP50AtUtc { get; init; }
    public DateTimeOffset? ProjectedEmptyP75AtUtc { get; init; }
    public DateTimeOffset? ProjectedEmptyP90AtUtc { get; init; }
    public double ProbabilityEmptyBeforeRefresh { get; init; }
    public double? ProjectedPercentAtRefresh { get; init; }
    public bool ProjectedEmptyBeforeRefresh { get; init; }

    public string? Engine { get; init; }

    // Phase 3 fields. The activity signal feeds the idle-freeze branch in
    // Tier 1; the Hawkes ratio is currently diagnostic-only (recorded for
    // observation, not yet used to scale rates — see project memory).
    public string? ActivityMode { get; init; }
    public int? ActiveSessionCount { get; init; }
    public bool RateFrozenFromIdle { get; init; }

    public double? HawkesIntensityRatio { get; init; }
    public double? HawkesMu { get; init; }
    public double? HawkesAlpha { get; init; }
    public double? HawkesBeta { get; init; }
    public int? HawkesEventsConsidered { get; init; }
}
