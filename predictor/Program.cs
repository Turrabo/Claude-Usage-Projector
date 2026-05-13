using System.Text.Json;

// ccum-predictor v0.0.1 — Phase 0 skeleton
//
// IPC contract (line-delimited JSON over stdin/stdout):
//   Host -> Predictor:  {"v":1,"type":"observe", ...}
//                       {"v":1,"type":"shutdown"}
//   Predictor -> Host:  {"v":1,"type":"log","level":"info","msg":"..."}
//                       (future: prediction, chart_data)
//
// stdout is reserved for protocol messages. stderr is for unstructured
// diagnostic output the host may forward to its log file.

Console.Error.WriteLine($"ccum-predictor v0.0.1 starting (pid={Environment.ProcessId})");

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    // Phase 0 behaviour: echo every incoming line back as a log message.
    var echo = JsonSerializer.Serialize(new
    {
        v = 1,
        type = "log",
        level = "info",
        msg = $"echo: {line}"
    });
    Console.Out.WriteLine(echo);
    Console.Out.Flush();

    // Recognise shutdown so the host can drain cleanly.
    try
    {
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.TryGetProperty("type", out var t)
            && t.ValueKind == JsonValueKind.String
            && t.GetString() == "shutdown")
        {
            Console.Error.WriteLine("ccum-predictor: shutdown received");
            return;
        }
    }
    catch (JsonException)
    {
        // Not JSON. Phase 0 tolerates this; phase 1+ will be stricter.
    }
}

Console.Error.WriteLine("ccum-predictor: stdin closed, exiting");
