// Hover popup window for the predictor. Borderless top-level Win32 window
// that materialises on continuous mouse hover over the widget (see hover.rs)
// and renders the current session's usage trend with a single risk-coloured
// projection line. Visual design ported from the predecessor CSM's
// ChartPopover (450x160, chart-only, no header or footer text).
//
// Architecture:
//   - The popup lives on its own dedicated thread with its own message loop,
//     decoupling it from the upstream widget's loop. Show/hide/shutdown are
//     all routed via PostMessageW so callers from any thread are safe.
//   - All Win32 calls touching the popup HWND happen on the popup thread.
//     The shared HWND handle is stored as an `isize` static so it's Send.
//   - Painting is raw GDI. Single-buffered: the 5-second repaint cadence
//     and ShowWindow-no-flicker properties are enough at this size.
//
// ADR-006 records the design rationale for picking a separate HWND over an
// embedded widget extension.

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::OnceLock;
use std::thread;

use windows::core::PCWSTR;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::*;

use crate::csm::prediction_store::{store, HistoryEntry, LatestPrediction};
use crate::diagnose;

pub const POPUP_WIDTH: i32 = 450;
pub const POPUP_HEIGHT: i32 = 160;

const POPUP_CLASS_NAME: &str = "ClaudeUsageProjectorPopup";

const REPAINT_TIMER_ID: usize = 1;
const REPAINT_INTERVAL_MS: u32 = 5_000;

const WM_APP_SHOW_POPUP: u32 = WM_USER + 1;
const WM_APP_HIDE_POPUP: u32 = WM_USER + 2;

// Stored as isize so the HWND raw pointer can live in a static across threads.
// Zero = not initialised.
static POPUP_HWND: AtomicIsize = AtomicIsize::new(0);
static INIT_DONE: OnceLock<()> = OnceLock::new();

/// Lazy initialisation. First call spawns the popup thread; subsequent calls
/// are no-ops. Safe to call from the main host thread at startup.
pub fn init() {
    INIT_DONE.get_or_init(|| {
        thread::Builder::new()
            .name("ccum-popup".into())
            .spawn(popup_thread)
            .expect("failed to spawn popup thread");
    });
}

/// Returns the popup window's screen rect, or None if not created or hidden.
/// Used by hover.rs to decide whether the cursor is "still hovering".
pub fn popup_screen_rect() -> Option<RECT> {
    let hwnd = current_hwnd()?;
    unsafe {
        if !IsWindowVisible(hwnd).as_bool() {
            return None;
        }
        let mut rect = RECT::default();
        if GetWindowRect(hwnd, &mut rect).is_ok() {
            Some(rect)
        } else {
            None
        }
    }
}

pub fn show_at(anchor_x: i32, anchor_y: i32) {
    let Some(hwnd) = current_hwnd() else { return };
    unsafe {
        let _ = PostMessageW(
            hwnd,
            WM_APP_SHOW_POPUP,
            WPARAM(anchor_x as usize),
            LPARAM(anchor_y as isize),
        );
    }
}

pub fn hide() {
    let Some(hwnd) = current_hwnd() else { return };
    unsafe {
        let _ = PostMessageW(hwnd, WM_APP_HIDE_POPUP, WPARAM(0), LPARAM(0));
    }
}

pub fn shutdown() {
    let Some(hwnd) = current_hwnd() else { return };
    unsafe {
        let _ = PostMessageW(hwnd, WM_CLOSE, WPARAM(0), LPARAM(0));
    }
}

fn current_hwnd() -> Option<HWND> {
    let raw = POPUP_HWND.load(Ordering::Acquire);
    if raw == 0 {
        None
    } else {
        Some(HWND(raw as *mut _))
    }
}

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

