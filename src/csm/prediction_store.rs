// Per-account state for the hover popup. The predictor sidecar's reader
// thread `push`es each parsed PredictionMessage; routing is by `account_id`
// (IPC v:2, see DECISIONS.md ADR-011). `snapshot()` returns the data for the
// account currently active on this machine, derived from the local Claude
// credentials file via `account_id::current_account_id`. Phase 7a.4 plumbing
// only — the popup and badge still see one account; multi-account UI is
// Phase 7c.
//
// All public methods take a short-lived lock and copy out the data; the popup
// renderer must not hold a reference into the store across Win32 calls.

use std::collections::{HashMap, VecDeque};
use std::sync::{Mutex, OnceLock};

use crate::csm::account_id::{current_account_id, DEFAULT_ACCOUNT_ID};
use crate::csm::ipc::PredictionMessage;

// Per-account cap. With three real accounts plus the `acct_default` bucket
// (which holds pre-multi-auth backfill until Phase 7b migrates it) the upper
// bound is ~4×600 = 2400 entries — still negligible memory; each entry is a
// handful of words.
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

#[derive(Default)]
struct AccountState {
    latest: Option<LatestPrediction>,
    history: VecDeque<HistoryEntry>,
}

struct Inner {
    by_account: HashMap<String, AccountState>,
}

pub struct PredictionStore {
    inner: Mutex<Inner>,
}

static STORE: OnceLock<PredictionStore> = OnceLock::new();

