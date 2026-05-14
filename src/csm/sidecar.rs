// Manages the C# predictor sidecar process.
//
// Lifecycle: `init` spawns `ccum-predictor.exe` from the host exe's
// directory, attaches stdin/stdout, and starts two background threads:
//   - reader: parses each predictor stdout line as a log message and
//     forwards it to diagnose::log.
//   - writer: drains an mpsc channel of outgoing lines, writes them to
//     the predictor's stdin with a trailing newline.
//
// `record_observation` is the entry point the host's poll loop calls. It
// formats an observation as JSON and pushes it onto the channel — never
// blocks the poll thread. If the sidecar failed to spawn (predictor exe
// missing, disabled by env, etc.) the call is a silent no-op.
//
// `shutdown` (called from main on app exit) sends a final shutdown
// message and gives the predictor up to a second to drain before the
// process tree closes.

use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Sender};
use std::sync::OnceLock;
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use std::os::windows::process::CommandExt;

use crate::csm::ipc::{ObserveMessage, ShutdownMessage, UsageBuckets};
use crate::diagnose;
use crate::models::{AppUsageData, UsageData};

const PREDICTOR_EXE: &str = "ccum-predictor.exe";
const CREATE_NO_WINDOW: u32 = 0x08000000;

struct Sidecar {
    sender: Sender<String>,
    // Kept alive for the process lifetime; dropping closes the channel and
    // ends the writer thread, which closes stdin and lets the predictor exit.
    _child: Child,
}

static SIDECAR: OnceLock<Option<Sidecar>> = OnceLock::new();

/// Spawn the predictor and set up the IPC threads. Call once at startup.
/// Idempotent — second and subsequent calls are no-ops.
pub fn init() {
    SIDECAR.get_or_init(|| match spawn() {
        Ok(s) => {
            diagnose::log("csm: predictor sidecar started");
            Some(s)
        }
        Err(err) => {
            diagnose::log(format!("csm: predictor sidecar disabled — {err}"));
            None
        }
    });
}

/// Forward a successful poll result to the predictor. Non-blocking; safe to
/// call from any thread. No-op if the sidecar isn't running.
pub fn record_observation(data: &AppUsageData) {
    let Some(Some(sidecar)) = SIDECAR.get() else {
        return;
    };

    let timestamp = format_iso8601_now();
    let observe = ObserveMessage::new(
        &timestamp,
        data.claude_code.as_ref().map(buckets_from),
        data.codex.as_ref().map(buckets_from),
    );
    let line = match serde_json::to_string(&observe) {
        Ok(s) => s,
        Err(err) => {
            diagnose::log(format!("csm: failed to encode observation — {err}"));
            return;
        }
    };
    let _ = sidecar.sender.send(line);
}

/// Best-effort graceful shutdown. Should be called from the main thread
/// on app exit (after the message loop returns).
pub fn shutdown() {
    let Some(Some(sidecar)) = SIDECAR.get() else {
        return;
    };
    let shutdown_msg = ShutdownMessage::new();
    if let Ok(line) = serde_json::to_string(&shutdown_msg) {
        let _ = sidecar.sender.send(line);
    }
    // Give the predictor a moment to drain before we drop the child handle.
    thread::sleep(Duration::from_millis(500));
}

fn spawn() -> Result<Sidecar, String> {
    let exe_path = locate_predictor()?;
    diagnose::log(format!("csm: spawning predictor at {}", exe_path.display()));

    let mut child = Command::new(&exe_path)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map_err(|e| format!("could not spawn predictor: {e}"))?;

    let stdin = child
        .stdin
        .take()
        .ok_or_else(|| "predictor stdin pipe missing".to_string())?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| "predictor stdout pipe missing".to_string())?;

    // Reader: dispatches each line by the "type" field. Prediction messages
    // are formatted as a single diagnose line for Phase 2; the popup window
    // (Phase 4) will subscribe to a richer channel.
    thread::spawn(move || {
        let reader = BufReader::new(stdout);
        for line in reader.lines() {
            let Ok(line) = line else { break };
            if line.is_empty() {
                continue;
            }
            match classify_message(&line) {
                Some("prediction") => {
                    match serde_json::from_str::<crate::csm::ipc::PredictionMessage>(&line) {
                        Ok(pred) => diagnose::log(format_prediction(&pred)),
                        Err(err) => diagnose::log(format!(
                            "predictor(prediction parse failed) {err}: {line}"
                        )),
                    }
                }
                Some("log") | None => {
                    match serde_json::from_str::<crate::csm::ipc::LogMessage>(&line) {
                        Ok(log) => diagnose::log(format!("predictor[{}] {}", log.level, log.msg)),
                        Err(_) => diagnose::log(format!("predictor(raw) {line}")),
                    }
                }
                Some(other) => {
                    diagnose::log(format!("predictor(unhandled type={other}) {line}"));
                }
            }
        }
        diagnose::log("csm: predictor stdout reader ended");
    });

    // Writer: serialises outgoing lines onto stdin from a channel so the
    // caller's thread never blocks on a slow pipe.
    let (tx, rx) = mpsc::channel::<String>();
    spawn_writer(stdin, rx);

    Ok(Sidecar {
        sender: tx,
        _child: child,
    })
}

