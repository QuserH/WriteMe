// 文档数据模型（与 Rust 侧 serde 结构保持一致）

export interface DocMeta {
  id: string;
  title: string;
  created_at: number; // Unix 毫秒
  updated_at: number; // Unix 毫秒
}

export interface Doc extends DocMeta {
  content: string;
}

export type SaveState = "idle" | "saving" | "saved" | "error";
