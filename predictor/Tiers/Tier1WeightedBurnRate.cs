using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeUsageProjector.Predictor.Projection;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Tiers;

/// <summary>
/// Computes burn rate via Weighted Least Squares (WLS) regression with
/// exponential recency decay. Reset-aware: splits the snapshot series at large
/// downward jumps so a session reset never corrupts the regression. When the
/// current session is too short for WLS, the pre-reset session's rate is used
/// as a bootstrap prior. Falls back to a simple endpoint-weighted average when
/// all else fails.
/// <para/>
/// ProjectedEmptyAt = now + (100 - latestPercent) / wlsRate (only if rate &gt; 0).
/// Risk: high if percent ≥ highRiskPercent, projected before refresh, or
/// minutesUntilEmpty &lt; highRiskMinutes; medium if percent ≥ mediumRiskPercent or
/// projected within ±mediumRiskWindow of refresh; low otherwise; unknown if no
/// rate data or stale beyond unknown threshold.
/// Staleness 5..15 min downgrades one level; &gt;15 min forces unknown.
/// <para/>
/// Ported from CSM (commit history at C:\Source\Claude Session Monitor\). The
/// idle-freeze cache and Hawkes wire-up are deferred to Phase 3 when the JSONL
/// activity adapter lands; until then activity is always unknown so the freeze
/// branch never fires.
/// </summary>
public sealed class Tier1WeightedBurnRate
{
    private const double HalfLifeMinutes = 20.0;
    private const double MinSpanMinutes = 5.0;
    private const double ResetThresholdPercent = 10.0;

    private const double Weight5 = 0.5;
    private const double Weight15 = 0.3;
    private const double Weight30 = 0.2;

    private readonly PredictorOptions _options;
    private readonly IProjectionEngine _projection;

    public Tier1WeightedBurnRate(PredictorOptions options, IProjectionEngine projection)
    {
        _options = options;
        _projection = projection;
    }

    public Tier1WeightedBurnRate() : this(new PredictorOptions(), new DeterministicProjectionEngine()) { }

