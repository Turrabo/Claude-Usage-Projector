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
