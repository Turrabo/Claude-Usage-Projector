// CSM extensions — the additive fork-specific surface area. Lives entirely
// in this directory so upstream merges never touch our code.
//
// `sidecar` is the public face for the rest of the host: initialise once at
// startup, then call `record_observation` after each successful poll. The
// rest is bookkeeping.
//
// Phase 4 adds `popup`, `hover`, and `prediction_store` — a hover-over
// projection chart and the shared prediction history that feeds it.
//
// `badge` is a companion always-on window pinned to the right of the upstream
// widget, surfacing risk + projected runout time so the user doesn't have to
// hover to see them. It also owns the hover trigger that opens the popup.

pub mod badge;
pub mod hover;
pub mod ipc;
pub mod popup;
pub mod prediction_store;
pub mod sidecar;
