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

    public static string HistoryJsonl => Path.Combine(Root, "history.jsonl");
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
