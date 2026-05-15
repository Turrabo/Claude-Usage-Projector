// Hover popup window for the predictor. Borderless top-level Win32 window
// that materialises on continuous mouse hover over the widget (see hover.rs)
// and renders the latest PredictionMessage plus a recent history graph.
//
// Architecture:
//   - The popup lives on its own dedicated thread with its own message loop,
//     decoupling it from the upstream widget's loop. Show/hide/shutdown are
//     all routed via PostMessageW so callers from any thread are safe.
//   - All Win32 calls touching the popup HWND happen on the popup thread.
//     The shared HWND handle is stored as an `isize` static so it's Send.
//   - Painting is raw GDI. Single-buffered for simplicity; the 5-second
//     repaint cadence and the ShowWindow-no-flicker properties of NOACTIVATE
//     means we don't need a memory DC swap at this point.
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

pub const POPUP_WIDTH: i32 = 480;
pub const POPUP_HEIGHT: i32 = 320;

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
            hbrBackground: HBRUSH((COLOR_3DFACE.0 + 1) as *mut _),
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

/// Compute the popup's top-left so it sits above-and-to-the-left of the
/// anchor (typical: cursor or widget centre), clamped to the work-area edges.
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

        let preferred_x = anchor_x - POPUP_WIDTH + 16; // slightly overlapping the widget
        let preferred_y = anchor_y - POPUP_HEIGHT - 8; // above the taskbar
        let x = preferred_x.clamp(min_x, max_x - POPUP_WIDTH);
        let y = preferred_y.clamp(min_y, max_y - POPUP_HEIGHT);
        (x, y)
    }
}

// ---------------- Painting ----------------

const MARGIN: i32 = 12;
const HEADER_H: i32 = 36;
const FOOTER_H: i32 = 84;

const COLOR_BG: COLORREF = COLORREF(0x202020); // dark slate
const COLOR_FG: COLORREF = COLORREF(0xEEEEEE);
const COLOR_MUTED: COLORREF = COLORREF(0x9A9A9A);
const COLOR_AXIS: COLORREF = COLORREF(0x444444);
const COLOR_LINE: COLORREF = COLORREF(0xE0C040); // amber
const COLOR_P50: COLORREF = COLORREF(0x60E060); // green
const COLOR_P75: COLORREF = COLORREF(0xE0E060); // yellow
const COLOR_P90: COLORREF = COLORREF(0x6080FF); // blue
const COLOR_HAWKES: COLORREF = COLORREF(0xC080FF); // violet

fn risk_color(risk: &str) -> COLORREF {
    match risk {
        "high" => COLORREF(0x4040FF),  // red (BGR)
        "medium" => COLORREF(0x40A0E0), // amber
        "low" => COLORREF(0x40C040),   // green
        _ => COLOR_MUTED,
    }
}

unsafe fn paint(hdc: HDC) {
    let (latest, history) = store().snapshot();

    let rect = RECT {
        left: 0,
        top: 0,
        right: POPUP_WIDTH,
        bottom: POPUP_HEIGHT,
    };
    fill_solid(hdc, &rect, COLOR_BG);

    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, COLOR_FG);

    let font = GetStockObject(DEFAULT_GUI_FONT);
    let old_font = SelectObject(hdc, font);

    draw_header(hdc, &latest);
    draw_chart(hdc, &latest, &history);
    draw_footer(hdc, &latest, &history);

    SelectObject(hdc, old_font);
}

unsafe fn draw_header(hdc: HDC, latest: &Option<LatestPrediction>) {
    let header_rect = RECT {
        left: 0,
        top: 0,
        right: POPUP_WIDTH,
        bottom: HEADER_H,
    };
    fill_solid(hdc, &header_rect, COLORREF(0x303030));

    let Some(p) = latest else {
        draw_text_left(
            hdc,
            "Waiting for first prediction...",
            POPUP_WIDTH,
            MARGIN,
            10,
            COLOR_MUTED,
        );
        return;
    };

    // Tier badge
    let badge_w = 40;
    let badge_rect = RECT {
        left: MARGIN,
        top: 8,
        right: MARGIN + badge_w,
        bottom: HEADER_H - 4,
    };
    fill_solid(hdc, &badge_rect, COLORREF(0x505050));
    let tier_text = format!("T{}", p.tier);
    draw_text_centered(hdc, &tier_text, &badge_rect, COLOR_FG);

    // Risk pill
    let risk_label = format!("RISK: {}", p.risk.to_uppercase());
    let risk_rect = RECT {
        left: MARGIN + badge_w + 8,
        top: 8,
        right: MARGIN + badge_w + 8 + 140,
        bottom: HEADER_H - 4,
    };
    fill_solid(hdc, &risk_rect, risk_color(&p.risk));
    draw_text_centered(hdc, &risk_label, &risk_rect, COLORREF(0x000000));

    // Used %
    let used = p
        .used_pct
        .map(|v| format!("used {v:.1}%"))
        .unwrap_or_else(|| "used ?".to_string());
    draw_text_left(
        hdc,
        &used,
        POPUP_WIDTH,
        MARGIN + badge_w + 8 + 140 + 12,
        12,
        COLOR_FG,
    );

    // Rate (right-aligned)
    let rate = p
        .rate_per_min
        .map(|v| format!("{v:.3} %/min"))
        .unwrap_or_else(|| "?/min".to_string());
    draw_text_right(hdc, &rate, POPUP_WIDTH - MARGIN, 12, COLOR_MUTED);
}

