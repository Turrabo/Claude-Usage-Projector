using System;
using System.Collections.Generic;
using System.Text.Json;
using ClaudeUsageProjector.Predictor.Activity;
using ClaudeUsageProjector.Predictor.Adapters;
using ClaudeUsageProjector.Predictor.Hawkes;
using ClaudeUsageProjector.Predictor.Ipc;
using ClaudeUsageProjector.Predictor.Persistence;
using ClaudeUsageProjector.Predictor.Projection;
using ClaudeUsageProjector.Predictor.State;
using ClaudeUsageProjector.Predictor.Tiers;

// ccum-predictor — Phase 7b wiring.
//
// Reads line-delimited JSON messages from stdin:
//   {"v":2,"type":"observe","t":"...","account_id":"acct_...","cc":{...},"cx":null}
//   {"v":2,"type":"shutdown"}
//
// On each observe we update the rolling snapshot window, persist the new
// observation to its per-account history shard, run Tier 1/2/3, and emit a
// prediction. In parallel the JSONL tail feeds Claude Code session events
// into the telemetry window for activity-mode classification and Hawkes
// intensity.
//
// On startup we restore prior state in three ways:
//   1. Any rows in per-account shards
//      %APPDATA%/Claude-Code-Usage-Monitor/predictor/history-<account>.jsonl
//      are loaded per account into their own ObservationWindow.
//   2. The pre-Phase-7b flat history.jsonl (if present) is migrated on the
//      first observe — see LegacyHistoryMigrator. Each row is tagged with
//      the active account_id the host supplies on that observe, and re-
//      sharded into history-<active>.jsonl. The legacy file is renamed to
//      history.jsonl.pre-multi-auth-backup.
//   3. The JSONL tail seeds each session file's offset to the position of
//      the first line whose timestamp is newer than now - 6h, so Hawkes
//      doesn't have to re-warm from cold on every relaunch.
// CSM SQLite migration (one-time first-run bootstrap from the predecessor
// project) writes its rows into the legacy un-sharded history.jsonl; the
// Phase 7b migrator picks them up on the next observe.

PersistencePaths.EnsureRootExists();

// Phase 7a.3: per-account state. Each observation routes by `account_id`
// (from IPC v:2). The window AND the Tier1 engine are both per-account
// (lazy-created on first observe) because Tier1WeightedBurnRate holds an
// idle-rate cache internally — sharing one instance across accounts
// would smear account A's frozen rate onto account B's prediction. See
// DECISIONS.md ADR-011 for the model.
//
// Phase 7b: HistoryJsonlWriter is also per-account, keyed by accountId.
// Each writer owns its own history-<account>.jsonl file.
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
var writersByAccount = new Dictionary<string, HistoryJsonlWriter>();
var telemetry = new TelemetryWindow();
var activityDetector = new JsonlActivityDetector();
var monteCarlo = new MonteCarloProjectionEngine();
var hawkesScaler = new DefaultHawkesIntensityScaler();
var predictorOptions = new PredictorOptions();
var legacyMigrator = new LegacyHistoryMigrator(Log);

// One-time-at-first-run migration from the CSM SQLite database. Writes to
// the legacy un-sharded history.jsonl path; the Phase 7b migrator will
// re-shard it on first observe under the active account_id.
var csmMigrator = new CsmSqliteMigrator(Log);
var migrated = csmMigrator.MigrateIfNeeded();
if (migrated is int m && m > 0)
{
    Log("info", $"persistence: seeded {m} rows from CSM SQLite into legacy history.jsonl (will re-shard on first observe)");
}

