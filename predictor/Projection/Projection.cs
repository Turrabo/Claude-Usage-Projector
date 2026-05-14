using System;

namespace ClaudeUsageProjector.Predictor.Projection;

/// <summary>
/// Probabilistic projection of when the 5-hour bucket will hit 100% (or remain
/// below at refresh). All percentile times are UTC and may be null when the
/// engine determines it's overwhelmingly likely the user will not run out
/// (e.g. P90 is null if &lt;10% of simulations hit 100%).
/// </summary>
public sealed record Projection(
    DateTimeOffset? P50EmptyAtUtc,
    DateTimeOffset? P75EmptyAtUtc,
    DateTimeOffset? P90EmptyAtUtc,
    double ProbabilityEmptyBeforeRefresh,
    double? ExpectedFinalPercent,
    string EngineName)
{
    public static Projection None(string engineName) => new(null, null, null, 0.0, null, engineName);
}