unsafe fn draw_chart(hdc: HDC, latest: &Option<LatestPrediction>, history: &[HistoryEntry]) {
    let chart_top = HEADER_H + 8;
    let chart_bottom = POPUP_HEIGHT - FOOTER_H - 8;
    let chart_left = MARGIN + 30; // leave room for y-axis labels
    let chart_right = POPUP_WIDTH - MARGIN;
    let chart_h = chart_bottom - chart_top;
    let chart_w = chart_right - chart_left;

    // y-axis: 0..100
    let pen_axis = CreatePen(PS_SOLID, 1, COLOR_AXIS);
    let old_pen = SelectObject(hdc, pen_axis);
    line(hdc, chart_left, chart_top, chart_left, chart_bottom);
    line(hdc, chart_left, chart_bottom, chart_right, chart_bottom);
    // gridlines at 25/50/75/100
    for tick in [25, 50, 75, 100] {
        let y = chart_bottom - (tick * chart_h / 100);
        line(hdc, chart_left, y, chart_right, y);
        let label = format!("{tick}");
        draw_text_right(hdc, &label, chart_left - 4, y - 7, COLOR_MUTED);
    }
    SelectObject(hdc, old_pen);
    let _ = DeleteObject(pen_axis);

    // Plot used-pct history. X axis = last 90 minutes ending at "now-ish"
    // (= last history entry's timestamp, or the latest prediction's).
    let now_unix = latest.as_ref().map(|p| p.computed_unix).unwrap_or_else(|| {
        history.last().map(|h| h.computed_unix).unwrap_or(0)
    });
    if now_unix == 0 || history.is_empty() {
        draw_text_centered(
            hdc,
            "Collecting data...",
            &RECT {
                left: chart_left,
                top: chart_top,
                right: chart_right,
                bottom: chart_bottom,
            },
            COLOR_MUTED,
        );
        return;
    }

    let window_secs: i64 = 90 * 60;
    let x_for = |t: i64| -> i32 {
        let offset = (t - (now_unix - window_secs)) as f64 / window_secs as f64;
        chart_left + (offset.clamp(0.0, 1.0) * chart_w as f64) as i32
    };
    let y_for = |pct: f64| -> i32 {
        let clamped = pct.clamp(0.0, 100.0);
        chart_bottom - (clamped / 100.0 * chart_h as f64) as i32
    };

    // history line
    let points: Vec<(i32, i32)> = history
        .iter()
        .filter_map(|h| h.used_pct.map(|p| (x_for(h.computed_unix), y_for(p))))
        .collect();
    if points.len() >= 2 {
        let pen_line = CreatePen(PS_SOLID, 2, COLOR_LINE);
        let old_pen = SelectObject(hdc, pen_line);
        polyline(hdc, &points);
        SelectObject(hdc, old_pen);
        let _ = DeleteObject(pen_line);
    }

    // Projection cone: line from latest observed point to each projected
    // empty timestamp at y=100, in three colours.
    if let Some(p) = latest {
        if let Some(used) = p.used_pct {
            let origin = (x_for(p.computed_unix), y_for(used));
            for (proj, color) in [
                (p.projected_p50_unix, COLOR_P50),
                (p.projected_p75_unix, COLOR_P75),
                (p.projected_p90_unix, COLOR_P90),
            ] {
                if let Some(target_unix) = proj {
                    let target = (x_for(target_unix), y_for(100.0));
                    let pen = CreatePen(PS_DASH, 1, color);
                    let old_pen = SelectObject(hdc, pen);
                    line(hdc, origin.0, origin.1, target.0, target.1);
                    SelectObject(hdc, old_pen);
                    let _ = DeleteObject(pen);
                }
            }
        }
    }
}