// Restore the in-memory windows from per-account shards so the popup chart
// survives reboots. Phase 7b: rows already in shards belong to known
// accounts; the legacy un-sharded file (if present) is left for first-
// observe migration so it can be tagged with the active account_id.
var reader = new HistoryJsonlReader();
var byAccount = reader.LoadAllByAccount(out var skipped);
int totalLoaded = 0;
foreach (var (accountId, persisted) in byAccount)
{
    if (persisted.Count == 0) continue;
    var window = new ObservationWindow();
    window.Seed(persisted, DateTimeOffset.UtcNow);
    snapshotsByAccount[accountId] = window;
    totalLoaded += persisted.Count;

    EmitBackfill(window.Snapshots, accountId);
}
if (totalLoaded > 0)
{
    Log("info", $"persistence: loaded {totalLoaded} snapshots across {snapshotsByAccount.Count} account shard(s) (skipped {skipped} malformed)");
}
else if (skipped > 0)
{
    Log("warn", $"persistence: no usable rows in any shard ({skipped} malformed lines)");
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
                HandleObserve(line, snapshotsByAccount, tiersByAccount, writersByAccount, legacyMigrator, predictorOptions, monteCarlo, hawkesScaler, telemetry, activityDetector);
                break;
            case "shutdown":
                Log("info", "shutdown received");
                tail?.Dispose();
                DisposeAllWriters(writersByAccount);
                return;
            default:
                Log("warn", $"unknown message type: {messageType}");
                break;
        }
    }
}

Log("info", "stdin closed; exiting");
tail?.Dispose();
DisposeAllWriters(writersByAccount);
return;


static void HandleObserve(
    string rawLine,
    Dictionary<string, ObservationWindow> snapshotsByAccount,
    Dictionary<string, Tier1WeightedBurnRate> tiersByAccount,
    Dictionary<string, HistoryJsonlWriter> writersByAccount,
    LegacyHistoryMigrator legacyMigrator,
    PredictorOptions predictorOptions,
    MonteCarloProjectionEngine monteCarlo,
    IHawkesIntensityScaler hawkesScaler,
    TelemetryWindow telemetry,
    IActivityDetector detector)
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
    // account_id (v:1 host or unreadable credentials) routes to the
    // top-level `LegacyDefaultAccountId` sentinel so we never drop data.
    var accountId = observe.AccountId ?? PersistencePaths.LegacyDefaultAccountId;
    Log("info", $"observed @ {observe.TimestampUtc}  acct={accountId}  {cc}  {cx}");

    if (observe.ClaudeCode is null) return;

    // Phase 7b: if the pre-Phase-7b flat history.jsonl is still on disk,
    // migrate it now under THIS observe's account_id, then immediately
    // seed the in-memory window and emit backfill for the migrated rows.
    // First-observe timing means the user sees a populated chart within
    // a second or two of widget launch, not the multi-second gap we'd
    // see if migration ran at startup tagged as acct_default.
    if (legacyMigrator.MigrationNeeded())
    {
        var migratedRows = legacyMigrator.MigrateIfNeeded(accountId);
        if (migratedRows.Count > 0)
        {
            if (!snapshotsByAccount.TryGetValue(accountId, out var seedWindow))
            {
                seedWindow = new ObservationWindow();
                snapshotsByAccount[accountId] = seedWindow;
            }
            seedWindow.Seed(migratedRows, capturedAt);
            EmitBackfill(seedWindow.Snapshots, accountId);
        }
    }

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
    if (!writersByAccount.TryGetValue(accountId, out var writer))
    {
        writer = new HistoryJsonlWriter(accountId);
        writersByAccount[accountId] = writer;
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

static void EmitBackfill(IEnumerable<UsageSnapshot> snapshots, string accountId)
{
    // Replay loaded/migrated snapshots to the host as tier=0 ("backfill")
    // prediction messages so the popup's history line is populated
    // immediately. The host's prediction_store sees tier=0 and pushes only
    // to history, leaving `latest` untouched for the live prediction that
    // arrives on the next observe.
    foreach (var s in snapshots)
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
            AccountId = accountId,
        };
        var backfillLine = JsonSerializer.Serialize(backfill, IpcJsonContext.Default.PredictionMessage);
        Console.Out.WriteLine(backfillLine);
    }
    Console.Out.Flush();
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

static void DisposeAllWriters(Dictionary<string, HistoryJsonlWriter> writers)
{
    foreach (var w in writers.Values)
    {
        try { w.Dispose(); } catch { /* best-effort cleanup on shutdown */ }
    }
    writers.Clear();
}

static string FormatUtc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

static void Log(string level, string msg)
{
    var line = JsonSerializer.Serialize(new LogMessage { Level = level, Msg = msg }, IpcJsonContext.Default.LogMessage);
    Console.Out.WriteLine(line);
    Console.Out.Flush();
}
