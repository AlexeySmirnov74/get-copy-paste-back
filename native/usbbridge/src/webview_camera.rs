//! A genuine, isolated WebView2 diagnostic. Native identities are selection hints,
//! never reported as values obtained through the browser's camera APIs.
use crate::{
    camera,
    desktop::{Role, wide},
    diagnostics, i18n, settings,
};
use serde::Serialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    io,
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::{Command, Stdio},
};
use windows_sys::Win32::{
    Foundation::HWND,
    UI::WindowsAndMessaging::{
        FindWindowW, IDYES, MB_DEFBUTTON2, MB_ICONQUESTION, MB_YESNO, MessageBoxW, PostMessageW,
        SW_RESTORE, SetForegroundWindow, ShowWindow, WM_CLOSE,
    },
};
/// A Windows-only UI hint. Never include native policy in the browser report.
fn camera_policy_denied() -> bool {
    use windows_sys::Win32::System::Registry::{
        HKEY_LOCAL_MACHINE, RRF_RT_REG_DWORD, RegGetValueW,
    };
    let mut value = 0u32;
    let mut size = std::mem::size_of_val(&value) as u32;
    let code = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            wide(r"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy").as_ptr(),
            wide("LetAppsAccessCamera").as_ptr(),
            RRF_RT_REG_DWORD,
            std::ptr::null_mut(),
            (&mut value as *mut u32).cast(),
            &mut size,
        )
    };
    code == 0 && value == 2
}
fn role_key(role: Role) -> &'static str {
    if role == Role::Server {
        "server"
    } else {
        "client"
    }
}
fn window_class(role: Role, site_check: bool) -> &'static str {
    match (role, site_check) {
        (Role::Server, false) => "USBBridge.WebViewCamera.Server.1",
        (Role::Server, true) => "USBBridge.WebViewCamera.Website.1",
        (Role::Client, false) => "USBBridge.WebViewCamera.Client.1",
        (Role::Client, true) => "USBBridge.WebViewCamera.Website.Client.1",
    }
}
const MAX_MESSAGE_UNITS: usize = 262_144;
const PAGE: &str = include_str!("../assets/webview-camera.html");
unsafe extern "C" {
    fn ub_webview_camera_run(
        html: *const u8,
        html_length: usize,
        script: *const u16,
        user_data: *const u16,
        title: *const u16,
        error_text: *const u16,
        context: *mut c_void,
        sink: unsafe extern "C" fn(*mut c_void, HWND, *const u16),
        check_seconds: u32,
        site_check: i32,
        client_role: i32,
    ) -> i32;
    fn ub_webview_camera_copy(owner: HWND, text: *const u16) -> i32;
    fn ub_webview_camera_ack(owner: HWND, message: *const u16);
    fn ub_webview_camera_permission(owner: HWND, allow: i32);
    fn ub_webview_camera_save_path(
        owner: HWND,
        title: *const u16,
        path: *mut u16,
        capacity: u32,
    ) -> i32;
}

