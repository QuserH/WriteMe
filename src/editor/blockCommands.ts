import { isActive, type ChainedCommands } from "@tiptap/core";
import type { IconName } from "../components/Icon";

// Note: 块类型搜索与幂等转换 — 见 .agents/notes/implemented/feature/2026-09-09-slash-menu.md

export const BLOCK_TYPES = [
  { kind: "paragraph", label: "正文", hint: "普通段落", icon: "text", keywords: "text paragraph normal zhengwen duanluo" },
  { kind: "h1", label: "标题1", hint: "大标题", icon: "heading", keywords: "heading1 heading title h1 biaoti" },
  { kind: "h2", label: "标题2", hint: "中标题", icon: "heading", keywords: "heading2 heading subtitle h2 biaoti" },
  { kind: "h3", label: "标题3", hint: "小标题", icon: "heading", keywords: "heading3 heading h3 biaoti" },
  { kind: "bulletList", label: "项目符号列表", hint: "无序", icon: "bulletList", keywords: "bullet unordered list liebiao wuxu" },
  { kind: "orderedList", label: "编号列表", hint: "有序", icon: "orderedList", keywords: "number ordered list liebiao bianhao" },
  { kind: "taskList", label: "待办清单", hint: "任务", icon: "taskList", keywords: "todo task checklist check daiban renwu" },
  { kind: "blockquote", label: "引用", hint: "块引用", icon: "quote", keywords: "quote blockquote yinyong" },
  { kind: "codeBlock", label: "代码块", hint: "等宽", icon: "code", keywords: "code codeblock daima" },
  { kind: "toggle", label: "折叠块", hint: "可收起", icon: "toggle", keywords: "toggle fold collapse outline zhedie 大纲" },
] as const satisfies ReadonlyArray<{ kind: string; label: string; hint: string; icon: IconName; keywords: string }>;

export type BlockKind = (typeof BLOCK_TYPES)[number]["kind"];

export function filterBlockTypes(query: string) {
  const words = query.normalize("NFKC").toLowerCase().trim().split(/\s+/).filter(Boolean);
  return BLOCK_TYPES.filter((item) => {
    const searchable = `${item.label} ${item.hint} ${item.kind} ${item.keywords}`.toLowerCase();
    return words.every((word) => searchable.includes(word));
  });
}

/** 块菜单与斜杠菜单共用命令，调用方负责选区与折叠标题解包。 */
export function applyBlockKind(chain: ChainedCommands, kind: BlockKind): ChainedCommands {
  switch (kind) {
    case "paragraph": return chain.setParagraph();
    case "h1": return chain.setHeading({ level: 1 });
    case "h2": return chain.setHeading({ level: 2 });
    case "h3": return chain.setHeading({ level: 3 });
    case "bulletList": return chain.command(({ state, commands }) => isActive(state, "bulletList") || commands.toggleBulletList());
    case "orderedList": return chain.command(({ state, commands }) => isActive(state, "orderedList") || commands.toggleOrderedList());
    case "taskList": return chain.command(({ state, commands }) => isActive(state, "taskList") || commands.toggleTaskList());
    case "blockquote": return chain.command(({ state, commands }) => isActive(state, "blockquote") || commands.toggleBlockquote());
    case "codeBlock": return chain.setCodeBlock();
    case "toggle": return chain.setToggleBlock();
  }
}
