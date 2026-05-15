using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Persistence;

/// <summary>
/// Append-only JSONL writer for UsageSnapshots. One line per observation.
/// Rotates the file when it grows past <see cref="RotationThresholdBytes"/>
/// by renaming to history-{unix}.jsonl and starting a fresh file.
/// <para/>
/// Single-writer assumption: only the predictor process appends. We open the
/// file with FileShare.Read so external tooling (tail, less) can follow it.
/// </summary>
public sealed class HistoryJsonlWriter : IDisposable
{
    public const long RotationThresholdBytes = 30L * 1024 * 1024; // 30 MB per ADR-004

    private readonly string _path;
    private readonly object _lock = new();
    private FileStream? _stream;
    private long _bytesWritten;

    public HistoryJsonlWriter(string? overridePath = null)
    {
        _path = overridePath ?? PersistencePaths.HistoryJsonl;
    }

    public string Path => _path;

    public void Append(UsageSnapshot snapshot)
    {
        if (!snapshot.UsedPercent.HasValue) return; // no value to persist
        var persisted = new PersistedSnapshot
        {
            CapturedAtUtc = FormatUtc(snapshot.CapturedAtUtc),
            UsedPercent = snapshot.UsedPercent,
            RefreshAtUtc = snapshot.RefreshAtUtc is { } r ? FormatUtc(r) : null,
        };

        var line = JsonSerializer.Serialize(persisted, PersistenceJsonContext.Default.PersistedSnapshot);
        AppendRaw(line);
    }

    public void AppendRaw(string line)
    {
        lock (_lock)
        {
            EnsureOpen();
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _bytesWritten += bytes.Length;
            if (_bytesWritten >= RotationThresholdBytes)
            {
                RotateLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void EnsureOpen()
    {
        if (_stream != null) return;
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        _bytesWritten = _stream.Length;
    }

    private void RotateLocked()
    {
        if (_stream == null) return;
        _stream.Dispose();
        _stream = null;

        var dir = System.IO.Path.GetDirectoryName(_path) ?? ".";
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rotated = System.IO.Path.Combine(dir, $"history-{stamp}.jsonl");
        try
        {
            File.Move(_path, rotated);
        }
        catch
        {
            // If rename fails (file held elsewhere) leave the file in place;
            // the next call will reopen it and continue appending. Worst case
            // we exceed the rotation threshold by a small margin.
        }
        _bytesWritten = 0;
    }

    private static string FormatUtc(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
}
