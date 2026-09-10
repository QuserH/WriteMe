# Agent Note: WriteME 技术栈——Windows 桌面 + Tauri 2 + React + SQLite

Status: implemented

## Problem

产品定位为类 Craft 的中文笔记应用：先交付 Windows 桌面端、后续可能扩展安卓；界面中文；体验以「流畅」为优先；数据采用离线优先的本地存储，并由自建 Docker 服务器做同步。桌面富文本/块编辑器的中文输入体验是核心约束，技术选型必须同时服务「桌面体验、编辑器生态、未来安卓复用」三条线。

## Decision

- 桌面壳采用 Tauri 2（Rust），前端采用 React 19 + TypeScript + Vite 6，块编辑器采用 TipTap/ProseMirror（M1 落地）。
- 本地存储采用 SQLite（rusqlite bundled，WAL），Rust 侧 5 个 IPC 命令（list/create/get/save/delete）封装，前端不直连数据库。
- 前端 `src/lib/api.ts` 提供双后端抽象：桌面走 Tauri invoke，纯浏览器走 localStorage 演示后端，保证无壳可预览、未来可平滑换同步后端。
- 本地数据库是唯一事实来源；同步（M6）由自建 Docker 服务器 + CRDT 完成，见 [原生同步实现](2026-09-08-sync-docker-crdt.md)。
- release 构建产物复制为根目录 `WriteME.exe`（`npm run release` 生成），是日常使用的独立运行形态，见 [启动形态约定](../process/2026-09-08-release-vs-debug-launch.md)。
- 以上描述保留的 Web/Tauri 基线。用户在 2026-09-09 明确补充“核心编辑不依赖 WebView、原生与低占用优先”后，新的主开发路线采用 C# / Avalonia，可运行原型、数据副本与测量边界见 [原生块编辑器](2026-09-09-native-block-editor.md)。原生发布入口为 `artifacts/native/win-x64/WriteME.Native.exe`，本笔记中的根目录 exe 仍指 Tauri 版，不代表全部功能已迁移。

## Alternatives considered

- **Flutter（Windows 与安卓共享界面）**：自身渲染、跨端复用是优势；Craft 级块交互、跨块选区与中文输入仍需具体原型验证，第三方编辑器许可也需逐项核实，不能一概以 AGPL 排除。
- **Electron + React**：成熟的桌面 Web 生态，自带 Chromium/Node 使分发体积通常更大；同负载内存和响应差距需要实测，不能由框架名字推导“最流畅”。它同样不满足用户新明确的无 WebView 编辑约束。
- **原生 WinUI 3**：Windows 集成和文字控件基础有价值，复杂块交互需构建；Android 界面无法直接复用，但数据模型或原生库可以共享。不以“生态为零”或未经测量的“最快”作为排除理由。
- **不做桌面、纯 Web**：仍可使用 IndexedDB/OPFS 离线存储，但浏览器产品形态与当前 Windows 桌面优先、SQLite 本地集成的实现范围不同。

## Consequences

- 收益：复用 TipTap/PM 的结构化编辑、事务、历史与浏览器输入设施；Web 前端可作为未来其他端的复用候选。应用自身不分发完整 Electron 运行时，单个 exe 的体积较小。
- 代价与上限：编辑核心和渲染仍依赖系统 WebView2，浏览器子进程有运行成本；小包体不证明低内存或低输入延迟。当前没有与原生方案的同负载性能基准，不能声称该栈“占用最低”或“最流畅”。开发机需 Rust + MSVC + Windows SDK；日常仍必须通过 Tauri CLI 产出独立 release。
