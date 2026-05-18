// Companion badge window. A small layered Win32 window pinned immediately to
// the LEFT of the upstream usage widget, showing two lines at a glance:
//   row 1: current risk (HIGH / MED / LOW / —)
//   row 2: projected runout local time, or "—"
//
// The badge is the at-a-glance "am I going to run out, and when?" answer that
// the popup chart deep-dives. Its left edge replicates upstream's 3-pixel
// two-tone bevel, so the combined cluster reads as one flush-right unit:
// `[badge-bevel][badge content][upstream-bevel][upstream content][tray]` —
// each section starts with its own left-edge handle. Placing the badge on
// the LEFT side preserves upstream's flush-right positioning so its drag
// clamp still aligns the unit correctly against the system tray.
//
// Architecture mirrors src/csm/popup.rs: a dedicated thread owns the HWND and
// its message loop; show/hide/repaint are driven by a 1-second timer that
// also handles repositioning when the upstream widget moves (DPI changes,
// taskbar layout changes, alignment switches).
//
// Visual parity with upstream is achieved by mirroring src/window.rs's
// UpdateLayeredWindow + premultiplied-alpha DIB technique. Background pixels
// go to alpha = 1 (barely visible but still hit-testable) and content pixels
// to alpha = 255, which lets us keep CLEARTYPE_QUALITY sub-pixel font
// rendering for crisp OS-native text on either light or dark themes.

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

use crate::csm::prediction_store::{store, LatestPrediction};
use crate::diagnose;
use crate::theme;

// Reference dimensions in unscaled (1.0x DPI) device pixels. The runtime
// scale is derived from the upstream widget's actual measured height so the
// badge tracks DPI without us calling GetDpiForWindow ourselves.
const REF_WIDGET_HEIGHT: i32 = 46;
const REF_BADGE_WIDTH: i32 = 80;
const REF_LEFT_DIVIDER_W: i32 = 3;
const REF_DIVIDER_RIGHT_MARGIN: i32 = 10;
const REF_RIGHT_PAD: i32 = 8;
const REF_DIVIDER_H: i32 = 25;
const REF_LINE_H: i32 = 14;
const REF_LINE_GAP: i32 = 4;
const REF_FONT_HEIGHT: i32 = -12; // CreateFontW lfHeight, negative = cell height

const BADGE_CLASS_NAME: &str = "ClaudeUsageProjectorBadge";
const WIDGET_CLASS_NAME: &str = "ClaudeCodeUsageMonitor";
const TASKBAR_CLASS_NAME: &str = "Shell_TrayWnd";

const TICK_TIMER_ID: usize = 1;
const TICK_INTERVAL_MS: u32 = 1_000;

const WM_APP_TICK: u32 = WM_USER + 1;

// COLORREF is 0x00BBGGRR (B in high byte, R in low byte).
const COLOR_BG_DARK: u32 = 0x001C1C1C;
const COLOR_BG_LIGHT: u32 = 0x00F3F3F3;
const COLOR_TEXT_DIM_DARK: u32 = 0x00888888;
const COLOR_TEXT_DIM_LIGHT: u32 = 0x00404040;

// CSM risk palette (encoded BGR).
const COLOR_RISK_HIGH: u32 = 0x004B4BE5; // #E54B4B
const COLOR_RISK_MED: u32 = 0x0000B3FF; // #FFB300
const COLOR_RISK_LOW: u32 = 0x0050AF4C; // #4CAF50
const COLOR_RISK_UNKNOWN: u32 = 0x009AA0A6;

// Bevel tones — two strips matching upstream's left-edge handle.
const COLOR_BEVEL_OUTER_DARK: u32 = 0x00505050; // RGB(80,80,80)
const COLOR_BEVEL_INNER_DARK: u32 = 0x00282828; // RGB(40,40,40)
const COLOR_BEVEL_OUTER_LIGHT: u32 = 0x00A0A0A0; // RGB(160,160,160)
const COLOR_BEVEL_INNER_LIGHT: u32 = 0x00E6E6E6; // RGB(230,230,230)

static BADGE_HWND: AtomicIsize = AtomicIsize::new(0);
static INIT_DONE: OnceLock<()> = OnceLock::new();

/// Lazy initialisation. First call spawns the badge thread; subsequent calls
/// are no-ops. Safe to call from the main host thread at startup.
pub fn init() {
    INIT_DONE.get_or_init(|| {
        if let Err(err) = thread::Builder::new()
            .name("ccum-badge".into())
            .spawn(badge_thread)
        {
            diagnose::log(format!("badge: spawn failed: {err}"));
        }
    });
}

