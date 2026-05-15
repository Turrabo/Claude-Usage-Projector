using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Hawkes;

/// <summary>
/// Computes Hawkes intensity ratio using literature-prior parameters by
/// default, refitting from observed data when enough events are available.
/// Refit is rate-limited to avoid re-running optimisation on every prediction
/// tick. "JSONL assistant_message" events are treated as the point process —
/// they correspond to completed Claude responses, the natural events that
/// excite further usage.
/// </summary>
public sealed class DefaultHawkesIntensityScaler : IHawkesIntensityScaler
{
    private const double FitWindowMinutes = 360.0;
    private const double IntensityWindowMinutes = 90.0;
    private const string JsonlSourceId = "jsonl";
    private static readonly TimeSpan RefitInterval = TimeSpan.FromMinutes(15);

    private readonly HawkesParameterFitter _fitter;
    private readonly object _stateLock = new();
    private HawkesParameters _params = HawkesParameters.Default;
    private DateTimeOffset _lastRefitUtc = DateTimeOffset.MinValue;

    public DefaultHawkesIntensityScaler(HawkesParameterFitter? fitter = null)
    {
        _fitter = fitter ?? new HawkesParameterFitter();
    }

    public HawkesIntensityResult ComputeRatio(IReadOnlyList<TelemetryEvent> recentTelemetry, DateTimeOffset now)
    {
        var origin = now.AddMinutes(-IntensityWindowMinutes);
        var pastTimes = recentTelemetry
            .Where(e => string.Equals(e.SourceId, JsonlSourceId, StringComparison.Ordinal)
                        && e.CapturedAtUtc >= origin
                        && e.CapturedAtUtc < now)
            .OrderBy(e => e.CapturedAtUtc)
            .Select(e => (e.CapturedAtUtc - origin).TotalMinutes)
            .ToList();

        TryRefit(recentTelemetry, now);

        HawkesParameters parameters;
        lock (_stateLock) { parameters = _params; }

        var process = new HawkesProcess(parameters);
        var nowMinutes = (now - origin).TotalMinutes;
        var currentIntensity = process.Intensity(nowMinutes, pastTimes);
        var baseline = parameters.Mu;
        var ratio = baseline > 0 ? currentIntensity / baseline : 1.0;

        return new HawkesIntensityResult(
            IntensityRatio: ratio,
            CurrentIntensity: currentIntensity,
            BaselineIntensity: baseline,
            Parameters: parameters,
            EventsConsidered: pastTimes.Count);
    }

    private void TryRefit(IReadOnlyList<TelemetryEvent> recentTelemetry, DateTimeOffset now)
    {
        lock (_stateLock)
        {
            if ((now - _lastRefitUtc) < RefitInterval) return;
        }

        var fitOrigin = now.AddMinutes(-FitWindowMinutes);
        var fitTimes = recentTelemetry
            .Where(e => string.Equals(e.SourceId, JsonlSourceId, StringComparison.Ordinal)
                        && e.CapturedAtUtc >= fitOrigin
                        && e.CapturedAtUtc < now)
            .OrderBy(e => e.CapturedAtUtc)
            .Select(e => (e.CapturedAtUtc - fitOrigin).TotalMinutes)
            .ToList();

        var T = (now - fitOrigin).TotalMinutes;
        var fitted = _fitter.Fit(fitTimes, T, initialGuess: HawkesParameters.Default);

        lock (_stateLock)
        {
            if (fitted is not null) _params = fitted;
            _lastRefitUtc = now;
        }
    }

    /// <summary>Test-only hook to reset the cached parameters and refit timer.</summary>
    internal void Reset()
    {
        lock (_stateLock)
        {
            _params = HawkesParameters.Default;
            _lastRefitUtc = DateTimeOffset.MinValue;
        }
    }
}
