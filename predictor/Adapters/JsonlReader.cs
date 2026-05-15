using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ClaudeUsageProjector.Predictor.State;

namespace ClaudeUsageProjector.Predictor.Adapters;

public sealed record JsonlReadResult(
    IReadOnlyList<TelemetryEvent> Events,
    long NewPosition,
    int SkippedLines,
    bool FileTruncated);

/// <summary>
/// Pure JSONL parser. Reads from a known byte offset, parses newline-delimited
/// JSON, extracts assistant-message records with `usage` fields into
/// TelemetryEvents, and returns the new offset at the end of the last
/// *complete* line. A partial trailing line (no terminating newline) is
/// intentionally not consumed so the caller can pick it up next time the file
/// grows.
/// </summary>
public sealed class JsonlReader
{
    public JsonlReadResult ReadFrom(Stream stream, long startPosition, string sourceFile, DateTimeOffset capturedAtUtc)
    {
        var length = stream.Length;
        if (length < startPosition)
        {
            return new JsonlReadResult(Array.Empty<TelemetryEvent>(), 0, 0, FileTruncated: true);
        }
        if (length == startPosition)
        {
            return new JsonlReadResult(Array.Empty<TelemetryEvent>(), startPosition, 0, false);
        }

        stream.Seek(startPosition, SeekOrigin.Begin);
        var available = (int)Math.Min(length - startPosition, int.MaxValue);
        var buf = new byte[available];
        var read = stream.Read(buf, 0, buf.Length);

        var events = new List<TelemetryEvent>();
        int skipped = 0;
        int searchStart = 0;
        int lastNewlineEnd = 0;

        while (searchStart < read)
        {
            var rel = Array.IndexOf(buf, (byte)'\n', searchStart, read - searchStart);
            if (rel < 0) break;
            var lineLen = rel - searchStart;
            if (lineLen > 0 && buf[rel - 1] == (byte)'\r') lineLen--;
            if (lineLen > 0)
            {
                var line = new ReadOnlySpan<byte>(buf, searchStart, lineLen);
                if (TryParseAssistantUsage(line, sourceFile, capturedAtUtc, out var evt))
                {
                    events.Add(evt!);
                }
                else
                {
                    skipped++;
                }
            }
            searchStart = rel + 1;
            lastNewlineEnd = searchStart;
        }

        return new JsonlReadResult(events, startPosition + lastNewlineEnd, skipped, false);
    }

    internal static bool TryParseAssistantUsage(
        ReadOnlySpan<byte> jsonLine,
        string sourceFile,
        DateTimeOffset fallbackCapturedAtUtc,
        out TelemetryEvent? evt)
    {
        evt = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("type", out var typeProp) ||
                typeProp.ValueKind != JsonValueKind.String ||
                !string.Equals(typeProp.GetString(), "assistant", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != JsonValueKind.Object)
                return false;
            if (!msgEl.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
                return false;

            var inputTokens = TryGetLong(usageEl, "input_tokens");
            var outputTokens = TryGetLong(usageEl, "output_tokens");
            var cacheRead = TryGetLong(usageEl, "cache_read_input_tokens")
                            ?? TryGetLong(usageEl, "cache_read_tokens");
            var cacheWrite = TryGetLong(usageEl, "cache_creation_input_tokens")
                             ?? TryGetLong(usageEl, "cache_creation_tokens")
                             ?? TryGetLong(usageEl, "cache_write_tokens");

            if (inputTokens is null && outputTokens is null) return false;

            string? model = msgEl.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;

            string? sessionId = null;
            if (root.TryGetProperty("sessionId", out var sId) && sId.ValueKind == JsonValueKind.String)
                sessionId = sId.GetString();
            else if (root.TryGetProperty("session_id", out var sIdSnake) && sIdSnake.ValueKind == JsonValueKind.String)
                sessionId = sIdSnake.GetString();

            var captured = fallbackCapturedAtUtc;
            if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(
                        ts.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    captured = parsed;
                }
            }

            evt = new TelemetryEvent
            {
                CapturedAtUtc = captured,
                SourceId = "jsonl",
                EventType = "assistant_message",
                SessionId = sessionId,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                Model = model,
                Notes = sourceFile
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? TryGetLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var v)) return v;
        return null;
    }
}
