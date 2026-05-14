using System;

namespace ClaudeUsageProjector.Predictor.Projection;

/// <summary>
/// Single-point projection: minutesUntilEmpty = (100 - currentPercent) / meanRate.
/// All three percentiles return the same time. Used as a Tier 1 fallback when
/// the Monte Carlo engine isn't suitable (zero variance, zero rate, &gt;= 100%).
/// </summary>
public sealed class DeterministicProjectionEngine : IProjectionEngine
{
    public const string Name = "deterministic";

    public Projection Project(ProjectionInputs inputs)
    {
        if (inputs.MeanRatePerMinute <= 0 || inputs.CurrentPercent >= 100)
        {
            return new Projection(null, null, null, 0.0, inputs.CurrentPercent, Name);
        }

        var minutesLeft = (100 - inputs.CurrentPercent) / inputs.MeanRatePerMinute;
        var emptyAt = inputs.NowUtc.AddMinutes(minutesLeft);
        var probEmpty = inputs.RefreshAtUtc.HasValue && emptyAt < inputs.RefreshAtUtc.Value ? 1.0 : 0.0;

        double? finalPct = null;
        if (inputs.RefreshAtUtc.HasValue)
        {
            var minutesToRefresh = (inputs.RefreshAtUtc.Value - inputs.NowUtc).TotalMinutes;
            finalPct = Math.Min(100.0, inputs.CurrentPercent + inputs.MeanRatePerMinute * minutesToRefresh);
        }

        return new Projection(emptyAt, emptyAt, emptyAt, probEmpty, finalPct, Name);
    }
}
