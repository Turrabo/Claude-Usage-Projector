using System.Text.Json.Serialization;

namespace ClaudeUsageProjector.Predictor.Ipc;

// Line-delimited JSON IPC contract between Rust host and C# predictor.
// Every message carries an explicit "v" version field so we can evolve the
// protocol without surprising the other end, and a "type" discriminator the
// receiver dispatches on. Each message is a flat record (no inheritance) so
// the System.Text.Json source generator can produce clean code for it.
//
// Phase 1 supports a minimal subset (observe / shutdown / log). Predictions
// and chart data land in later phases.

// ---------- Host -> Predictor ----------

public sealed record ObserveMessage
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("type")] public string Type { get; init; } = "observe";

    /// <summary>UTC timestamp of the observation, ISO 8601 with 'Z' suffix.</summary>
    [JsonPropertyName("t")] public required string TimestampUtc { get; init; }

    /// <summary>Claude Code usage. Null if the host couldn't read it this poll.</summary>
    [JsonPropertyName("cc")] public UsageBuckets? ClaudeCode { get; init; }

    /// <summary>Codex usage. Null if Codex is disabled or unreadable.</summary>
    [JsonPropertyName("cx")] public UsageBuckets? Codex { get; init; }
}

public sealed record ShutdownMessage
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("type")] public string Type { get; init; } = "shutdown";
}

public sealed record UsageBuckets
{
    [JsonPropertyName("five_hour")] public double FiveHourPct { get; init; }
    [JsonPropertyName("seven_day")] public double SevenDayPct { get; init; }
    [JsonPropertyName("resets_at")] public string? ResetsAtUtc { get; init; }
}

// ---------- Predictor -> Host ----------

public sealed record LogMessage
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("type")] public string Type { get; init; } = "log";

    [JsonPropertyName("level")] public required string Level { get; init; }
    [JsonPropertyName("msg")] public required string Msg { get; init; }
}

// Source-generated JSON context (AOT-friendly even though we currently
// publish non-AOT; keeping the discipline makes a later AOT switch trivial).
[JsonSerializable(typeof(ObserveMessage))]
[JsonSerializable(typeof(ShutdownMessage))]
[JsonSerializable(typeof(LogMessage))]
[JsonSerializable(typeof(UsageBuckets))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
public partial class IpcJsonContext : JsonSerializerContext { }
