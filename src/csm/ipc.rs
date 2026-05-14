// IPC message types mirroring predictor/Ipc/Messages.cs. Line-delimited JSON
// over the predictor child's stdin/stdout. Every message carries `v` and
// `type` discriminators so the protocol can evolve safely.

use serde::{Deserialize, Serialize};

pub const PROTOCOL_VERSION: u32 = 1;

// ---------- Host -> Predictor ----------

#[derive(Serialize)]
pub struct ObserveMessage<'a> {
    pub v: u32,
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub t: &'a str,
    pub cc: Option<UsageBuckets>,
    pub cx: Option<UsageBuckets>,
}

impl<'a> ObserveMessage<'a> {
    pub fn new(timestamp_utc: &'a str, cc: Option<UsageBuckets>, cx: Option<UsageBuckets>) -> Self {
        Self {
            v: PROTOCOL_VERSION,
            kind: "observe",
            t: timestamp_utc,
            cc,
            cx,
        }
    }
}

#[derive(Serialize)]
pub struct ShutdownMessage {
    pub v: u32,
    #[serde(rename = "type")]
    pub kind: &'static str,
}

impl ShutdownMessage {
    pub fn new() -> Self {
        Self {
            v: PROTOCOL_VERSION,
            kind: "shutdown",
        }
    }
}

#[derive(Serialize)]
pub struct UsageBuckets {
    pub five_hour: f64,
    pub seven_day: f64,
    pub resets_at: Option<String>,
}

// ---------- Predictor -> Host ----------

#[derive(Deserialize, Debug)]
pub struct LogMessage {
    #[allow(dead_code)]
    pub v: Option<u32>,
    #[allow(dead_code)]
    #[serde(rename = "type")]
    pub kind: Option<String>,
    pub level: String,
    pub msg: String,
}

/// Mirror of `predictor/Ipc/Messages.cs::PredictionMessage`. Every field is
/// optional except `tier` and `risk` because Tier 1 leaves the Monte Carlo
/// fields null. The host currently only formats a subset for the diagnose
/// log; Phase 4's popup window will consume the remaining fields, so they're
/// allowed-dead-code rather than removed.
#[derive(Deserialize, Debug)]
#[allow(dead_code)]
pub struct PredictionMessage {
    #[allow(dead_code)]
    pub v: Option<u32>,
    #[allow(dead_code)]
    #[serde(rename = "type")]
    pub kind: Option<String>,
    pub t: Option<String>,
    pub tier: u32,
    pub risk: String,
    pub reason: Option<String>,
    pub stale: Option<bool>,
    pub used_pct: Option<f64>,
    pub refresh_at: Option<String>,
    pub rate_per_min: Option<f64>,
    pub rate_stddev: Option<f64>,
    pub projected_empty_p50: Option<String>,
    pub projected_empty_p75: Option<String>,
    pub projected_empty_p90: Option<String>,
    pub prob_empty_before_refresh: Option<f64>,
    pub projected_pct_at_refresh: Option<f64>,
    pub projected_empty_before_refresh: Option<bool>,
    pub engine: Option<String>,
}
