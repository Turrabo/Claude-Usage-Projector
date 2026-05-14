namespace ClaudeUsageProjector.Predictor.State;

/// <summary>
/// Tunable thresholds for risk classification. Defaults mirror the CSM
/// archive's AppSettings.Thresholds so behaviour ports verbatim. Configurable
/// via predictor settings later (Phase 6); kept as a record so a future
/// settings layer can swap whole instances in.
/// </summary>
public sealed record Thresholds
{
    public double HighRiskPercent { get; init; } = 90;
    public double MediumRiskPercent { get; init; } = 75;
    public int HighRiskMinutesUntilEmpty { get; init; } = 30;
    public int MediumRiskMinutesUntilEmpty { get; init; } = 90;
    public int RefreshSafetyMinutes { get; init; } = 5;
    public int MediumRiskWindowMinutes { get; init; } = 15;
}

/// <summary>
/// Staleness thresholds: how old (in minutes since the latest snapshot's
/// capture time) before we downgrade the computed risk or refuse to claim
/// any risk at all. Defaults mirror CSM AppSettings.Staleness.
/// </summary>
public sealed record StalenessSettings
{
    public int DowngradeAfterMinutes { get; init; } = 5;
    public int UnknownAfterMinutes { get; init; } = 15;
}

public sealed record PredictorOptions
{
    public Thresholds Thresholds { get; init; } = new();
    public StalenessSettings Staleness { get; init; } = new();
}
