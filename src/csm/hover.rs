// Cursor hover detector driving the popup window.
//
// Background thread polls the cursor position every 100 ms. State machine:
//   Out                                  : cursor not over widget or popup
//   EnteringSince(t)                     : cursor over widget; show after 200ms
//   Shown                                : popup is visible
//   GraceSince(t)                        : cursor left both; hide after 100ms
//
// Widget HWND is located on each tick via FindWindowW with the upstream's
// class name (`ClaudeCodeUsageMonitor`) — avoids needing to plumb the HWND
// out of `src/window.rs`.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;
use std::thread;
use std::time::{Duration, Instant};

use windows::core::PCWSTR;
use windows::Win32::Foundation::*;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::csm::popup;
use crate::diagnose;

const POLL_INTERVAL_MS: u64 = 100;
const SHOW_DELAY: Duration = Duration::from_millis(200);
const HIDE_GRACE: Duration = Duration::from_millis(100);

const WIDGET_CLASS_NAME: &str = "ClaudeCodeUsageMonitor";
const TASKBAR_CLASS_NAME: &str = "Shell_TrayWnd";

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

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

fn hover_thread() {
    diagnose::log("hover: thread started");
    let widget_class = wide(WIDGET_CLASS_NAME);
    let widget_class_pcwstr = PCWSTR::from_raw(widget_class.as_ptr());
    let taskbar_class = wide(TASKBAR_CLASS_NAME);
    let taskbar_class_pcwstr = PCWSTR::from_raw(taskbar_class.as_ptr());

    let mut state = HoverState::Out;
    let mut widget_found_once = false;

    while !SHUTDOWN.load(Ordering::Acquire) {
        let cursor = match cursor_pos() {
            Some(p) => p,
            None => {
                thread::sleep(Duration::from_millis(POLL_INTERVAL_MS));
                continue;
            }
        };

        // The widget gets reparented into Shell_TrayWnd when embedding succeeds
        // (the common case). FindWindowW only walks top-level windows, so we
        // first try FindWindowExW under the taskbar; if that fails, fall back
        // to the top-level lookup for the rare fallback-popup mode.
        let widget_hwnd = unsafe {
            let mut found: Option<HWND> = None;
            if let Ok(tray) = FindWindowW(taskbar_class_pcwstr, PCWSTR::null()) {
                if !tray.is_invalid() {
                    if let Ok(child) =
                        FindWindowExW(tray, HWND::default(), widget_class_pcwstr, PCWSTR::null())
                    {
                        if !child.is_invalid() {
                            found = Some(child);
                        }
                    }
                }
            }
            if found.is_none() {
                if let Ok(top) = FindWindowW(widget_class_pcwstr, PCWSTR::null()) {
                    if !top.is_invalid() {
                        found = Some(top);
                    }
                }
            }
            found
        };
        let widget_rect = widget_hwnd.and_then(window_rect);
        if !widget_found_once && widget_rect.is_some() {
            widget_found_once = true;
            if let Some(r) = widget_rect.as_ref() {
                diagnose::log(format!(
                    "hover: widget located at L={} T={} R={} B={}",
                    r.left, r.top, r.right, r.bottom
                ));
            }
        }

        let popup_rect = popup::popup_screen_rect();

        let inside = match (widget_rect.as_ref(), popup_rect.as_ref()) {
            (Some(w), Some(p)) => point_in(*w, cursor) || point_in(*p, cursor),
            (Some(w), None) => point_in(*w, cursor),
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
                    if let Some(rect) = widget_rect {
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

fn window_rect(hwnd: HWND) -> Option<RECT> {
    if hwnd.is_invalid() {
        return None;
    }
    let mut r = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut r).ok().map(|_| r) }
}

fn point_in(rect: RECT, p: POINT) -> bool {
    p.x >= rect.left && p.x < rect.right && p.y >= rect.top && p.y < rect.bottom
}