pub fn shutdown() {
    let Some(hwnd) = current_hwnd() else { return };
    unsafe {
        let _ = PostMessageW(hwnd, WM_CLOSE, WPARAM(0), LPARAM(0));
    }
}

/// Returns the badge HWND if it has been created, else None. Used by
/// `hover.rs` so it can test the cursor against our window directly
/// without having to FindWindow our class every tick.
pub fn current_hwnd() -> Option<HWND> {
    let raw = BADGE_HWND.load(Ordering::Acquire);
    if raw == 0 {
        None
    } else {
        Some(HWND(raw as *mut _))
    }
}

/// Screen rect of the badge if it is visible, else None.
pub fn badge_screen_rect() -> Option<RECT> {
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

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}

fn badge_thread() {
    unsafe {
        let hinstance = match GetModuleHandleW(PCWSTR::null()) {
            Ok(h) => HINSTANCE(h.0),
            Err(err) => {
                diagnose::log(format!("badge: GetModuleHandleW failed: {err}"));
                return;
            }
        };

        let class_name = wide(BADGE_CLASS_NAME);
        let wc = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(badge_wnd_proc),
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
            diagnose::log("badge: RegisterClassExW returned 0");
            return;
        }

        let hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            PCWSTR::from_raw(class_name.as_ptr()),
            PCWSTR::null(),
            WS_POPUP,
            0,
            0,
            REF_BADGE_WIDTH,
            REF_WIDGET_HEIGHT,
            HWND::default(),
            HMENU::default(),
            hinstance,
            None,
        );

        let hwnd = match hwnd {
            Ok(h) if !h.is_invalid() => h,
            Ok(_) => {
                diagnose::log("badge: CreateWindowExW returned invalid hwnd");
                return;
            }
            Err(err) => {
                diagnose::log(format!("badge: CreateWindowExW failed: {err}"));
                return;
            }
        };

        BADGE_HWND.store(hwnd.0 as isize, Ordering::Release);
        diagnose::log(format!("badge: created hwnd={:?}", hwnd));

        // The 1s tick repositions and repaints. We also post one immediate
        // tick so the badge appears within ~one message-loop dispatch of
        // startup rather than waiting a full second.
        let _ = SetTimer(hwnd, TICK_TIMER_ID, TICK_INTERVAL_MS, None);
        let _ = PostMessageW(hwnd, WM_APP_TICK, WPARAM(0), LPARAM(0));

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, HWND::default(), 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        BADGE_HWND.store(0, Ordering::Release);
        diagnose::log("badge: message loop exited");
    }
}

