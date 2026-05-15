using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Adapters;

/// <summary>
/// Polls Claude Code's session JSONL files for new lines and feeds parsed
/// assistant-message events into a TelemetryWindow. Polling (rather than
/// FileSystemWatcher) was chosen because the .jsonl files are often left
/// open for write by the Claude Code CLI; FSW's change-notification semantics
/// are unreliable for actively-written files, while a periodic length check
/// + read-from-offset is simple and correct.
/// <para/>
/// Scan root defaults to <c>%USERPROFILE%/.claude/projects/</c>, which is
/// where the Claude Code CLI persists session transcripts (one file per
/// session, named with the session GUID). The scan is recursive over project
/// folders.
/// </summary>
public sealed class JsonlTail : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _root;
    private readonly TelemetryWindow _window;
    private readonly Action<string, string>? _log;  // (level, message)
    private readonly JsonlReader _reader = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public JsonlTail(string root, TelemetryWindow window, Action<string, string>? log = null)
    {
        _root = root;
        _window = window;
        _log = log;
    }

    public static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "projects");
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "JsonlTail" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }

    private void Run()
    {
        try
        {
            // First scan: seed offsets at end-of-file so we don't ingest a flood of
            // historical events on startup. We only care about events from "now"
            // onward — the predictor's rate/Hawkes windows are short.
            SeedOffsets();
            _log?.Invoke("info", $"jsonl tail watching {_root} ({_offsets.Count} files)");

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    _log?.Invoke("warn", $"jsonl tail poll error: {ex.Message}");
                }

                _cts.Token.WaitHandle.WaitOne(PollInterval);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("warn", $"jsonl tail thread aborting: {ex.Message}");
        }
    }

    private void SeedOffsets()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in EnumerateJsonlFiles())
        {
            try
            {
                var len = new FileInfo(file).Length;
                _offsets[file] = len;
            }
            catch
            {
                // ignore — we'll retry on next poll
            }
        }
    }

    private void PollOnce()
    {
        if (!Directory.Exists(_root)) return;

        var capturedAt = DateTimeOffset.UtcNow;
        foreach (var file in EnumerateJsonlFiles())
        {
            try
            {
                var start = _offsets.TryGetValue(file, out var off) ? off : 0L;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var result = _reader.ReadFrom(fs, start, file, capturedAt);

                if (result.FileTruncated)
                {
                    _log?.Invoke("info", $"jsonl rotation detected, reseeking: {Path.GetFileName(file)}");
                    _offsets[file] = 0;
                    continue;
                }

                if (result.Events.Count > 0)
                {
                    _window.AddRange(result.Events);
                    _log?.Invoke("info", $"jsonl +{result.Events.Count} ev from {Path.GetFileName(file)}");
                }
                _offsets[file] = result.NewPosition;
            }
            catch (FileNotFoundException)
            {
                _offsets.Remove(file);
            }
            catch (IOException)
            {
                // File locked or in use — retry next poll
            }
        }
    }

    private IEnumerable<string> EnumerateJsonlFiles()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _log?.Invoke("warn", $"jsonl enumerate failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
