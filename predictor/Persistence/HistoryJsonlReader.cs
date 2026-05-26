using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// Reads every persisted history JSONL file in the predictor's data folder.
/// Phase 7b — files are sharded by account
/// (<c>history-&lt;account_id&gt;.jsonl</c> for the current writer's output,
/// <c>history-&lt;account_id&gt;-&lt;unix&gt;.jsonl</c> for rotated files).
/// <see cref="LoadAllByAccount"/> returns a per-account map of time-ordered
/// snapshots; the legacy un-sharded <c>history.jsonl</c> file (if still
/// present) is intentionally NOT loaded here — Program.cs runs
/// <see cref="LegacyHistoryMigrator"/> to tag and re-shard it on first
/// observe before the next reader pass.
/// <para/>
/// Tolerates malformed lines (logged-and-skipped by the caller; we just
/// return what we can).
/// </summary>
public sealed class HistoryJsonlReader
{
    // Captures the account_id from the filename. Permissive on the account
    // body (the format is "acct_" + 12 hex chars in practice, but a
    // hand-edited file shouldn't be silently dropped). Allows either the
    // current-file form ("history-<acct>.jsonl") or the rotated form
    // ("history-<acct>-<unix>.jsonl") and ignores the unix suffix.
    private static readonly Regex ShardFileNamePattern = new(
        @"^history-(?<account>[A-Za-z0-9_]+?)(?:-\d+)?\.jsonl$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _root;

    public HistoryJsonlReader(string? overrideRoot = null)
    {
        _root = overrideRoot ?? PersistencePaths.Root;
    }

    /// <summary>
    /// Loads every per-account shard under the persistence root and returns
    /// a dictionary keyed by account_id. Each account's snapshots are sorted
    /// by capture timestamp. Returns an empty dictionary if the root is
    /// missing or contains no shards.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<UsageSnapshot>> LoadAllByAccount(out int skippedLines)
    {
        skippedLines = 0;
        var result = new Dictionary<string, List<UsageSnapshot>>();
        if (!Directory.Exists(_root))
        {
            return ToReadOnly(result);
        }

        var files = Directory.EnumerateFiles(_root, "history-*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);
            var match = ShardFileNamePattern.Match(name);
            // Filename gives us the account_id; we fall back to the row's
            // own account_id field if the filename doesn't match the pattern
            // (e.g. a hand-renamed file), then to PersistencePaths.LegacyDefaultAccountId
            // as a last resort.
            var accountFromFilename = match.Success ? match.Groups["account"].Value : null;

            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!TryParse(line, out var snap, out var rowAccount))
                    {
                        skippedLines++;
                        continue;
                    }

                    var account = accountFromFilename
                        ?? rowAccount
                        ?? PersistencePaths.LegacyDefaultAccountId;

                    if (!result.TryGetValue(account, out var list))
                    {
                        list = new List<UsageSnapshot>(capacity: 256);
                        result[account] = list;
                    }
                    list.Add(snap!);
                }
            }
            catch (IOException)
            {
                skippedLines++;
            }
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => a.CapturedAtUtc.CompareTo(b.CapturedAtUtc));
        }
        return ToReadOnly(result);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<UsageSnapshot>> ToReadOnly(
        Dictionary<string, List<UsageSnapshot>> result)
    {
        var ro = new Dictionary<string, IReadOnlyList<UsageSnapshot>>(result.Count);
        foreach (var kvp in result) ro[kvp.Key] = kvp.Value;
        return ro;
    }

    private static bool TryParse(string line, out UsageSnapshot? snap, out string? accountId)
    {
        snap = null;
        accountId = null;
        try
        {
            var persisted = JsonSerializer.Deserialize(line, PersistenceJsonContext.Default.PersistedSnapshot);
            if (persisted is null) return false;
            if (!DateTimeOffset.TryParse(
                    persisted.CapturedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var captured))
            {
                return false;
            }

            DateTimeOffset? refresh = null;
            if (!string.IsNullOrEmpty(persisted.RefreshAtUtc)
                && DateTimeOffset.TryParse(
                    persisted.RefreshAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                refresh = parsed;
            }

            snap = new UsageSnapshot
            {
                CapturedAtUtc = captured,
                UsedPercent = persisted.UsedPercent,
                RefreshAtUtc = refresh,
            };
            accountId = string.IsNullOrEmpty(persisted.AccountId) ? null : persisted.AccountId;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
