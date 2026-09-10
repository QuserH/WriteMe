# Agent Note: M1 块编辑器——TipTap JSON 持久化与旧文本迁移

Status: implemented

## Problem

M0 用纯文本 textarea 占位。产品核心是类 Craft 的块级编辑体验；同时未来同步（M6 CRDT）与双向链接需要结构化正文，正文存储格式需在编辑器落地时立约，避免二次返工。

## Decision

- 正文 `content` 字段存储 TipTap 文档 JSON（`doc` 树 `JSON.stringify` 字符串），解析/序列化集中在 `src/editor/doc.ts`。
- `src/editor/Editor.tsx` 提供编辑器组件：StarterKit（heading h1–h3，v3 内置 Underline 与 Link）＋ Highlight(multicolor) ＋ TaskList/TaskItem(nested) ＋ Placeholder ＋ TabIndent。列表与待办的 Tab/Shift+Tab 交给列表命令，折叠树由 ToggleBlock 的高优先级命令处理。Markdown 快捷输入（`##`/`-`/`1.`/`[ ]`/`>`/```）由 TipTap 输入规则提供，`+ 空格` 专用于折叠块。
- 内联数据直接使用 marks：bold / italic / underline / strike / code / link(href 等属性) / highlight(color)。`doc.ts` 原样解析与保存，不剥离 marks，不新增平行富文本字段。多色高亮使用 `attrs.color`；旧文档不需要额外迁移。Link 设置 openOnClick=false、defaultProtocol=https，避免编辑时普通点击导航。具体表单、选区与快捷键约定见 [文字格式工具栏](2026-09-09-text-formatting-toolbar.md)。
- 折叠块是 `toggleBlock(paragraph block*)`：首段标题、后续子树，`attrs.collapsed` 持久化各级状态，仅含标题的节点合法。解析器递归迁移旧 `attrs.title`（包括空标题）并保留原正文；规范文档重复打开不会重复迁移。详细编辑与折叠边界见 [折叠块笔记](2026-09-09-toggle-block.md)。
- 打开文档时 `parseContent` 负责兼容：旧纯文本按行迁移为段落块，空内容规范化为 `{type:"doc",content:[paragraph]}`；迁移发生时通过 `onCreate` 回写一次规范 JSON，随后自动保存落盘。
- `src/lib/api.ts` 接口、800ms 防抖自动保存、状态栏机制不变（content 仍是字符串）；App 以 `key=doc.id` 重建编辑器实例，规避文档切换时的实例串扰。

## Alternatives considered

- **content 存 Markdown**：人可读、可 diff，但块类型/嵌套/选区/任务状态需自造扩展；与未来 CRDT 同步和协作游标（按结构节点寻址）匹配度低于 TipTap JSON。
- **不用 TipTap、自研块引擎**：选区与中文 IME 细节极易出错，工作量远超预期，违背已定技术决策。
- **裸用 ProseMirror**：需自建 React 绑定与扩展维护体系，TipTap 已封装并被广泛验证。
- **维持纯文本**：无法承载块结构，后续双链/同步无从谈起。

## Consequences

- 收益：标题/列表（含嵌套）/任务/引用/代码块开箱即用；结构化 JSON 对齐 M6 同步路线；双后端与自动保存零改动。
- 代价与上限：bundle 增大约 400KB（TipTap/ProseMirror，桌面应用可接受）；编辑器受控性由 `useEditor` 管理，切换文档必须整体重建（key 模式）；存量纯文本依赖一次性自动迁移，已写入欢迎文档等场景。
