import { invoke } from "@tauri-apps/api/core";
import type { Doc, DocMeta } from "../types";

// Note: 桌面/演示双后端抽象与 M0 自动保存数据契约 — 见 .agents/notes/implemented/feature/2026-09-08-m0-skeleton-local-autosave.md
/**
 * 数据访问层。
 * 桌面端（Tauri）→ 调用 Rust 侧 SQLite 命令；
 * 浏览器演示模式 → localStorage 模拟（便于纯 Web 预览与后续 PWA）。
 */

const TAURI_MARKER = "__TAURI_INTERNALS__";

export const isDesktop: boolean =
  typeof window !== "undefined" && TAURI_MARKER in window;

const DEMO_KEY = "writeme-demo-v1";

function now(): number {
  return Date.now();
}

function readDemo(): Doc[] {
  try {
    const raw = localStorage.getItem(DEMO_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as Doc[]) : [];
  } catch {
    return [];
  }
}

function writeDemo(docs: Doc[]): void {
  localStorage.setItem(DEMO_KEY, JSON.stringify(docs));
}

function metaOf(doc: Doc): DocMeta {
  return {
    id: doc.id,
    title: doc.title,
    created_at: doc.created_at,
    updated_at: doc.updated_at,
  };
}

export async function listDocuments(): Promise<DocMeta[]> {
  if (isDesktop) {
    return invoke<DocMeta[]>("list_documents");
  }
  return readDemo()
    .map(metaOf)
    .sort((a, b) => b.updated_at - a.updated_at);
}

export async function createDocument(
  id: string,
  title: string,
): Promise<DocMeta> {
  if (isDesktop) {
    return invoke<DocMeta>("create_document", { id, title });
  }
  const doc: Doc = { id, title, content: "", created_at: now(), updated_at: now() };
  const docs = readDemo();
  docs.push(doc);
  writeDemo(docs);
  return metaOf(doc);
}

export async function getDocument(id: string): Promise<Doc | null> {
  if (isDesktop) {
    return invoke<Doc | null>("get_document", { id });
  }
  return readDemo().find((d) => d.id === id) ?? null;
}

export async function updateDocument(
  id: string,
  title: string,
  content: string,
): Promise<void> {
  if (isDesktop) {
    await invoke("save_document", { id, title, content });
    return;
  }
  const docs = readDemo();
  const doc = docs.find((d) => d.id === id);
  if (!doc) throw new Error("文档不存在: " + id);
  doc.title = title;
  doc.content = content;
  doc.updated_at = now();
  writeDemo(docs);
}

export async function deleteDocument(id: string): Promise<void> {
  if (isDesktop) {
    await invoke("delete_document", { id });
    return;
  }
  writeDemo(readDemo().filter((d) => d.id !== id));
}
