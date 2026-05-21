// Derive a stable opaque account_id from the Claude OAuth access token.
//
// See DECISIONS.md ADR-011 for the why. Briefly: every IPC observation
// carries an account_id field so the predictor can key per-account state.
// The id is derived from the JWT's `sub` claim hashed with SHA-256 and
// truncated — short, stable across token refreshes (sub doesn't change),
// and free of PII even in transit / on disk.
//
//   account_id = "acct_" + first 12 hex chars of SHA-256(jwt.sub)
//
// We don't validate the JWT signature. The credentials file is the local
// source of truth; if it's been tampered with we have bigger problems
// than account_id. We just need to parse the payload and extract `sub`.

use std::sync::Mutex;
use std::time::SystemTime;

use sha2::{Digest, Sha256};

use crate::diagnose;

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
        let token = parse_access_token(&content)?;
        let derived = derive_from_jwt(&token);
        cache.file_mtime = mtime;
        cache.account_id = derived.clone();
        if let Some(ref id) = derived {
            diagnose::log(format!("csm: active account = {id}"));
        }
        derived
    } else {
        None
    }
}

/// Returns the canonical account_id for a given JWT access token, or
/// `None` if the token doesn't decode to something with a `sub` claim.
pub fn derive_from_jwt(jwt: &str) -> Option<String> {
    let parts: Vec<&str> = jwt.split('.').collect();
    if parts.len() != 3 {
        return None;
    }
    let payload_bytes = base64url_decode(parts[1])?;
    let payload: serde_json::Value = serde_json::from_slice(&payload_bytes).ok()?;
    let sub = payload.get("sub")?.as_str()?;
    Some(format_account_id(sub))
}

fn format_account_id(sub: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(sub.as_bytes());
    let digest = hasher.finalize();
    let mut out = String::from("acct_");
    // 12 hex chars = 6 bytes of entropy. Plenty for ~hundreds of accounts
    // before any practical collision risk; short enough to be readable.
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

fn parse_access_token(content: &str) -> Option<String> {
    let json: serde_json::Value = serde_json::from_str(content).ok()?;
    json.get("claudeAiOauth")?
        .get("accessToken")?
        .as_str()
        .map(str::to_string)
}

/// Decode base64url (no padding tolerated either way). Returns `None`
/// on any malformed input. Hand-rolled to avoid pulling in a base64
/// crate just for this one call site.
fn base64url_decode(input: &str) -> Option<Vec<u8>> {
    const ALPHABET: &[u8] =
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    let mut lookup = [255u8; 256];
    for (i, &c) in ALPHABET.iter().enumerate() {
        lookup[c as usize] = i as u8;
    }

    // Strip any padding ('=') and ignore whitespace.
    let cleaned: Vec<u8> = input
        .bytes()
        .filter(|&b| b != b'=' && !b.is_ascii_whitespace())
        .collect();

    let mut out = Vec::with_capacity(cleaned.len() * 3 / 4 + 3);
    let mut buf: u32 = 0;
    let mut bits: u32 = 0;
    for byte in cleaned {
        let v = lookup[byte as usize];
        if v == 255 {
            return None;
        }
        buf = (buf << 6) | v as u32;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((buf >> bits) as u8 & 0xFF);
        }
    }
    // After the loop, valid (de-padded) inputs leave `bits` at 0, 2, or 4.
    // `bits == 6` indicates a length mod 4 == 1, which is never produced by
    // a canonical base64 encoder. Reject it. Similarly, any leftover bits
    // must be zero — non-zero leftovers come from corrupt or truncated
    // encodings rather than legitimate decoders.
    if bits >= 6 {
        return None;
    }
    if bits > 0 && (buf & ((1 << bits) - 1)) != 0 {
        return None;
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_account_id_is_stable_and_short() {
        let a = format_account_id("auth0|abc123");
        let b = format_account_id("auth0|abc123");
        assert_eq!(a, b);
        assert_eq!(a.len(), 17); // "acct_" + 12 hex chars
        assert!(a.starts_with("acct_"));
    }

    #[test]
    fn format_account_id_differs_per_sub() {
        let a = format_account_id("auth0|abc123");
        let b = format_account_id("auth0|def456");
        assert_ne!(a, b);
    }

    #[test]
    fn base64url_decode_roundtrip_known_vector() {
        // "Hello" base64url-encoded
        assert_eq!(base64url_decode("SGVsbG8").unwrap(), b"Hello");
        // With padding tolerated
        assert_eq!(base64url_decode("SGVsbG8=").unwrap(), b"Hello");
        // URL-safe '-' and '_' alphabet exercised.
        // a-_z = (26, 62, 63, 51) in the alphabet, 6 bits each:
        // 011010 111110 111111 110011 = 0x6B 0xEF 0xF3
        assert_eq!(base64url_decode("a-_z").unwrap(), vec![0x6b, 0xef, 0xf3]);
    }

    #[test]
    fn derive_from_jwt_handles_typical_payload() {
        // Build a synthetic JWT: header.payload.sig where payload contains sub.
        // base64url("{\"sub\":\"auth0|user-xyz\"}") = eyJzdWIiOiJhdXRoMHx1c2VyLXh5eiJ9
        let jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhdXRoMHx1c2VyLXh5eiJ9.ignored";
        let id = derive_from_jwt(jwt).expect("should derive");
        assert!(id.starts_with("acct_"));
        assert_eq!(id.len(), 17);
    }

    #[test]
    fn derive_from_jwt_rejects_garbage() {
        assert_eq!(derive_from_jwt("not-a-jwt"), None);
        assert_eq!(derive_from_jwt("only.two.dots.thing.here"), None);
        assert_eq!(derive_from_jwt("foo.!!!.bar"), None);
    }

    #[test]
    fn base64url_decode_rejects_mod4_length_1() {
        // A single base64 char encodes only 6 bits — never produced by a
        // canonical encoder. Reject it rather than silently dropping the
        // 6-bit remainder.
        assert_eq!(base64url_decode("A"), None);
        assert_eq!(base64url_decode("AAAAA"), None); // 4+1 == 5
    }
}