#[derive(Serialize)]
struct Hint {
    identity: String,
    name: String,
    vendor: Option<u16>,
    product: Option<u16>,
}
fn usb_identity(value: &str) -> Option<(u16, u16)> {
    let id = value.to_ascii_uppercase();
    if !id.starts_with("USB\\VID_") || id.contains("&MI_") {
        return None;
    }
    let vendor = u16::from_str_radix(id.get(8..12)?, 16).ok()?;
    if id.get(12..17)? != "&PID_" {
        return None;
    }
    let product = u16::from_str_radix(id.get(17..21)?, 16).ok()?;
    Some((vendor, product))
}
fn physical_identity(value: &str) -> Option<(u16, u16)> {
    usb_identity(value).filter(|pair| *pair != (0x1209, 0xf002))
}
fn hints(
    role: Role,
    cameras: Vec<camera::Camera>,
    catalog: Option<&crate::management::Catalog>,
) -> Vec<Hint> {
    cameras
        .into_iter()
        .filter_map(|camera| {
            let identity = camera.stable_identity();
            let ids = if role == Role::Server {
                Some(physical_identity(&identity)?)
            } else {
                usb_identity(&identity)
            };
            if role == Role::Server
                && catalog.is_some_and(|catalog| {
                    !catalog
                        .devices
                        .iter()
                        .any(|row| row.present && row.identity.eq_ignore_ascii_case(&identity))
                })
            {
                return None;
            }
            Some(Hint {
                identity,
                name: camera.name,
                vendor: ids.map(|pair| pair.0),
                product: ids.map(|pair| pair.1),
            })
        })
        .take(256)
        .collect()
}
fn foreground_existing_mode(role: Role, site_check: bool) -> bool {
    unsafe {
        let class = window_class(role, site_check);
        let window = FindWindowW(wide(class).as_ptr(), std::ptr::null());
        if window.is_null() {
            return false;
        }
        ShowWindow(window, SW_RESTORE);
        SetForegroundWindow(window);
    }
    true
}
pub fn open(_owner: HWND, config: &Path, role: Role, identity: Option<String>) -> io::Result<()> {
    launch(config, role, identity.as_deref(), None)
}
pub fn launch(
    config: &Path,
    role: Role,
    identity: Option<&str>,
    check_seconds: Option<u32>,
) -> io::Result<()> {
    launch_mode(config, role, identity, check_seconds, false)
}
pub fn launch_site(config: &Path, role: Role) -> io::Result<()> {
    launch_mode(config, role, None, None, true)
}
fn launch_mode(
    config: &Path,
    role: Role,
    identity: Option<&str>,
    check_seconds: Option<u32>,
    site_check: bool,
) -> io::Result<()> {
    if role == Role::Server && identity.is_some_and(|value| physical_identity(value).is_none()) {
        return Err(io::Error::other(i18n::t(
            "Choose a physical USB camera for WebView inspection",
        )));
    }
    if check_seconds.is_none() && foreground_existing_mode(role, site_check) {
        if identity.is_some() {
            return Err(io::Error::other(i18n::t(
                "WebView inspection is already open. Select the camera in that window, or close it before starting another inspection.",
            )));
        }
        return Ok(());
    }
    let mut command = Command::new(std::env::current_exe()?.with_file_name("usbbridge-agent.exe"));
    command
        .args(["--webview-camera", "--role", role_key(role), "--config"])
        .arg(config);
    if site_check {
        command.arg("--webview-website-check");
    }
    if let Some(identity) = identity {
        command.args(["--camera-identity", identity]);
    }
    if let Some(seconds) = check_seconds {
        if (!site_check && identity.is_none()) || !(5..=60).contains(&seconds) {
            return Err(io::Error::other(
                "WebView capture check requires an exact locally available camera and 5..60 seconds",
            ));
        }
        command.args(["--webview-check-seconds", &seconds.to_string()]);
    }
    command
        .creation_flags(0x08000000)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()?;
    Ok(())
}
struct Context {
    config: PathBuf,
    role: Role,
    site_check: bool,
    check_file: Option<PathBuf>,
    report: Option<Value>,
    error: Option<String>,
}
fn report_payload(message: &Value) -> io::Result<&Value> {
    let report = message
        .get("report")
        .filter(|report| report.is_object())
        .ok_or_else(|| io::Error::other("Missing WebView report object"))?;
    if [
        "schema",
        "nativeHint",
        "collectedAt",
        "measurement",
        "limitations",
    ]
    .iter()
    .any(|key| report.get(key).is_some())
    {
        return Err(io::Error::other(
            "Only browser API data belongs in this report",
        ));
    }
    if serde_json::to_vec(report).map_err(io::Error::other)?.len() > MAX_MESSAGE_UNITS {
        return Err(io::Error::other("WebView report exceeds 256 KiB"));
    }
    Ok(report)
}
unsafe extern "C" fn on_message(context: *mut c_void, owner: HWND, message: *const u16) {
    // This callback receives only the fixed embedded origin. Bound conversion and
    // never log/export images, frame buffers or native camera control values.
    if context.is_null() || message.is_null() {
        return;
    }
    let mut length = 0;
    while length < MAX_MESSAGE_UNITS && unsafe { *message.add(length) } != 0 {
        length += 1;
    }
    if length == MAX_MESSAGE_UNITS {
        return;
    }
    let text = String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(message, length) });
    let Ok(value) = serde_json::from_str::<Value>(&text) else {
        return;
    };
    // Native save dialogs pump messages. Keep no Rust borrow of Context alive
    // across a native/UI call: report notifications can re-enter this callback.
    let context = context.cast::<Context>();
    let kind = value.get("type").and_then(Value::as_str).unwrap_or("");
    let mut cancelled = false;
    let result: io::Result<()> = (|| {
        match kind {
            "ready" => {}
            "report" => {
                let report = report_payload(&value)?.clone();
                diagnostics::event(
                    "ui.webview_camera.report",
                    json!({"bytes":text.len(),"capture_active":report.pointer("/camera/readyState").and_then(Value::as_str)==Some("live"),"capture_error":report.pointer("/errors/getUserMedia/name").and_then(Value::as_str).map(|s|s.chars().take(80).collect::<String>())}),
                );
                unsafe {
                    (*context).report = Some(report);
                }
            }
            "website-check" => {
                let config = unsafe { (*context).config.clone() };
                let role = unsafe { (*context).role };
                launch_mode(&config, role, None, None, true)?;
            }
            "request-permission" => {
                if !unsafe { (*context).site_check } {
                    return Ok(());
                }
                let prompt = match i18n::language().code() {
                    "ru" => {
                        "Разрешить этой локальной тестовой странице доступ к камерам?\n\nПосле разрешения страница сможет увидеть названия и возможности камер. Камера откроется только после её выбора и нажатия Start inspection. Микрофон не запрашивается."
                    }
                    "de" => {
                        "Dieser lokalen Testseite Kamerazugriff erlauben?\n\nDie Seite kann danach Kameranamen und Fähigkeiten sehen. Eine Kamera öffnet sich erst nach Auswahl und Start inspection. Kein Mikrofonzugriff."
                    }
                    "fr" => {
                        "Autoriser cette page de test locale à accéder aux caméras ?\n\nElle pourra ensuite lire les noms et capacités des caméras. Une caméra s’ouvre seulement après sélection et Start inspection. Aucun accès microphone."
                    }
                    "zh" => {
                        "允许此本地测试页面访问摄像头？\n\n允许后页面可读取摄像头名称和功能。仅在选择摄像头并点击 Start inspection 后打开摄像头。不请求麦克风访问。"
                    }
                    _ => {
                        "Allow this local test page to access cameras?\n\nAfter permission, the page can read camera names and capabilities. A camera opens only after selection and Start inspection. No microphone access is requested."
                    }
                };
                let answer = unsafe {
                    MessageBoxW(
                        owner,
                        wide(prompt).as_ptr(),
                        wide(crate::APP_NAME).as_ptr(),
                        MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2,
                    )
                };
                unsafe {
                    ub_webview_camera_permission(owner, i32::from(answer == IDYES));
                }
            }
            "copy" => {
                let report = report_payload(&value)?;
                let text = wide(&serde_json::to_string_pretty(report).map_err(io::Error::other)?);
                let code = unsafe { ub_webview_camera_copy(owner, text.as_ptr()) };
                if code < 0 {
                    return Err(io::Error::other(format!(
                        "Clipboard: 0x{:08X}",
                        code as u32
                    )));
                }
                diagnostics::event("ui.webview_camera.copy", json!({}));
            }
            "save" => {
                let report = report_payload(&value)?;
                let title = wide(i18n::t("Save WebView camera report"));
                let mut path = [0u16; 32768];
                let result = unsafe {
                    ub_webview_camera_save_path(
                        owner,
                        title.as_ptr(),
                        path.as_mut_ptr(),
                        path.len() as u32,
                    )
                };
                if result == 1 {
                    cancelled = true;
                    return Ok(());
                }
                if result < 0 {
                    return Err(io::Error::other(format!(
                        "Save dialog: 0x{:08X}",
                        result as u32
                    )));
                }
                let length = path.iter().position(|value| *value == 0).unwrap_or(0);
                if length == 0 {
                    return Err(io::Error::other("No report path"));
                }
                let path = PathBuf::from(String::from_utf16_lossy(&path[..length]));
                std::fs::write(
                    path,
                    serde_json::to_vec_pretty(report).map_err(io::Error::other)?,
                )?;
                diagnostics::event("ui.webview_camera.save", json!({}));
            }
            "close" => unsafe {
                PostMessageW(owner, WM_CLOSE, 0, 0);
            },
            "host_error" => unsafe {
                (*context).error = Some(format!(
                    "WebView initialization failed: {}",
                    value["hresult"]
                ));
            },
            "event" => {
                let action = value
                    .get("name")
                    .and_then(Value::as_str)
                    .unwrap_or("unknown");
                if action.len() <= 64 {
                    diagnostics::event("ui.webview_camera.action", json!({"action":action}));
                }
            }
            _ => {}
        }
        Ok(())
    })();
    if matches!(kind, "copy" | "save") {
        let feedback = wide(&json!({"type":"action-result","action":kind,"status":if result.is_err(){"error"}else if cancelled{"cancelled"}else{"success"},"error":result.as_ref().err().map(ToString::to_string)}).to_string());
        unsafe {
            ub_webview_camera_ack(owner, feedback.as_ptr());
        }
    }
    if let Err(error) = result {
        diagnostics::event(
            "ui.webview_camera.error",
            json!({"error":error.to_string()}),
        );
        unsafe {
            (*context).error = Some(error.to_string());
        }
    }
}
pub fn run(
    config: &Path,
    role: Role,
    identity: Option<String>,
    check_seconds: Option<u32>,
) -> io::Result<()> {
    run_mode(config, role, identity, check_seconds, false)
}
pub fn run_mode(
    config: &Path,
    role: Role,
    identity: Option<String>,
    check_seconds: Option<u32>,
    site_check: bool,
) -> io::Result<()> {
    i18n::set_language(i18n::preferred(config));
    diagnostics::init(config, "webview-camera");
    let Some(_instance) = crate::instance::acquire(
        role_key(role),
        if site_check {
            "webview-website-check"
        } else {
            "webview-camera"
        },
    )?
    else {
        foreground_existing_mode(role, site_check);
        return Ok(());
    };
    let catalog = if role == Role::Server {
        let _: settings::HostSettings = settings::read(config)?;
        settings::read::<crate::management::Catalog>(&config.with_extension("catalog.local.json"))
            .ok()
    } else {
        let _: settings::ClientSettings = settings::read(config)?;
        None
    };
    let native_cameras = camera::list()?;
    let requested_identity = identity.as_ref().and_then(|identity| {
        let mut matches = native_cameras
            .iter()
            .filter(|camera| camera.matches(identity));
        let first = matches.next()?;
        matches.next().is_none().then(|| first.stable_identity())
    });
    let cameras = hints(role, native_cameras, catalog.as_ref());
    let selected = requested_identity.as_ref().and_then(|identity| {
        cameras
            .iter()
            .find(|camera| camera.identity.eq_ignore_ascii_case(identity))
    });
    if identity.is_some() && selected.is_none() {
        return Err(io::Error::other(i18n::t(if role == Role::Server {
            "The selected physical camera is not available locally. It may be shared or in use by a client. No sharing settings were changed."
        } else {
            "The selected camera is not available on this client. Connect it first or use the local camera picker."
        })));
    }
    if check_seconds.is_some_and(|seconds| !(5..=60).contains(&seconds))
        || (check_seconds.is_some() && !site_check && selected.is_none())
    {
        return Err(io::Error::other(
            "WebView capture check requires a locally available exact camera and 5..60 seconds",
        ));
    }
    let digest = Sha256::digest(config.to_string_lossy().to_ascii_lowercase().as_bytes());
    let key: String = digest[..12]
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    let mut user_data = PathBuf::from(
        std::env::var_os("LOCALAPPDATA")
            .ok_or_else(|| io::Error::other("LOCALAPPDATA is unavailable"))?,
    )
    .join(crate::APP_NAME)
    .join("WebViewCamera")
    .join(if role == Role::Client {
        format!("Client/{key}")
    } else {
        key
    });
    if site_check {
        user_data = user_data.join("WebsiteCheck");
    }
    std::fs::create_dir_all(&user_data)?;
    let bootstrap = json!({
        "language":i18n::language().code(),"version":crate::APP_VERSION,
        "siteCheck":site_check,"role":role_key(role),"cameraPolicyDenied":camera_policy_denied(),
        "requested":selected.map(|hint|json!({"name":hint.name,"vendor":hint.vendor,"product":hint.product})),
        "cameras":cameras.iter().map(|hint|json!({"name":hint.name,"vendor":hint.vendor,"product":hint.product})).collect::<Vec<_>>(),
        "features":{"save":true},
        "autoStart":check_seconds.is_some() && !site_check,
    });
    let script = wide(&format!("window.bridgeCameraProbe.configure({bootstrap});"));
    let user_data = wide(&user_data.to_string_lossy());
    let title = wide(&format!(
        "{} — {} — {} v.{}",
        crate::APP_NAME,
        role.label(),
        if site_check {
            match i18n::language().code() {
                "ru" => "Проверка доступа сайта",
                "de" => "Website-Zugriff prüfen",
                "fr" => "Vérifier l’accès du site",
                "zh" => "检查网站访问权限",
                _ => "Website access check",
            }
        } else {
            i18n::t("WebView Camera")
        },
        crate::APP_VERSION
    ));
    let error_text = wide(i18n::t(
        "WebView2 camera inspection could not start. An installed Microsoft Edge WebView2 Runtime is required; this tool does not download components.",
    ));
    let mut context = Context {
        config: config.to_path_buf(),
        role,
        site_check,
        check_file: check_seconds.map(|_| config.with_extension("webview-check.local.json")),
        report: None,
        error: None,
    };
    diagnostics::event(
        "ui.webview_camera.open",
        json!({"role":role_key(role),"locally_available_cameras":bootstrap["cameras"].as_array().map(Vec::len),"check_mode":check_seconds.is_some(),"site_check":site_check}),
    );
    let result = unsafe {
        ub_webview_camera_run(
            PAGE.as_ptr(),
            PAGE.len(),
            script.as_ptr(),
            user_data.as_ptr(),
            title.as_ptr(),
            error_text.as_ptr(),
            (&mut context as *mut Context).cast(),
            on_message,
            check_seconds.unwrap_or(0),
            i32::from(site_check),
            i32::from(role == Role::Client),
        )
    };
    if let Some(path) = &context.check_file {
        settings::save(
            path,
            &json!({"version":crate::APP_VERSION,"hresult":result,"error":context.error,"report":context.report}),
        )?;
    }
    diagnostics::event("ui.webview_camera.close", json!({"hresult":result}));
    if result < 0 {
        return Err(io::Error::other(format!(
            "{} (0x{:08X})",
            i18n::t(
                "WebView2 camera inspection could not start. An installed Microsoft Edge WebView2 Runtime is required; this tool does not download components."
            ),
            result as u32
        )));
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn webview_client_keeps_generated_and_capture_sources_while_server_filters_them() {
        let cameras = vec![
            camera::Camera {
                path: "physical-path".into(),
                name: "Physical camera".into(),
                identities: vec!["USB\\VID_1234&PID_ABCD\\SERIAL".into()],
            },
            camera::Camera {
                path: "generated-path".into(),
                name: "Client USB camera".into(),
                identities: vec!["USB\\VID_1209&PID_F002\\STABLE".into()],
            },
            camera::Camera {
                path: "unique-capture-moniker".into(),
                name: "Unity Capture Client".into(),
                identities: vec![],
            },
        ];
        let client = hints(Role::Client, cameras.clone(), None);
        assert_eq!(client.len(), 3);
        assert_eq!(client[1].vendor, Some(0x1209));
        assert_eq!(client[2].vendor, None);
        assert_eq!(hints(Role::Server, cameras, None).len(), 1);
    }
    #[test]
    fn webview_client_and_server_windows_are_independent() {
        let classes = [
            window_class(Role::Server, false),
            window_class(Role::Server, true),
            window_class(Role::Client, false),
            window_class(Role::Client, true),
        ];
        assert_eq!(
            classes
                .iter()
                .collect::<std::collections::HashSet<_>>()
                .len(),
            4
        );
        assert_ne!(role_key(Role::Server), role_key(Role::Client));
    }
    #[test]
    fn webview_physical_identity_excludes_generated_and_non_usb_sources() {
        assert_eq!(
            physical_identity("USB\\VID_04F2&PID_B729\\SERIAL"),
            Some((0x04f2, 0xb729))
        );
        for value in [
            "foo:bar",
            "@device:software",
            "USB\\VID_1209&PID_F002\\SERIAL",
            "USB\\VID_04F2&PID_B729&MI_00\\SERIAL",
        ] {
            assert!(physical_identity(value).is_none());
        }
    }
    #[test]
    fn webview_native_hints_never_substitute_browser_ids() {
        let cameras = vec![
            camera::Camera {
                path: "native-device-path".into(),
                name: "Physical camera".into(),
                identities: vec!["USB\\VID_1234&PID_ABCD\\SERIAL".into()],
            },
            camera::Camera {
                path: "foo:bar".into(),
                name: "Software source".into(),
                identities: vec![],
            },
        ];
        let values = hints(Role::Server, cameras, None);
        assert_eq!(values.len(), 1);
        assert_eq!(values[0].vendor, Some(0x1234));
        let value = serde_json::to_value(&values[0]).unwrap();
        assert!(value.get("deviceId").is_none());
        assert!(value.get("groupId").is_none());
        assert!(value.get("path").is_none());
    }
    #[test]
    fn webview_report_object_and_size_are_validated() {
        assert!(
            report_payload(&json!({"report":{"nativeHint":{"identity":"native-serial"}}})).is_err()
        );
        assert!(report_payload(&json!({"report":{"schema":"private-format"}})).is_err());
        assert!(report_payload(&json!({"report":[]})).is_err());
        assert!(
            report_payload(&json!({"report":{"value":"x".repeat(MAX_MESSAGE_UNITS)}})).is_err()
        );
        assert!(
            report_payload(&json!({"report":{"selectedCamera":{"deviceId":"actual-browser-id"}}}))
                .is_ok()
        );
    }
}
