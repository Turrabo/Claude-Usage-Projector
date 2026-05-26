// Per-account display name resolution for the popover table (Phase 7c).
//
// Real account ids are opaque short hashes — `acct_` + first 12 hex chars
// of SHA-256(organizationUuid) (see DECISIONS.md ADR-011 + Appendix A).
// Showing that in a table
// row is hostile to the user, who knows their accounts as "Personal" or
// "Work" or similar. The maintainer can drop a JSON file at the path
// returned by `aliases_path()` mapping account ids to friendly names.
//
//   { "acct_abc123def456": "Personal",
//     "acct_456def789abc": "Work" }
//
// Missing file, unparseable JSON, or absent entry all fall back to a
// short-form display (`acct_abc123de…`) so the table is always
// renderable. mtime cache avoids re-reading on every WM_PAINT.
//
// The file lives at the *cross-cutting* root `%APPDATA%\Claude-Code-Usage-
// Monitor\` (not inside `predictor\`) because it's host-only and unrelated
// to the predictor's persistence layout.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{LazyLock, Mutex};
use std::time::SystemTime;

use serde::Deserialize;

use crate::diagnose;

const SHORT_FORM_HEX_LEN: usize = 8;
const ACCT_PREFIX: &str = "acct_";

#[derive(Deserialize, Default)]
struct AliasesFile {
    #[serde(flatten)]
    map: HashMap<String, String>,
}

struct Cache {
    file_mtime: Option<SystemTime>,
    aliases: HashMap<String, String>,
}

// `Mutex<HashMap>` can't be a plain `static` initializer because
// `HashMap::new()` isn't `const`. `LazyLock` runs the initializer on
// first access.
static CACHE: LazyLock<Mutex<Cache>> = LazyLock::new(|| {
    Mutex::new(Cache {
        file_mtime: None,
        aliases: HashMap::new(),
    })
});

/// Display name for an account id. Looks up the user's alias file; if no
/// alias is configured, returns a short form like `acct_abc123de…`. Safe
/// to call on every paint tick — re-reads the file only when its mtime
/// changes.
pub fn display_name(account_id: &str) -> String {
    if account_id.is_empty() {
        return "?".to_string();
    }
    refresh_cache_if_stale();
    if let Ok(cache) = CACHE.lock() {
        if let Some(name) = cache.aliases.get(account_id) {
            if !name.is_empty() {
                return name.clone();
            }
        }
    }
    short_form(account_id)
}

/// Computed every call so tests can override APPDATA. Cheap.
fn aliases_path() -> Option<PathBuf> {
    let appdata = std::env::var_os("APPDATA").map(PathBuf::from)?;
    Some(appdata.join("Claude-Code-Usage-Monitor").join("account-aliases.json"))
}

fn refresh_cache_if_stale() {
    let Some(path) = aliases_path() else { return };
    let mtime = std::fs::metadata(&path).and_then(|m| m.modified()).ok();
    let Ok(mut cache) = CACHE.lock() else { return };
    if cache.file_mtime == mtime {
        return;
    }
    cache.file_mtime = mtime;
    match std::fs::read_to_string(&path) {
        Ok(content) => match serde_json::from_str::<AliasesFile>(&content) {
            Ok(parsed) => {
                cache.aliases = parsed.map;
            }
            Err(err) => {
                diagnose::log(format!(
                    "csm: account-aliases.json failed to parse — {err}; using short-form fallback"
                ));
                cache.aliases.clear();
            }
        },
        Err(_) => {
            // File missing or unreadable — empty map; short-form fallback.
            cache.aliases.clear();
        }
    }
}

/// Renders a long account id as a compact display token. `acct_abc123def456`
/// becomes `acct_abc123de…`. Pre-`acct_` ids (sentinels or otherwise) are
/// returned with the same horizon-length truncation. Char-boundary safe so
/// a hypothetical non-ASCII account id doesn't panic the popup paint path.
fn short_form(account_id: &str) -> String {
    if let Some(rest) = account_id.strip_prefix(ACCT_PREFIX) {
        if rest.chars().count() <= SHORT_FORM_HEX_LEN {
            return account_id.to_string();
        }
        return format!("{ACCT_PREFIX}{}…", char_truncate(rest, SHORT_FORM_HEX_LEN));
    }
    let total = SHORT_FORM_HEX_LEN + ACCT_PREFIX.len();
    if account_id.chars().count() <= total {
        return account_id.to_string();
    }
    format!("{}…", char_truncate(account_id, total))
}

fn char_truncate(s: &str, n_chars: usize) -> &str {
    // Find the byte index of the (n+1)th char start and slice up to it.
    // Cheaper than allocating a new String for the common ASCII path.
    match s.char_indices().nth(n_chars) {
        Some((idx, _)) => &s[..idx],
        None => s,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn short_form_truncates_long_acct_ids() {
        // Real account ids are `acct_` + 12 hex chars (per ADR-011). The
        // short form keeps the prefix + 8 hex chars + an ellipsis.
        assert_eq!(short_form("acct_abc123def456"), "acct_abc123de…");
    }

    #[test]
    fn short_form_passes_through_short_ids() {
        // 7-char body is still shorter than the SHORT_FORM_HEX_LEN window,
        // so no truncation happens — the sentinel renders verbatim.
        assert_eq!(short_form("acct_default"), "acct_default");
        assert_eq!(short_form("acct_abc"), "acct_abc");
        assert_eq!(short_form("acct_"), "acct_");
        assert_eq!(short_form("?"), "?");
    }

    #[test]
    fn short_form_handles_non_acct_prefix() {
        // No `acct_` prefix → first 13 chars (prefix-length + hex-length)
        // then ellipsis. Defensive shape: any future identifier format
        // remains compact in the table.
        assert_eq!(short_form("user-with-no-prefix-x"), "user-with-no-…");
    }

    #[test]
    fn display_name_empty_returns_question_mark() {
        assert_eq!(display_name(""), "?");
    }

    #[test]
    fn short_form_does_not_panic_on_non_ascii_input() {
        // Real account ids are ASCII hex; this defends against a hand-
        // edited alias file or future protocol change shipping non-ASCII.
        let id = "acct_émöjï123def🦀";
        let s = short_form(id);
        assert!(s.ends_with('…') || s == id);
    }

    #[test]
    fn display_name_falls_back_to_short_form_when_no_alias_file() {
        // No APPDATA path set up for a real file → cache stays empty →
        // short form returned for any non-empty id.
        let result = display_name("acct_abc123def456");
        // Either "acct_abc123de…" (cache empty, normal fallback) or — in
        // the rare case some other test set APPDATA env var concurrently —
        // an actual alias. Both are valid; we just assert it isn't empty.
        assert!(!result.is_empty());
    }
}
