using System;
using System.Linq;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.Random;

namespace ClaudeUsageProjector.Predictor.Projection;

/// <summary>
/// Forward simulation with rate uncertainty. Each minute samples
/// rate ~ N(μ, σ²) clamped to [0, ∞) and accumulates percent. After
/// NumSimulations runs, derives P50/P75/P90 empty times and the probability
/// of hitting 100% before refresh.
/// <para/>
/// Uses MathNet.Numerics for the RNG (Mersenne Twister) and Normal
/// distribution sampling — both well-tested. Seed is configurable for
/// deterministic test runs.
/// <para/>
/// References:
///   * Sheldon Ross, <i>Simulation</i>, 5th ed., Ch. 4 (Discrete-event Monte Carlo).
///   * MathNet.Numerics RNG validated against SciPy in MathNet's own test suite.
/// </summary>
public sealed class MonteCarloProjectionEngine : IProjectionEngine
{
    public const string Name = "monte-carlo";

    private const int DefaultNumSimulations = 1000;
    private const int DefaultMaxMinutes = 360;  // 6 hours — covers full session window
    private const double MinimumStdDev = 0.01;  // %/min; avoids degenerate σ=0 simulations

    private readonly int _numSimulations;
    private readonly int _maxMinutesIfNoRefresh;
    private readonly int? _seed;

    public MonteCarloProjectionEngine(int numSimulations = DefaultNumSimulations, int? seed = null,
                                      int maxMinutesIfNoRefresh = DefaultMaxMinutes)
    {
        _numSimulations = numSimulations;
        _maxMinutesIfNoRefresh = maxMinutesIfNoRefresh;
        _seed = seed;
    }

    public Projection Project(ProjectionInputs inputs)
    {
        if (inputs.CurrentPercent >= 100)
        {
            return new Projection(inputs.NowUtc, inputs.NowUtc, inputs.NowUtc, 1.0, 100.0, Name);
        }
        if (inputs.MeanRatePerMinute <= 0)
        {
            return new Projection(null, null, null, 0.0, inputs.CurrentPercent, Name);
        }

        var horizonMinutes = inputs.RefreshAtUtc.HasValue
            ? Math.Max(1, (int)Math.Ceiling((inputs.RefreshAtUtc.Value - inputs.NowUtc).TotalMinutes))
            : _maxMinutesIfNoRefresh;

        var sigma = Math.Max(MinimumStdDev, inputs.RateStdDevPerMinute);
        var rng = _seed.HasValue ? new MersenneTwister(_seed.Value) : new MersenneTwister();
        var rateDist = new Normal(inputs.MeanRatePerMinute, sigma, rng);

        var emptyMinutes = new int[_numSimulations];
        var finalPercents = new double[_numSimulations];
        int hitCount = 0;

        for (int i = 0; i < _numSimulations; i++)
        {
            double pct = inputs.CurrentPercent;
            int emptyAt = -1;
            for (int t = 1; t <= horizonMinutes; t++)
            {
                var rate = rateDist.Sample();
                if (rate < 0) rate = 0;
                pct += rate;
                if (pct >= 100.0 && emptyAt < 0)
                {
                    emptyAt = t;
                    pct = 100.0;
                    break;
                }
            }
            emptyMinutes[i] = emptyAt;
            finalPercents[i] = pct;
            if (emptyAt >= 0) hitCount++;
        }

        var probEmpty = hitCount / (double)_numSimulations;
        var expectedFinal = finalPercents.Average();

        var hitTimes = emptyMinutes.Where(m => m >= 0).OrderBy(m => m).ToArray();

        DateTimeOffset? p50 = PercentileAt(hitTimes, _numSimulations, 0.50, inputs.NowUtc);
        DateTimeOffset? p75 = PercentileAt(hitTimes, _numSimulations, 0.75, inputs.NowUtc);
        DateTimeOffset? p90 = PercentileAt(hitTimes, _numSimulations, 0.90, inputs.NowUtc);

        return new Projection(p50, p75, p90, probEmpty, expectedFinal, Name);
    }

    // The quantile is taken against the FULL simulation count, not just hit
    // count — so P50 is null when hitCount < 50% of simulations.
    private static DateTimeOffset? PercentileAt(int[] sortedHitMinutes, int totalSimulations, double quantile, DateTimeOffset now)
    {
        var requiredIndex = (int)Math.Ceiling(quantile * totalSimulations) - 1;
        if (requiredIndex < 0) requiredIndex = 0;
        if (requiredIndex >= sortedHitMinutes.Length) return null;
        var minutes = sortedHitMinutes[requiredIndex];
        return now.AddMinutes(minutes);
    }
}
