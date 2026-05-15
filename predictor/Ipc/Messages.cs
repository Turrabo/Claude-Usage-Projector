using System.Text.Json.Serialization;

namespace ClaudeUsageProjector.Predictor.Ipc;

// Line-delimited JSON IPC contract between Rust host and C# predictor.
// Every message carries an explicit "v" version field so we can evolve the
// protocol without surprising the other end, and a "type" discriminator the
// receiver dispatches on. Each message is a flat record (no inheritance) so
// the System.Text.Json source generator can produce clean code for it.

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

/// <summary>
/// Prediction emitted after each observation. Percentile timestamps and the
/// final-percent are nullable: Tier 1 fills only the burn-rate + P50 fields,
/// Tier 2 (Monte Carlo) fills the full P75/P90 + probability triplet.
/// </summary>
public sealed record PredictionMessage
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("type")] public string Type { get; init; } = "prediction";

    /// <summary>When the prediction was computed (UTC, ISO 8601 Z).</summary>
    [JsonPropertyName("t")] public required string TimestampUtc { get; init; }

    /// <summary>1 = linear burn-rate only, 2 = Monte Carlo projection.</summary>
    [JsonPropertyName("tier")] public int Tier { get; init; }

    /// <summary>"unknown" | "low" | "medium" | "high".</summary>
    [JsonPropertyName("risk")] public required string Risk { get; init; }

    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("stale")] public bool Stale { get; init; }

    /// <summary>Latest observed 5-hour bucket percentage at prediction time.</summary>
    [JsonPropertyName("used_pct")] public double? UsedPercent { get; init; }

    /// <summary>Refresh time of the 5-hour bucket, if known (ISO 8601 Z).</summary>
    [JsonPropertyName("refresh_at")] public string? RefreshAtUtc { get; init; }

    /// <summary>Recency-weighted burn rate in %/min (output of Tier 1).</summary>
    [JsonPropertyName("rate_per_min")] public double? RatePerMinute { get; init; }

    /// <summary>Standard deviation of per-minute rate changes (input to Tier 2).</summary>
    [JsonPropertyName("rate_stddev")] public double? RateStdDev { get; init; }

    /// <summary>Projected time at which usage hits 100% with 50% confidence.</summary>
    [JsonPropertyName("projected_empty_p50")] public string? ProjectedEmptyP50Utc { get; init; }

    /// <summary>P75 projection (Tier 2 only).</summary>
    [JsonPropertyName("projected_empty_p75")] public string? ProjectedEmptyP75Utc { get; init; }

    /// <summary>P90 projection (Tier 2 only).</summary>
    [JsonPropertyName("projected_empty_p90")] public string? ProjectedEmptyP90Utc { get; init; }

    /// <summary>Fraction of simulations that hit 100% before refresh (0..1).</summary>
    [JsonPropertyName("prob_empty_before_refresh")] public double ProbabilityEmptyBeforeRefresh { get; init; }

    /// <summary>Mean simulated percentage at refresh time (Tier 2).</summary>
    [JsonPropertyName("projected_pct_at_refresh")] public double? ProjectedPercentAtRefresh { get; init; }

    /// <summary>True when the engine projects empty strictly before the refresh window.</summary>
    [JsonPropertyName("projected_empty_before_refresh")] public bool ProjectedEmptyBeforeRefresh { get; init; }

    /// <summary>"deterministic" or "monte-carlo".</summary>
    [JsonPropertyName("engine")] public string? Engine { get; init; }

    // ---- Phase 3 fields ----

    /// <summary>"unknown" | "idle" | "active".</summary>
    [JsonPropertyName("activity")] public string? ActivityMode { get; init; }

    /// <summary>Distinct Claude Code sessions seen in the last concurrency window.</summary>
    [JsonPropertyName("active_sessions")] public int? ActiveSessionCount { get; init; }

    /// <summary>True when the rate was held at the cached active value because the user is idle.</summary>
    [JsonPropertyName("rate_frozen_from_idle")] public bool RateFrozenFromIdle { get; init; }

    /// <summary>Hawkes intensity ratio λ(now)/μ. &gt;1 = bursting, =1 = baseline. Diagnostic only.</summary>
    [JsonPropertyName("hawkes_ratio")] public double? HawkesIntensityRatio { get; init; }

    [JsonPropertyName("hawkes_mu")] public double? HawkesMu { get; init; }
    [JsonPropertyName("hawkes_alpha")] public double? HawkesAlpha { get; init; }
    [JsonPropertyName("hawkes_beta")] public double? HawkesBeta { get; init; }
    [JsonPropertyName("hawkes_events")] public int? HawkesEventsConsidered { get; init; }
}

// Source-generated JSON context (AOT-friendly even though we currently
// publish non-AOT; keeping the discipline makes a later AOT switch trivial).
[JsonSerializable(typeof(ObserveMessage))]
[JsonSerializable(typeof(ShutdownMessage))]
[JsonSerializable(typeof(LogMessage))]
[JsonSerializable(typeof(PredictionMessage))]
[JsonSerializable(typeof(UsageBuckets))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
public partial class IpcJsonContext : JsonSerializerContext { }
