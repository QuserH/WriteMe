# AGENTS.md — WriteME 工程手册（AI 接手必读）

> 本文件是「接手 Agent 的入口」：项目是什么、代码在哪、怎么跑、有什么铁律与坑。
> 用户视角的说明见 `README.md`；架构决策留痕规范见 `.agents/notes/` 与
> `.agents/skills/write-notes-like-deepseek/SKILL.md`。

## 项目是什么

WriteME：类 Craft 的中文笔记应用。Windows 桌面优先（后续可能扩展安卓），
**离线优先本地存储**，M3–M6 的原生功能及自建同步服务已实现。界面语言为中文。
Web 基线源码里程碑：M0/M1 完成；M2 自研块拖拽、多级折叠、可搜索斜杠菜单与选区格式工具栏已实现。当前主开发路线转为 C# 原生版，原生编辑基础已构建、发布并在 Windows 验证。

**用户约束（2026-09-09）**：核心编辑不采用 WebView，原生实现与运行占用优先。用户已认可 C# 并授权完成后续里程碑；先读 [原生块编辑器](.agents/notes/implemented/architecture/2026-09-09-native-block-editor.md)。已落地 .NET 10 + Avalonia + AvaloniaEdit + SQLite，核心文档事务由 C# 承担。不能宣称完整 Craft 功能、所有 Web 行为、逐字符实时协作或跨平台迁移已完成。

## 技术栈与代码地图

### 当前原生版

| 层 | 技术 / 职责 | 位置 |
|---|---|---|
| 桌面界面 | C# / .NET 10 / Avalonia，本机绘制，无 WebView | `native/WriteMe.Desktop/` |
| 编辑表面 | AvaloniaEdit，文字排版/输入/选区/视口虚拟化 | `native/WriteMe.Desktop/Editing/` |
| 选区格式 | 原生浮动工具栏、文字色/高亮、链接草稿、键盘导航 | `native/WriteMe.Desktop/Editing/FormattingToolbar.cs` / `TextColorPicker.cs`，核心在 `native/WriteMe.Core/SelectionFormats.cs` / `TextColor.cs` |
| 左侧导航 | 空间/当前文档模式、标题目录、隐藏标题定位 | `native/WriteMe.Desktop/Editing/DocumentOutlinePane.cs`，布局与模式在 `MainWindow.axaml` / `.cs`，目录模型在 `native/WriteMe.Core/DocumentTools.cs` |
| 右侧工具 | 纸张外的插入/格式/样式/信息横向标签、收起胶囊、双侧栏响应布局 | `native/WriteMe.Desktop/Editing/EditorSidebar.cs` / `SidebarGlyph.cs`，扩展面板在 `MainWindow.Appearance.cs` |
| 表格与分栏 | 区域编辑、共享历史、行列/宽度/比例、TSV、对齐 | Core 的 `LayoutBlocks.cs` / `ScopedSessions.cs`，Desktop 的 `Editing/BlockEditor.Layouts.cs` / `NativeTableView*.cs` / `NativeColumnsView.cs` |
| 知识关联 | Unicode 标签、稳定 noteLink、反向链接、收藏 | `native/WriteMe.Core/NoteReferences.cs` / `KnowledgeStore.cs`，UI 在 `Editing/ReferenceCompletion.cs` / `MainWindow.Library.cs` |
| 资料库导航 | 空间、层级文件夹、最近、回收站、FTS5 中文搜索、卡片总览 | `native/WriteMe.Core/LibraryStore.cs`，UI 在 `MainWindow.Library.cs` / `MainWindow.Overview.cs` |
| 页面与文件 | 外观/封面、主题、资产、每日笔记、本地版本、Markdown 与 ZIP | `native/WriteMe.Core/WorkspaceStore.cs` / `NoteMarkdown.cs` / `LibraryBackup.cs`，UI 在 `MainWindow.Appearance.cs` / `MainWindow.Files.cs` / `PresentationWindow.cs` |
| 同步核心/客户端 | 版本向量、多值寄存器、HTTP 与附件传输、Windows DPAPI | `native/WriteMe.Core/SyncProtocol.cs` / `SyncStore.cs` / `SyncClient.cs`，UI 在 `MainWindow.Sync.cs` / `SyncCredentials.cs` |
| 自建服务 | ASP.NET Core、账号隔离 SQLite、会话哈希、Docker | `native/WriteMe.SyncServer/`、`sync/`，部署见 `sync/README.md` |
| 折叠排版 | 矢量三角、28px 标记列/缩进、长标题续行与选区坐标 | `native/WriteMe.Desktop/Editing/ToggleDisclosureButton.cs` / `BlockRendering.cs` / `BlockTextFormatter.cs` |
| 文档核心 | 不可变文档树、TipTap JSON、可见投影、富文本、统一历史、结构移动 | `native/WriteMe.Core/` |
| 本地存储 | Microsoft.Data.Sqlite，独立原生库及旧库在线备份导入 | `native/WriteMe.Core/NoteStore.cs` |
| 原生回归 | xUnit + Avalonia.Headless.XUnit，目前 180 项，含真实 HTTP | `native/WriteMe.Tests/` |
| 发布 | 自带 .NET 运行时的 Windows 目录；最近成功版本清单 | `scripts/Publish-Native.ps1` / `Start-Native.ps1` → `artifacts/native/` |