    public PredictionResult Compute(IReadOnlyList<UsageSnapshot> recentSnapshots, DateTimeOffset now)
    {
        var thresholds = _options.Thresholds;
        var staleness = _options.Staleness;

        var snaps = recentSnapshots
            .Where(s => s.UsedPercent.HasValue)
            .OrderBy(s => s.CapturedAtUtc)
            .ToList();

        if (snaps.Count == 0)
        {
            return new PredictionResult
            {
                ComputedAtUtc = now,
                Tier = ResolveTier(_projection),
                Risk = RiskLevel.Unknown,
                Stale = true,
                Reason = "No snapshots in window",
                Engine = _projection.GetType().Name
            };
        }

        var (currentSnaps, priorSnaps) = SplitAtLastReset(snaps);
        var latest = currentSnaps[^1];
        var ageMinutes = (now - latest.CapturedAtUtc).TotalMinutes;

        var rate5  = RateOverWindow(currentSnaps, latest, TimeSpan.FromMinutes(5));
        var rate15 = RateOverWindow(currentSnaps, latest, TimeSpan.FromMinutes(15));
        var rate30 = RateOverWindow(currentSnaps, latest, TimeSpan.FromMinutes(30));

        var wlsCurrent = ComputeWlsRate(currentSnaps, now);
        double? wlsPrior = null;
        string? priorReason = null;
        if (wlsCurrent is null && priorSnaps.Count >= 2)
        {
            var (priorCurrent, _) = SplitAtLastReset(priorSnaps);
            wlsPrior = ComputeWlsRate(priorCurrent, now);
            if (wlsPrior.HasValue)
                priorReason = "Rate seeded from prior session";
        }

        var weighted = wlsCurrent ?? wlsPrior ?? WeightedAverage(rate5, rate15, rate30);

        var sigmaEstimate = EstimateRateVolatility(currentSnaps, now);
        var sigma = sigmaEstimate ?? (weighted.HasValue ? weighted.Value * 0.5 : 0.0);

        Projection.Projection projection;
        if (weighted.HasValue && weighted.Value > 0 && latest.UsedPercent < 100)
        {
            projection = _projection.Project(new ProjectionInputs(
                NowUtc: now,
                RefreshAtUtc: latest.RefreshAtUtc,
                CurrentPercent: latest.UsedPercent!.Value,
                MeanRatePerMinute: weighted.Value,
                RateStdDevPerMinute: sigma));
        }
        else
        {
            projection = Projection.Projection.None(_projection.GetType().Name);
        }

        var projectedEmptyAt = projection.P50EmptyAtUtc;
        double? minutesUntilEmpty = projectedEmptyAt.HasValue
            ? (projectedEmptyAt.Value - now).TotalMinutes
            : null;

        var baseRisk = ClassifyRisk(
            usedPercent: latest.UsedPercent!.Value,
            refreshAt: latest.RefreshAtUtc,
            projectedEmptyAt: projectedEmptyAt,
            minutesUntilEmpty: minutesUntilEmpty,
            thresholds: thresholds);

        var stale = false;
        var risk = baseRisk;
        var reason = priorReason;

        if (ageMinutes > staleness.UnknownAfterMinutes)
        {
            risk = RiskLevel.Unknown;
            stale = true;
            reason = $"Latest snapshot {ageMinutes:F1}min old (> {staleness.UnknownAfterMinutes})";
        }
        else if (ageMinutes > staleness.DowngradeAfterMinutes)
        {
            risk = Downgrade(risk);
            stale = true;
            reason = $"Snapshot {ageMinutes:F1}min old — risk downgraded";
        }
        else if (!weighted.HasValue)
        {
            reason = "Insufficient rate data";
        }

        var projectedBeforeRefresh = latest.RefreshAtUtc.HasValue
            && projectedEmptyAt.HasValue
            && projectedEmptyAt.Value < latest.RefreshAtUtc.Value;

        return new PredictionResult
        {
            ComputedAtUtc = now,
            Tier = ResolveTier(_projection),
            Risk = risk,
            Stale = stale,
            Reason = reason,
            UsedPercent = latest.UsedPercent,
            RefreshAtUtc = latest.RefreshAtUtc,
            WeightedBurnRate = weighted,
            RateStdDev = sigmaEstimate,
            ProjectedEmptyP50AtUtc = projection.P50EmptyAtUtc,
            ProjectedEmptyP75AtUtc = projection.P75EmptyAtUtc,
            ProjectedEmptyP90AtUtc = projection.P90EmptyAtUtc,
            ProbabilityEmptyBeforeRefresh = projection.ProbabilityEmptyBeforeRefresh,
            ProjectedPercentAtRefresh = projection.ExpectedFinalPercent,
            ProjectedEmptyBeforeRefresh = projectedBeforeRefresh,
            Engine = projection.EngineName,
        };
    }

    private static int ResolveTier(IProjectionEngine engine) =>
        engine is MonteCarloProjectionEngine ? 2 : 1;

    // Finds the last reset (downward jump >= ResetThresholdPercent) and splits
    // the list there. Returns (current segment, prior segment). If no reset,
    // current = all, prior = empty.
    private static (List<UsageSnapshot> current, List<UsageSnapshot> prior) SplitAtLastReset(
        IReadOnlyList<UsageSnapshot> snapshots)
    {
        int resetIdx = -1;
        for (int i = 1; i < snapshots.Count; i++)
        {
            if (snapshots[i - 1].UsedPercent!.Value - snapshots[i].UsedPercent!.Value >= ResetThresholdPercent)
                resetIdx = i;
        }

        if (resetIdx < 0)
            return ([.. snapshots], []);

        return ([.. snapshots.Skip(resetIdx)], [.. snapshots.Take(resetIdx)]);
    }

    internal static double? EstimateRateVolatility(IReadOnlyList<UsageSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots.Count < 3) return null;

        var rates = new List<(double rate, double weight)>(snapshots.Count - 1);
        for (int i = 1; i < snapshots.Count; i++)
        {
            var dt = (snapshots[i].CapturedAtUtc - snapshots[i - 1].CapturedAtUtc).TotalMinutes;
            if (dt <= 0) continue;
            var dp = snapshots[i].UsedPercent!.Value - snapshots[i - 1].UsedPercent!.Value;
            var rate = dp / dt;
            var age = (now - snapshots[i].CapturedAtUtc).TotalMinutes;
            var w = Math.Exp(-age / HalfLifeMinutes);
            rates.Add((rate, w));
        }

        if (rates.Count < 2) return null;

