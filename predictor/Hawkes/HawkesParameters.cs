using System;

namespace ClaudeUsageProjector.Predictor.Hawkes;

/// <summary>
/// Parameters of a univariate exponential-kernel Hawkes process:
///   λ(t) = μ + Σ_{tᵢ &lt; t} α · exp(-β · (t - tᵢ))
/// <para/>
/// μ (mu)    — baseline intensity (events per minute when no recent history).
/// α (alpha) — excitation: each past event boosts intensity by α at the moment it fires.
/// β (beta)  — decay rate: excitation half-life ≈ ln(2)/β minutes.
/// <para/>
/// The branching ratio is α/β. For the process to be stationary it must be &lt; 1.
/// Default (μ=0.1, α=0.3, β=0.1/min) reflects literature priors for human-paced
/// events (Laub-Lee-Pollett 2015, "The Hawkes process: a tutorial" §6).
/// </summary>
public sealed record HawkesParameters(double Mu, double Alpha, double Beta)
{
    public static HawkesParameters Default { get; } = new(Mu: 0.1, Alpha: 0.3, Beta: 0.1);

    public double BranchingRatio => Beta <= 0 ? double.PositiveInfinity : Alpha / Beta;

    public bool IsStationary => BranchingRatio < 1.0 && Mu >= 0 && Alpha >= 0 && Beta > 0;

    /// <summary>
    /// Expected number of events in [0, T] for a stationary process starting from quiescence.
    /// E[N(T)] = μ·T / (1 - α/β).  See Hawkes 1971 §3.
    /// </summary>
    public double ExpectedEventCount(double durationMinutes)
    {
        if (!IsStationary || durationMinutes <= 0) return 0;
        return Mu * durationMinutes / (1.0 - BranchingRatio);
    }
}