fn popup_thread() {
    unsafe {
        let hinstance = match GetModuleHandleW(PCWSTR::null()) {
            Ok(h) => HINSTANCE(h.0),
            Err(err) => {
                diagnose::log(format!("popup: GetModuleHandleW failed: {err}"));
                return;
            }
        };

        let class_name = wide(POPUP_CLASS_NAME);
        let wc = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(popup_wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: hinstance,
            hIcon: HICON::default(),
            hCursor: LoadCursorW(HINSTANCE::default(), IDC_ARROW).unwrap_or_default(),
            hbrBackground: HBRUSH::default(),
            lpszMenuName: PCWSTR::null(),
            lpszClassName: PCWSTR::from_raw(class_name.as_ptr()),
            hIconSm: HICON::default(),
        };
        if RegisterClassExW(&wc) == 0 {
            diagnose::log("popup: RegisterClassExW returned 0");
            return;
        }

        let hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            PCWSTR::from_raw(class_name.as_ptr()),
            PCWSTR::null(),
            WS_POPUP | WS_BORDER,
            0,
            0,
            POPUP_WIDTH,
            POPUP_HEIGHT,
            HWND::default(),
            HMENU::default(),
            hinstance,
            None,
        );

        let hwnd = match hwnd {
            Ok(h) if !h.is_invalid() => h,
            Ok(_) => {
                diagnose::log("popup: CreateWindowExW returned invalid hwnd");
                return;
            }
            Err(err) => {
                diagnose::log(format!("popup: CreateWindowExW failed: {err}"));
                return;
            }
        };

        POPUP_HWND.store(hwnd.0 as isize, Ordering::Release);
        diagnose::log(format!("popup: created hwnd={:?}", hwnd));

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, HWND::default(), 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        POPUP_HWND.store(0, Ordering::Release);
        diagnose::log("popup: message loop exited");
    }
}