pub fn store() -> &'static PredictionStore {
    STORE.get_or_init(|| PredictionStore {
        inner: Mutex::new(Inner {
            by_account: HashMap::new(),
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

        // Route by account_id. A v:1 predictor (or a v:2 predictor that
        // couldn't determine the account) leaves the field null; we map
        // that to the same sentinel the predictor uses for its observe
        // routing, so both sides end up keyed identically.
        let account_id = msg
            .account_id
            .as_deref()
            .unwrap_or(DEFAULT_ACCOUNT_ID)
            .to_string();

        let Ok(mut inner) = self.inner.lock() else {
            return;
        };
        let state = inner.by_account.entry(account_id).or_default();

        if state.history.len() == HISTORY_LIMIT {
            state.history.pop_front();
        }
        state.history.push_back(entry);

        // tier=0 is the predictor's "backfill" marker emitted at startup for
        // each replayed historical observation. Push it into history so the
        // chart line is populated immediately, but leave `latest` alone —
        // there's no live projection or risk to display for a stale point.
        if msg.tier == 0 {
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
        state.latest = Some(latest);
    }

    /// Latest live prediction + rolling history for the account currently
    /// active on this machine. "Active" resolves via
    /// `account_id::current_account_id`, falling back to
    /// `DEFAULT_ACCOUNT_ID` when credentials are unreadable — matching the
    /// predictor's own fallback so both sides agree on the bucket.
    /// Returns empty when that account hasn't received any predictions yet.
    ///
    /// `badge.rs` and the popover chart in `popup.rs` keep seeing the
    /// active account only. The per-account popover table added in
    /// Phase 7c uses `accounts()` + `snapshot_for_account` to enumerate
    /// rows.
    pub fn snapshot(&self) -> (Option<LatestPrediction>, Vec<HistoryEntry>) {
        let active = current_account_id().unwrap_or_else(|| DEFAULT_ACCOUNT_ID.to_string());
        self.snapshot_for_account(&active)
    }

    /// Same as `snapshot()` but for an explicit account id. Returns
    /// `(None, vec![])` if the account hasn't been seen yet. Public from
    /// Phase 7c so `popup.rs` can pull per-row latest predictions for the
    /// per-account table.
    pub fn snapshot_for_account(
        &self,
        account_id: &str,
    ) -> (Option<LatestPrediction>, Vec<HistoryEntry>) {
        match self.inner.lock() {
            Ok(inner) => match inner.by_account.get(account_id) {
                Some(state) => (
                    state.latest.clone(),
                    state.history.iter().cloned().collect(),
                ),
                None => (None, Vec::new()),
            },
            Err(_) => (None, Vec::new()),
        }
    }

    /// Stable-sorted list of account ids that have received at least one
    /// prediction (live or backfill) in this process. Phase 7c uses this
    /// to enumerate popover table rows. The sort order is alphabetical so
    /// the row order is stable across paint ticks — important because the
    /// popup repaints every few seconds and a churning row order would
    /// read as flicker.
    pub fn accounts(&self) -> Vec<String> {
        match self.inner.lock() {
            Ok(inner) => {
                let mut ids: Vec<String> = inner.by_account.keys().cloned().collect();
                ids.sort();
                ids
            }
            Err(_) => Vec::new(),
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

#[cfg(test)]
mod tests {
    use super::*;

    fn msg(account: Option<&str>, tier: u32, used: f64) -> PredictionMessage {
        PredictionMessage {
            v: Some(2),
            kind: Some("prediction".into()),
            t: Some("2026-05-21T10:00:00Z".into()),
            account_id: account.map(str::to_string),
            tier,
            risk: "green".into(),
            reason: None,
            stale: None,
            used_pct: Some(used),
            refresh_at: None,
            rate_per_min: None,
            rate_stddev: None,
            projected_empty_p50: None,
            projected_empty_p75: None,
            projected_empty_p90: None,
            prob_empty_before_refresh: None,
            projected_pct_at_refresh: None,
            projected_empty_before_refresh: None,
            engine: None,
            activity: None,
            active_sessions: None,
            rate_frozen_from_idle: None,
            hawkes_ratio: None,
            hawkes_mu: None,
            hawkes_alpha: None,
            hawkes_beta: None,
            hawkes_events: None,
        }
    }

    fn fresh_store() -> PredictionStore {
        PredictionStore {
            inner: Mutex::new(Inner {
                by_account: HashMap::new(),
            }),
        }
    }

    #[test]
    fn push_routes_by_account_id() {
        let store = fresh_store();
        store.push(&msg(Some("acct_AAAA"), 2, 10.0));
        store.push(&msg(Some("acct_BBBB"), 2, 90.0));

        let (a_latest, _) = store.snapshot_for_account("acct_AAAA");
        let (b_latest, _) = store.snapshot_for_account("acct_BBBB");
        assert_eq!(a_latest.unwrap().used_pct, Some(10.0));
        assert_eq!(b_latest.unwrap().used_pct, Some(90.0));
    }

    #[test]
    fn push_with_missing_account_id_uses_default_sentinel() {
        let store = fresh_store();
        store.push(&msg(None, 2, 42.0));
        let (latest, _) = store.snapshot_for_account(DEFAULT_ACCOUNT_ID);
        assert_eq!(latest.unwrap().used_pct, Some(42.0));
    }

    #[test]
    fn other_accounts_are_isolated() {
        // Pushing to A must not surface in B — this is the regression the
        // 7a.4 refactor exists to prevent (pre-refactor the single global
        // `latest` would be overwritten by whichever account pushed last).
        let store = fresh_store();
        store.push(&msg(Some("acct_AAAA"), 2, 10.0));
        let (b_latest, b_hist) = store.snapshot_for_account("acct_BBBB");
        assert!(b_latest.is_none());
        assert!(b_hist.is_empty());
    }

    #[test]
    fn tier_zero_backfill_appends_history_but_not_latest() {
        let store = fresh_store();
        store.push(&msg(Some("acct_AAAA"), 0, 5.0));
        let (latest, hist) = store.snapshot_for_account("acct_AAAA");
        assert!(latest.is_none(), "tier=0 must not set latest");
        assert_eq!(hist.len(), 1);
    }

    #[test]
    fn unknown_account_snapshot_is_empty() {
        // Querying an account that has received no predictions returns the
        // empty (None, vec![]) tuple — the popup renderer relies on this to
        // hit its "no snapshots yet" hint rather than panic on a missing key.
        let store = fresh_store();
        let (latest, hist) = store.snapshot_for_account("acct_unseen");
        assert!(latest.is_none());
        assert!(hist.is_empty());
    }

    #[test]
    fn interleaved_pushes_stay_isolated() {
        // A, B, A, B, A — each account's history should grow independently
        // without cross-contamination, and each account's `latest` should
        // reflect its own most recent push.
        let store = fresh_store();
        store.push(&msg(Some("acct_AAAA"), 2, 10.0));
        store.push(&msg(Some("acct_BBBB"), 2, 50.0));
        store.push(&msg(Some("acct_AAAA"), 2, 11.0));
        store.push(&msg(Some("acct_BBBB"), 2, 51.0));
        store.push(&msg(Some("acct_AAAA"), 2, 12.0));

        let (a_latest, a_hist) = store.snapshot_for_account("acct_AAAA");
        let (b_latest, b_hist) = store.snapshot_for_account("acct_BBBB");
        assert_eq!(a_latest.unwrap().used_pct, Some(12.0));
        assert_eq!(b_latest.unwrap().used_pct, Some(51.0));
        assert_eq!(a_hist.len(), 3);
        assert_eq!(b_hist.len(), 2);
    }

    #[test]
    fn history_limit_is_per_account() {
        let store = fresh_store();
        for _ in 0..(HISTORY_LIMIT + 5) {
            store.push(&msg(Some("acct_AAAA"), 2, 1.0));
        }
        for _ in 0..3 {
            store.push(&msg(Some("acct_BBBB"), 2, 1.0));
        }
        let (_, a_hist) = store.snapshot_for_account("acct_AAAA");
        let (_, b_hist) = store.snapshot_for_account("acct_BBBB");
        assert_eq!(a_hist.len(), HISTORY_LIMIT, "A capped at limit");
        assert_eq!(b_hist.len(), 3, "B's count unaffected by A's overflow");
    }

    #[test]
    fn accounts_empty_on_fresh_store() {
        let store = fresh_store();
        assert!(store.accounts().is_empty());
    }

    #[test]
    fn accounts_returns_all_seen_ids_sorted() {
        let store = fresh_store();
        store.push(&msg(Some("acct_ZZZZ"), 2, 1.0));
        store.push(&msg(Some("acct_AAAA"), 2, 1.0));
        store.push(&msg(Some("acct_MMMM"), 0, 1.0)); // backfill also counts
        store.push(&msg(None, 2, 1.0));              // → acct_default

        let accounts = store.accounts();
        // Stable alphabetical order so the popover table doesn't flicker
        // between paint ticks.
        assert_eq!(accounts, vec!["acct_AAAA", "acct_MMMM", "acct_ZZZZ", DEFAULT_ACCOUNT_ID]);
    }

    #[test]
    fn accounts_does_not_include_unpushed_ids() {
        // Calling snapshot_for_account with an unknown id must not register it.
        let store = fresh_store();
        store.push(&msg(Some("acct_real"), 2, 1.0));
        let _ = store.snapshot_for_account("acct_phantom");
        assert_eq!(store.accounts(), vec!["acct_real"]);
    }
}