### 保留的 Web / Tauri 基线

| 层 | 技术 | 位置 |
|---|---|---|
| 桌面壳 | Tauri 2 (Rust) | `src-tauri/` |
| IPC 命令（5 个：list/create/get/save/delete） | Rust + tauri | `src-tauri/src/lib.rs` |
| 本地存储 | SQLite（rusqlite bundled, WAL） | `src-tauri/src/db.rs`，数据在 `%APPDATA%\com.writeme.desktop\writeme.db` |
| 前端 | React 19 + TS + Vite 6（严格模式） | `src/` |
| 数据访问层 | 桌面 invoke / 浏览器 localStorage 演示双后端 | `src/lib/api.ts`（`isDesktop` 判断） |
| 编辑器 | TipTap/ProseMirror 块级编辑器（正文存 TipTap JSON） | `src/editor/`，宿主在 `src/App.tsx` |
| 折叠与拖拽 | ProseMirror DOM NodeView + 自研指针拖拽 / PM 事务映射 | `Toggle.tsx` / `toggleCommands.ts` / `BlockHandle.tsx` / `blockTree.ts`（均在 `src/editor/`） |
| 编辑浮层 | 搜索命令、文字格式、链接与高亮 | `SlashMenu.tsx` / `FormattingToolbar.tsx` / `blockCommands.ts` / `floatingPosition.ts`（均在 `src/editor/`） |
| 样式 | Craft 风格 CSS 变量 | `src/styles.css` |

## 常用命令（npm）

```sh
npm run native:dev          # C# / Avalonia 开发运行
npm run native:build        # 原生 Release 编译
npm run native:test         # 模型/存储/原生指针与键盘回归
npm run native:release      # 自包含发布到 artifacts/native/win-x64/
npm run native:app          # 启动最近成功发布的 WriteME.Native.exe
npm run dev                 # 浏览器演示模式（数据在 localStorage）
npm run build               # tsc 类型检查 + vite 打包到 dist/
npm run test:editor         # 折叠/拖拽/搜索/文字格式/保存与迁移回归（首次先 npx playwright install chromium）
npm run tauri dev           # 旧 Tauri 开发模式（热重载，勿频繁重启）
npm run release             # 旧版前端打包 + Tauri release + 复制根目录 WriteME.exe
npm run app                 # 启动旧 Tauri release 版
npm run verify-notes        # 校验决策笔记树 + 格式（改动后必跑）
npm run init-board          # 生成决策看板 board.html
```