        var totalWeight = rates.Sum(r => r.weight);
        if (totalWeight <= 0) return null;
        var weightedMean = rates.Sum(r => r.rate * r.weight) / totalWeight;
        var weightedVar = rates.Sum(r => r.weight * Math.Pow(r.rate - weightedMean, 2)) / totalWeight;
        return Math.Sqrt(Math.Max(0, weightedVar));
    }

    // Fits percent = a + b*t via WLS with exponential recency weights. Returns
    // the slope b (%/min), clamped to 0, or null when data is insufficient.
    private static double? ComputeWlsRate(IReadOnlyList<UsageSnapshot> snapshots, DateTimeOffset now)
    {
        if (snapshots.Count < 2) return null;

        var origin = snapshots[0].CapturedAtUtc;
        var span = (snapshots[^1].CapturedAtUtc - origin).TotalMinutes;
        if (span < MinSpanMinutes) return null;

        double S = 0, St = 0, Stt = 0, Sp = 0, Stp = 0;
        foreach (var snap in snapshots)
        {
            var t   = (snap.CapturedAtUtc - origin).TotalMinutes;
            var p   = snap.UsedPercent!.Value;
            var age = (now - snap.CapturedAtUtc).TotalMinutes;
            var w   = Math.Exp(-age / HalfLifeMinutes);
            S   += w;
            St  += w * t;
            Stt += w * t * t;
            Sp  += w * p;
            Stp += w * t * p;
        }

        var denom = S * Stt - St * St;
        if (Math.Abs(denom) < 1e-10) return null;

        var slope = (S * Stp - St * Sp) / denom;
        return slope < 0 ? 0 : slope;
    }

    private static double? RateOverWindow(IReadOnlyList<UsageSnapshot> snapshots, UsageSnapshot latest, TimeSpan window)
    {
        var cutoff = latest.CapturedAtUtc - window;
        var inWindow = snapshots.Where(s => s.CapturedAtUtc >= cutoff && s.UsedPercent.HasValue).ToList();
        if (inWindow.Count < 2) return null;

        var earliest = inWindow[0];
        var minutes = (latest.CapturedAtUtc - earliest.CapturedAtUtc).TotalMinutes;
        if (minutes <= 0) return null;

        var delta = latest.UsedPercent!.Value - earliest.UsedPercent!.Value;
        var rate = delta / minutes;
        return rate < 0 ? 0 : rate;
    }

    private static double? WeightedAverage(double? r5, double? r15, double? r30)
    {
        double sum = 0, weights = 0;
        if (r5.HasValue) { sum += r5.Value * Weight5; weights += Weight5; }
        if (r15.HasValue) { sum += r15.Value * Weight15; weights += Weight15; }
        if (r30.HasValue) { sum += r30.Value * Weight30; weights += Weight30; }
        return weights == 0 ? null : sum / weights;
    }

    private static RiskLevel ClassifyRisk(
        double usedPercent,
        DateTimeOffset? refreshAt,
        DateTimeOffset? projectedEmptyAt,
        double? minutesUntilEmpty,
        Thresholds thresholds)
    {
        if (usedPercent >= thresholds.HighRiskPercent) return RiskLevel.High;
        if (minutesUntilEmpty.HasValue && minutesUntilEmpty.Value < thresholds.HighRiskMinutesUntilEmpty)
            return RiskLevel.High;
        if (refreshAt.HasValue && projectedEmptyAt.HasValue &&
            projectedEmptyAt.Value < refreshAt.Value.AddMinutes(-thresholds.RefreshSafetyMinutes))
            return RiskLevel.High;

        if (usedPercent >= thresholds.MediumRiskPercent) return RiskLevel.Medium;
        if (minutesUntilEmpty.HasValue && minutesUntilEmpty.Value < thresholds.MediumRiskMinutesUntilEmpty)
            return RiskLevel.Medium;
        if (refreshAt.HasValue && projectedEmptyAt.HasValue)
        {
            var window = TimeSpan.FromMinutes(thresholds.MediumRiskWindowMinutes);
            var diff = (projectedEmptyAt.Value - refreshAt.Value).Duration();
            if (diff <= window) return RiskLevel.Medium;
        }

        return RiskLevel.Low;
    }

    private static RiskLevel Downgrade(RiskLevel r) => r switch
    {
        RiskLevel.High => RiskLevel.Medium,
        RiskLevel.Medium => RiskLevel.Low,
        RiskLevel.Low => RiskLevel.Unknown,
        _ => RiskLevel.Unknown
    };
}
