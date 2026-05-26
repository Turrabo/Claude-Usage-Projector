using System;
using System.IO;
using System.Linq;
using ClaudeUsageProjector.Predictor.Persistence;
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
