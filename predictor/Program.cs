using System;
using System.Text.Json;
using ClaudeUsageProjector.Predictor.Activity;
using ClaudeUsageProjector.Predictor.Adapters;
using ClaudeUsageProjector.Predictor.Hawkes;
using ClaudeUsageProjector.Predictor.Ipc;
using ClaudeUsageProjector.Predictor.Persistence;
using ClaudeUsageProjector.Predictor.Projection;
using ClaudeUsageProjector.Predictor.State;
using ClaudeUsageProjector.Predictor.Tiers;

// ccum-predictor — Phase 5 wiring.
//
// Reads line-delimited JSON messages from stdin:
//   {"v":1,"type":"observe","t":"...","cc":{...},"cx":null}
//   {"v":1,"type":"shutdown"}
//
// On each observe we update the rolling snapshot window, persist the new
// observation to history.jsonl, run Tier 1/2/3, and emit a prediction. In
// parallel the JSONL tail feeds Claude Code session events into the
// telemetry window for activity-mode classification and Hawkes intensity.
//
// On startup we restore prior state in two ways:
//   1. Any rows in %APPDATA%/Claude-Code-Usage-Monitor/predictor/history*.jsonl
//      are loaded into the ObservationWindow (CSM SQLite is migrated into
//      that file on first run as a one-time bootstrap).
//   2. The JSONL tail seeds each session file's offset to the position of
//      the first line whose timestamp is newer than now - 6h, so Hawkes
//      doesn't have to re-warm from cold on every relaunch.

PersistencePaths.EnsureRootExists();

var snapshots = new ObservationWindow();
var telemetry = new TelemetryWindow();
var activityDetector = new JsonlActivityDetector();
var monteCarlo = new MonteCarloProjectionEngine();
var hawkesScaler = new DefaultHawkesIntensityScaler();
var tier = new Tier1WeightedBurnRate(new PredictorOptions(), monteCarlo, hawkesScaler);

var historyWriter = new HistoryJsonlWriter();

// One-time-at-first-run migration from the CSM SQLite database.
var migrator = new CsmSqliteMigrator(historyWriter, Log);
var migrated = migrator.MigrateIfNeeded();
if (migrated is int m && m > 0)
{
    Log("info", $"persistence: seeded {m} rows from CSM SQLite");
}

// Restore the in-memory window from disk so the popup chart survives reboots.
var reader = new HistoryJsonlReader();
var persisted = reader.LoadAll(out var skipped);
if (persisted.Count > 0)
{
    snapshots.Seed(persisted, DateTimeOffset.UtcNow);
    Log("info", $"persistence: loaded {persisted.Count} snapshots (skipped {skipped} malformed)");

    // Replay loaded snapshots to the host as tier=0 ("backfill") prediction
    // messages so the popup's history line is populated immediately on a
    // fresh launch. The host's prediction_store sees tier=0 and pushes only
    // to history, leaving `latest` untouched for the live prediction that
    // arrives on the next observe.
    foreach (var s in snapshots.Snapshots)
    {
        if (!s.UsedPercent.HasValue) continue;
        var backfill = new PredictionMessage
        {
            TimestampUtc = FormatUtc(s.CapturedAtUtc),
            Tier = 0,
            Risk = "unknown",
            Stale = false,
            UsedPercent = s.UsedPercent,
            RefreshAtUtc = s.RefreshAtUtc is { } r ? FormatUtc(r) : null,
            ProbabilityEmptyBeforeRefresh = 0.0,
        };
        var backfillLine = JsonSerializer.Serialize(backfill, IpcJsonContext.Default.PredictionMessage);
        Console.Out.WriteLine(backfillLine);
    }
    Console.Out.Flush();
}
else if (skipped > 0)
{
    Log("warn", $"persistence: no usable rows ({skipped} malformed lines)");
}

var jsonlRoot = JsonlTail.DefaultRoot();
JsonlTail? tail = null;
try
{
    tail = new JsonlTail(jsonlRoot, telemetry, Log);
    tail.Start();
}
catch (Exception ex)
{
    Log("warn", $"jsonl tail failed to start: {ex.Message}");
}

Log("info", $"ccum-predictor v0.5.0 started (pid={Environment.ProcessId})");

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    string messageType;
    JsonDocument? doc;
    try
    {
        doc = JsonDocument.Parse(line);
        messageType = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : "(missing-type)";
    }
    catch (JsonException ex)
    {
        Log("warn", $"unparseable input: {ex.Message}");
        continue;
    }

    using (doc)
    {
        switch (messageType)
        {
            case "observe":
                HandleObserve(line, snapshots, telemetry, activityDetector, tier, historyWriter);
                break;
            case "shutdown":
                Log("info", "shutdown received");
                tail?.Dispose();
                historyWriter.Dispose();
                return;
            default:
                Log("warn", $"unknown message type: {messageType}");
                break;
        }
    }
}

