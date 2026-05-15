using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeUsageProjector.Predictor.State;

/// <summary>
/// Bounded rolling buffer of TelemetryEvents, ordered by CapturedAtUtc. Holds
/// enough history for the Hawkes fitter's 6-hour window plus headroom. Trims
/// on every Add by both age and count. Mirrors ObservationWindow's discipline.
/// </summary>
public sealed class TelemetryWindow
{
    public const int DefaultMaxCapacity = 5000;
    public const double DefaultRetentionMinutes = 480.0; // 8 hours

    private readonly List<TelemetryEvent> _events = new();
    private readonly object _lock = new();
    private readonly int _maxCapacity;
    private readonly double _retentionMinutes;

    public TelemetryWindow(int maxCapacity = DefaultMaxCapacity, double retentionMinutes = DefaultRetentionMinutes)
    {
        _maxCapacity = maxCapacity;
        _retentionMinutes = retentionMinutes;
    }

    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    public void Add(TelemetryEvent ev)
    {
        lock (_lock)
        {
            _events.Add(ev);
            Trim(ev.CapturedAtUtc);
        }
    }

    public void AddRange(IEnumerable<TelemetryEvent> evs)
    {
        lock (_lock)
        {
            DateTimeOffset newest = DateTimeOffset.MinValue;
            foreach (var ev in evs)
            {
                _events.Add(ev);
                if (ev.CapturedAtUtc > newest) newest = ev.CapturedAtUtc;
            }
            if (newest > DateTimeOffset.MinValue) Trim(newest);
        }
    }

    /// <summary>Returns a stable snapshot ordered by CapturedAtUtc ascending.</summary>
    public IReadOnlyList<TelemetryEvent> Snapshot()
    {
        lock (_lock)
        {
            return _events.OrderBy(e => e.CapturedAtUtc).ToList();
        }
    }

    private void Trim(DateTimeOffset reference)
    {
        var cutoff = reference - TimeSpan.FromMinutes(_retentionMinutes);
        _events.RemoveAll(e => e.CapturedAtUtc < cutoff);
        if (_events.Count > _maxCapacity)
        {
            _events.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
            _events.RemoveRange(0, _events.Count - _maxCapacity);
        }
    }
}
