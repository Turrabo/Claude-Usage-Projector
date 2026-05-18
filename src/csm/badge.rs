// Companion badge window. A small layered Win32 window pinned immediately to
// the LEFT of the upstream usage widget, showing two lines at a glance:
//   row 1: current risk (High / Med / Low / —)
//   row 2: projected runout local time, or "—"
//
// The badge is the at-a-glance "am I going to run out, and when?" answer that
// the popup chart deep-dives. The visible body is a translucent rounded card
// with generous outer margin, so the content reads as a bounded chip on the
// taskbar rather than free-floating text. The card sits to the LEFT of the
// upstream widget; upstream's flush-right positioning against the system tray
// remains valid for the cluster.
//
// Architecture mirrors src/csm/popup.rs: a dedicated thread owns the HWND and
// its message loop; show/hide/repaint are driven by a 1-second timer that
// also handles repositioning when the upstream widget moves (DPI changes,
// taskbar layout changes, alignment switches).
//
// Visual is achieved by mirroring src/window.rs's UpdateLayeredWindow + DIB
// technique, with two adjustments for the rounded translucent card:
//   1. SetWindowRgn clips the visible area to a rounded rectangle inset by
//      the card margin, so the margin around the card is automatically
//      transparent and hit-testable through to the taskbar.
//   2. Post-process applies premultiplied alpha at CARD_ALPHA (~78%) to card
//      backdrop pixels and full opacity to text pixels. Text font matches
//      upstream's sc(-12) FW_MEDIUM Segoe UI.

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
const REF_CARD_WIDTH: i32 = 80;
const REF_CARD_MARGIN: i32 = 6; // outer margin around the card on all sides
const REF_CARD_CORNER_RADIUS: i32 = 6;
const REF_BADGE_WIDTH: i32 = REF_CARD_WIDTH + 2 * REF_CARD_MARGIN;
const REF_CARD_PAD_H: i32 = 8; // horizontal padding from card edge to text
const REF_LINE_H: i32 = 14;
const REF_LINE_GAP: i32 = 4;
const REF_FONT_HEIGHT: i32 = -12; // matches upstream's sc(-12) Segoe UI font

const BADGE_CLASS_NAME: &str = "ClaudeUsageProjectorBadge";
const WIDGET_CLASS_NAME: &str = "ClaudeCodeUsageMonitor";
const TASKBAR_CLASS_NAME: &str = "Shell_TrayWnd";

const TICK_TIMER_ID: usize = 1;
const TICK_INTERVAL_MS: u32 = 1_000;

const WM_APP_TICK: u32 = WM_USER + 1;

// COLORREF is 0x00BBGGRR (B in high byte, R in low byte).
//
// Card backdrop colour — chosen to sit a tone off the taskbar background so
// the chip has presence without harshly blocking it. The CARD_ALPHA blend
// keeps the taskbar visible through the card for that "tinted chip" look.
const COLOR_CARD_DARK: u32 = 0x002C2C2C; // slightly lighter than #1C1C1C dark taskbar
const COLOR_CARD_LIGHT: u32 = 0x00DEDEDE; // slightly darker than #F3F3F3 light taskbar
const CARD_ALPHA: u8 = 200; // ~78% opaque; rest blends with the taskbar

const COLOR_TEXT_DIM_DARK: u32 = 0x00888888;
const COLOR_TEXT_DIM_LIGHT: u32 = 0x00404040;

// CSM risk palette (encoded BGR).
const COLOR_RISK_HIGH: u32 = 0x004B4BE5; // #E54B4B
const COLOR_RISK_MED: u32 = 0x0000B3FF; // #FFB300
const COLOR_RISK_LOW: u32 = 0x0050AF4C; // #4CAF50
const COLOR_RISK_UNKNOWN: u32 = 0x009AA0A6;

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

    // Clip the visible area to the rounded card shape. Pixels outside this
    // region (i.e. the outer margin) are not drawn to the screen and are
    // hit-test-transparent — clicks pass through to the taskbar. SetWindowRgn
    // takes ownership of the HRGN; replacing it on each tick frees the old
    // one. We re-set every tick so DPI changes are picked up automatically.
    let card_margin = ((REF_CARD_MARGIN as f64) * scale).round() as i32;
    let card_radius = ((REF_CARD_CORNER_RADIUS as f64) * scale).round() as i32;
    let region = CreateRoundRectRgn(
        card_margin,
        card_margin,
        badge_w - card_margin + 1,
        badge_h - card_margin + 1,
        card_radius * 2,
        card_radius * 2,
    );
    if !region.is_invalid() {
        let _ = SetWindowRgn(hwnd, region, BOOL(1));
    }

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
    let card_color = if is_dark {
        COLOR_CARD_DARK
    } else {
        COLOR_CARD_LIGHT
    };
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

    paint_content(mem_dc, width, height, scale, card_color, text_dim, &latest);

    // Post-process: every pixel currently has either the card backdrop
    // colour (the sentinel we filled the DIB with) or a text colour drawn
    // on top. Card pixels become translucent (premultiplied at CARD_ALPHA),
    // text pixels become fully opaque. Pixels outside the rounded card area
    // are also card-coloured but are clipped at display time by the
    // SetWindowRgn applied in tick(), so they never reach the user.
    let card_alpha = CARD_ALPHA as u32;
    let pixel_count = (width * height) as usize;
    let pixel_data = std::slice::from_raw_parts_mut(bits as *mut u32, pixel_count);
    for px in pixel_data.iter_mut() {
        let rgb = *px & 0x00FFFFFF;
        if rgb == card_color {
            let b = ((rgb & 0xFF) * card_alpha) / 255;
            let g = (((rgb >> 8) & 0xFF) * card_alpha) / 255;
            let r = (((rgb >> 16) & 0xFF) * card_alpha) / 255;
            *px = (card_alpha << 24) | (r << 16) | (g << 8) | b;
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
    card_color: u32,
    text_dim: u32,
    latest: &Option<LatestPrediction>,
) {
    let sc = |v: i32| -> i32 { ((v as f64) * scale).round() as i32 };

    // Fill entire DIB with the card backdrop colour. The post-process
    // discriminates these pixels by exact-match against `card_color`, so any
    // anti-aliased text edges (blends of text colour with backdrop) won't
    // collide. The SetWindowRgn in tick() clips off the outer margin at
    // display time, so margin pixels never appear on screen.
    let full = RECT {
        left: 0,
        top: 0,
        right: width,
        bottom: height,
    };
    let card_brush = CreateSolidBrush(COLORREF(card_color));
    FillRect(hdc, &full, card_brush);
    let _ = DeleteObject(card_brush);

    // Text region: inset by margin + inner padding on each side so text
    // never crowds the rounded card edge.
    let inset_h = sc(REF_CARD_MARGIN) + sc(REF_CARD_PAD_H);
    let content_x = inset_h;
    let content_right = width - inset_h;

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

    // Two rows centred vertically in the full window height. The card area
    // is also vertically centred by symmetric margins, so this places the
    // text in the middle of the visible card.
    let line_h = sc(REF_LINE_H);
    let gap = sc(REF_LINE_GAP);
    let total_h = line_h * 2 + gap;
    let row1_y = (height - total_h) / 2;
    let row2_y = row1_y + line_h + gap;

    // Risk labels use title-case ("High" / "Med" / "Low") rather than ALL
    // CAPS so the visual weight matches upstream's mixed-case text style at
    // the same font height.
    let (risk_label, line_color) = match latest {
        Some(p) => {
            let label = match p.risk.as_str() {
                "high" => "High",
                "medium" => "Med",
                "low" => "Low",
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
