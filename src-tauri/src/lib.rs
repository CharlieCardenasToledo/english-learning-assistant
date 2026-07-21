use tauri::Emitter;

#[tauri::command]
fn dotnet_request(request: &str) -> String {
    tauri_dotnet_bridge_host::process_request(request)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .invoke_handler(tauri::generate_handler![dotnet_request])
        .setup(|app| {
            let app_handle = app.handle().clone();
            tauri_dotnet_bridge_host::register_emit(move |event_name, payload| {
                // payload arrives as a JSON string from .NET — parse it into a Value
                // so Tauri emits an object (not a double-encoded string) to the frontend.
                let json: serde_json::Value = serde_json::from_str(payload)
                    .unwrap_or_else(|_| serde_json::Value::String(payload.to_string()));
                app_handle
                    .emit(event_name, json)
                    .expect(&format!("Failed to emit event '{}'", event_name));
            });
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
