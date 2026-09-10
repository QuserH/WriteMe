use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use tauri::{Manager, State};

mod db;

// Note: 桌面壳 + 本地 SQLite（rusqlite/WAL）技术栈决策 — 见 .agents/notes/implemented/architecture/2026-09-08-stack-windows-tauri-react-sqlite.md
/// 全局数据库连接（SQLite 连接非 Sync，用 Mutex 包装）
struct AppDb(Mutex<rusqlite::Connection>);

fn now_millis() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn init_app(app: &mut tauri::App) -> Result<(), String> {
    // 数据目录：%APPDATA%\com.writeme.desktop
    let dir = app
        .path()
        .app_data_dir()
        .map_err(|e| format!("无法定位数据目录: {e}"))?;
    std::fs::create_dir_all(&dir).map_err(|e| format!("无法创建数据目录: {e}"))?;
    let conn = db::open(&dir.join("writeme.db"))?;
    app.manage(AppDb(Mutex::new(conn)));
    Ok(())
}

#[tauri::command]
fn list_documents(db: State<'_, AppDb>) -> Result<Vec<db::DocMeta>, String> {
    let conn = db.0.lock().map_err(|e| e.to_string())?;
    db::list_documents(&conn)
}

#[tauri::command]
fn create_document(
    db: State<'_, AppDb>,
    id: String,
    title: String,
) -> Result<db::DocMeta, String> {
    let conn = db.0.lock().map_err(|e| e.to_string())?;
    db::insert_document(&conn, &id, &title, now_millis())
}

#[tauri::command]
fn get_document(db: State<'_, AppDb>, id: String) -> Result<Option<db::Doc>, String> {
    let conn = db.0.lock().map_err(|e| e.to_string())?;
    db::get_document(&conn, &id)
}

#[tauri::command]
fn save_document(
    db: State<'_, AppDb>,
    id: String,
    title: String,
    content: String,
) -> Result<(), String> {
    let conn = db.0.lock().map_err(|e| e.to_string())?;
    db::update_document(&conn, &id, &title, &content, now_millis())
}

#[tauri::command]
fn delete_document(db: State<'_, AppDb>, id: String) -> Result<(), String> {
    let conn = db.0.lock().map_err(|e| e.to_string())?;
    db::delete_document(&conn, &id)
}

pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            if let Err(e) = init_app(app) {
                eprintln!("[writeme] 初始化失败: {e}");
                std::process::exit(1);
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            list_documents,
            create_document,
            get_document,
            save_document,
            delete_document
        ])
        .run(tauri::generate_context!())
        .expect("运行 WriteME 失败");
}