## 铁律（每次接手先读，改动必遵守）

1. **非平凡变更必须留痕**：遵循 `.agents/skills/write-notes-like-deepseek/SKILL.md`。
   决策笔记放 `.agents/notes/{lifecycle}/{class}/yyyy-mm-dd-topic.md`，与代码同批提交。
2. **动手前先搜反向注释** `// Note:` —— 代码入口可能绑定既有决策，先读对应笔记再改。
3. **先探测再写**：80% 是**原地更新**既有笔记的事实（路径/类名/默认值），不要盲目新开文件。
4. **格式由脚本把守**：行 1–4 为 `# Agent Note: <标题>` / 空行 / `Status: <状态>` / 空行；
   首节 `## Problem`；implemented 必有 `## Decision` + `## Consequences`；
   每篇必有 `## Alternatives considered`（含"不做/复用"与对手最强论据）。
   改完跑 `npm run verify-notes`，通过才算完成。
5. 发布打标、纯排版、无行为依赖补丁 → 标 not applicable，不写笔记。

## 环境与坑（真实踩过）

- GitHub 只提交源码、必要资产、测试、文档、构建配置和锁文件；本机 SDK、依赖目录、发布物、数据库、`.env` 和凭据一律排除。不要为了上传而删除用户本机环境或资料。技能演示图片不入库，校验脚本/模板/看板源文件保留；详见 [源码仓库边界](.agents/notes/implemented/process/2026-09-10-source-only-repository.md)。
- 原生开发需要 .NET 10 SDK，本机已安装。**`npm run native:app` 启动最近成功发布的原生版**，默认输出为 `artifacts/native/win-x64/WriteME.Native.exe`，其他目录由 `artifacts/native/latest.json` 记录。根目录 `WriteME.exe` 仍是 Tauri 基线。原生发布分发整个目录，不只复制 apphost exe；用户运行时不需要 Node、WebView2 或额外安装 .NET。
- 原生默认资料库为 `%APPDATA%\com.writeme.native\writeme.db`。首次运行使用 SQLite 在线备份从旧库导入副本，旧库不回写；禁止直接复制活跃 WAL 数据库作为迁移方案。`--data-dir` 使用独立测试库且不导入旧库，真实 UI 验证应使用隔离库。
- .NET restore/build/test/publish 不要并行操作共享项目的 `obj`；包锁文件入库。SQLitePCLRaw 固定 `2.1.13`，不能为消除旧依赖漏洞告警而关闭安全检查。
- 普通开发使用 `packages.lock.json`，显式 RID 发布使用 `packages.<RID>.lock.json`；Desktop 和 Core 的 `win-x64` 发布锁都入库，发布脚本开启锁模式。新增平台或升级包时同步审核对应锁文件，发布后普通锁定还原仍须可用。
- Markdown 使用 Markdig `1.3.2`；Windows 凭据使用 ProtectedData `10.0.12`。Docker 构建按锁文件 restore，不把 `.env`、本机资产和凭据放进镜像或提交。
- 两个原生 PowerShell 脚本保留 UTF-8 BOM，Windows PowerShell 5 读取无 BOM 中文会误解析。发布时若同目录程序在运行，使用 `npm run native:release -- -OutputDirectory artifacts/native/<版本目录>`，不要强行关闭用户程序。成功发布才更新最近版本清单；用户关闭旧窗口后自行启动新版。
- 用户已要求自行测试，不要控制其桌面窗口。UI 自动验证用 Avalonia headless 与隔离资料库；`WRITEME_QA_ARTIFACTS` 可指定格式浮层、折叠和右侧工具栏的 PNG 输出目录，供渲染检查。
- AvaloniaEdit 11.4.1 的 `ScrollToVerticalOffset` 是空实现；拖动边缘滚动要更新模板 ScrollViewer 的 Offset，不能只改变 TextView 后又被容器旧值覆盖。
- AvaloniaEdit 11.4.1 的继承换行缩进没有实际生效，Avalonia 11.3.21 也不实现段落 Indent。`BlockTextFormatter` 只在本编辑器的 TextView 装配续行适配，保留原生文字索引和断行缓存；绘制、命中、选区矩形必须一起平移。三个 `UnsafeAccessor` 绑定固定包的字段/构造签名，升级排版包必须重新验证长标题、软换行、点击与中文光标，公开接口补齐后删除适配。禁止用实际空格/换行污染正文来模拟缩进。
- Rust 装在 `%USERPROFILE%\.cargo`，**当前 shell PATH 不含 cargo**：命令行用完整路径
  `$env:USERPROFILE\.cargo\bin\cargo.exe`；`npm run release` 脚本开头会把该目录注入 PATH。
