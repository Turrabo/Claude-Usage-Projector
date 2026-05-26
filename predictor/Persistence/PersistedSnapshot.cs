using System.Text.Json.Serialization;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// On-disk JSONL line shape for a single observation. Versioned so a future
/// schema bump can coexist with old lines on the same file. UTC ISO 8601
/// timestamps with the 'Z' suffix.
/// <para/>
/// Schema versions:
///   v:1 — original (pre-multi-auth). No account_id field. Files named
///         <c>history.jsonl</c> (or <c>history-&lt;unix&gt;.jsonl</c> after
///         rotation).
///   v:2 — Phase 7b. Adds nullable <c>account_id</c>. Files named
///         <c>history-&lt;account_id&gt;.jsonl</c> (rotated:
///         <c>history-&lt;account_id&gt;-&lt;unix&gt;.jsonl</c>).
///         Legacy v:1 rows are migrated by tagging with the active
///         <c>account_id</c> at first-observe time — see
///         <see cref="LegacyHistoryMigrator"/>.
/// </summary>
public sealed record PersistedSnapshot
{
    [JsonPropertyName("v")] public int Version { get; init; } = 2;
    [JsonPropertyName("t")] public required string CapturedAtUtc { get; init; }
    [JsonPropertyName("used_pct")] public double? UsedPercent { get; init; }
    [JsonPropertyName("refresh_at")] public string? RefreshAtUtc { get; init; }

    /// <summary>
    /// Owning account. Optional on the wire so v:1 rows still deserialise;
    /// post-migration every row has an explicit value. Reader code uses the
    /// containing filename as primary source of truth and falls back to this
    /// field when the filename doesn't match the per-account pattern.
    /// </summary>
    [JsonPropertyName("account_id")] public string? AccountId { get; init; }
}

[JsonSerializable(typeof(PersistedSnapshot))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
public partial class PersistenceJsonContext : JsonSerializerContext { }
