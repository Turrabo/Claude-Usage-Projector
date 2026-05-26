using System;
using System.IO;
using System.Linq;
using ClaudeUsageProjector.Predictor.Persistence;
using ClaudeUsageProjector.Predictor.State;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class LegacyHistoryMigratorTests : IDisposable
{
    private const string AccountA = "acct_AAAA";

    private readonly string _root;

    public LegacyHistoryMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccum-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MigrationNeeded_TrueOnlyWhenLegacyFilePresent()
    {
        var m = new LegacyHistoryMigrator(rootOverride: _root);
        m.MigrationNeeded().Should().BeFalse();

        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");
        m.MigrationNeeded().Should().BeTrue();
    }

    [Fact]
    public void MigrateIfNeeded_NoLegacyFile_ReturnsEmpty()
    {
        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);
        rows.Should().BeEmpty();
        File.Exists(Path.Combine(_root, "history.jsonl.pre-multi-auth-backup")).Should().BeFalse();
    }

    [Fact]
    public void MigrateIfNeeded_ShardsAndBacksUpLegacy()
    {
        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:05:00Z\",\"used_pct\":12.5,\"refresh_at\":\"2026-04-01T15:00:00Z\"}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:10:00Z\",\"used_pct\":15.0}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);

        rows.Should().HaveCount(3);
        rows.Select(r => r.UsedPercent).Should().Equal(10.0, 12.5, 15.0);

        // Legacy renamed to .pre-multi-auth-backup
        File.Exists(Path.Combine(_root, "history.jsonl")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "history.jsonl.pre-multi-auth-backup")).Should().BeTrue();

        // New shard exists, tagged
        var shardPath = Path.Combine(_root, $"history-{AccountA}.jsonl");
        File.Exists(shardPath).Should().BeTrue();
        var content = File.ReadAllText(shardPath);
        content.Should().Contain($"\"account_id\":\"{AccountA}\"");
        content.Should().Contain("\"v\":2");
    }

    [Fact]
    public void MigrateIfNeeded_RejectsEmptyAccountId()
    {
        WriteLegacy("{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");
        var m = new LegacyHistoryMigrator(rootOverride: _root);
        FluentActions.Invoking(() => m.MigrateIfNeeded("")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => m.MigrateIfNeeded("  ")).Should().Throw<ArgumentException>();
        // Source file must remain untouched on validation failure — a misconfigured
        // host shouldn't destroy data.
        File.Exists(Path.Combine(_root, "history.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void MigrateIfNeeded_RejectsAccountIdWithInvalidChars()
    {
        WriteLegacy("{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");
        var m = new LegacyHistoryMigrator(rootOverride: _root);
        foreach (var bad in new[] { "../../evil", "with space", "with-dash", "with/slash", "with\\back", "with.dot" })
        {
            FluentActions.Invoking(() => m.MigrateIfNeeded(bad))
                .Should().Throw<ArgumentException>($"accountId '{bad}' must be rejected to prevent filename traversal");
        }
        File.Exists(Path.Combine(_root, "history.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void MigrateIfNeeded_Idempotent_AfterMigrationDoesNothing()
    {
        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var first = m.MigrateIfNeeded(AccountA);
        first.Should().HaveCount(1);

        // Second call: legacy file is gone, MigrationNeeded() is false,
        // MigrateIfNeeded returns empty without touching anything.
        m.MigrationNeeded().Should().BeFalse();
        var second = m.MigrateIfNeeded(AccountA);
        second.Should().BeEmpty();

        // The shard from the first migration is untouched (no duplicate rows).
        var shardLines = File.ReadAllLines(Path.Combine(_root, $"history-{AccountA}.jsonl"));
        shardLines.Should().HaveCount(1);
    }

    [Fact]
    public void MigrateIfNeeded_PreservesPriorBackupWithTimestampSuffix()
    {
        // Simulates the rare path where CSM re-runs after a sentinel-write
        // failure, recreating history.jsonl with new content. The first
        // migration's backup must not be overwritten.
        var canonicalBackup = Path.Combine(_root, "history.jsonl.pre-multi-auth-backup");
        File.WriteAllText(canonicalBackup, "ORIGINAL_BACKUP_CONTENT");

        WriteLegacy("{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        m.MigrateIfNeeded(AccountA);

        // Canonical backup is untouched
        File.ReadAllText(canonicalBackup).Should().Be("ORIGINAL_BACKUP_CONTENT");
        // A timestamped backup now exists for the latest run
        var dated = Directory.GetFiles(_root, "history.jsonl.pre-multi-auth-backup-*");
        dated.Should().HaveCount(1, "second migration should produce a timestamp-suffixed backup so the original survives");
    }

    [Fact]
    public void MigrateIfNeeded_OnlyAttemptsOncePerProcess()
    {
        // If a write succeeds but rename fails (e.g., FS held by AV), the
        // legacy file is still on disk. We must NOT retry within the same
        // process — that would duplicate the rows already in the shard.
        WriteLegacy("{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");
        var m = new LegacyHistoryMigrator(rootOverride: _root);

        m.MigrationNeeded().Should().BeTrue();
        var first = m.MigrateIfNeeded(AccountA);
        first.Should().HaveCount(1);

        // After a successful first run the legacy file is gone, so this
        // assertion is trivially true. The real protection is the
        // _attemptedThisProcess flag — recreate the legacy file and
        // confirm the same migrator instance refuses to re-migrate.
        WriteLegacy("{\"v\":1,\"t\":\"2026-04-01T11:00:00Z\",\"used_pct\":20.0}\n");
        m.MigrationNeeded().Should().BeFalse("the migrator should refuse a second attempt in the same process");
        var second = m.MigrateIfNeeded(AccountA);
        second.Should().BeEmpty();

        // The shard still only contains the first migration's row.
        var shard = File.ReadAllLines(Path.Combine(_root, $"history-{AccountA}.jsonl"));
        shard.Should().HaveCount(1);
    }

    [Fact]
    public void MigratedRows_CanBeAddedToPrePopulatedWindowWithoutClobber()
    {
        // Regression for the BLOCKER from Reviewer A on commit 1ccdd5e:
        // Program.cs originally called ObservationWindow.Seed(migratedRows),
        // which calls Clear() first. If the window already held rows from
        // a startup LoadAllByAccount pass for the same account, those rows
        // were silently lost in memory. The fix is to iterate Add() instead.
        // This test verifies the contract the migrator promises: the
        // returned rows are safe to Add into an existing window without
        // clobbering its prior contents.
        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:05:00Z\",\"used_pct\":12.0}\n");

        var window = new ObservationWindow();
        // Pre-existing observation, e.g. from a prior live observe between
        // startup and migration — same approximate timestamp range.
        window.Add(new UsageSnapshot
        {
            CapturedAtUtc = new DateTimeOffset(2026, 4, 1, 9, 50, 0, TimeSpan.Zero),
            UsedPercent = 5.0,
            RefreshAtUtc = null,
        });
        var preCount = window.Count;
        preCount.Should().Be(1);

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);
        foreach (var r in rows) window.Add(r);

        window.Count.Should().Be(preCount + rows.Count, "Add-loop must not clobber the pre-existing snapshot");
        window.Snapshots.Select(s => s.UsedPercent).Should().Contain(5.0);
        window.Snapshots.Select(s => s.UsedPercent).Should().Contain(10.0);
        window.Snapshots.Select(s => s.UsedPercent).Should().Contain(12.0);
    }

    [Fact]
    public void MigrateIfNeeded_AppendsToPreExistingShard()
    {
        // A user could have already accumulated live observations under the
        // active account before the migrator runs (unlikely but possible
        // on a slow first-tick). Migration should APPEND to that shard, not
        // overwrite it.
        var shardPath = Path.Combine(_root, $"history-{AccountA}.jsonl");
        File.WriteAllText(
            shardPath,
            "{\"v\":2,\"t\":\"2026-04-01T11:00:00Z\",\"used_pct\":50.0,\"account_id\":\"" + AccountA + "\"}\n");

        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);
        rows.Should().HaveCount(1);

        // Reader sees both: pre-existing live row + migrated legacy row.
        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out _);
        byAccount[AccountA].Should().HaveCount(2);
        byAccount[AccountA].Select(s => s.UsedPercent).Should().Equal(10.0, 50.0); // time-ordered
    }

    [Fact]
    public void MigrateIfNeeded_SkipsMalformedRows()
    {
        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n" +
            "this is not json\n" +
            "{}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:05:00Z\",\"used_pct\":12.5}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public void MigrateIfNeeded_SkipsRowsWithoutUsedPercent()
    {
        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:05:00Z\"}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);
        rows.Should().HaveCount(1);
    }

    [Fact]
    public void MigrateIfNeeded_TimeOrdersOutput()
    {
        // Legacy file in reverse-chronological order. Migrator should sort.
        WriteLegacy(
            "{\"v\":1,\"t\":\"2026-04-01T10:10:00Z\",\"used_pct\":30.0}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:05:00Z\",\"used_pct\":20.0}\n");

        var m = new LegacyHistoryMigrator(rootOverride: _root);
        var rows = m.MigrateIfNeeded(AccountA);
        rows.Select(r => r.UsedPercent).Should().Equal(10.0, 20.0, 30.0);
    }

    private void WriteLegacy(string content)
    {
        File.WriteAllText(Path.Combine(_root, "history.jsonl"), content);
    }
}
