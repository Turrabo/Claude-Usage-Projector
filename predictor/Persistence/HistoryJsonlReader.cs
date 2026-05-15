using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// Reads every persisted history JSONL file in the predictor's data folder
/// (history.jsonl + any rotated history-*.jsonl) and returns the union as a
/// time-ordered list of UsageSnapshots.
/// <para/>
/// Tolerates malformed lines (logged-and-skipped by the caller; we just
/// return what we can).
/// </summary>
public sealed class HistoryJsonlReader
{
    private readonly string _root;

    public HistoryJsonlReader(string? overrideRoot = null)
    {
        _root = overrideRoot ?? PersistencePaths.Root;
    }

    public IReadOnlyList<UsageSnapshot> LoadAll(out int skippedLines)
    {
        skippedLines = 0;
        if (!Directory.Exists(_root)) return Array.Empty<UsageSnapshot>();

        var files = Directory.EnumerateFiles(_root, "history*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var snapshots = new List<UsageSnapshot>(capacity: 1024);

        foreach (var file in files)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (TryParse(line, out var snap))
                    {
                        snapshots.Add(snap!);
                    }
                    else
                    {
                        skippedLines++;
                    }
                }
            }
            catch (IOException)
            {
                skippedLines++;
            }
        }

        snapshots.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
        return snapshots;
    }

    private static bool TryParse(string line, out UsageSnapshot? snap)
    {
        snap = null;
        try
        {
            var persisted = JsonSerializer.Deserialize(line, PersistenceJsonContext.Default.PersistedSnapshot);
            if (persisted is null) return false;
            if (!DateTimeOffset.TryParse(
                    persisted.CapturedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var captured))
            {
                return false;
            }

            DateTimeOffset? refresh = null;
            if (!string.IsNullOrEmpty(persisted.RefreshAtUtc)
                && DateTimeOffset.TryParse(
                    persisted.RefreshAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                refresh = parsed;
            }

            snap = new UsageSnapshot
            {
                CapturedAtUtc = captured,
                UsedPercent = persisted.UsedPercent,
                RefreshAtUtc = refresh,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
