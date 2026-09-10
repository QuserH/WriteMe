# Agent Note: 日常启动用 release 形态，debug 版依赖开发服务器

Status: implemented

## Problem

Tauri 配置 `build.devUrl` 后，debug 构建会把界面指向 `http://localhost:1420`。单独双击 `target/debug/writeme.exe`（无开发服务器运行）会直接报 ERR_CONNECTION_REFUSED / 「无法访问此页面」，用户把日常使用与开发运行混为一谈会反复踩坑。

补充踩坑：**普通 `cargo build --release` 产出的 exe 仍会加载 `http://localhost:1420`**（tauri-build 的"生产模式内嵌前端资源"需要 Tauri CLI 的环境标记），单独运行同样报拒绝连接。经 WebView2 远程调试实测：独立版加载 `http://tauri.localhost/`，devUrl 版加载 `http://localhost:1420/`。

## Decision

- 明确两种启动形态并写进根文档：日常使用运行根目录 `WriteME.exe`（release 独立版，内置前端资源、无服务器依赖）或 `npm run app`；开发调试使用 `npm run tauri dev`（带热重载，不应频繁 Ctrl+C 重开）。
- `npm run release` = 强制重编（`cargo clean -p writeme`）→ **Tauri CLI `tauri build --no-bundle`（唯一能产出独立版的方式）** → 复制为根目录 `WriteME.exe`。脚本开头把 `%USERPROFILE%\.cargo\bin` 加进 PATH 供 tauri 调用 cargo（shell PATH 未含 cargo）。
- 验证方法：设置 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` 启动 exe，查 `http://127.0.0.1:9222/json` 的页面 URL——`http://tauri.localhost/` 才是独立版。
- 代码入口（`vite.config.ts`）与根文档均标注此区别，禁止再次引导用户双击 debug exe。

## Alternatives considered

- **删除 devUrl、让 debug 也内嵌 dist**：会破坏 `tauri dev` 的前端热重载工作流。
- **窗口先隐藏、内容就绪再显示**：只改善观感，不能阻止用户错跑 debug exe，问题本质是产物形态混淆。
- **不做处理**：坑会持续复现；文档化 + npm 脚本固化是投入产出比最高的方案。

## Consequences

- 收益：日常打开为独立 release 形态（内置前端资源，加载 `tauri.localhost`、无 1420 依赖）；误跑 devUrl 版 exe 的报错可在根文档中定位到原因。
- 代价与上限：每次代码变更需重跑 `npm run release`（含强制重编，约 1–2 分钟）才会刷新日常版；根目录 `WriteME.exe`（gitignore）与 target 目录两处产物并存，靠文档与脚本保持语义清晰；`release` 脚本依赖 `%USERPROFILE%\.cargo` 在 PATH 中的注入。
