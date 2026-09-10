# Agent Note: M0 骨架——本地持久化与自动保存

Status: implemented

## Problem

需要先交付一个可运行的最小闭环来验证「Windows 壳 + 中文界面 + 数据不丢」，并作为块编辑器（M1）的地基。此阶段无块级编辑能力，正文内容以纯文本暂存。

## Decision

- 数据表 `documents(id, title, content, created_at, updated_at)`，启用 WAL 与 `updated_at` 索引；数据库位于 `%APPDATA%\com.writeme.desktop\writeme.db`。
- 自动保存采用 800ms 防抖；切换文档前强制落盘、`beforeunload` 兜底；状态栏显示「正在保存 / 已保存 / 保存失败」。
- 空库首次启动自动创建欢迎文档；初始化使用 ref 防止 React StrictMode 重复执行 effect 后创建两篇欢迎文档。新建/切换由侧栏完成，删除位于状态栏并带确认。
- 当前界面保留个人空间、文档列表、正文与保存状态，去除不可操作的后续导航占位；侧栏可收起，顶部路径显示当前文档。图标与文字颜色复用公共组件和 CSS 变量。正文约 720px 居中，折叠及拖拽规则见 [M2 笔记](2026-09-08-m2-block-drag.md)。
- 浏览器演示后端与桌面后端共享同一数据访问接口（`src/lib/api.ts`，见架构笔记中的 api 抽象），界面层不感知存储后端差异。

## Alternatives considered

- **仅 localStorage 存储**：改动最小，但无法支撑未来的全文索引、跨端同步与更大文档体量。
- **M0 就引入 CRDT（Yjs/Automerge）**：同步（M6）尚未开始，过早引入复杂运行时与序列化格式会拖慢编辑器地基。
- **不落本地库、内容只存内存**：违背离线优先的产品前提。

## Consequences

- 收益：从界面到 SQLite 的完整链路已闭环；正文格式在 M0 为纯文本、M1 起为 TipTap 块 JSON，单字段迁移由 [M1 笔记](../../implemented/feature/2026-09-08-m1-tiptap-block-editor.md) 完成。
- 代价与上限：演示模式与桌面端数据分叉，改数据层需双后端同步验证；WAL 单写者语义决定写并发上限，当前单机规模无影响。
