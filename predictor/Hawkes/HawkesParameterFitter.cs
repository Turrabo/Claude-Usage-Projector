using System;
using System.Collections.Generic;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace ClaudeUsageProjector.Predictor.Hawkes;

/// <summary>
/// Maximum-likelihood estimation of (μ, α, β) given an observed event
/// sequence over [0, T].
/// <para/>
/// Optimisation: Nelder-Mead simplex (derivative-free) on the unconstrained
/// log-parameter space (we optimise log μ, log α, log β so the simplex never
/// proposes negative values, and we penalise non-stationary candidates α/β ≥ 1).
/// </summary>
public sealed class HawkesParameterFitter
{
    private const double StationarityCutoff = 0.999;
    private const double LargePenalty = 1e9;
    private const int MinEventsToFit = 5;

    private readonly int _maxIterations;
    private readonly double _tolerance;

    public HawkesParameterFitter(int maxIterations = 500, double tolerance = 1e-5)
    {
        _maxIterations = maxIterations;
        _tolerance = tolerance;
    }

    /// <summary>
    /// Fits parameters by maximising log-likelihood. Returns null if the fit
    /// fails to converge or the data is too sparse.
    /// </summary>
    public HawkesParameters? Fit(IReadOnlyList<double> events, double T, HawkesParameters? initialGuess = null)
    {
        if (events.Count < MinEventsToFit || T <= 0) return null;

        var start = initialGuess ?? HawkesParameters.Default;

        var logStart = Vector<double>.Build.Dense(new[]
        {
            Math.Log(Math.Max(1e-6, start.Mu)),
            Math.Log(Math.Max(1e-6, start.Alpha)),
            Math.Log(Math.Max(1e-6, start.Beta))
        });

        var objective = ObjectiveFunction.Value(v =>
        {
            var p = new HawkesParameters(Math.Exp(v[0]), Math.Exp(v[1]), Math.Exp(v[2]));
            if (p.BranchingRatio >= StationarityCutoff) return LargePenalty;
            var logLik = new HawkesProcess(p).LogLikelihood(events, T);
            return double.IsFinite(logLik) ? -logLik : LargePenalty;
        });

        try
        {
            var solver = new NelderMeadSimplex(_tolerance, _maxIterations);
            var result = solver.FindMinimum(objective, logStart);
            var v = result.MinimizingPoint;
            var fitted = new HawkesParameters(Math.Exp(v[0]), Math.Exp(v[1]), Math.Exp(v[2]));
            return fitted.IsStationary ? fitted : null;
        }
        catch (MaximumIterationsException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
