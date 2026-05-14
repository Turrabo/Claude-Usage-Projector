// CSM extensions — the additive fork-specific surface area. Lives entirely
// in this directory so upstream merges never touch our code.
//
// `sidecar` is the public face for the rest of the host: initialise once at
// startup, then call `record_observation` after each successful poll. The
// rest is bookkeeping.

pub mod ipc;
pub mod sidecar;
