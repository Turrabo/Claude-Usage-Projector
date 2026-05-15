using System;
using System.Collections.Generic;

namespace ClaudeUsageProjector.Predictor.Hawkes;

/// <summary>
/// Univariate exponential-kernel Hawkes process. Pure numerical core — no I/O
/// or storage deps.
/// <para/>
/// References:
///   * Hawkes (1971) "Spectra of some self-exciting and mutually exciting point processes"
///   * Ogata (1981) "On Lewis' simulation method for point processes" — thinning algorithm
///   * Laub-Lee-Pollett (2015) "The Hawkes process: a tutorial" — closed-form log-likelihood (§3.4)
/// </summary>
public sealed class HawkesProcess
{
    public HawkesParameters Parameters { get; }

    public HawkesProcess(HawkesParameters parameters) => Parameters = parameters;

    /// <summary>
    /// Conditional intensity λ(t | history). Caller passes only events strictly
    /// before t. Linear in event count.
    /// </summary>
    public double Intensity(double t, IReadOnlyList<double> pastEvents)
    {
        double excitation = 0;
        for (int i = 0; i < pastEvents.Count; i++)
        {
            var dt = t - pastEvents[i];
            if (dt <= 0) continue;
            excitation += Parameters.Alpha * Math.Exp(-Parameters.Beta * dt);
        }
        return Parameters.Mu + excitation;
    }

    /// <summary>
    /// Simulates the process forward over [start, end] using Ogata's thinning
    /// algorithm. Adds simulated events (in absolute time) to <paramref name="output"/>.
    /// <paramref name="initialHistory"/> is treated as already-occurred events at
    /// times &lt; start and contributes excitation to the early simulation period.
    /// </summary>
    public void Simulate(
        double startTime,
        double endTime,
        IReadOnlyList<double> initialHistory,
        Random rng,
        List<double> output)
    {
        var events = new List<double>(initialHistory.Count + 16);
        events.AddRange(initialHistory);

        double t = startTime;
        while (t < endTime)
        {
            // Upper bound on intensity over the next instant: λ*(t) = λ(t)
            // since the kernel is decreasing.
            var lambdaStar = Intensity(t, events);
            if (lambdaStar <= 0) return;

            var u = rng.NextDouble();
            if (u <= 0) u = double.Epsilon;
            var dt = -Math.Log(u) / lambdaStar;
            t += dt;
            if (t >= endTime) return;

            var lambdaT = Intensity(t, events);
            var acceptU = rng.NextDouble();
            if (acceptU <= lambdaT / lambdaStar)
            {
                events.Add(t);
                output.Add(t);
            }
        }
    }

    /// <summary>
    /// Log-likelihood of an observed event sequence over [0, T] under these parameters.
    /// L = Σ log λ(tᵢ) - ∫₀ᵀ λ(s) ds.
    /// Closed-form integral via the recursive form (Laub 2015 §3.4):
    ///   ∫₀ᵀ λ(s)ds = μ·T + (α/β) · Σᵢ (1 - exp(-β·(T - tᵢ)))
    /// Sum-of-logs uses a recurrence A_i = (1 + A_{i-1}) · exp(-β·(t_i - t_{i-1}))
    /// avoiding O(n²) scaling.
    /// </summary>
    public double LogLikelihood(IReadOnlyList<double> events, double T)
    {
        if (T <= 0) return double.NegativeInfinity;
        var p = Parameters;
        if (p.Mu <= 0 || p.Alpha < 0 || p.Beta <= 0) return double.NegativeInfinity;

        double compensator = p.Mu * T;
        for (int i = 0; i < events.Count; i++)
        {
            compensator += (p.Alpha / p.Beta) * (1.0 - Math.Exp(-p.Beta * (T - events[i])));
        }

        double sumLog = 0;
        double a = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                a = Math.Exp(-p.Beta * (events[i] - events[i - 1])) * (1.0 + a);
            }
            var lambdaAt = p.Mu + p.Alpha * a;
            if (lambdaAt <= 0) return double.NegativeInfinity;
            sumLog += Math.Log(lambdaAt);
        }

        return sumLog - compensator;
    }
}
