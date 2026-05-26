using System;
using System.IO;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// Central resolver for the predictor's on-disk paths. Keeps every other file
/// from caring about Environment.SpecialFolder / Path.Combine plumbing.
/// </summary>
public static class PersistencePaths
{
    public const string AppFolderName = "Claude-Code-Usage-Monitor";
    public const string SubFolderName = "predictor";

    /// <summary>%APPDATA%\Claude-Code-Usage-Monitor\predictor\</summary>
    public static string Root { get; } = ComputeRoot();

    /// <summary>
    /// Sentinel account id used both on disk and on the wire when the active
    /// account can't be determined (credentials.json missing/unreadable, or
    /// a v:1 host paired with a v:2 predictor). Mirrors the
    /// <c>DEFAULT_ACCOUNT_ID</c> constant on the Rust host side.
    /// </summary>
    public const string LegacyDefaultAccountId = "acct_default";

    /// <summary>
    /// Pre-Phase-7b flat history file. Migrated to a per-account shard by
    /// <see cref="LegacyHistoryMigrator"/> on first observe; once migration
    /// runs this file no longer exists (the backup is at
    /// <see cref="LegacyHistoryJsonlBackup"/>).
    /// </summary>
    public static string LegacyHistoryJsonl => Path.Combine(Root, "history.jsonl");

    /// <summary>
    /// Where the legacy <c>history.jsonl</c> is renamed to after migration.
    /// Existence of this file doubles as the migration-already-ran sentinel.
    /// </summary>
    public static string LegacyHistoryJsonlBackup =>
        Path.Combine(Root, "history.jsonl.pre-multi-auth-backup");

    public static string MigrationSentinel => Path.Combine(Root, ".csm-migrated");
    public static string CsmSqlite => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeSessionMonitor",
        "csm.sqlite");

    public static void EnsureRootExists()
    {
        try
        {
            Directory.CreateDirectory(Root);
        }
        catch
        {
            // Best-effort: the caller's open/write will fail with a clearer
            // error if the directory can't be created.
        }
    }

    private static string ComputeRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, AppFolderName, SubFolderName);
    }
}