Log("info", "stdin closed; exiting");
tail?.Dispose();
historyWriter.Dispose();
return;


static void HandleObserve(
    string rawLine,
    ObservationWindow snapshots,
    TelemetryWindow telemetry,
    IActivityDetector detector,
    Tier1WeightedBurnRate tier,
    HistoryJsonlWriter writer)
{
    ObserveMessage? observe;
    try
    {
        observe = JsonSerializer.Deserialize(rawLine, IpcJsonContext.Default.ObserveMessage);
    }
    catch (JsonException ex)
    {
        Log("warn", $"observe parse failed: {ex.Message}");
        return;
    }

    if (observe is null)
    {
        Log("warn", "observe deserialised to null");
        return;
    }

    if (!DateTimeOffset.TryParse(observe.TimestampUtc, out var capturedAt))
    {
        Log("warn", $"observe timestamp not parseable: {observe.TimestampUtc}");
        return;
    }
    capturedAt = capturedAt.ToUniversalTime();

    var cc = observe.ClaudeCode is null
        ? "cc=none"
        : $"cc 5h={observe.ClaudeCode.FiveHourPct:0.0}% 7d={observe.ClaudeCode.SevenDayPct:0.0}%";
    var cx = observe.Codex is null
        ? "cx=none"
        : $"cx 5h={observe.Codex.FiveHourPct:0.0}% 7d={observe.Codex.SevenDayPct:0.0}%";
    Log("info", $"observed @ {observe.TimestampUtc}  {cc}  {cx}");

    if (observe.ClaudeCode is null) return;

    DateTimeOffset? refreshAt = null;
    if (!string.IsNullOrEmpty(observe.ClaudeCode.ResetsAtUtc)
        && DateTimeOffset.TryParse(observe.ClaudeCode.ResetsAtUtc, out var parsedRefresh))
    {
        refreshAt = parsedRefresh.ToUniversalTime();
    }

    var snapshot = new UsageSnapshot
    {
        CapturedAtUtc = capturedAt,
        UsedPercent = observe.ClaudeCode.FiveHourPct,
        RefreshAtUtc = refreshAt,
    };
    snapshots.Add(snapshot);
    try
    {
        writer.Append(snapshot);
    }
    catch (Exception ex)
    {
        Log("warn", $"persistence: append failed -- {ex.Message}");
    }

    var telemetrySnapshot = telemetry.Snapshot();
    var activity = detector.Detect(telemetrySnapshot, capturedAt);
    var result = tier.Compute(snapshots.Snapshots, activity, telemetrySnapshot, capturedAt);
    EmitPrediction(result);
}

static void EmitPrediction(PredictionResult r)
{
    var message = new PredictionMessage
    {
        TimestampUtc = FormatUtc(r.ComputedAtUtc),
        Tier = r.Tier,
        Risk = r.Risk.ToString().ToLowerInvariant(),
        Reason = r.Reason,
        Stale = r.Stale,
        UsedPercent = r.UsedPercent,
        RefreshAtUtc = r.RefreshAtUtc is { } ra ? FormatUtc(ra) : null,
        RatePerMinute = r.WeightedBurnRate,
        RateStdDev = r.RateStdDev,
        ProjectedEmptyP50Utc = r.ProjectedEmptyP50AtUtc is { } p50 ? FormatUtc(p50) : null,
        ProjectedEmptyP75Utc = r.ProjectedEmptyP75AtUtc is { } p75 ? FormatUtc(p75) : null,
        ProjectedEmptyP90Utc = r.ProjectedEmptyP90AtUtc is { } p90 ? FormatUtc(p90) : null,
        ProbabilityEmptyBeforeRefresh = r.ProbabilityEmptyBeforeRefresh,
        ProjectedPercentAtRefresh = r.ProjectedPercentAtRefresh,
        ProjectedEmptyBeforeRefresh = r.ProjectedEmptyBeforeRefresh,
        Engine = r.Engine,
        ActivityMode = r.ActivityMode?.ToLowerInvariant(),
        ActiveSessionCount = r.ActiveSessionCount,
        RateFrozenFromIdle = r.RateFrozenFromIdle,
        HawkesIntensityRatio = r.HawkesIntensityRatio,
        HawkesMu = r.HawkesMu,
        HawkesAlpha = r.HawkesAlpha,
        HawkesBeta = r.HawkesBeta,
        HawkesEventsConsidered = r.HawkesEventsConsidered,
    };
    var line = JsonSerializer.Serialize(message, IpcJsonContext.Default.PredictionMessage);
    Console.Out.WriteLine(line);
    Console.Out.Flush();
}

static string FormatUtc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

static void Log(string level, string msg)
{
    var line = JsonSerializer.Serialize(new LogMessage { Level = level, Msg = msg }, IpcJsonContext.Default.LogMessage);
    Console.Out.WriteLine(line);
    Console.Out.Flush();
}
