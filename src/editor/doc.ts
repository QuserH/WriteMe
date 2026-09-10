import type { JSONContent } from "@tiptap/core";

// Note: 文档正文在 content 字段以 TipTap JSON 持久化（含旧纯文本自动迁移） — 见 .agents/notes/implemented/feature/2026-09-08-m1-tiptap-block-editor.md

/**
 * 正文内容格式约定：
 * - content 字段存储 TipTap 文档 JSON（JSON.stringify 后的字符串）。
 * - M0 时代的纯文本（不以 { 开头）在打开时自动迁移为段落块。
 * - 内联 marks 原样往返：bold / italic / underline / strike / code / link(href) / highlight(color)。
 *   多色高亮使用 TipTap Highlight 的 attrs.color；不额外剥离 marks 或生成另一套富文本字段。
 */

export const EMPTY_DOC: JSONContent = {
  type: "doc",
  content: [{ type: "paragraph" }],
};

export interface ParsedDoc {
  doc: JSONContent;
  /** true 表示输入不是规范 JSON，需要回写一次规范格式 */
  changed: boolean;
}

function isDocShape(v: unknown): v is JSONContent {
  return (
    typeof v === "object" &&
    v !== null &&
    (v as { type?: unknown }).type === "doc"
  );
}

/** 递归归一化：旧 toggleBlock（attrs.title + block+）→ 新结构（首段=标题段落） */
function normalizeToggle(doc: JSONContent): boolean {
  let changed = false;
  const walk = (node: JSONContent): void => {
    if (node.type === "toggleBlock") {
      if (!Array.isArray(node.content)) {
        node.content = [];
        changed = true;
      }
      const attrs = { ...node.attrs };
      // 旧标题即使为空，也必须补回标题段，不能把第一段正文误当标题。
      if (Object.prototype.hasOwnProperty.call(attrs, "title")) {
        const title = typeof attrs.title === "string" ? attrs.title : "";
        delete attrs.title;
        node.attrs = attrs;
        node.content.unshift({
          type: "paragraph",
          ...(title ? { content: [{ type: "text", text: title }] } : {}),
        });
        changed = true;
      }
      // 旧格式首块可能不是段落 → 补空标题段在最前
      if (node.content[0]?.type !== "paragraph") {
        node.content.unshift({ type: "paragraph" });
        changed = true;
      }
    }
    for (const child of node.content ?? []) walk(child);
  };
  walk(doc);
  return changed;
}

/** 把数据库里的原始 content 解析为 TipTap 文档对象。 */
export function parseContent(raw: string): ParsedDoc {
  if (raw === "") {
    return { doc: EMPTY_DOC, changed: true };
  }
  if (raw.trimStart().startsWith("{")) {
    try {
      const parsed: unknown = JSON.parse(raw);
      if (isDocShape(parsed)) {
        const migrated = normalizeToggle(parsed);
        return { doc: parsed, changed: migrated };
      }
    } catch {
      // 落到下方纯文本迁移
    }
  }
  // M0 纯文本迁移：按行切成段落（空行跳过）
  const paragraphs: JSONContent[] = raw
    .split(/\r?\n/)
    .filter((line) => line.trim().length > 0)
    .map((line) => ({ type: "paragraph", content: [{ type: "text", text: line }] }));
  return {
    doc: paragraphs.length
      ? { type: "doc", content: paragraphs }
      : { ...EMPTY_DOC, content: [{ type: "paragraph" }] },
    changed: true,
  };
}

export function serializeDoc(doc: JSONContent): string {
  return JSON.stringify(doc);
}
