# Account-switch cold-start fix — implementation plan

> Forward-direction plan only. Retire this file once shipped. Pairs with [`PHASE-7-PLAN.md`](PHASE-7-PLAN.md) but isn't a Phase 7 sub-milestone — it's a defect found while road-testing the multi-auth code on 2026-05-26.

## What's broken

When the user switches Claude OAuth accounts mid-session (e.g. `adam.harding@instem.com` → `adamc.harding@instem.com` between two polls), the popover chart for the now-active account visually reads as **"usage starts at 0%, then jumps to the real value (23–24%)"**. The badge correctly switches accounts and the live values are correct, but the chart's X axis stretches back to `refresh_at − 5h` while the account's history only contains entries from the switch moment forward. The empty stretch between `session_start` and the first observation reads as a flat line at 0% against the chart's 0% gridline, even though the chart isn't literally drawing a 0% data point.

Concretely (see [`src/csm/popup.rs:363`](../src/csm/popup.rs#L363)):

```
let (session_start, session_end, known_session) = match refresh_unix {
    Some(end) => (end - 5 * 3600, end, true),
```

When `refresh_unix` comes back from Anthropic as e.g. 14:30 (the new account's real reset time) but our local history for that account only starts at 10:00 (the moment of the switch), the chart paints 4.5 hours of empty space to the left of the first dot.

## Goal in one line

The chart's X axis should never imply more history than the per-account store actually has.

## Sub-tasks

### A — narrow the chart window when account history is sparse (shipping in commit `<TBD>`)

- **File:** [`src/csm/popup.rs`](../src/csm/popup.rs)
- **Change:** extracted a pure helper `compute_session_window(refresh_unix, earliest_truth_unix, now_unix) -> Option<(i64, i64, bool)>` at the bottom of `popup.rs` with a `#[cfg(test)] mod tests` block covering all four input branches. The chart-rendering function calls the helper and emits the "no snapshots yet" hint when the helper returns `None`. When `refresh_unix` is `Some(end)` AND there's at least one history entry, `session_start = max(end - 5*3600, earliest_truth_unix)`.
- **Grace seconds dropped (decided during implementation):** the original plan suggested `GRACE_SECONDS = 60` for visual breathing room, but the existing `None` branch in the same function already places its first data point flush against the left edge without padding, and that's never looked clipped in practice. Keeping zero grace preserves consistency. Easy to add back if visual testing shows clipping on the new path.
- **What this changes visually:** the chart's X axis shrinks to cover only the time range we have data for. The refresh-marker on the right stays at its real wall-clock position. The "Now" dotted vertical and projection line both still work — they don't depend on `session_start` directly, just on `time_to_x` which uses `t_range` (whose `.max(60)` floor still holds for narrow windows).
- **What this does NOT change:** the badge, the prediction maths, or any predictor-side code.
- **Phase 7e transition:** once cross-machine sync backfills history for the active account from the other machine, `earliest_truth_unix` becomes older than `nominal_start` and the `max` reverts to picking `nominal_start` — the narrowing self-cancels with no further code change.

### B — Tier 1 cold-start rate guard (confirmed needed; deferred to a follow-up commit)

- **File:** [`predictor/Tiers/Tier1WeightedBurnRate.cs`](../predictor/Tiers/Tier1WeightedBurnRate.cs)
- **Verification result (2026-05-26):** `RateOverWindow` ([line 336](../predictor/Tiers/Tier1WeightedBurnRate.cs#L336)) returns `null` when `inWindow.Count < 2`, but with 2 samples 60s apart it returns a real (extrapolated, noisy) rate. `WeightedAverage` ([line 351](../predictor/Tiers/Tier1WeightedBurnRate.cs#L351)) propagates that. So a brand-new account on its 2nd poll DOES emit a non-null `weighted`, which Tier 2 Monte Carlo then projects from. **Task B IS needed.**
- **Fix:** in `RateOverWindow`, add a minimum-sample-count check (`inWindow.Count >= 3`) and a minimum-span check (`minutes >= MinRateSpanMinutes`, suggest 2.0). Optionally add a new `Reason = "Warming up after account switch"` string when the sparse-data path causes `weighted = null`. Add a unit test in `Tier1WeightedBurnRateTests.cs` covering the 2-samples-60s-apart case.
- **Why separate commit:** different file, different language (C# sidecar vs Rust host), different tests. The Task A diff is self-contained around the chart window; bundling B would muddy the review. Reviewer B's recommendation, agreed.

## Acceptance criteria

Manual test, walking through an account switch:

1. Widget running. Active account is `adam.harding@instem.com`. Chart shows a full 5-hour line at non-trivial usage.
2. Run `claude login` to switch to `adamc.harding@instem.com`. Wait one poll interval (~60s).
3. **Badge** switches to the new account's risk colour and runout. (Already works pre-fix.)
4. **Chart** now shows the new account at its real percentage with **no implied "starts at 0" stretch**. The X axis is shorter (covers only the post-switch window). The refresh-time marker on the right still sits at the new account's real refresh-at time.
5. After another 5–10 minutes of polling, the projection line appears once the predictor has enough samples (Task B's existing guards or, if needed, the strengthened guard).
6. Run `claude login` back to the original account. Chart returns to the full 5-hour line — original account's history was preserved while the other was active.

## Sub-agent review checkpoint

Same cadence as the Phase 7 work: spawn two general-purpose reviewer sub-agents in parallel before pushing. The Task A change is small (single function, ~5–10 lines) but the review should explicitly check:

- The `time_to_x` closure ([line 376](../src/csm/popup.rs#L376)) still produces sensible values with the narrowed window — it does `(t - session_start) / t_range`, so a smaller `t_range` is fine as long as it's `.max(60)`.
- The "Now" vertical marker ([line 400](../src/csm/popup.rs#L400)) and projection line ([line 471](../src/csm/popup.rs#L471)) both still draw correctly — they use `now_unix` and `last.computed_unix` respectively, both of which sit *inside* the narrowed window.
- The X-axis label rendering (`draw_x_axis_labels`) handles the narrower window gracefully — needs a quick read of that function to confirm the label cadence (hourly? quarter-hourly?) still works at shorter spans.
- No regression on the existing "no snapshots yet" hint path or the "no snapshots in this session yet" path.

## Estimated effort

- Task A: ~1.5 hours for code + helper extraction + 6 unit tests + sub-agent review pass. **Shipping in this commit.**
- Task B: separate follow-up commit. ~1 hour for code + unit test + sub-agent review pass.

Total ~2.5 hours across two commits, not one (split decided after the verification-during-implementation result on Task B).

## What this does NOT fix

- The genuine cross-machine sync (Phase 7e in [`PHASE-7-PLAN.md`](PHASE-7-PLAN.md)). The chart will still be empty for an account the user has never used on this machine, even after this fix. Phase 7e fills in the actual data; this fix just stops the chart from lying about what it has.
- The local-build USER32 crash documented in the auto-memory. Orthogonal.
- The Phase 7b history sharding (still pending). The fix sits cleanly on top of either pre- or post-7b state — it only reads `history`, doesn't care where it came from.

## Open implementation questions

- What `GRACE_SECONDS` value looks best? 60s reads cleanly in my head but worth eyeballing during testing — 30s might be tighter, 120s gives more visual breathing room. Pick during implementation.
- When the new account has history from before the switch (e.g. if Phase 7b has shipped and it was previously the active account on this machine), the `earliest_history_point_unix` would be much earlier — and the `max(...)` does the right thing automatically, falling back to the full 5-hour window. Good — no special case needed.
- Should the chart visually indicate "limited history (switched at HH:MM)" via a marker or hint text? Probably not — the narrower X axis is self-documenting once the user has used the widget a few times. Reconsider if testing shows confusion.
