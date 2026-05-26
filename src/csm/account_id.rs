// Derive a stable opaque account_id from the user's local Claude
// credentials file.
//
// ADR-011 originally specified "hash the JWT subject claim of the access
// token" — but on real Anthropic-issued credentials the access token is an
// opaque `sk-ant-oat01-…` bearer, not a JWT. Hashing the token itself
// would also rotate the account_id on every token refresh. The right
// signal is the top-level `organizationUuid` field, which is stable per
// Claude organization and doesn't change when the access/refresh tokens
// rotate. See ADR-011 Appendix A for the disproof of the original JWT
// assumption and the case for the current shape:
//
//   account_id = "acct_" + first 12 hex chars of SHA-256(organizationUuid)
//
// 12 hex chars = 6 bytes of entropy. Plenty for the small number of
// Claude orgs a single user touches before any practical collision risk;
// short enough to be readable in a diagnose log line or a popup table.

use std::sync::Mutex;
use std::time::SystemTime;

use sha2::{Digest, Sha256};

use crate::diagnose;

/// Sentinel used when the active account can't be determined (credentials
/// file missing/unreadable, pre-login state, or a v:1 host paired with a
/// v:2 predictor). Matches the `DefaultAccountId` constant on the predictor
/// side so observe-routing and prediction-store lookup agree on the same
/// bucket.
pub const DEFAULT_ACCOUNT_ID: &str = "acct_default";

/// Cache of the last-derived account_id and the credentials-file mtime
/// when we computed it. Re-reads the file only when its mtime changes.
struct Cache {
    file_mtime: Option<SystemTime>,
    account_id: Option<String>,
}

static CACHE: Mutex<Cache> = Mutex::new(Cache {
    file_mtime: None,
    account_id: None,
});

/// Returns the currently-active account_id derived from the user's local
/// Claude credentials file. Returns `None` if the file is missing,
/// unreadable, or doesn't parse as the expected shape. The result is
/// cached against the file's mtime so steady-state calls are cheap.
pub fn current_account_id() -> Option<String> {
    let path = credentials_path()?;
    let mtime = std::fs::metadata(&path).and_then(|m| m.modified()).ok();

    if let Ok(mut cache) = CACHE.lock() {
        if cache.file_mtime.is_some() && cache.file_mtime == mtime {
            return cache.account_id.clone();
        }
        let content = std::fs::read_to_string(&path).ok()?;
        let derived = parse_organization_uuid(&content).map(|uuid| format_account_id(&uuid));
        cache.file_mtime = mtime;
        cache.account_id = derived.clone();
        if let Some(ref id) = derived {
            diagnose::log(format!("csm: active account = {id}"));
        } else {
            diagnose::log(
                "csm: credentials.json present but no organizationUuid field — account_id unavailable",
            );
        }
        derived
    } else {
        None
    }
}

/// Canonical hex account id from any stable per-account string. Public
/// only for tests and the predictor-side companion to agree on the format.
pub fn format_account_id(stable_id: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(stable_id.as_bytes());
    let digest = hasher.finalize();
    let mut out = String::from("acct_");
    for byte in digest.iter().take(6) {
        out.push_str(&format!("{byte:02x}"));
    }
    out
}

fn credentials_path() -> Option<std::path::PathBuf> {
    // Matches the canonical Claude CLI location. Windows-only here; WSL
    // distros are handled by the upstream poller's WSL-specific code
    // path and we accept that the host attributes WSL polls to the
    // local Windows account_id. (See ADR-011 'consequences'.)
    Some(dirs::home_dir()?.join(".claude").join(".credentials.json"))
}

/// Extract the top-level `organizationUuid` field from the JSON content
/// of `.credentials.json`. Returns None if the field is missing or not
/// a string — both cases mean the credentials file doesn't have the
/// shape we need to identify the account.
fn parse_organization_uuid(content: &str) -> Option<String> {
    let json: serde_json::Value = serde_json::from_str(content).ok()?;
    json.get("organizationUuid")?
        .as_str()
        .map(str::to_string)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_account_id_is_stable_and_short() {
        let a = format_account_id("550e8400-e29b-41d4-a716-446655440000");
        let b = format_account_id("550e8400-e29b-41d4-a716-446655440000");
        assert_eq!(a, b);
        assert_eq!(a.len(), 17); // "acct_" + 12 hex chars
        assert!(a.starts_with("acct_"));
    }

    #[test]
    fn format_account_id_differs_per_org() {
        let a = format_account_id("550e8400-e29b-41d4-a716-446655440000");
        let b = format_account_id("00000000-0000-0000-0000-000000000001");
        assert_ne!(a, b);
    }

    #[test]
    fn parse_organization_uuid_happy_path() {
        let content = r#"{
            "claudeAiOauth": {"accessToken":"sk-ant-oat01-…","refreshToken":"sk-ant-oat01-…"},
            "organizationUuid": "550e8400-e29b-41d4-a716-446655440000"
        }"#;
        assert_eq!(
            parse_organization_uuid(content).as_deref(),
            Some("550e8400-e29b-41d4-a716-446655440000")
        );
    }

    #[test]
    fn parse_organization_uuid_missing_field_returns_none() {
        let content = r#"{"claudeAiOauth": {"accessToken":"sk-ant-oat01-…"}}"#;
        assert_eq!(parse_organization_uuid(content), None);
    }

    #[test]
    fn parse_organization_uuid_wrong_type_returns_none() {
        let content = r#"{"organizationUuid": 12345}"#;
        assert_eq!(parse_organization_uuid(content), None);
    }

    #[test]
    fn parse_organization_uuid_malformed_json_returns_none() {
        assert_eq!(parse_organization_uuid("not json"), None);
        assert_eq!(parse_organization_uuid(""), None);
    }

    #[test]
    fn full_pipeline_for_known_uuid() {
        // Verify end-to-end: known uuid → known account_id. Stable signal
        // (not a function of token rotation) so this is safe to pin in
        // test code as a regression guard.
        let content = r#"{"organizationUuid": "550e8400-e29b-41d4-a716-446655440000"}"#;
        let uuid = parse_organization_uuid(content).unwrap();
        let id = format_account_id(&uuid);
        assert_eq!(id, "acct_a3a9e1ed9732");
    }
}
