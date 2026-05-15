using System;
using System.IO;
using System.Text;
using ClaudeUsageProjector.Predictor.Adapters;
using FluentAssertions;
using Xunit;

namespace ClaudeUsageProjector.Predictor.Tests;

public sealed class JsonlReaderTests
{
    private static readonly DateTimeOffset Fallback = new(2026, 4, 28, 12, 0, 0, TimeSpan.Zero);

    private static Stream MakeStream(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void EmptyStream_ReturnsNoEvents()
    {
        var r = new JsonlReader();
        using var s = MakeStream("");
        var result = r.ReadFrom(s, 0, "file", Fallback);

        result.Events.Should().BeEmpty();
        result.NewPosition.Should().Be(0);
        result.FileTruncated.Should().BeFalse();
    }

    [Fact]
    public void TruncatedFile_FlaggedAndResetsPosition()
    {
        var r = new JsonlReader();
        using var s = MakeStream("short");
        // We claim the previous offset was past the current length.
        var result = r.ReadFrom(s, startPosition: 9999, "file", Fallback);
        result.FileTruncated.Should().BeTrue();
        result.NewPosition.Should().Be(0);
    }

    [Fact]
    public void AssistantMessage_WithUsage_BecomesEvent()
    {
        var line = """
{"type":"assistant","sessionId":"abc","timestamp":"2026-04-28T11:30:00Z","message":{"model":"claude-opus-4-7","usage":{"input_tokens":100,"output_tokens":250,"cache_read_input_tokens":42}}}
""";
        var r = new JsonlReader();
        using var s = MakeStream(line + "\n");

        var result = r.ReadFrom(s, 0, "session.jsonl", Fallback);

        result.Events.Should().HaveCount(1);
        var ev = result.Events[0];
        ev.SourceId.Should().Be("jsonl");
        ev.EventType.Should().Be("assistant_message");
        ev.SessionId.Should().Be("abc");
        ev.InputTokens.Should().Be(100);
        ev.OutputTokens.Should().Be(250);
        ev.CacheReadTokens.Should().Be(42);
        ev.Model.Should().Be("claude-opus-4-7");
        ev.CapturedAtUtc.Should().Be(new DateTimeOffset(2026, 4, 28, 11, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NonAssistantLines_AreSkipped()
    {
        var content = """
{"type":"user","content":"hi"}
{"type":"assistant","sessionId":"s","message":{"usage":{"input_tokens":5}}}
""";
        var r = new JsonlReader();
        using var s = MakeStream(content + "\n");
        var result = r.ReadFrom(s, 0, "f", Fallback);

        result.Events.Should().HaveCount(1);
        result.SkippedLines.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PartialTrailingLine_NotConsumed()
    {
        var complete = """
{"type":"assistant","sessionId":"s","message":{"usage":{"input_tokens":5}}}
""";
        var partial = "{\"type\":\"assistant\""; // no trailing newline — partial
        var r = new JsonlReader();
        using var s = MakeStream(complete + "\n" + partial);

        var result = r.ReadFrom(s, 0, "f", Fallback);

        result.Events.Should().HaveCount(1);
        // New position should equal length of the complete line + its newline,
        // NOT length of the whole stream.
        result.NewPosition.Should().Be(complete.Length + 1);
    }

    [Fact]
    public void AssistantWithoutUsage_IsSkipped()
    {
        var line = "{\"type\":\"assistant\",\"sessionId\":\"s\",\"message\":{\"model\":\"x\"}}";
        var r = new JsonlReader();
        using var s = MakeStream(line + "\n");
        var result = r.ReadFrom(s, 0, "f", Fallback);

        result.Events.Should().BeEmpty();
        result.SkippedLines.Should().Be(1);
    }
}
