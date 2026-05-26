using System;
using System.IO;
using ClaudeUsageProjector.Predictor.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class CsmSqliteMigratorTests : IDisposable
{
    private readonly string _root;

    public CsmSqliteMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccum-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Migrator_SkippedWhenSentinelPresent()
    {
        var sentinel = Path.Combine(_root, ".csm-migrated");
        File.WriteAllText(sentinel, "prior run");
        var migrator = new CsmSqliteMigrator(
            log: null,
            sqlitePathOverride: Path.Combine(_root, "absent.sqlite"),
            sentinelPathOverride: sentinel,
            outputPathOverride: Path.Combine(_root, "history.jsonl"));

        migrator.MigrateIfNeeded().Should().BeNull();
    }

    [Fact]
    public void Migrator_SkippedWhenSqliteAbsent_WritesSentinel()
    {
        var sentinel = Path.Combine(_root, ".csm-migrated");
        var migrator = new CsmSqliteMigrator(
            log: null,
            sqlitePathOverride: Path.Combine(_root, "absent.sqlite"),
            sentinelPathOverride: sentinel,
            outputPathOverride: Path.Combine(_root, "history.jsonl"));

        migrator.MigrateIfNeeded().Should().BeNull();
        File.Exists(sentinel).Should().BeTrue();
    }

    [Fact]
    public void Migrator_CopiesTruthSourceRowsWithinWindow_IntoLegacyPath()
    {
        var sqlitePath = Path.Combine(_root, "csm.sqlite");
        SeedSqlite(sqlitePath);

        var historyPath = Path.Combine(_root, "history.jsonl");
        var migrator = new CsmSqliteMigrator(
            log: null,
            sqlitePathOverride: sqlitePath,
            sentinelPathOverride: Path.Combine(_root, ".csm-migrated"),
            outputPathOverride: historyPath);

        var n = migrator.MigrateIfNeeded();

        n.Should().BeGreaterThan(0);
        File.Exists(historyPath).Should().BeTrue("CSM migration writes the legacy un-sharded file; Phase 7b migrator re-shards on first observe");

        // The migrator writes v:1 rows (no account_id). Reader's
        // LoadAllByAccount intentionally ignores the legacy file — that's
        // LegacyHistoryMigrator's responsibility. Confirm the file contents
        // are well-formed JSONL though.
        var lines = File.ReadAllLines(historyPath);
        lines.Should().HaveCount(n!.Value);
        foreach (var l in lines)
        {
            l.Should().StartWith("{");
            l.Should().Contain("\"used_pct\":");
        }
    }

    private static void SeedSqlite(string path)
    {
        var cs = $"Data Source={path}";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE UsageSnapshots (" +
                "  Id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "  CapturedAtUtc TEXT NOT NULL," +
                "  SourceId TEXT NOT NULL," +
                "  UsedPercent REAL," +
                "  RefreshAtUtc TEXT," +
                "  WeeklyUsedPercent REAL," +
                "  ParserVersion INTEGER NOT NULL DEFAULT 0," +
                "  Confidence TEXT NOT NULL," +
                "  Notes TEXT)";
            cmd.ExecuteNonQuery();
        }

        var rows = new[]
        {
            (DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"), 12.5, "Truth"),
            (DateTimeOffset.UtcNow.AddDays(-2).ToString("yyyy-MM-ddTHH:mm:ssZ"), 14.0, "Truth"),
            // Older than the 14-day window — should be excluded
            (DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ"), 22.0, "Truth"),
            // Non-truth source — should be excluded
            (DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"), 99.0, "BurnRateOnly"),
        };
        foreach (var (ts, pct, conf) in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO UsageSnapshots(CapturedAtUtc, SourceId, UsedPercent, Confidence) " +
                "VALUES($t, 'csm-test', $p, $c)";
            cmd.Parameters.AddWithValue("$t", ts);
            cmd.Parameters.AddWithValue("$p", pct);
            cmd.Parameters.AddWithValue("$c", conf);
            cmd.ExecuteNonQuery();
        }
    }
}
