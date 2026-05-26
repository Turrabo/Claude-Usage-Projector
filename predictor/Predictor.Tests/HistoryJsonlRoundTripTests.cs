using System;
using System.IO;
using System.Linq;
using ClaudeUsageProjector.Predictor.Persistence;
using ClaudeUsageProjector.Predictor.State;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class HistoryJsonlRoundTripTests : IDisposable
{
    private const string AccountA = "acct_AAAA";
    private const string AccountB = "acct_BBBB";

    private readonly string _root;

    public HistoryJsonlRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccum-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Append_ThenLoad_RoundTripsChronologically()
    {
        var snaps = new[]
        {
            Snap("2026-04-01T10:00:00Z", 12.5, "2026-04-01T15:00:00Z"),
            Snap("2026-04-01T10:05:00Z", 14.0, "2026-04-01T15:00:00Z"),
            Snap("2026-04-01T10:10:00Z", 16.5, null),
        };

        using (var w = new HistoryJsonlWriter(AccountA, _root))
        {
            foreach (var s in snaps) w.Append(s);
        }

        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out var skipped);
        skipped.Should().Be(0);
        byAccount.Should().ContainKey(AccountA);
        var loaded = byAccount[AccountA];
        loaded.Should().HaveCount(3);
        loaded[0].UsedPercent.Should().Be(12.5);
        loaded[0].CapturedAtUtc.Should().Be(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));
        loaded[2].UsedPercent.Should().Be(16.5);
        loaded[2].RefreshAtUtc.Should().BeNull();
    }

    [Fact]
    public void Writer_PathIncludesAccountId()
    {
        using var w = new HistoryJsonlWriter(AccountA, _root);
        w.Path.Should().EndWith($"history-{AccountA}.jsonl");
        w.AccountId.Should().Be(AccountA);
    }

    [Fact]
    public void Writer_RejectsEmptyAccountId()
    {
        FluentActions
            .Invoking(() => new HistoryJsonlWriter("", _root))
            .Should().Throw<ArgumentException>();
        FluentActions
            .Invoking(() => new HistoryJsonlWriter("  ", _root))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Writer_RejectsAccountIdsThatWouldEscapePersistenceRoot()
    {
        // Defends against a future protocol change or hand-edited credential
        // putting a malicious or filename-unsafe accountId on the wire. The
        // real accountId format is "acct_" + 12 hex chars (alphanumeric +
        // underscore only); anything else fails ValidateAccountId.
        foreach (var bad in new[]
        {
            "../../evil",
            "with space",
            "with-dash",      // dash isn't in real accountIds; reserved for the rotation suffix
            "with/slash",
            @"with\backslash",
            "with.dot",
            "x:y",
            "x*y",
        })
        {
            FluentActions
                .Invoking(() => new HistoryJsonlWriter(bad, _root))
                .Should().Throw<ArgumentException>($"accountId '{bad}' would corrupt the per-account filename scheme");
        }
    }

    [Fact]
    public void Reader_TolersMalformedLines()
    {
        File.WriteAllText(
            Path.Combine(_root, $"history-{AccountA}.jsonl"),
            "{\"v\":2,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0,\"account_id\":\"" + AccountA + "\"}\n" +
            "this is not json\n" +
            "{}\n" +
            "{\"v\":2,\"t\":\"2026-04-01T10:05:00Z\",\"used_pct\":15.0,\"account_id\":\"" + AccountA + "\"}\n");

        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out var skipped);
        byAccount[AccountA].Should().HaveCount(2);
        skipped.Should().Be(2);
    }

    [Fact]
    public void Reader_LoadsAcrossRotatedFiles_InTimeOrder()
    {
        File.WriteAllText(
            Path.Combine(_root, $"history-{AccountA}-1.jsonl"),
            "{\"v\":2,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0,\"account_id\":\"" + AccountA + "\"}\n");
        File.WriteAllText(
            Path.Combine(_root, $"history-{AccountA}.jsonl"),
            "{\"v\":2,\"t\":\"2026-04-01T11:00:00Z\",\"used_pct\":12.0,\"account_id\":\"" + AccountA + "\"}\n");

        var loaded = new HistoryJsonlReader(_root).LoadAllByAccount(out _)[AccountA];

        loaded.Should().HaveCount(2);
        loaded[0].UsedPercent.Should().Be(10.0);
        loaded[1].UsedPercent.Should().Be(12.0);
    }

    [Fact]
    public void Reader_SeparatesAccounts()
    {
        // Two accounts writing concurrently, each row tagged with the correct id.
        using (var wa = new HistoryJsonlWriter(AccountA, _root))
        using (var wb = new HistoryJsonlWriter(AccountB, _root))
        {
            wa.Append(Snap("2026-04-01T10:00:00Z", 10.0, null));
            wb.Append(Snap("2026-04-01T10:01:00Z", 90.0, null));
            wa.Append(Snap("2026-04-01T10:02:00Z", 11.0, null));
            wb.Append(Snap("2026-04-01T10:03:00Z", 91.0, null));
        }

        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out _);
        byAccount.Keys.Should().BeEquivalentTo(new[] { AccountA, AccountB });
        byAccount[AccountA].Select(s => s.UsedPercent).Should().Equal(10.0, 11.0);
        byAccount[AccountB].Select(s => s.UsedPercent).Should().Equal(90.0, 91.0);
    }

    [Fact]
    public void Reader_FallsBackToRowAccountIdWhenFilenameUnparseable()
    {
        // Hand-edited / oddly-named shard: filename passes the glob
        // (history-*.jsonl) but fails the strict regex because of the
        // embedded dots. The reader falls back to the row's account_id
        // field. Routing must land under AccountA (not the default
        // sentinel) so the row isn't silently misattributed.
        File.WriteAllText(
            Path.Combine(_root, "history-renamed.weird.jsonl"),
            "{\"v\":2,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0,\"account_id\":\"" + AccountA + "\"}\n");

        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out _);
        byAccount.Should().ContainKey(AccountA);
        byAccount[AccountA].Should().HaveCount(1);
    }

    [Fact]
    public void Reader_IgnoresLegacyUnsharededHistoryFile()
    {
        // Pre-Phase-7b history.jsonl must not be read by LoadAllByAccount —
        // LegacyHistoryMigrator owns that path.
        File.WriteAllText(
            Path.Combine(_root, "history.jsonl"),
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");

        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out _);
        byAccount.Should().BeEmpty();
    }

    [Fact]
    public void Reader_EmptyRoot_ReturnsEmptyDictionary()
    {
        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out var skipped);
        byAccount.Should().BeEmpty();
        skipped.Should().Be(0);
    }

    [Fact]
    public void Writer_SkipsSnapshotsWithoutPercent()
    {
        using (var w = new HistoryJsonlWriter(AccountA, _root))
        {
            w.Append(new UsageSnapshot
            {
                CapturedAtUtc = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero),
                UsedPercent = null,
                RefreshAtUtc = null,
            });
        }

        var byAccount = new HistoryJsonlReader(_root).LoadAllByAccount(out _);
        byAccount.Should().BeEmpty();
    }

    [Fact]
    public void Writer_StampsRowsWithItsAccountId()
    {
        // Belt-and-braces: row's account_id field should match the writer's
        // accountId, so a future migration / cross-machine sync that re-reads
        // rows without the filename context still routes correctly.
        using (var w = new HistoryJsonlWriter(AccountA, _root))
        {
            w.Append(Snap("2026-04-01T10:00:00Z", 12.5, null));
        }
        var line = File.ReadAllText(Path.Combine(_root, $"history-{AccountA}.jsonl")).Trim();
        line.Should().Contain($"\"account_id\":\"{AccountA}\"");
    }

    private static UsageSnapshot Snap(string ts, double pct, string? refreshIso) =>
        new()
        {
            CapturedAtUtc = DateTimeOffset.Parse(ts),
            UsedPercent = pct,
            RefreshAtUtc = refreshIso is null ? null : DateTimeOffset.Parse(refreshIso),
        };
}