fn spawn_writer(mut stdin: ChildStdin, rx: mpsc::Receiver<String>) {
    thread::spawn(move || {
        while let Ok(line) = rx.recv() {
            if writeln!(stdin, "{line}").is_err() {
                diagnose::log("csm: predictor stdin write failed; reader thread will exit");
                break;
            }
            let _ = stdin.flush();
        }
        diagnose::log("csm: predictor stdin writer ended");
    });
}

fn classify_message(line: &str) -> Option<&str> {
    // Cheap discriminator without allocating: scan once for `"type":"..."`. We
    // avoid a full JSON parse on every line because the writer thread already
    // pays for the line decode below, and this lets us bail out early on
    // malformed lines.
    let key = "\"type\":\"";
    let start = line.find(key)? + key.len();
    let rest = &line[start..];
    let end = rest.find('"')?;
    Some(&rest[..end])
}

fn format_prediction(p: &crate::csm::ipc::PredictionMessage) -> String {
    let pct = p
        .used_pct
        .map(|v| format!("{v:.1}%"))
        .unwrap_or_else(|| "?".to_string());
    let rate = p
        .rate_per_min
        .map(|v| format!("{v:.3}/min"))
        .unwrap_or_else(|| "?/min".to_string());
    let p50 = p
        .projected_empty_p50
        .as_deref()
        .unwrap_or("none");
    let prob = p.prob_empty_before_refresh.unwrap_or(0.0);
    let stale = p.stale.unwrap_or(false);
    let reason = p.reason.as_deref().unwrap_or("");
    format!(
        "predictor[pred] tier={} risk={} used={} rate={} p50={} pE={:.2} stale={}{}",
        p.tier,
        p.risk,
        pct,
        rate,
        p50,
        prob,
        stale,
        // ASCII separator only — the diagnose log file is read by viewers that
        // interpret it as Windows-1252, which would otherwise mangle UTF-8.
        if reason.is_empty() {
            String::new()
        } else {
            format!(" -- {reason}")
        }
    )
}

fn locate_predictor() -> Result<std::path::PathBuf, String> {
    let host_exe =
        std::env::current_exe().map_err(|e| format!("current_exe failed: {e}"))?;
    let dir = host_exe
        .parent()
        .ok_or_else(|| "host exe has no parent directory".to_string())?;
    let candidate = dir.join(PREDICTOR_EXE);
    if candidate.is_file() {
        return Ok(candidate);
    }
    Err(format!(
        "predictor exe not found at {} (expected co-located with host)",
        candidate.display()
    ))
}

fn buckets_from(usage: &UsageData) -> UsageBuckets {
    UsageBuckets {
        five_hour: usage.session.percentage,
        seven_day: usage.weekly.percentage,
        resets_at: usage.session.resets_at.and_then(system_time_to_iso8601),
    }
}

fn format_iso8601_now() -> String {
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    unix_secs_to_iso8601(secs)
}

fn system_time_to_iso8601(t: SystemTime) -> Option<String> {
    let secs = t.duration_since(UNIX_EPOCH).ok()?.as_secs() as i64;
    Some(unix_secs_to_iso8601(secs))
}

/// Format a Unix epoch second count as `YYYY-MM-DDTHH:MM:SSZ`. Avoids
/// pulling in `chrono` for what amounts to a single timestamp serialisation.
fn unix_secs_to_iso8601(secs: i64) -> String {
    // Days from epoch and seconds-of-day.
    let days = secs.div_euclid(86_400);
    let secs_of_day = secs.rem_euclid(86_400);
    let hour = (secs_of_day / 3600) as u32;
    let minute = ((secs_of_day % 3600) / 60) as u32;
    let second = (secs_of_day % 60) as u32;

    // Convert days since 1970-01-01 to civil date using Howard Hinnant's
    // algorithm (public domain, well-known).
    let z = days + 719_468;
    let era = if z >= 0 { z / 146_097 } else { (z - 146_096) / 146_097 };
    let doe = (z - era * 146_097) as u32; // [0, 146096]
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365; // [0, 399]
    let y = yoe as i32 + era as i32 * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100); // [0, 365]
    let mp = (5 * doy + 2) / 153; // [0, 11]
    let d = doy - (153 * mp + 2) / 5 + 1; // [1, 31]
    let m = if mp < 10 { mp + 3 } else { mp - 9 }; // [1, 12]
    let year = if m <= 2 { y + 1 } else { y };

    format!(
        "{year:04}-{m:02}-{d:02}T{hour:02}:{minute:02}:{second:02}Z"
    )
}
