using System;

namespace ClaudeUsageProjector.Predictor.State;

/// <summary>
/// A discrete event from the user's Claude Code activity stream (currently
/// the JSONL session files; hooks/OTLP may follow). Ported from CSM but with
/// the SQLite identity field dropped — predictor storage is in-memory only.
/// </summary>
public sealed record TelemetryEvent
{
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string SourceId { get; init; }      // "jsonl" (other sources later)
    public required string EventType { get; init; }     // "assistant_message"
    public string? SessionId { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public string? Model { get; init; }
    public string? Notes { get; init; }                 // e.g. source file path
}