- **禁止引导用户双击 `target\debug\writeme.exe`**：debug 构建指向 devUrl
  `http://localhost:1420`，无开发服务器时报 ERR_CONNECTION_REFUSED。
  旧 Tauri 能独立运行的是 **release 独立版**（根目录 `WriteME.exe` / `npm run app`）。
  注意：**普通 `cargo build --release` 也会内嵌 devUrl**，必须用 `npm run release`
  （内部走 Tauri CLI `tauri build --no-bundle`）产出。可用 WebView2 远程调试验证
  （见对应笔记的 Decision）。
  详见 `.agents/notes/implemented/process/2026-09-08-release-vs-debug-launch.md`。
- 开发端口 1420（Vite）、1421（HMR）。`tauri dev` 是唯一需要它们的形态。
- 工具链已就位：.NET SDK 10.0.103 / Node 24 / Rust 1.98 / VS2022 MSVC / Windows SDK 10.0.26100 / WebView2（仅旧 Tauri 需要）。

## 数据模型现状与演进方向

- `documents(id, title, content, created_at, updated_at)`，WAL；两版 `content` 均使用 **TipTap 块 JSON 字符串**。原生 `NoteJson` 兼容旧纯文本、旧空折叠标题、marks 与未知节点；Web 由 `src/editor/doc.ts#parseContent` 迁移。两个资料库后续修改独立，不做双向同步。
- 新增块类型/序列化改动同步原生 `NoteJson`、`DocumentProjection`、相关回归与原生决策笔记；涉及 Web 时同步 `src/editor/doc.ts` 与 [M1 决策笔记](.agents/notes/implemented/feature/2026-09-08-m1-tiptap-block-editor.md)。保持旧 `src/lib/api.ts` 契约。
- 原生文字、格式、折叠、拖动都经过 `DocumentSession`，共用 200 项有界历史；不要另开 UI 撤销栈。原生 650ms 自动保存在后台串行完成，切换和关闭必须等待保存。相同标题与正规化正文的保存必须无操作；Avalonia 标题变更事件会延后触发，不能只用 `_loading` 阻止远端显示刷新置脏。
- 表格/分栏使用 TipTap 复合节点，单元格与栏通过 `CreateScope` 提交到根会话；历史记录事务前后选区，重做不能从后续导航位置推断光标。父表面通过 `LayoutHost` 定位内部节点，父键盘处理不能截获子区域事件。范围选择期间焦点留在父 TextArea 以保留 IME；提交文字先结束预编辑，再执行范围替换。
- 复合块缓存控件必须同步字体、行距、底色、文字色、分割线、主题和关联目录，不能只在创建时复制设置。`PrefixInset/TextStart` 统一紧凑区域的续行、层级线、命中和拖放。表格最多 100×12，合并单元格保留但不可编辑；列宽用标准 `colwidth: [px]`，拖动只在松手时提交，轻点/Esc/捕获丢失不落盘。完整规则见 [原生表格与分栏](.agents/notes/implemented/feature/2026-09-11-native-tables-columns-and-editing.md)。
- 链接/高亮显式应用调用 `Format(..., toggle: false)`，普通格式按钮使用开关语义。浮层草稿必须核对会话、修订号与选区；核心规则见 [格式工具栏](.agents/notes/implemented/feature/2026-09-09-text-formatting-toolbar.md)。
- 文字颜色通过 `SetTextColor` 写入标准 `textStyle.color`，接受 3/6 位十六进制，null 恢复默认且保留其他样式属性；重复同色不增加历史。浮层和右栏共用 `TextColorPicker`，右栏按会话、Revision 与选区缓存，不能每次焦点刷新都重建并丢掉草稿；Enter/Escape 必须保护 TextPresenter 预编辑。
- 待办/列表段首 Backspace 解除当前项包装并保留文字及子树；空项 Backspace/Delete/普通 Enter 回正文。复用 `ConvertBlock` 的拆分和编号规则，块菜单“删除此块”删除整项，均走统一历史。列表与待办预留 28px 标记列，复选框 24px 点击区不能和悬停手柄重叠；旧块菜单回调核对会话及文档树。
- 原生新建折叠项显式 `collapsed: true`；空标题 Enter 也进入子项，父级展开、子级收起；Shift+Tab 退出层级。旧 JSON 状态不统一改写。箭头由 `BlockRow.IsExpanded` 决定，祖先线由 `GuideDepths` 单处绘制；不要退回按普通 Depth 为所有缩进画线。
- 左侧目录只收录 `heading`，普通折叠标题不入目录；收起祖先内部真正的标题仍可定位，层次按标题等级组织。点击或 Enter 才跳转，打开/聚焦目录不自动展开折叠。底部切换空间/文档模式，`Ctrl+Alt+3` 打开左侧目录；右侧提供插入/Aa/页面/信息，`Ctrl+Alt+1/2` 打开插入/格式，全文展开/收起放文档菜单。中央卡片是资料库总览，不能混同于正文嵌套页面块。
- 右侧插入使用 `InsertBlock`，不替换文字选区；样式使用 `ConvertBlock`，相同样式不清除任务勾选。有序列表拆分保留后续编号；面板、导航与历史规则见 [编辑导航](.agents/notes/implemented/feature/2026-09-09-editor-sidebar.md)。
- 标签/双链索引和 FTS5 搜索从完整文档树派生，包含表格和分栏；两个索引版本均为 2。保存正文与重建本篇索引同事务，来源使用块路径/局部偏移，不持久化运行期节点 GUID。`noteLink.documentId` 稳定绑定目标，改名保留别名，回收站来源不计为有效反链。见 [M3 关联](.agents/notes/implemented/feature/2026-09-09-m3-note-connections.md)。
- 空间/文件夹/位置独立于标题，容器删除保留正文；最近按打开时间排序。中文 ≥3 字符走 FTS5 trigram，短词回退参数化子串；同一当前文档的搜索结果也必须定位。卡片每批 40 张，查询尚未分页；见 [资料库与搜索](.agents/notes/implemented/feature/2026-09-10-library-and-search.md)。
- 页面外观与正文 marks 分开，主题/幽灵块偏好只在本机。正文标记颜色跟随实际页面底色；修改 XAML DynamicResource 时同步 `Ui.MarkupColors.cs`。SHA-256 资产不可变，单文件 50 MiB；删除引用/撤销不清物理文件，封面也必须纳入备份/同步。见 [M5 页面与可携带性](.agents/notes/implemented/feature/2026-09-10-page-assets-and-portability.md)。
- ZIP 导入先验证清单/ID/循环/哈希，事务创建副本并重映射内部链接；不覆盖现有资料，不包含凭据、同步前沿和历史版本。Markdown 的相邻 `assets/` 导入需递归处理自定义块且校验哈希，不能读取越界、重解析点或远程 URL。
- 同步是按文档/空间/文件夹的多值寄存器 CRDT，禁止用时间戳覆盖并发。普通写入不能消除未决冲突；显式选择必须核对前沿。网络期间继续编辑，收到响应后先保存最新草稿再合并；仅元信息变化保留会话和历史，正文变化前保存修订。关闭先取消/等待同步、登录和退出，再保存/释放库。
- 版本向量设备 ID 属于当前资料库，导入旧库副本要生成新 actor；禁止复制已同步的活跃库作为另一设备。Windows DPAPI 令牌不进入 ZIP/日志，非 Windows 不降级明文。同步全部空间到当前账号，切换账号需明确该合并语义。服务器不提供端到端加密。
- M6 协议、界限与部署验证见 [同步架构](.agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md)，部署操作见 [同步指南](sync/README.md)。Compose 使用独立持久卷，默认回环绑定，端口由 `WRITEME_SYNC_PORT` 设置；不能重启其他用户服务来修复本项目环境。

