using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeUsageProjector.Predictor.State;

/// <summary>
/// Bounded in-memory rolling buffer of recent UsageSnapshots, ordered by
/// CapturedAtUtc. The predictor only needs the last ~hour to fit WLS, so we
/// trim both by age (older than RetentionMinutes) and by count (no more than
/// MaxCapacity entries) on every Add. Phase 3 will replace this with a JSONL
/// append-only file that the predictor tails at startup for warm rehydration.
/// </summary>
public sealed class ObservationWindow
{
    public const int DefaultMaxCapacity = 600;        // ~10 hours at 1/min
    public const double DefaultRetentionMinutes = 240;  // 4 hours back

    private readonly List<UsageSnapshot> _snapshots = new();
    private readonly int _maxCapacity;
    private readonly double _retentionMinutes;

    public ObservationWindow(int maxCapacity = DefaultMaxCapacity, double retentionMinutes = DefaultRetentionMinutes)
    {
        _maxCapacity = maxCapacity;
        _retentionMinutes = retentionMinutes;
    }

    public int Count => _snapshots.Count;

    public IReadOnlyList<UsageSnapshot> Snapshots => _snapshots;

    public void Add(UsageSnapshot snapshot)
    {
        _snapshots.Add(snapshot);

        var cutoff = snapshot.CapturedAtUtc - TimeSpan.FromMinutes(_retentionMinutes);
        _snapshots.RemoveAll(s => s.CapturedAtUtc < cutoff);

        if (_snapshots.Count > _maxCapacity)
        {
            _snapshots.RemoveRange(0, _snapshots.Count - _maxCapacity);
        }
    }

    /// <summary>
    /// Bulk load a chronologically-ordered batch (typically read back from
    /// the persistence layer at startup). Replaces any existing content.
    /// Applies the same retention rule once at the end.
    /// </summary>
    public void Seed(IEnumerable<UsageSnapshot> snapshots, DateTimeOffset now)
    {
        _snapshots.Clear();
        _snapshots.AddRange(snapshots);
        _snapshots.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));

        var cutoff = now - TimeSpan.FromMinutes(_retentionMinutes);
        _snapshots.RemoveAll(s => s.CapturedAtUtc < cutoff);

        if (_snapshots.Count > _maxCapacity)
        {
            _snapshots.RemoveRange(0, _snapshots.Count - _maxCapacity);
        }
    }
}
