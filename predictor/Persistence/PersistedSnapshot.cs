using System.Text.Json.Serialization;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// On-disk JSONL line shape for a single observation. Versioned so a future
/// schema bump can coexist with old lines on the same file. UTC ISO 8601
/// timestamps with the 'Z' suffix.
/// </summary>
public sealed record PersistedSnapshot
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("t")] public required string CapturedAtUtc { get; init; }
    [JsonPropertyName("used_pct")] public double? UsedPercent { get; init; }
    [JsonPropertyName("refresh_at")] public string? RefreshAtUtc { get; init; }
}

[JsonSerializable(typeof(PersistedSnapshot))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
public partial class PersistenceJsonContext : JsonSerializerContext { }