unsafe extern "system" fn popup_wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_APP_SHOW_POPUP => {
            let ax = wparam.0 as i32;
            let ay = lparam.0 as i32;
            let (x, y) = position_relative_to(ax, ay);
            let _ = SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                x,
                y,
                POPUP_WIDTH,
                POPUP_HEIGHT,
                SWP_NOACTIVATE,
            );
            let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            let _ = SetTimer(hwnd, REPAINT_TIMER_ID, REPAINT_INTERVAL_MS, None);
            let _ = InvalidateRect(hwnd, None, BOOL(1));
            LRESULT(0)
        }
        WM_APP_HIDE_POPUP => {
            let _ = KillTimer(hwnd, REPAINT_TIMER_ID);
            let _ = ShowWindow(hwnd, SW_HIDE);
            LRESULT(0)
        }
        WM_TIMER => {
            if wparam.0 == REPAINT_TIMER_ID {
                let _ = InvalidateRect(hwnd, None, BOOL(0));
            }
            LRESULT(0)
        }
        WM_PAINT => {
            let mut ps = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut ps);
            paint(hdc);
            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        WM_CLOSE => {
            let _ = KillTimer(hwnd, REPAINT_TIMER_ID);
            let _ = DestroyWindow(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

/// Compute the popup's top-left so it sits above the anchor (typical: widget
/// centre-top), clamped to the work-area edges.
fn position_relative_to(anchor_x: i32, anchor_y: i32) -> (i32, i32) {
    unsafe {
        let mut wa = RECT::default();
        let ok = SystemParametersInfoW(
            SPI_GETWORKAREA,
            0,
            Some(&mut wa as *mut _ as *mut _),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        )
        .is_ok();

        let (min_x, min_y, max_x, max_y) = if ok {
            (wa.left, wa.top, wa.right, wa.bottom)
        } else {
            (0, 0, 1920, 1080)
        };

        // CSM behaviour: align left edge with the widget; place above with a
        // small gap; clamp to work area.
        let preferred_x = anchor_x - POPUP_WIDTH / 2;
        let preferred_y = anchor_y - POPUP_HEIGHT - 6;
        let x = preferred_x.clamp(min_x, max_x - POPUP_WIDTH);
        let y = preferred_y.clamp(min_y, max_y - POPUP_HEIGHT);
        (x, y)
    }
}

// ---------------- Painting ----------------

// All sizes in client-area pixels.
const PAD_L: i32 = 32;
const PAD_R: i32 = 10;
const PAD_T: i32 = 10;
const PAD_B: i32 = 22;

// COLORREF is 0x00BBGGRR. Define the CSM palette literally.
const COLOR_BG: COLORREF = COLORREF(0x181818);
const COLOR_GRID: COLORREF = COLORREF(0x333333);
const COLOR_DIM: COLORREF = COLORREF(0x707070);
const COLOR_NOW: COLORREF = COLORREF(0x555555);
const COLOR_HISTORY: COLORREF = COLORREF(0xFF9F4C); // BGR for CSM #4C9FFF blue
const COLOR_HINT: COLORREF = COLORREF(0x606060);

fn risk_color(risk: &str) -> COLORREF {
    // BGR encoding.
    match risk {
        "high" => COLORREF(0x4B4BE5),   // CSM #E54B4B red
        "medium" => COLORREF(0x00B3FF), // CSM #FFB300 amber
        _ => COLORREF(0x50AF4C),        // CSM #4CAF50 green
    }
}

unsafe fn paint(hdc: HDC) {
    let (latest, history) = store().snapshot();

    let full = RECT {
        left: 0,
        top: 0,
        right: POPUP_WIDTH,
        bottom: POPUP_HEIGHT,
    };
    fill_solid(hdc, &full, COLOR_BG);

    SetBkMode(hdc, TRANSPARENT);

    let font = GetStockObject(DEFAULT_GUI_FONT);
    let old_font = SelectObject(hdc, font);

    paint_chart(hdc, &latest, &history);

    SelectObject(hdc, old_font);
}

unsafe fn paint_chart(
    hdc: HDC,
    latest: &Option<LatestPrediction>,
    history: &[HistoryEntry],
) {
    let plot_left = PAD_L;
    let plot_right = POPUP_WIDTH - PAD_R;
    let plot_top = PAD_T;
    let plot_bottom = POPUP_HEIGHT - PAD_B;
    let plot_w = plot_right - plot_left;
    let plot_h = plot_bottom - plot_top;

    // Determine session window.
    let now_unix = latest
        .as_ref()
        .map(|p| p.computed_unix)
        .or_else(|| history.last().map(|h| h.computed_unix))
        .unwrap_or(0);

    let refresh_unix = latest.as_ref().and_then(|p| p.refresh_unix);

    let truths: Vec<&HistoryEntry> = history
        .iter()
        .filter(|h| h.used_pct.is_some())
        .collect();

    let (session_start, session_end, known_session) = match refresh_unix {
        Some(end) => (end - 5 * 3600, end, true),
        None => {
            if let Some(first) = truths.first() {
                (first.computed_unix, now_unix.max(first.computed_unix + 60), false)
            } else {
                draw_hint(hdc, "no snapshots yet", plot_left + 4, plot_top + 4);
                return;
            }
        }
    };

    let t_range = (session_end - session_start).max(60) as f64;
    let time_to_x = |t: i64| -> i32 {
        let frac = ((t - session_start) as f64 / t_range).clamp(0.0, 1.0);
        plot_left + (frac * plot_w as f64) as i32
    };
    let pct_to_y = |pct: f64| -> i32 {
        let clamped = pct.clamp(0.0, 100.0);
        plot_top + ((1.0 - clamped / 100.0) * plot_h as f64) as i32
    };

    // Y gridlines + labels
    let pen_grid = create_dashed_pen(COLOR_GRID, &[2, 4]);
    let old_pen = SelectObject(hdc, pen_grid);
    SetTextColor(hdc, COLOR_DIM);
    for p in [0, 25, 50, 75, 100] {
        let y = pct_to_y(p as f64);
        line(hdc, plot_left, y, plot_right, y);
        draw_text_left(hdc, &format!("{p}%"), plot_left - 4, 2, y - 7, COLOR_DIM);
    }
    SelectObject(hdc, old_pen);
    let _ = DeleteObject(pen_grid);

    // "Now" dotted vertical marker
    if now_unix > session_start && now_unix < session_end {
        let nx = time_to_x(now_unix);
        let pen_now = create_dashed_pen(COLOR_NOW, &[3, 4]);
        let old_pen = SelectObject(hdc, pen_now);
        line(hdc, nx, plot_top, nx, plot_bottom);
        SelectObject(hdc, old_pen);
        let _ = DeleteObject(pen_now);
    }

    // Historical polyline
    let in_session: Vec<&HistoryEntry> = truths
        .into_iter()
        .filter(|h| h.computed_unix >= session_start)
        .collect();

    if in_session.is_empty() {
        draw_hint(hdc, "no snapshots in this session yet", plot_left + 4, plot_top + 4);
        draw_x_axis_labels(
            hdc,
            session_start,
            session_end,
            now_unix,
            known_session,
            time_to_x,
            plot_top,
            plot_h,
            plot_left,
            plot_w,
        );
        return;
    }

    let points: Vec<(i32, i32)> = in_session
        .iter()
        .filter_map(|h| h.used_pct.map(|p| (time_to_x(h.computed_unix), pct_to_y(p))))
        .collect();
    if points.len() >= 2 {
        let pen_line = CreatePen(PS_SOLID, 2, COLOR_HISTORY);
        let old_pen = SelectObject(hdc, pen_line);
        polyline(hdc, &points);
        SelectObject(hdc, old_pen);
        let _ = DeleteObject(pen_line);
    }

    let last = in_session.last().copied().unwrap();
    let last_pct = last.used_pct.unwrap();
    let last_x = time_to_x(last.computed_unix);
    let last_y = pct_to_y(last_pct);

    // Projection + run-out marker
    if let Some(p) = latest {
        if let Some(rate) = p.rate_per_min {
            if rate > 0.0 {
                let burn = rate;
                let start_pct = last_pct;
                let start_time = last.computed_unix;
                let runs_out = p
                    .projected_p50_unix
                    .map(|t| t < session_end)
                    .unwrap_or(false);

                let (proj_end_time, proj_end_pct) = if runs_out {
                    (p.projected_p50_unix.unwrap(), 100.0)
                } else {
                    let final_pct = (start_pct
                        + burn * ((session_end - start_time) as f64 / 60.0))
                        .min(100.0);
                    (session_end, final_pct)
                };

                let risk = risk_color(&p.risk);
                if proj_end_time > start_time {
                    let proj_x = time_to_x(proj_end_time);
                    let proj_y = pct_to_y(proj_end_pct);
                    let pen = create_dashed_pen(risk, &[6, 3]);
                    let old_pen = SelectObject(hdc, pen);
                    line(hdc, last_x, last_y, proj_x, proj_y);
                    SelectObject(hdc, old_pen);
                    let _ = DeleteObject(pen);

                    if runs_out && proj_x > plot_left && proj_x < plot_right {
                        let pen2 = create_dashed_pen(risk, &[4, 3]);
                        let old_pen2 = SelectObject(hdc, pen2);
                        line(hdc, proj_x, plot_top, proj_x, plot_bottom);
                        SelectObject(hdc, old_pen2);
                        let _ = DeleteObject(pen2);

                        let warn = format!("! {}", short_local_time(proj_end_time));
                        let label_x = if plot_w - (proj_x - plot_left) > 60 {
                            proj_x + 3
                        } else {
                            proj_x - 55
                        };
                        draw_text_left(
                            hdc,
                            &warn,
                            POPUP_WIDTH - PAD_R,
                            label_x,
                            plot_top + 2,
                            risk,
                        );
                    }
                }
            }
        }
    }

    // Current value dot
    let dot_r = 4;
    let dot_rect = RECT {
        left: last_x - dot_r,
        top: last_y - dot_r,
        right: last_x + dot_r,
        bottom: last_y + dot_r,
    };
    let brush = CreateSolidBrush(COLOR_HISTORY);
    let _ = FillRect(hdc, &dot_rect, brush);
    let _ = DeleteObject(brush);

    draw_x_axis_labels(
        hdc,
        session_start,
        session_end,
        now_unix,
        known_session,
        time_to_x,
        plot_top,
        plot_h,
        plot_left,
        plot_w,
    );
}

unsafe fn draw_x_axis_labels(
    hdc: HDC,
    session_start: i64,
    session_end: i64,
    now_unix: i64,
    known_session: bool,
    time_to_x: impl Fn(i64) -> i32,
    plot_top: i32,
    plot_h: i32,
    plot_left: i32,
    plot_w: i32,
) {
    let y_label = plot_top + plot_h + 5;

    draw_text_left(
        hdc,
        &short_local_time(session_start),
        POPUP_WIDTH - PAD_R,
        plot_left,
        y_label,
        COLOR_DIM,
    );

    if known_session {
        draw_text_left(
            hdc,
            &short_local_time(session_end),
            POPUP_WIDTH - PAD_R,
            plot_left + plot_w - 46,
            y_label,
            COLOR_DIM,
        );
    }

    if now_unix > session_start && now_unix < session_end {
        let nx = time_to_x(now_unix);
        if nx - plot_left > 28 && plot_left + plot_w - nx > 28 {
            draw_text_left(hdc, "now", POPUP_WIDTH - PAD_R, nx - 9, y_label, COLOR_NOW);
        }
    }
}

unsafe fn draw_hint(hdc: HDC, text: &str, x: i32, y: i32) {
    draw_text_left(hdc, text, POPUP_WIDTH - PAD_R, x, y, COLOR_HINT);
}

/// Render a Unix-timestamp as the user's local-time "h:mmtt" lowercase,
/// matching CSM's CultureInfo.InvariantCulture formatting. Uses the local
/// timezone via the Win32 conversion path so DST etc. is respected.
fn short_local_time(unix: i64) -> String {
    use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
    use windows::Win32::System::Time::{
        FileTimeToSystemTime, SystemTimeToTzSpecificLocalTime,
    };

    // Unix epoch in 100-ns ticks since 1601-01-01: 11644473600 seconds.
    let ticks = ((unix as i128) + 11_644_473_600) * 10_000_000;
    let ft = FILETIME {
        dwLowDateTime: ticks as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    unsafe {
        let mut utc = SYSTEMTIME::default();
        if FileTimeToSystemTime(&ft, &mut utc).is_err() {
            return "?".into();
        }
        let mut local = SYSTEMTIME::default();
        if SystemTimeToTzSpecificLocalTime(None, &utc, &mut local).is_err() {
            return "?".into();
        }
        let mut hour12 = local.wHour % 12;
        if hour12 == 0 {
            hour12 = 12;
        }
        let suffix = if local.wHour < 12 { "am" } else { "pm" };
        format!("{}:{:02}{}", hour12, local.wMinute, suffix)
    }
}

// ---------------- GDI helpers ----------------

unsafe fn fill_solid(hdc: HDC, rect: &RECT, color: COLORREF) {
    let brush = CreateSolidBrush(color);
    FillRect(hdc, rect, brush);
    let _ = DeleteObject(brush);
}

unsafe fn create_dashed_pen(color: COLORREF, dash: &[u32]) -> HPEN {
    // ExtCreatePen with a user-defined dash pattern; we fall back to PS_DASH
    // if the extended call fails on the runtime.
    let logbrush = LOGBRUSH {
        lbStyle: BS_SOLID,
        lbColor: color,
        lbHatch: 0,
    };
    let pen = ExtCreatePen(
        PS_GEOMETRIC | PS_USERSTYLE | PS_ENDCAP_FLAT,
        1,
        &logbrush,
        Some(dash),
    );
    if pen.is_invalid() {
        CreatePen(PS_DASH, 1, color)
    } else {
        pen
    }
}

unsafe fn line(hdc: HDC, x1: i32, y1: i32, x2: i32, y2: i32) {
    let _ = MoveToEx(hdc, x1, y1, None);
    let _ = LineTo(hdc, x2, y2);
}

unsafe fn polyline(hdc: HDC, points: &[(i32, i32)]) {
    let pts: Vec<POINT> = points.iter().map(|(x, y)| POINT { x: *x, y: *y }).collect();
    let _ = Polyline(hdc, &pts);
}

unsafe fn draw_text_left(
    hdc: HDC,
    text: &str,
    right_limit: i32,
    x: i32,
    y: i32,
    color: COLORREF,
) {
    SetTextColor(hdc, color);
    let mut buf: Vec<u16> = OsStr::new(text).encode_wide().collect();
    let mut rect = RECT {
        left: x,
        top: y,
        right: right_limit,
        bottom: y + 24,
    };
    DrawTextW(
        hdc,
        &mut buf,
        &mut rect,
        DT_LEFT | DT_SINGLELINE | DT_NOPREFIX,
    );
}
