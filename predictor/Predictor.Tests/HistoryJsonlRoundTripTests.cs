using System;
using System.IO;
using ClaudeUsageProjector.Predictor.Persistence;
using ClaudeUsageProjector.Predictor.State;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class HistoryJsonlRoundTripTests : IDisposable
{
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
        var path = Path.Combine(_root, "history.jsonl");
        var snaps = new[]
        {
            Snap("2026-04-01T10:00:00Z", 12.5, "2026-04-01T15:00:00Z"),
            Snap("2026-04-01T10:05:00Z", 14.0, "2026-04-01T15:00:00Z"),
            Snap("2026-04-01T10:10:00Z", 16.5, null),
        };

        using (var w = new HistoryJsonlWriter(path))
        {
            foreach (var s in snaps) w.Append(s);
        }

        var loaded = new HistoryJsonlReader(_root).LoadAll(out var skipped);
        skipped.Should().Be(0);
        loaded.Should().HaveCount(3);
        loaded[0].UsedPercent.Should().Be(12.5);
        loaded[0].CapturedAtUtc.Should().Be(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));
        loaded[2].UsedPercent.Should().Be(16.5);
        loaded[2].RefreshAtUtc.Should().BeNull();
    }

    [Fact]
    public void Reader_TolersMalformedLines()
    {
        var path = Path.Combine(_root, "history.jsonl");
        File.WriteAllText(path,
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n" +
            "this is not json\n" +
            "{}\n" +
            "{\"v\":1,\"t\":\"2026-04-01T10:05:00Z\",\"used_pct\":15.0}\n");

        var loaded = new HistoryJsonlReader(_root).LoadAll(out var skipped);
        loaded.Should().HaveCount(2);
        skipped.Should().Be(2);
    }

    [Fact]
    public void Reader_LoadsAcrossRotatedFiles_InTimeOrder()
    {
        File.WriteAllText(
            Path.Combine(_root, "history-1.jsonl"),
            "{\"v\":1,\"t\":\"2026-04-01T10:00:00Z\",\"used_pct\":10.0}\n");
        File.WriteAllText(
            Path.Combine(_root, "history.jsonl"),
            "{\"v\":1,\"t\":\"2026-04-01T11:00:00Z\",\"used_pct\":12.0}\n");

        var loaded = new HistoryJsonlReader(_root).LoadAll(out _);

        loaded.Should().HaveCount(2);
        loaded[0].UsedPercent.Should().Be(10.0);
        loaded[1].UsedPercent.Should().Be(12.0);
    }

    [Fact]
    public void Writer_SkipsSnapshotsWithoutPercent()
    {
        var path = Path.Combine(_root, "history.jsonl");
        using (var w = new HistoryJsonlWriter(path))
        {
            w.Append(new UsageSnapshot
            {
                CapturedAtUtc = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero),
                UsedPercent = null,
                RefreshAtUtc = null,
            });
        }

        var loaded = new HistoryJsonlReader(_root).LoadAll(out _);
        loaded.Should().BeEmpty();
    }

    private static UsageSnapshot Snap(string ts, double pct, string? refreshIso) =>
        new()
        {
            CapturedAtUtc = DateTimeOffset.Parse(ts),
            UsedPercent = pct,
            RefreshAtUtc = refreshIso is null ? null : DateTimeOffset.Parse(refreshIso),
        };
}
