using System.Collections.Generic;
using System;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Activity;

public interface IActivityDetector
{
    ActivitySignal Detect(IReadOnlyList<TelemetryEvent> recentTelemetry, DateTimeOffset now);
}