## 文档地图

| 文件 | 给谁看 |
|---|---|
| `README.md` | 用户/开发者视角：运行、里程碑 |
| `AGENTS.md` | 接手 AI：本文件 |
| `.agents/skills/write-notes-like-deepseek/SKILL.md` | 决策笔记写作契约（先读它） |
| `.agents/notes/` | 决策笔记（`implemented/`=现行约束，`proposed/`=待评审，`rejected/`=禁区） |
| `board.html` | 可选决策看板（`npm run init-board` 生成，已 gitignore） |

## 待办备忘（用户点名的需求，防止遗忘）

- **幽灵块滑块已实现**：设置中 40–100%、5% 步进、即时预览、跨重启保存；实际指针幽灵块与预览使用同一数值。见 `.agents/notes/implemented/feature/2026-09-09-settings-ghost-opacity.md`。
- **折叠列表已实现**：原生版具备独立层级状态、子树拖入/移出、无损取消折叠及快捷键；手柄只在悬停时出现，移动保留折叠类型/子树/状态。禁止重开 AvaloniaEdit 默认文本拖放绕过结构事务。原生修改运行 `npm run native:test`；Web 细节见 `.agents/notes/implemented/feature/2026-09-09-toggle-block.md`，对应 `npm run test:editor`。
- **原生后续验证**：已通过 Windows 中文输入、跨块选区、统一撤销与旧库兼容回归；10,000 块测试只证明虚拟化与投影正确，不是完整性能基准。还需同负载的真实输入/滚动延迟测量、跨平台输入、普通列表项独立拖动、完整剪贴板与无障碍支持。禁止由语言比例、exe 大小或不同负载的内存快照推导性能提升。
- **M3–M6 已实现**：标签/双链/收藏、空间/文件夹/中文搜索/卡片、页面/封面/附件/主题/备份/每日/演示，以及自建同步。后续工作以真实使用反馈、跨平台输入、完整剪贴板/无障碍、普通列表独立拖动和同负载性能测量为依据；不把当前完成范围写成全部 Craft 功能。用户可见范围见 `README.md`，Docker 部署验证结果见同步笔记。



## 编码约定

- C#：nullable、警告视为错误、依赖版本锁定；中文文案与颜色使用原生 `App.axaml` / `Ui.cs` 的共用约定。文档树/JSON 逻辑放 Core，窗口事件与绘制留在 Desktop。
- TS：`strict` + `noUnusedLocals/Parameters`；界面文案一律中文；主题色用 `src/styles.css` 的 CSS 变量。
- 新增 IPC 命令时同步：Rust command + `generate_handler!` + `src/lib/api.ts` 双后端 + types.ts。
- 每次完成改动：`npm run build`（前端）通过 + `cargo build` 通过 + `npm run verify-notes` 通过。
- 涉及原生代码另跑 `npm run native:build` + `npm run native:test`；交付原生程序用 `npm run native:release`，并确认最新发布文件可运行。