unsafe extern "system" fn badge_wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_TIMER => {
            if wparam.0 == TICK_TIMER_ID {
                tick(hwnd);
            }
            LRESULT(0)
        }
        WM_APP_TICK => {
            tick(hwnd);
            LRESULT(0)
        }
        WM_CLOSE => {
            let _ = KillTimer(hwnd, TICK_TIMER_ID);
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

unsafe fn tick(hwnd: HWND) {
    // Pin to the upstream widget. If the widget isn't there yet (cold-start
    // race, or the user has hidden it via the tray context menu), hide the
    // badge — we have nothing to attach to.
    let widget_rect = match find_widget_rect() {
        Some(r) => r,
        None => {
            if IsWindowVisible(hwnd).as_bool() {
                let _ = ShowWindow(hwnd, SW_HIDE);
            }
            return;
        }
    };

    let widget_h = (widget_rect.bottom - widget_rect.top).max(1);
    let scale = widget_h as f64 / REF_WIDGET_HEIGHT as f64;
    let badge_w = ((REF_BADGE_WIDTH as f64) * scale).round() as i32;
    let badge_h = widget_h;
    // Sit immediately to the LEFT of the upstream widget so upstream's own
    // flush-right positioning (which clamps its right edge to the system
    // tray) still anchors the combined unit correctly.
    let badge_x = widget_rect.left - badge_w;
    let badge_y = widget_rect.top;

    let _ = SetWindowPos(
        hwnd,
        HWND_TOPMOST,
        badge_x,
        badge_y,
        badge_w,
        badge_h,
        SWP_NOACTIVATE,
    );

    render_layered(hwnd, badge_w, badge_h, scale);

    if !IsWindowVisible(hwnd).as_bool() {
        let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }
}

unsafe fn find_widget_rect() -> Option<RECT> {
    let widget_class = wide(WIDGET_CLASS_NAME);
    let widget_class_pcwstr = PCWSTR::from_raw(widget_class.as_ptr());
    let taskbar_class = wide(TASKBAR_CLASS_NAME);
    let taskbar_class_pcwstr = PCWSTR::from_raw(taskbar_class.as_ptr());

    let mut hwnd_found: Option<HWND> = None;

    // Common case: widget is reparented into the taskbar — FindWindowW only
    // walks top-level, so search the tray's children first.
    if let Ok(tray) = FindWindowW(taskbar_class_pcwstr, PCWSTR::null()) {
        if !tray.is_invalid() {
            if let Ok(child) =
                FindWindowExW(tray, HWND::default(), widget_class_pcwstr, PCWSTR::null())
            {
                if !child.is_invalid() {
                    hwnd_found = Some(child);
                }
            }
        }
    }

    // Fallback: rare top-level mode (embedding into the taskbar failed).
    if hwnd_found.is_none() {
        if let Ok(top) = FindWindowW(widget_class_pcwstr, PCWSTR::null()) {
            if !top.is_invalid() {
                hwnd_found = Some(top);
            }
        }
    }

    let hwnd = hwnd_found?;
    let mut r = RECT::default();
    GetWindowRect(hwnd, &mut r).ok()?;
    Some(r)
}

// ---------------- Painting ----------------

fn risk_color(risk: &str) -> u32 {
    match risk {
        "high" => COLOR_RISK_HIGH,
        "medium" => COLOR_RISK_MED,
        "low" => COLOR_RISK_LOW,
        _ => COLOR_RISK_UNKNOWN,
    }
}

unsafe fn render_layered(hwnd: HWND, width: i32, height: i32, scale: f64) {
    let is_dark = theme::is_dark_mode();
    let bg_color = if is_dark { COLOR_BG_DARK } else { COLOR_BG_LIGHT };
    let text_dim = if is_dark {
        COLOR_TEXT_DIM_DARK
    } else {
        COLOR_TEXT_DIM_LIGHT
    };

    let (latest, _history) = store().snapshot();

    let screen_dc = GetDC(hwnd);

    let bmi = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height, // top-down
            biPlanes: 1,
            biBitCount: 32,
            biCompression: 0, // BI_RGB
            ..Default::default()
        },
        ..Default::default()
    };

    let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
    let mem_dc = CreateCompatibleDC(screen_dc);
    let dib =
        CreateDIBSection(mem_dc, &bmi, DIB_RGB_COLORS, &mut bits, None, 0).unwrap_or_default();

    if dib.is_invalid() || bits.is_null() {
        let _ = DeleteDC(mem_dc);
        ReleaseDC(hwnd, screen_dc);
        return;
    }

    let old_bmp = SelectObject(mem_dc, dib);

    paint_content(mem_dc, width, height, scale, is_dark, bg_color, text_dim, &latest);

    // Premultiplied-alpha post-process matching src/window.rs render_layered:
    // background pixels become barely-visible (alpha 1, still hit-testable),
    // content pixels become fully opaque (preserves ClearType sub-pixel
    // rendering colours).
    let pixel_count = (width * height) as usize;
    let pixel_data = std::slice::from_raw_parts_mut(bits as *mut u32, pixel_count);
    for px in pixel_data.iter_mut() {
        let rgb = *px & 0x00FFFFFF;
        if rgb == bg_color {
            *px = 0x01000000;
        } else {
            *px = rgb | 0xFF000000;
        }
    }

    let pt_src = POINT { x: 0, y: 0 };
    let sz = SIZE {
        cx: width,
        cy: height,
    };
    let blend = BLENDFUNCTION {
        BlendOp: 0, // AC_SRC_OVER
        BlendFlags: 0,
        SourceConstantAlpha: 255,
        AlphaFormat: 1, // AC_SRC_ALPHA
    };

    let _ = UpdateLayeredWindow(
        hwnd,
        screen_dc,
        None,
        Some(&sz),
        mem_dc,
        Some(&pt_src),
        COLORREF(0),
        Some(&blend),
        ULW_ALPHA,
    );

    SelectObject(mem_dc, old_bmp);
    let _ = DeleteObject(dib);
    let _ = DeleteDC(mem_dc);
    ReleaseDC(hwnd, screen_dc);
}

