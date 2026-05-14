using System;

namespace ClaudeUsageProjector.Predictor.State;

public enum RiskLevel { Unknown, Low, Medium, High }

/// <summary>
/// Single point-in-time observation of the 5-hour usage bucket. Modelled after
/// CSM's UsageSnapshot but trimmed to the fields the predictor actually uses —
/// the storage layer's SQLite identity / source / weekly / parser-version fields
/// are intentionally dropped (no SQLite, single truth source, single parser).
/// </summary>
public sealed record UsageSnapshot
{
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public double? UsedPercent { get; init; }
    public DateTimeOffset? RefreshAtUtc { get; init; }
}