unsafe fn draw_footer(hdc: HDC, latest: &Option<LatestPrediction>, history: &[HistoryEntry]) {
    let footer_top = POPUP_HEIGHT - FOOTER_H;
    let footer_rect = RECT {
        left: 0,
        top: footer_top,
        right: POPUP_WIDTH,
        bottom: POPUP_HEIGHT,
    };
    fill_solid(hdc, &footer_rect, COLORREF(0x282828));

    // Hawkes sparkline (last 30 entries, scaled 0..max ratio)
    let spark_left = MARGIN;
    let spark_top = footer_top + 8;
    let spark_w = 200;
    let spark_h = 26;
    let baseline_y = spark_top + spark_h - 1;

    let pen_axis = CreatePen(PS_DOT, 1, COLOR_AXIS);
    let old_pen = SelectObject(hdc, pen_axis);
    line(hdc, spark_left, baseline_y, spark_left + spark_w, baseline_y);
    SelectObject(hdc, old_pen);
    let _ = DeleteObject(pen_axis);

    let ratios: Vec<f64> = history
        .iter()
        .rev()
        .take(30)
        .filter_map(|h| h.hawkes_ratio)
        .collect();
    let max_ratio = ratios.iter().copied().fold(1.5_f64, f64::max);
    if ratios.len() >= 2 {
        let mut pts: Vec<(i32, i32)> = ratios
            .iter()
            .enumerate()
            .map(|(i, r)| {
                let x = spark_left + (i as i32 * spark_w / 30);
                let y = baseline_y - ((r / max_ratio) * spark_h as f64) as i32;
                (x, y.clamp(spark_top, baseline_y))
            })
            .collect();
        pts.reverse(); // history was reversed for the take; reverse back to time-ordered
        let pen = CreatePen(PS_SOLID, 1, COLOR_HAWKES);
        let old_pen = SelectObject(hdc, pen);
        polyline(hdc, &pts);
        SelectObject(hdc, old_pen);
        let _ = DeleteObject(pen);
    }
    draw_text_left(hdc, "Hawkes burst ratio", POPUP_WIDTH, spark_left, spark_top - 14, COLOR_MUTED);

    // Right side: P50/P75/P90 + probability + activity
    let info_left = spark_left + spark_w + 20;
    if let Some(p) = latest {
        let mut y = footer_top + 8;
        let p50 = p
            .projected_p50_unix
            .map(|t| format!("P50 {}", short_time(t)))
            .unwrap_or_else(|| "P50 -".to_string());
        let p75 = p
            .projected_p75_unix
            .map(|t| format!("P75 {}", short_time(t)))
            .unwrap_or_else(|| "P75 -".to_string());
        let p90 = p
            .projected_p90_unix
            .map(|t| format!("P90 {}", short_time(t)))
            .unwrap_or_else(|| "P90 -".to_string());
        draw_text_left(hdc, &p50, POPUP_WIDTH, info_left, y, COLOR_P50);
        y += 14;
        draw_text_left(hdc, &p75, POPUP_WIDTH, info_left, y, COLOR_P75);
        y += 14;
        draw_text_left(hdc, &p90, POPUP_WIDTH, info_left, y, COLOR_P90);
        y += 16;
        let prob = format!("pE {:.2}", p.prob_empty_before_refresh);
        draw_text_left(hdc, &prob, POPUP_WIDTH, info_left, y, COLOR_FG);

        // Bottom strip: status text spanning the footer width
        let status = format!(
            "act={}{}{}",
            p.activity,
            if p.frozen { " (frozen)" } else { "" },
            p.reason
                .as_deref()
                .map(|r| format!(" -- {r}"))
                .unwrap_or_default()
        );
        draw_text_left(
            hdc,
            &status,
            POPUP_WIDTH - MARGIN,
            MARGIN,
            POPUP_HEIGHT - 18,
            COLOR_MUTED,
        );
    } else {
        draw_text_left(
            hdc,
            "(no prediction yet)",
            POPUP_WIDTH,
            info_left,
            footer_top + 8,
            COLOR_MUTED,
        );
        let _ = history;
    }
}

/// HH:MMZ rendering of a Unix timestamp, in UTC.
fn short_time(unix: i64) -> String {
    let secs_of_day = unix.rem_euclid(86_400);
    let hour = (secs_of_day / 3600) as u32;
    let minute = ((secs_of_day % 3600) / 60) as u32;
    format!("{hour:02}:{minute:02}Z")
}

// ---------------- GDI helpers ----------------

unsafe fn fill_solid(hdc: HDC, rect: &RECT, color: COLORREF) {
    let brush = CreateSolidBrush(color);
    FillRect(hdc, rect, brush);
    let _ = DeleteObject(brush);
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

unsafe fn draw_text_right(hdc: HDC, text: &str, right_x: i32, y: i32, color: COLORREF) {
    SetTextColor(hdc, color);
    let mut buf: Vec<u16> = OsStr::new(text).encode_wide().collect();
    let mut rect = RECT {
        left: 0,
        top: y,
        right: right_x,
        bottom: y + 24,
    };
    DrawTextW(
        hdc,
        &mut buf,
        &mut rect,
        DT_RIGHT | DT_SINGLELINE | DT_NOPREFIX,
    );
}

unsafe fn draw_text_centered(hdc: HDC, text: &str, rect: &RECT, color: COLORREF) {
    SetTextColor(hdc, color);
    let mut buf: Vec<u16> = OsStr::new(text).encode_wide().collect();
    let mut r = *rect;
    DrawTextW(
        hdc,
        &mut buf,
        &mut r,
        DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX,
    );
}
