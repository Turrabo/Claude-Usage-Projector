// Shared state for the hover popup. The predictor sidecar's reader thread
// `push`es each parsed PredictionMessage; the popup's WM_PAINT handler reads
// the latest snapshot plus a rolling history of (timestamp, used_pct, rate,
// hawkes_ratio) entries. Bounded to a few hours so the in-memory cost stays
// negligible.
//
// All public methods take a short-lived lock and copy out the data; the popup
// renderer must not hold a reference into the store across Win32 calls.

use std::collections::VecDeque;
use std::sync::{Mutex, OnceLock};

use crate::csm::ipc::PredictionMessage;

const HISTORY_LIMIT: usize = 600; // ~10 hours at one prediction per minute

#[derive(Clone, Debug)]
#[allow(dead_code)] // some fields read only by future popup expansion
pub struct HistoryEntry {
    pub computed_unix: i64,
    pub used_pct: Option<f64>,
    pub rate_per_min: Option<f64>,
    pub hawkes_ratio: Option<f64>,
    pub activity: String,
    pub frozen: bool,
}

#[derive(Clone, Debug, Default)]
#[allow(dead_code)] // some fields read only by future popup expansion
pub struct LatestPrediction {
    pub computed_unix: i64,
    pub tier: u32,
    pub risk: String,
    pub used_pct: Option<f64>,
    pub refresh_unix: Option<i64>,
    pub rate_per_min: Option<f64>,
    pub rate_stddev: Option<f64>,
    pub projected_p50_unix: Option<i64>,
    pub projected_p75_unix: Option<i64>,
    pub projected_p90_unix: Option<i64>,
    pub prob_empty_before_refresh: f64,
    pub projected_pct_at_refresh: Option<f64>,
    pub activity: String,
    pub frozen: bool,
    pub hawkes_ratio: Option<f64>,
    pub reason: Option<String>,
    pub stale: bool,
}

struct Inner {
    latest: Option<LatestPrediction>,
    history: VecDeque<HistoryEntry>,
}

pub struct PredictionStore {
    inner: Mutex<Inner>,
}

static STORE: OnceLock<PredictionStore> = OnceLock::new();

pub fn store() -> &'static PredictionStore {
    STORE.get_or_init(|| PredictionStore {
        inner: Mutex::new(Inner {
            latest: None,
            history: VecDeque::with_capacity(HISTORY_LIMIT),
        }),
    })
}

impl PredictionStore {
    pub fn push(&self, msg: &PredictionMessage) {
        let computed_unix = msg
            .t
            .as_deref()
            .and_then(parse_iso8601_unix)
            .unwrap_or(0);

        let entry = HistoryEntry {
            computed_unix,
            used_pct: msg.used_pct,
            rate_per_min: msg.rate_per_min,
            hawkes_ratio: msg.hawkes_ratio,
            activity: msg
                .activity
                .clone()
                .unwrap_or_else(|| "unknown".to_string()),
            frozen: msg.rate_frozen_from_idle.unwrap_or(false),
        };

        // tier=0 is the predictor's "backfill" marker emitted at startup for
        // each replayed historical observation. Push it into history so the
        // chart line is populated immediately, but leave `latest` alone —
        // there's no live projection or risk to display for a stale point.
        if msg.tier == 0 {
            if let Ok(mut inner) = self.inner.lock() {
                if inner.history.len() == HISTORY_LIMIT {
                    inner.history.pop_front();
                }
                inner.history.push_back(entry);
            }
            return;
        }

        let latest = LatestPrediction {
            computed_unix,
            tier: msg.tier,
            risk: msg.risk.clone(),
            used_pct: msg.used_pct,
            refresh_unix: msg.refresh_at.as_deref().and_then(parse_iso8601_unix),
            rate_per_min: msg.rate_per_min,
            rate_stddev: msg.rate_stddev,
            projected_p50_unix: msg
                .projected_empty_p50
                .as_deref()
                .and_then(parse_iso8601_unix),
            projected_p75_unix: msg
                .projected_empty_p75
                .as_deref()
                .and_then(parse_iso8601_unix),
            projected_p90_unix: msg
                .projected_empty_p90
                .as_deref()
                .and_then(parse_iso8601_unix),
            prob_empty_before_refresh: msg.prob_empty_before_refresh.unwrap_or(0.0),
            projected_pct_at_refresh: msg.projected_pct_at_refresh,
            activity: msg
                .activity
                .clone()
                .unwrap_or_else(|| "unknown".to_string()),
            frozen: msg.rate_frozen_from_idle.unwrap_or(false),
            hawkes_ratio: msg.hawkes_ratio,
            reason: msg.reason.clone(),
            stale: msg.stale.unwrap_or(false),
        };

        if let Ok(mut inner) = self.inner.lock() {
            if inner.history.len() == HISTORY_LIMIT {
                inner.history.pop_front();
            }
            inner.history.push_back(entry);
            inner.latest = Some(latest);
        }
    }

    pub fn snapshot(&self) -> (Option<LatestPrediction>, Vec<HistoryEntry>) {
        match self.inner.lock() {
            Ok(inner) => (inner.latest.clone(), inner.history.iter().cloned().collect()),
            Err(_) => (None, Vec::new()),
        }
    }
}

/// Parses an ISO 8601 'YYYY-MM-DDTHH:MM:SSZ' string to a Unix epoch second
/// count. Returns None on any parse failure. Lightweight: no chrono dep —
/// we only ever consume the format the predictor sidecar emits.
fn parse_iso8601_unix(s: &str) -> Option<i64> {
    // Expected: "YYYY-MM-DDTHH:MM:SSZ" (19 chars + 'Z')
    if s.len() < 20 || !s.ends_with('Z') {
        return None;
    }
    let bytes = s.as_bytes();
    let year: i32 = std::str::from_utf8(&bytes[0..4]).ok()?.parse().ok()?;
    let month: u32 = std::str::from_utf8(&bytes[5..7]).ok()?.parse().ok()?;
    let day: u32 = std::str::from_utf8(&bytes[8..10]).ok()?.parse().ok()?;
    let hour: u32 = std::str::from_utf8(&bytes[11..13]).ok()?.parse().ok()?;
    let minute: u32 = std::str::from_utf8(&bytes[14..16]).ok()?.parse().ok()?;
    let second: u32 = std::str::from_utf8(&bytes[17..19]).ok()?.parse().ok()?;

    // Howard Hinnant's date algorithm (public domain), in reverse — civil → days
    // since 1970-01-01 then convert to seconds.
    let y = if month <= 2 { year - 1 } else { year };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = (y - era * 400) as u32;
    let m_adj = if month > 2 { month - 3 } else { month + 9 };
    let doy = (153 * m_adj + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era as i64 * 146_097 + doe as i64 - 719_468;
    Some(days * 86_400 + hour as i64 * 3600 + minute as i64 * 60 + second as i64)
}