unsafe fn paint_content(
    hdc: HDC,
    width: i32,
    height: i32,
    scale: f64,
    is_dark: bool,
    bg_color: u32,
    text_dim: u32,
    latest: &Option<LatestPrediction>,
) {
    let sc = |v: i32| -> i32 { ((v as f64) * scale).round() as i32 };

    // Solid background.
    let full = RECT {
        left: 0,
        top: 0,
        right: width,
        bottom: height,
    };
    let bg_brush = CreateSolidBrush(COLORREF(bg_color));
    FillRect(hdc, &full, bg_brush);
    let _ = DeleteObject(bg_brush);

    // Left bevel — two-tone strip mirroring src/window.rs:1346-1381.
    let divider_h = sc(REF_DIVIDER_H);
    let divider_top = (height - divider_h) / 2;
    let divider_bottom = divider_top + divider_h;
    let (bevel_outer, bevel_inner) = if is_dark {
        (COLOR_BEVEL_OUTER_DARK, COLOR_BEVEL_INNER_DARK)
    } else {
        (COLOR_BEVEL_OUTER_LIGHT, COLOR_BEVEL_INNER_LIGHT)
    };
    let outer_brush = CreateSolidBrush(COLORREF(bevel_outer));
    let outer_rect = RECT {
        left: 0,
        top: divider_top,
        right: sc(2),
        bottom: divider_bottom,
    };
    FillRect(hdc, &outer_rect, outer_brush);
    let _ = DeleteObject(outer_brush);
    let inner_brush = CreateSolidBrush(COLORREF(bevel_inner));
    let inner_rect = RECT {
        left: sc(2),
        top: divider_top,
        right: sc(3),
        bottom: divider_bottom,
    };
    FillRect(hdc, &inner_rect, inner_brush);
    let _ = DeleteObject(inner_brush);

    // Text region.
    let content_x = sc(REF_LEFT_DIVIDER_W) + sc(REF_DIVIDER_RIGHT_MARGIN);
    let content_right = width - sc(REF_RIGHT_PAD);

    let font_name = wide("Segoe UI");
    let font_height = ((REF_FONT_HEIGHT as f64) * scale).round() as i32;
    let font = CreateFontW(
        font_height,
        0,
        0,
        0,
        FW_MEDIUM.0 as i32,
        0,
        0,
        0,
        DEFAULT_CHARSET.0 as u32,
        OUT_TT_PRECIS.0 as u32,
        CLIP_DEFAULT_PRECIS.0 as u32,
        CLEARTYPE_QUALITY.0 as u32,
        (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
        PCWSTR::from_raw(font_name.as_ptr()),
    );
    let old_font = SelectObject(hdc, font);
    let _ = SetBkMode(hdc, TRANSPARENT);

    // Two rows centred vertically.
    let line_h = sc(REF_LINE_H);
    let gap = sc(REF_LINE_GAP);
    let total_h = line_h * 2 + gap;
    let row1_y = (height - total_h) / 2;
    let row2_y = row1_y + line_h + gap;

    let (risk_label, line_color) = match latest {
        Some(p) => {
            let label = match p.risk.as_str() {
                "high" => "HIGH",
                "medium" => "MED",
                "low" => "LOW",
                _ => "—",
            };
            (label.to_string(), risk_color(&p.risk))
        }
        None => ("—".to_string(), COLOR_RISK_UNKNOWN),
    };

    // Runout: prefer projected_p50_unix, but only if it's actually before
    // refresh — otherwise the session resets first and "runout" is wrong.
    let runout = latest
        .as_ref()
        .and_then(|p| match (p.projected_p50_unix, p.refresh_unix) {
            (Some(empty), Some(refresh)) if empty < refresh => Some(empty),
            (Some(empty), None) => Some(empty),
            _ => None,
        })
        .map(short_local_time)
        .unwrap_or_else(|| "—".to_string());

    draw_text(hdc, &risk_label, content_x, row1_y, content_right, line_color);
    let runout_color = if runout == "—" { text_dim } else { line_color };
    draw_text(hdc, &runout, content_x, row2_y, content_right, runout_color);

    SelectObject(hdc, old_font);
    let _ = DeleteObject(font);
}

unsafe fn draw_text(hdc: HDC, text: &str, x: i32, y: i32, right_limit: i32, color: u32) {
    SetTextColor(hdc, COLORREF(color));
    let mut buf: Vec<u16> = OsStr::new(text).encode_wide().collect();
    let mut rect = RECT {
        left: x,
        top: y,
        right: right_limit,
        bottom: y + 32,
    };
    DrawTextW(
        hdc,
        &mut buf,
        &mut rect,
        DT_LEFT | DT_SINGLELINE | DT_NOPREFIX,
    );
}

/// Render a Unix epoch second as the user's local-time "h:mmtt" (lowercase),
/// matching CSM's CultureInfo.InvariantCulture formatting via the Win32
/// FileTime→SystemTime→LocalTime conversion path so DST is respected.
fn short_local_time(unix: i64) -> String {
    use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
    use windows::Win32::System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTime};

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
