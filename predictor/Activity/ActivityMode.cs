namespace ClaudeUsageProjector.Predictor.Activity;

public enum ActivityMode
{
    /// <summary>No telemetry data available, or last activity is older than the idle horizon.</summary>
    Unknown,
    /// <summary>Recent telemetry exists but no events within the active threshold (between prompts).</summary>
    Idle,
    /// <summary>Telemetry events occurred within the active threshold (currently prompting).</summary>
    Active
}
