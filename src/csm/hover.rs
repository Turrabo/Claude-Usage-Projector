// Cursor hover detector driving the popup window.
//
// Background thread polls the cursor position every 100 ms. State machine:
//   Out                                  : cursor not over badge or popup
//   EnteringSince(t)                     : cursor over badge; show after 200ms
//   Shown                                : popup is visible
//   GraceSince(t)                        : cursor left both; hide after 100ms
//
// The hover trigger lives on our own companion badge window (see badge.rs).
// We previously walked FindWindowExW under Shell_TrayWnd to find the upstream
// widget — owning the badge HWND directly is simpler and survives upstream
// renames of its window class.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;
use std::thread;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::*;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::csm::{badge, popup};
use crate::diagnose;

const POLL_INTERVAL_MS: u64 = 100;
const SHOW_DELAY: Duration = Duration::from_millis(200);
const HIDE_GRACE: Duration = Duration::from_millis(100);

static SHUTDOWN: AtomicBool = AtomicBool::new(false);
static STARTED: OnceLock<()> = OnceLock::new();

enum HoverState {
    Out,
    EnteringSince(Instant),
    Shown,
    GraceSince(Instant),
}

pub fn start() {
    STARTED.get_or_init(|| {
        if let Err(err) = thread::Builder::new()
            .name("ccum-hover".into())
            .spawn(hover_thread)
        {
            diagnose::log(format!("hover: spawn failed: {err}"));
        }
    });
}

pub fn stop() {
    SHUTDOWN.store(true, Ordering::Release);
}

fn hover_thread() {
    diagnose::log("hover: thread started");
    let mut state = HoverState::Out;
    let mut badge_found_once = false;

    while !SHUTDOWN.load(Ordering::Acquire) {
        let cursor = match cursor_pos() {
            Some(p) => p,
            None => {
                thread::sleep(Duration::from_millis(POLL_INTERVAL_MS));
                continue;
            }
        };

        let badge_rect = badge::badge_screen_rect();
        if !badge_found_once {
            if let Some(r) = badge_rect.as_ref() {
                badge_found_once = true;
                diagnose::log(format!(
                    "hover: badge located at L={} T={} R={} B={}",
                    r.left, r.top, r.right, r.bottom
                ));
            }
        }

        let popup_rect = popup::popup_screen_rect();

        let inside = match (badge_rect.as_ref(), popup_rect.as_ref()) {
            (Some(b), Some(p)) => point_in(*b, cursor) || point_in(*p, cursor),
            (Some(b), None) => point_in(*b, cursor),
            _ => false,
        };

        state = match state {
            HoverState::Out => {
                if inside {
                    HoverState::EnteringSince(Instant::now())
                } else {
                    HoverState::Out
                }
            }
            HoverState::EnteringSince(t) => {
                if !inside {
                    HoverState::Out
                } else if t.elapsed() >= SHOW_DELAY {
                    if let Some(rect) = badge_rect {
                        let cx = (rect.left + rect.right) / 2;
                        let cy = rect.top;
                        popup::show_at(cx, cy);
                        diagnose::log(format!("hover: popup show requested at ({cx},{cy})"));
                    }
                    HoverState::Shown
                } else {
                    HoverState::EnteringSince(t)
                }
            }
            HoverState::Shown => {
                if !inside {
                    HoverState::GraceSince(Instant::now())
                } else {
                    HoverState::Shown
                }
            }
            HoverState::GraceSince(t) => {
                if inside {
                    HoverState::Shown
                } else if t.elapsed() >= HIDE_GRACE {
                    popup::hide();
                    HoverState::Out
                } else {
                    HoverState::GraceSince(t)
                }
            }
        };

        thread::sleep(Duration::from_millis(POLL_INTERVAL_MS));
    }

    diagnose::log("hover: thread exiting");
}

fn cursor_pos() -> Option<POINT> {
    let mut p = POINT::default();
    unsafe { GetCursorPos(&mut p).ok().map(|_| p) }
}

fn point_in(rect: RECT, p: POINT) -> bool {
    p.x >= rect.left && p.x < rect.right && p.y >= rect.top && p.y < rect.bottom
}
