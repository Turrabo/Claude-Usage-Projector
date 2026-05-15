using System;
using System.Collections.Generic;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Hawkes;

/// <summary>
/// Computes a current "burst intensity ratio" λ(now) / μ using a Hawkes
/// self-exciting model. Ratio &gt; 1 means recent events have boosted the
/// expected near-term arrival rate; ratio = 1 means the process has settled
/// to baseline.
/// </summary>
public interface IHawkesIntensityScaler
{
    HawkesIntensityResult ComputeRatio(IReadOnlyList<TelemetryEvent> recentTelemetry, DateTimeOffset now);
}

public sealed record HawkesIntensityResult(
    double IntensityRatio,
    double CurrentIntensity,
    double BaselineIntensity,
    HawkesParameters Parameters,
    int EventsConsidered);
