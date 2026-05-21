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

// Phase 7a.3: per-account state. Each observation routes by `account_id`
// (from IPC v:2). The window AND the Tier1 engine are both per-account
// (lazy-created on first observe) because Tier1WeightedBurnRate holds an
// idle-rate cache internally — sharing one instance across accounts
// would smear account A's frozen rate onto account B's prediction. See
// DECISIONS.md ADR-011 for the model.
//
// TelemetryWindow + JsonlActivityDetector + the Hawkes scaler stay
// shared across accounts: they reflect this machine's session-timing
// data, which ADR-011 explicitly keeps machine-scoped (JsonlTail events
// aren't account-tagged on disk). MonteCarloProjectionEngine is shared
// because it's effectively stateless — only its internal Random is
// reused, and the predictor's IPC loop is single-threaded so calls
// serialise naturally.
var snapshotsByAccount = new Dictionary<string, ObservationWindow>();
var tiersByAccount = new Dictionary<string, Tier1WeightedBurnRate>();
var telemetry = new TelemetryWindow();
var activityDetector = new JsonlActivityDetector();
var monteCarlo = new MonteCarloProjectionEngine();
var hawkesScaler = new DefaultHawkesIntensityScaler();
var predictorOptions = new PredictorOptions();

// Sentinel for observations that arrive without an account_id (v:1 host
// paired with v:2 predictor, or the host couldn't read credentials.json).
// On startup we also load all pre-v:2 persisted history under this id;
// Phase 7b will migrate it to per-account shards via a tagged rewrite.
const string DefaultAccountId = "acct_default";

var historyWriter = new HistoryJsonlWriter();

// One-time-at-first-run migration from the CSM SQLite database.
var migrator = new CsmSqliteMigrator(historyWriter, Log);
var migrated = migrator.MigrateIfNeeded();
if (migrated is int m && m > 0)
{
    Log("info", $"persistence: seeded {m} rows from CSM SQLite");
}

// Restore the in-memory window from disk so the popup chart survives reboots.
// Phase 7a.3: all persisted rows go under DefaultAccountId because the
// flat history.jsonl format pre-dates per-account sharding. The next
// observe with a real account_id will create that account's own window
// and live data accrues there. Backfill predictions thus tag as
// DefaultAccountId — the host's prediction_store currently doesn't key
// by account so this is a no-op for UI today; Phase 7a.4 / 7c will fix.
var reader = new HistoryJsonlReader();
var persisted = reader.LoadAll(out var skipped);
if (persisted.Count > 0)
{
    var defaultSnapshots = new ObservationWindow();
    defaultSnapshots.Seed(persisted, DateTimeOffset.UtcNow);
    snapshotsByAccount[DefaultAccountId] = defaultSnapshots;
    Log("info", $"persistence: loaded {persisted.Count} snapshots (skipped {skipped} malformed) into {DefaultAccountId}");

    // Replay loaded snapshots to the host as tier=0 ("backfill") prediction
    // messages so the popup's history line is populated immediately on a
    // fresh launch. The host's prediction_store sees tier=0 and pushes only
    // to history, leaving `latest` untouched for the live prediction that
    // arrives on the next observe.
    foreach (var s in defaultSnapshots.Snapshots)
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
            AccountId = DefaultAccountId,
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

Log("info", $"ccum-predictor v0.6.0 started (pid={Environment.ProcessId})");

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
                HandleObserve(line, snapshotsByAccount, tiersByAccount, predictorOptions, monteCarlo, hawkesScaler, telemetry, activityDetector, historyWriter);
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
    Dictionary<string, ObservationWindow> snapshotsByAccount,
    Dictionary<string, Tier1WeightedBurnRate> tiersByAccount,
    PredictorOptions predictorOptions,
    MonteCarloProjectionEngine monteCarlo,
    IHawkesIntensityScaler hawkesScaler,
    TelemetryWindow telemetry,
    IActivityDetector detector,
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
    // Phase 7a.3: route observations to per-account windows. Missing
    // account_id (v:1 host or unreadable credentials) routes to a
    // sentinel "acct_default" so we never drop data.
    const string DefaultAccountId = "acct_default";
    var accountId = observe.AccountId ?? DefaultAccountId;
    var acct = $"acct={accountId}";
    Log("info", $"observed @ {observe.TimestampUtc}  {acct}  {cc}  {cx}");

    if (observe.ClaudeCode is null) return;

    if (!snapshotsByAccount.TryGetValue(accountId, out var snapshots))
    {
        snapshots = new ObservationWindow();
        snapshotsByAccount[accountId] = snapshots;
        Log("info", $"new account window: {accountId}");
    }
    if (!tiersByAccount.TryGetValue(accountId, out var tier))
    {
        // Per-account Tier1 instance — its internal idle-rate cache
        // must not be shared across accounts (otherwise account A's
        // frozen rate would smear onto B's projection).
        tier = new Tier1WeightedBurnRate(predictorOptions, monteCarlo, hawkesScaler);
        tiersByAccount[accountId] = tier;
    }

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
    EmitPrediction(result, accountId);
}

static void EmitPrediction(PredictionResult r, string accountId)
{
    var message = new PredictionMessage
    {
        TimestampUtc = FormatUtc(r.ComputedAtUtc),
        AccountId = accountId,
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
