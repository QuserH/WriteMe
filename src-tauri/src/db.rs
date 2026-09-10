use rusqlite::{params, Connection};
use serde::Serialize;

#[derive(Clone, Serialize)]
pub struct DocMeta {
    pub id: String,
    pub title: String,
    pub created_at: i64,
    pub updated_at: i64,
}

#[derive(Clone, Serialize)]
pub struct Doc {
    pub id: String,
    pub title: String,
    pub content: String,
    pub created_at: i64,
    pub updated_at: i64,
}

/// 打开（必要时创建）数据库并初始化表结构。
pub fn open(path: &std::path::Path) -> Result<Connection, String> {
    let conn = Connection::open(path).map_err(|e| format!("无法打开数据库: {e}"))?;
    conn.execute_batch(
        "PRAGMA journal_mode=WAL;
         PRAGMA foreign_keys=ON;
         CREATE TABLE IF NOT EXISTS documents (
             id         TEXT PRIMARY KEY,
             title      TEXT NOT NULL DEFAULT '',
             content    TEXT NOT NULL DEFAULT '',
             created_at INTEGER NOT NULL,
             updated_at INTEGER NOT NULL
         );
         CREATE INDEX IF NOT EXISTS idx_documents_updated ON documents(updated_at DESC);",
    )
    .map_err(|e| format!("数据库初始化失败: {e}"))?;
    Ok(conn)
}

pub fn list_documents(conn: &Connection) -> Result<Vec<DocMeta>, String> {
    let mut stmt = conn
        .prepare("SELECT id, title, created_at, updated_at FROM documents ORDER BY updated_at DESC")
        .map_err(|e| e.to_string())?;
    let rows = stmt
        .query_map([], |r| {
            Ok(DocMeta {
                id: r.get(0)?,
                title: r.get(1)?,
                created_at: r.get(2)?,
                updated_at: r.get(3)?,
            })
        })
        .map_err(|e| e.to_string())?;
    let mut out = Vec::new();
    for row in rows {
        out.push(row.map_err(|e| e.to_string())?);
    }
    Ok(out)
}

pub fn insert_document(
    conn: &Connection,
    id: &str,
    title: &str,
    now: i64,
) -> Result<DocMeta, String> {
    conn.execute(
        "INSERT INTO documents (id, title, content, created_at, updated_at)
         VALUES (?1, ?2, '', ?3, ?3)",
        params![id, title, now],
    )
    .map_err(|e| format!("创建文档失败: {e}"))?;
    Ok(DocMeta {
        id: id.to_string(),
        title: title.to_string(),
        created_at: now,
        updated_at: now,
    })
}

pub fn get_document(conn: &Connection, id: &str) -> Result<Option<Doc>, String> {
    let mut stmt = conn
        .prepare(
            "SELECT id, title, content, created_at, updated_at
             FROM documents WHERE id = ?1",
        )
        .map_err(|e| e.to_string())?;
    let mut rows = stmt
        .query_map(params![id], |r| {
            Ok(Doc {
                id: r.get(0)?,
                title: r.get(1)?,
                content: r.get(2)?,
                created_at: r.get(3)?,
                updated_at: r.get(4)?,
            })
        })
        .map_err(|e| e.to_string())?;
    rows.next().map(|r| r.map_err(|e| e.to_string())).transpose()
}

pub fn update_document(
    conn: &Connection,
    id: &str,
    title: &str,
    content: &str,
    now: i64,
) -> Result<(), String> {
    let n = conn
        .execute(
            "UPDATE documents SET title = ?1, content = ?2, updated_at = ?3 WHERE id = ?4",
            params![title, content, now, id],
        )
        .map_err(|e| format!("保存文档失败: {e}"))?;
    if n == 0 {
        return Err(format!("文档不存在: {id}"));
    }
    Ok(())
}

pub fn delete_document(conn: &Connection, id: &str) -> Result<(), String> {
    conn.execute("DELETE FROM documents WHERE id = ?1", params![id])
        .map_err(|e| format!("删除文档失败: {e}"))?;
    Ok(())
}
