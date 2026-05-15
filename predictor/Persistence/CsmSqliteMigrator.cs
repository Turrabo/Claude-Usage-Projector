using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// One-time-at-first-run migration: reads truth-source UsageSnapshot rows
/// from the predecessor Claude Session Monitor's SQLite database and appends
/// them to the predictor's history.jsonl. Records a sentinel file so we
/// don't migrate twice.
/// <para/>
/// Microsoft.Data.Sqlite is the only place in the predictor that touches a
/// native dependency (e_sqlite3.dll). Self-contained single-file publish
/// bundles it; runtime impact ~3 MB on disk, zero at steady state.
/// </summary>
public sealed class CsmSqliteMigrator
{
    public const int MigrationWindowDays = 14;

    private readonly string _sqlitePath;
    private readonly string _sentinelPath;
    private readonly HistoryJsonlWriter _writer;
    private readonly Action<string, string>? _log;

    public CsmSqliteMigrator(
        HistoryJsonlWriter writer,
        Action<string, string>? log = null,
        string? sqlitePathOverride = null,
        string? sentinelPathOverride = null)
    {
        _writer = writer;
        _log = log;
        _sqlitePath = sqlitePathOverride ?? PersistencePaths.CsmSqlite;
        _sentinelPath = sentinelPathOverride ?? PersistencePaths.MigrationSentinel;
    }

    /// <summary>
    /// Returns the number of rows migrated, or null if migration was skipped
    /// (sentinel present, or SQLite not found). Errors are logged and treated
    /// as zero-row migrations (sentinel still written, so we don't retry).
    /// </summary>
    public int? MigrateIfNeeded()
    {
        if (File.Exists(_sentinelPath))
        {
            _log?.Invoke("info", "csm-migrate: skipped (sentinel present)");
            return null;
        }
        if (!File.Exists(_sqlitePath))
        {
            _log?.Invoke("info", $"csm-migrate: skipped (no source DB at {_sqlitePath})");
            WriteSentinel("no-source");
            return null;
        }

        int rows;
        try
        {
            rows = RunMigration();
        }
        catch (Exception ex)
        {
            _log?.Invoke("warn", $"csm-migrate: failed -- {ex.Message}");
            WriteSentinel($"error: {ex.GetType().Name}");
            return 0;
        }

        WriteSentinel($"ok rows={rows}");
        _log?.Invoke("info", $"csm-migrate: imported {rows} truth-source rows from {_sqlitePath}");
        return rows;
    }

    private int RunMigration()
    {
        var cutoff = DateTimeOffset.UtcNow
            .AddDays(-MigrationWindowDays)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Default,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT CapturedAtUtc, UsedPercent, RefreshAtUtc " +
            "FROM UsageSnapshots " +
            "WHERE Confidence = 'Truth' " +
            "  AND UsedPercent IS NOT NULL " +
            "  AND CapturedAtUtc >= $cutoff " +
            "ORDER BY CapturedAtUtc ASC";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);

        int rows = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var captured = reader.GetString(0);
            var usedPct = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
            var refreshAt = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (usedPct is null) continue;

            // Write directly via AppendRaw so we don't round-trip through
            // UsageSnapshot's stricter type system; the JSON shape is fixed.
            var line = "{\"v\":1,\"t\":\"" + captured + "\",\"used_pct\":" +
                       usedPct.Value.ToString("R", CultureInfo.InvariantCulture) +
                       (refreshAt is null ? "" : ",\"refresh_at\":\"" + refreshAt + "\"") +
                       "}";
            _writer.AppendRaw(line);
            rows++;
        }
        return rows;
    }

    private void WriteSentinel(string note)
    {
        try
        {
            File.WriteAllText(_sentinelPath, $"{DateTimeOffset.UtcNow:O} {note}\n");
        }
        catch
        {
            // Sentinel write failure is non-fatal; we'll just re-attempt
            // migration next launch and discover the sentinel is still missing.
        }
    }
}
