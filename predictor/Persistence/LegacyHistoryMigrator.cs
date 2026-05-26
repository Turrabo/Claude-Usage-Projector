using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// One-time migration of the pre-Phase-7b flat <c>history.jsonl</c> into a
/// per-account shard. Runs at first observe (not at startup) so the
/// predictor can tag legacy rows with the active <c>account_id</c> the host
/// supplied on the IPC envelope, without the predictor reading
/// <c>credentials.json</c> itself.
/// <para/>
/// Idempotency: once migration completes, the legacy file is renamed to
/// <c>history.jsonl.pre-multi-auth-backup</c>. The absence of the legacy
/// file is sufficient to mean "nothing to migrate this tick" — even if
/// <see cref="CsmSqliteMigrator"/> later recreates the file on a future
/// launch with fresh CSM data, the second migration just runs again
/// against the new content.
/// <para/>
/// Failure modes (the migrator chooses safety over progress):
/// <list type="bullet">
///   <item>Read fails: log warning, leave files untouched, return empty.</item>
///   <item>Write fails part-way: the new shard may end up with duplicate
///     timestamps, but the source file isn't renamed so the next tick can
///     retry. The in-memory ObservationWindow tolerates duplicates by
///     timestamp.</item>
///   <item>Rename fails after successful write: log loudly, return what
///     was migrated; user can manually rename the source to avoid duplicates
///     on next launch.</item>
/// </list>
/// </summary>
public sealed class LegacyHistoryMigrator
{
    private readonly string _legacyPath;
    private readonly string _backupPath;
    private readonly string _root;
    private readonly Action<string, string>? _log;

    public LegacyHistoryMigrator(
        Action<string, string>? log = null,
        string? rootOverride = null)
    {
        _root = rootOverride ?? PersistencePaths.Root;
        _legacyPath = Path.Combine(_root, "history.jsonl");
        _backupPath = Path.Combine(_root, "history.jsonl.pre-multi-auth-backup");
        _log = log;
    }

    /// <summary>
    /// True when the legacy file is present — i.e., migration would do work
    /// if invoked. Cheap; safe to call on every observe.
    /// </summary>
    public bool MigrationNeeded() => File.Exists(_legacyPath);

    /// <summary>
    /// Migrates every parseable row from <c>history.jsonl</c> into
    /// <c>history-{activeAccountId}.jsonl</c>, tagging each row with
    /// <paramref name="activeAccountId"/>. Renames the source on success.
    /// Returns the migrated rows in time order (so the caller can seed the
    /// in-memory window and emit backfill PredictionMessages without doing
    /// a second disk pass). Empty if nothing was done.
    /// </summary>
    public IReadOnlyList<UsageSnapshot> MigrateIfNeeded(string activeAccountId)
    {
        if (string.IsNullOrWhiteSpace(activeAccountId))
        {
            throw new ArgumentException("activeAccountId must be non-empty", nameof(activeAccountId));
        }
        if (!File.Exists(_legacyPath))
        {
            return Array.Empty<UsageSnapshot>();
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_legacyPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log?.Invoke("warn", $"legacy-migrate: read failed -- {ex.Message}");
            return Array.Empty<UsageSnapshot>();
        }

        var shardPath = Path.Combine(_root, $"history-{activeAccountId}.jsonl");
        var migrated = new List<UsageSnapshot>(capacity: lines.Length);
        int skipped = 0;
        try
        {
            Directory.CreateDirectory(_root);
            using var shard = new FileStream(shardPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!TryParse(raw, out var parsed) || parsed!.UsedPercent is null)
                {
                    skipped++;
                    continue;
                }

                // Re-serialise tagged with the active account_id and v:2 so
                // the new shard is internally consistent.
                var retagged = parsed with
                {
                    Version = 2,
                    AccountId = activeAccountId,
                };
                var taggedLine = JsonSerializer.Serialize(retagged, PersistenceJsonContext.Default.PersistedSnapshot);
                var bytes = Encoding.UTF8.GetBytes(taggedLine + "\n");
                shard.Write(bytes, 0, bytes.Length);

                migrated.Add(ToSnapshot(parsed));
            }
            shard.Flush();
        }
        catch (Exception ex)
        {
            _log?.Invoke("warn", $"legacy-migrate: write failed after {migrated.Count} rows -- {ex.Message}. Source file left in place; next tick will retry.");
            return Array.Empty<UsageSnapshot>();
        }

        try
        {
            File.Move(_legacyPath, _backupPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke("warn", $"legacy-migrate: rename failed -- {ex.Message}. {migrated.Count} rows in shard but source still present; manual rename recommended to avoid duplicates on next launch.");
            return migrated;
        }

        var skipNote = skipped > 0 ? $" (skipped {skipped} malformed)" : "";
        _log?.Invoke("info", $"legacy-migrate: re-sharded {migrated.Count} rows under {activeAccountId}{skipNote}; legacy file backed up as history.jsonl.pre-multi-auth-backup");

        migrated.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
        return migrated;
    }

    private static bool TryParse(string raw, out PersistedSnapshot? parsed)
    {
        parsed = null;
        try
        {
            parsed = JsonSerializer.Deserialize(raw, PersistenceJsonContext.Default.PersistedSnapshot);
            return parsed is not null
                && !string.IsNullOrEmpty(parsed.CapturedAtUtc);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static UsageSnapshot ToSnapshot(PersistedSnapshot p)
    {
        DateTimeOffset.TryParse(
            p.CapturedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var captured);
        DateTimeOffset? refreshAt = null;
        if (!string.IsNullOrEmpty(p.RefreshAtUtc)
            && DateTimeOffset.TryParse(
                p.RefreshAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var r))
        {
            refreshAt = r;
        }
        return new UsageSnapshot
        {
            CapturedAtUtc = captured,
            UsedPercent = p.UsedPercent,
            RefreshAtUtc = refreshAt,
        };
    }
}
