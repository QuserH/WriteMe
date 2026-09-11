# Agent Note: C# 原生块编辑器——Avalonia 绘制、统一文档事务与旧库副本

Status: implemented

## Problem

用户明确要求核心笔记编辑不依赖 WebView，并优先考虑运行占用；Windows 优先、未来可能扩展 Android、Craft 风格与离线本地数据仍是约束。用户进一步指出两个具体问题：每个块的拖动手柄常驻导致界面密集，子折叠块拖出父块后退化成普通文字。界面需要清晰的悬停反馈，结构移动必须保留节点类型、子树及独立折叠状态。

待办另有两处删除障碍：清空文字后，普通字符删除无法解除 `taskItem` 包装，Enter 还会继续生成空项；手柄与复选框命中区重叠，点击手柄可能变成勾选。键盘退出与块菜单删除都需要接通，且保留邻项、子树、撤销和保存。

用户还指出斜杠菜单缺少 Craft 的分类与二级导航，三级折叠拖出时无法准确停在二级，展开子树的拖动预览只有标题。原生默认光标又把块间留白算入高度，显得过长。上述交互在表格、分栏内也必须成立，不能只验证主编辑表面。

[既有 Tauri 技术栈](2026-09-08-stack-windows-tauri-react-sqlite.md) 的文本事务、选区、折叠与撤销由 WebView 内的 TipTap/ProseMirror 承担，Rust 只封装 SQLite CRUD。更换存储层语言无法满足新的核心编辑约束。用户认可 C# 的跨平台方向并授权落实选型，本记录覆盖可运行的原生基础，不代表所有 Web 功能与各平台迁移完成。

## Decision

### 技术选择与代码边界

- 使用 **C# / .NET 10 + Avalonia + AvaloniaEdit + SQLite**。Avalonia 负责中文桌面界面与本机绘制，AvaloniaEdit 提供文字输入、排版、选区、滚动及视口虚拟化；核心路径不装配 WebView2、Electron 或浏览器 DOM。这里的原生指无浏览器的本机绘制，界面并非全部 Windows 系统控件，C# 核心仍由 .NET 托管运行。
- `native/WriteMe.Core/` 实现文档树、富文本、编辑与结构事务、历史和 SQLite；`native/WriteMe.Desktop/` 装配 Craft 风格侧栏、标题、工具栏与原生编辑表面；`native/WriteMe.Tests/` 覆盖模型、存储及 Avalonia 指针/键盘路径。无需在当前五个 CRUD 操作之外增加 Rust/C# 互操作层。
- Avalonia 允许共享桌面及未来移动端的 C# 模型和部分界面。与 Qt 相比，本次选择使文档事务、存储与交互处于同一套托管类型系统，复用 AvaloniaEdit 的现成文字设施，避免同时引入 C++/QML 与另一套核心桥接。此选择基于实现边界和已运行的 Windows 原型，不宣称 C# 的运行占用必然更低，也不把框架支持平台等同于应用已通过跨平台验收。
- 固定 Avalonia `11.3.21`、AvaloniaEdit `11.4.1`、Microsoft.Data.Sqlite `10.0.9`；显式固定 SQLitePCLRaw.bundle_e_sqlite3 `2.1.13`，避免传递依赖 `2.1.11` 的 NuGet 高危漏洞告警。保留依赖锁文件和警告视为错误的构建约束，不关闭安全检查。

### 文档、投影与历史

- `NoteNode` 是不可变节点，运行期 GUID 稳定；`NoteJson` 延续 TipTap JSON 契约，保留 marks、未知属性与未知块。旧 `attrs.title` 转成标题段落时区分空标题与缺失标题，不吞掉第一个正文子块。旧纯文本保留空行及空白。
- `DocumentProjection` 把可见文字块投影到一个 AvaloniaEdit 文本表面。收起子树仅保留在文档模型，不进入文字投影；行缩进、折叠三角和手柄只为视口内行创建控件。`BlockRendering` 按 marks 和块类型绘制文字样式；`ToggleDisclosureButton` 与 `BlockLayout` 统一 Craft 参考的矢量三角、28px 标记列与层级间距。`BlockTextFormatter` 局部补齐续行缩进，使绘制、命中和选区坐标一致；固定包版本的兼容入口与升级约束见 [折叠块](../feature/2026-09-09-toggle-block.md)。
- `DocumentSession` 统一处理文字、格式、折叠、移动与复合块的撤销/重做，历史最多 200 项，同一区域内同一文字块的连续输入以 750ms 分组；每项保留事务前后的文档与选区，后续光标导航不能改写重做的位置。选区替换与 Enter 合并为一次事务。AvaloniaEdit 的默认历史不作为第二个事实来源。收起后若光标位于隐藏子树，定位到可见祖先标题。
- 表格和分栏采用原生区域编辑，`CreateScope` 把单元格/栏事务提交到根会话，父投影将其视为原子块并用 `LayoutHost` 定位内部文字。稳定控件、IME 归属、TSV、列宽和无损取消规则见 [表格与分栏](../feature/2026-09-11-native-tables-columns-and-editing.md)。完整选区可以删除复合块；未知占位提示的局部字符编辑继续受到保护。
- 新建折叠项默认收起，显示右箭头；折叠标题（包括空标题）Enter 创建子折叠项并展开父级，下箭头与左侧祖先线同步出现，新子项仍收起。Ctrl+Enter 创建同级，Tab/Shift+Tab 调整折叠层级。只有标题的叶节点始终显示右箭头；既有 JSON 状态不被统一重写，细节见 [折叠交互](../feature/2026-09-09-toggle-block.md)。斜杠菜单按中文、英文及拼音别名筛选，转换时移除完整 `/query` 并保留后文。块菜单支持保留内容的取消折叠。
- `NativeInputClient` 将中文预编辑与已提交文字分开。composition 时 Enter/Tab/Delete 等结构或默认文字编辑键标记为已处理，直到提交的 TextInput 到达；仅从自定义处理器 return 会继续触发 AvaloniaEdit 默认处理器，仍会误插入换行。
- 原生文字格式采用选区浮动工具栏，支持混合状态、九种预设文字色、自定义十六进制色与恢复默认、五色高亮、跨样式链接范围、草稿失效与键盘导航，见 [文字格式工具栏](../feature/2026-09-09-text-formatting-toolbar.md)。`TextColor.cs` 使用标准 `textStyle.color`，恢复默认保留同一 mark 的其他属性；`TextColorPicker.cs` 供浮层与右栏共用。文字颜色、链接和高亮使用显式设置，重复应用相同值不删除格式或增加空历史；常驻格式栏与独立链接窗口已移除。
- `DocumentOutlinePane` 放在 292px 左栏的当前文档模式，底部胶囊可切到空间笔记列表。目录只收录真正的 heading，包含收起祖先、表格与分栏内部标题，按标题等级组织；普通折叠标题不进入目录。点击或 Enter 激活条目才按需展开和跳转，获得焦点不改变折叠状态。`EditorSidebar` 位于纸张外，收起为胶囊，展开为 280px 的插入/格式/样式/信息横向标签与工具区；`SidebarGlyph` 原生绘制统一图标，`Themes/EditorSidebar.axaml` 管理按钮状态。窄窗口暂时让出左栏并保留模式偏好。插入与转换分别进入当前活动区域的事务；侧栏与选区浮层共享格式事实，统一历史和自动保存保持不变。左右分工、快捷键与虚拟化见 [编辑导航](../feature/2026-09-09-editor-sidebar.md)。

### 分类斜杠菜单与文字光标

- `SlashCommandMenu.cs` 使用同位置切换的分类菜单，首层包含列表、文本样式、装饰、颜色、缩进、对齐、文字格式、表格与分栏，以及可直接执行的分隔符。列表二级包含待办、折叠、无序、编号和取消列表格式；表格二级列出 2×2 至 9×9。这里只列有实现的命令，不为尚未实现的 Craft 页面/卡片放置占位项。
- 鼠标点击、Enter 或 → 进入分类，↑/↓、Home/End 移动选择，Enter/Tab 执行叶项；←、Escape、Shift+Tab 或返回行回到上级，根级 Escape 关闭并保留原查询。继续输入中文、英文或拼音在全目录搜索，空结果不执行命令。禁用项不可确认；切换类别不改正文、不抢编辑焦点。
- `BlockEditor.Commands.cs` 缓存会话、Revision、节点、查询前缀和选区；应用前再次核对文字表面、光标、有效启用状态及预编辑。重建菜单后，旧按钮不能通过索引调用新菜单。外部点击、文档切换或失效选区清理当前菜单，尺寸与滚动布局完成后按实际高度放在光标上方或下方。
- `SlashCommands.cs` 在确认叶项时移除完整 `/query`，保留后续富文本和子树。块转换、表格/分栏复用既有命令；颜色、对齐、文字格式及缩进先在分离的草稿会话中完成，再向真实根会话提交一次文档/选区事务。无效查询或缩进不吞文字、不新增历史；空块格式保留后续输入的 TypingMarks。
- 单元格/栏的菜单和幽灵块挂到根编辑区浮层，避免被区域高度裁剪。IME 优先，然后处理取消拖动和当前补全/斜杠菜单，最后才处理表格/分栏导航；因此 Escape 首先取消拖动或返回菜单，Tab 首先确认菜单，不会意外退出区域。卸载区域时立即隐藏、解除捕获与监听，延后归还浮层控件，避免在 Avalonia 正遍历祖先子控件时改动集合。关闭窗口、重建复合块和旧会话均受此约束。
- `BlockCaretGeometry.cs` 保留 AvaloniaEdit 原生光标的闪烁、焦点和定位，用现有 CaretLayer 的公开 Clip 裁掉块留白；按当前文字字体的 ascent/descent、实际基线与显示缩放计算范围，中文预编辑及 `NativeInputClient.CursorRectangle` 使用相同文字坐标。正文与标题分别跟随真实字号，调行距不会拉长光标，不重新排版正文、不增加闪烁定时器。通过已固定版本的 `AvaloniaEdit.Editing.CaretLayer` 类型名识别层，升级包时必须连同光标像素、换行和 IME 回归验证；没有增加私有字段访问。

### 手柄与子折叠块拖出

- `BlockRendering` 的 `blockGrip` 默认 `Opacity=0`、`IsHitTestVisible=false`；`BlockEditor` 用 `HoveredBlockId` 只显示鼠标所在块的手柄，退出编辑区域后隐藏。折叠三角保持可发现，不随手柄一起隐藏。
- 待办、有序/无序列表与折叠块共用 28px 标记列。待办复选框为独立的 24×24px 点击区，与手柄互不重叠，点击手柄可以稳定打开块菜单；菜单绑定当前编辑表面，关闭后解除绑定。回调核对会话、文档树引用、编辑启用状态与预编辑，旧菜单不能修改切换后的文档。
- 禁用 AvaloniaEdit 的 `EnableTextDragDrop`。默认选中文字的拖动走纯文本路径，会绕过结构事务；块手柄捕获指针后统一调用 `DocumentSession.Move`，传递拥有该行的块 ID，直接移动完整节点，保留 `toggleBlock` 类型、全部后代与 `collapsed`。
- 落点层级以按下手柄时的真实横坐标为起点，每移动 28px 调整一层；三级向左一档成为二级同级，不再把手柄与文字之间的偏移重复扣成两层。相对目标标题更深且落在中间区域才表示移入。`CanMove` 与 `Move` 复用同一合法性判断，拒绝循环、拆出标题及相邻空移动；松手按最终指针位置复算。编辑区域外释放、捕获丢失或模型变化取消旧落点。
- 插到展开折叠块后方时，蓝线与起点圆环绘制在最后一个可见后代之后，横向起点显示最终层级。`BlockDragPreview.cs` 复用只读原生编辑表面，按实际展开子树绘制宽度、换行和高度，收起时仅显示标题；只实例化有限视口，取消后释放。预览与透明度边界见 [幽灵块设置](../feature/2026-09-09-settings-ghost-opacity.md)。拖出、收起及恢复均使用统一历史。
- 拖到编辑视口上下边缘时，定时更新模板 ScrollViewer 的 Offset 并重新计算落点。AvaloniaEdit `11.4.1` 的 `TextEditor.ScrollToVerticalOffset` 是空实现，直接修改 TextView 偏移也会被 ScrollViewer 的旧值覆盖，因此使用模板滚动容器。headless 指针拖动与定时器测试确认实际发生滚动并保留块数量。

### 待办与列表退出

- 在待办或普通列表项段首按 Backspace，通过 `ConvertBlock(..., "paragraph")` 解除当前项包装并保留文字。清空当前项后，Backspace、Delete 或普通 Enter 都回到正文；`ExitEmptyList` 只处理空 `listItem/taskItem`，Shift+Enter 软换行与 Ctrl+Enter 同级操作保持原语义。
- 复用块转换的列表拆分：前后项、勾选状态、后续编号与当前项的折叠子树保留。块菜单的“删除此块”则调用 `DeleteBlock` 删除所选项及其子内容；退出和删除都进入同一套文档历史，可一次撤销结构操作。

### 本地数据与发布

- 原生库为 `%APPDATA%\com.writeme.native\writeme.db`。首次启动且该库不存在时，`NoteStore` 只读打开 `%APPDATA%\com.writeme.desktop\writeme.db`，使用 SQLite `BackupDatabase` 在线备份到临时库，执行 `quick_check` 并核对 `documents` 字段，再移动到原生库路径。导入包含 WAL 中已提交的数据，不直接复制活跃数据库文件，不回写旧库。
- `documents(id, title, content, created_at, updated_at)` 和 TipTap 正文 JSON 保持兼容。两套应用的后续修改各自保存，不做双向同步。`--data-dir <path>` 指定隔离库时不自动导入旧库，测试与实际鼠标验证都使用隔离库。
- 自动保存防抖为 650ms；不可变快照在后台线程序列化并写入 SQLite，保存串行化。切换文档与关闭窗口先等待保存，失败显示错误并保留窗口，不谎报成功。空间/文件夹、全文搜索、标签/双链、收藏、回收站及导入导出均接通原生库。派生索引、页面/附件与同步沿用此存储边界，分别见 [资料库](../feature/2026-09-10-library-and-search.md)、[页面与资产](../feature/2026-09-10-page-assets-and-portability.md) 和 [同步](2026-09-08-sync-docker-crdt.md)。
- `npm run native:release` 调用 `scripts/Publish-Native.ps1`，生成自带 .NET 运行时的 `artifacts/native/win-x64/WriteME.Native.exe` 及依赖文件。分发必须保留整个目录，不能只复制小型 apphost exe；运行不依赖 Node、WebView2 或额外安装 .NET。脚本保留 UTF-8 BOM，兼容 Windows PowerShell 5 的中文读取。
- `Directory.Build.props` 在显式指定 `RuntimeIdentifier` 时使用 `packages.<RID>.lock.json`，普通开发继续使用 `packages.lock.json`。Windows 发布会将 RID 传给 Desktop 和 Core，因此两者各保留入库的 `packages.win-x64.lock.json`；发布脚本开启 `RestoreLockedMode`。同一锁文件混用两种还原会使发布污染普通开发锁，干净克隆或发布后的普通锁定还原触发 NU1004。新运行目标需先显式生成并审核对应锁文件，不能关闭锁模式来绕过。
- `native:release` 支持 `-OutputDirectory`，可在旧程序仍运行时发布到独立目录；只在成功产出 Windows exe 后更新 `artifacts/native/latest.json`。`npm run native:app` 由 `scripts/Start-Native.ps1` 启动最近成功发布的程序，没有有效清单时回退到默认目录。发布不关闭或重启用户窗口；同一资料库由运行时互斥锁约束，用户关闭旧窗口后启动新版。
- 根目录 `WriteME.exe` 和 `npm run app` 仍为 Tauri 基线。现有 Web 源码、测试及数据访问层保留，浏览器组件不能直接编译到 Avalonia；原生 UI 使用独立控件实现相同的核心交互。

## Verification

- 本次新增的 28 项回归分布在 `SlashMenuInteractionTests`（9 项）、`SlashCommandTests`（6 项）、`BlockDragInteractionTests`（8 项）和 `CaretRenderingTests`（5 项）。覆盖分类/搜索/空结果/旧按钮、原子撤销与富文本后缀、三级退出一层、展开尺寸、长标题断行、表格/分栏浮层、禁用区域、Tab/Escape 优先级及拖动/菜单打开时关闭窗口。光标测试直接读原生 CaretLayer 渲染像素，确认细线宽度、正文/标题/代码/空行的文字高度，以及行距变化、软换行和中文预编辑定位。
- `artifacts/native/qa-craft-menus-drag/` 保存菜单根级/列表/表格、浅色/深色/窄窗口、展开/收起/区域拖动、整页及光标的原生渲染图，已检查定位、行高、圆角、落点线与文字清晰度；图像属于本机验证产物，不进入 Git。CaretLayer 的像素检查使用 headless 1× 缩放，不能替代真实系统输入法或全部 DPI 验收。

- `npm run native:build` 无警告/错误，`npm run native:test`：208 项通过。覆盖富文本/未知块 JSON 往返、旧空标题、五级折叠、跨块选区、emoji/软换行、统一撤销、子树移动与循环拒绝、SQLite WAL 在线导入和保存重开，并包含选区浮层与右侧工具栏的指针/键盘/草稿/保存回归、新建箭头与 Enter 层级、缩进按钮保留折叠子树、列表编号、左侧标题目录与虚拟化，以及长标题和连续软换行的续行缩进/选区/点击/输入回归。表格/分栏的模型、剪贴板、区域焦点、宽度、主题与集成检查集中在三个 `Layout*Tests.cs`。
- `TaskEditingTests.cs` 的 6 项回归覆盖已完成/未完成待办的段首退出、保留邻项与折叠子树、清空文字后三种键盘出口、实际指针点击手柄打开删除菜单和连续撤销。`TextColorTests.cs` 的 6 项回归覆盖文字颜色范围、保留其他 textStyle 属性、默认恢复、软换行与隐藏子树、实际文字 run 绘制、双入口自定义色/中文预编辑/旧文档草稿、窄窗口及颜色与待办退出后的 SQLite 保存。持久化重开用内容与节点类型定位，不把运行期 GUID 当作持久化 ID。
- Avalonia headless 测试执行原生键盘及指针路径：手柄初始隐藏、只悬停一个、移开隐藏；展开和收起的子折叠块分别拖到父级外，保持类型/子树/状态并可撤销；选中文字的拖动不能绕过块事务；composition 不保存候选拼音，Enter 不误建块；窗口立即切换/关闭也保存未落盘修改。
- 渲染测试检查实际 native text run 的粗体、高亮及标题字号；10,000 块测试检查只为视口创建装饰控件，以及隐藏的 10,000 个子块不进入文字投影。这是虚拟化与投影正确性验证，不是输入延迟或滚动帧率跑分。
- Windows 发布版实际鼠标验证使用 `artifacts/native-qa/`：拖出子折叠块后，三角和孙级内容仍存在，点击三角可正常隐藏孙级。真实中文输入法验证 `ni` 候选后空格提交“你”，候选阶段只读库正文仍未改变；候选确认 Return 不误建子项，确认后的普通 Return 才创建子折叠块。
- `npm run native:release` 生成自包含 Windows 发布版 `artifacts/native/craft-menus-drag-win-x64/WriteME.Native.exe`，无窗口 `--smoke-test` 在新建隔离目录返回成功，验证随包运行时、SQLite、中文检索和标签。既有 Web 基线的 `npm run build` 与完整路径 `cargo build --locked` 通过。Web 编辑器已有 45 项 Playwright 回归，本轮原生排版扩展未改变 Web 编辑行为。

## Measurement boundaries

- GitHub 语言比例按代码字节统计。既有 HTML 大部分来自工程决策看板模板，不参与应用构建；比例不等于编辑核心的语言边界或性能。思源的 Go 核心占比也不意味着其 Electron 桌面界面没有浏览器引擎。
- 2026-09-09 16:29（Asia/Shanghai），发布版 PID 15392 在上述两篇短 QA 文档完成输入、拖动和折叠验证后，`Get-Process` 记录 PrivateMemorySize64 约 **249.5 MiB**，WorkingSet64 约 **326.8 MiB**，当时无子进程。私有提交和工作集是不同指标，此快照不是冷启动、峰值或稳定内存上限。
- 既有 Tauri 某一时刻为一个主进程和六个 WebView2 子进程，私有提交合计约 285.6 MiB。负载、生命周期不同，不能算节省百分比或倍数；尚无相同数据集下的 Qt 对照，以及 1,000/10,000 块真实 Windows 输入、滚动、展开延迟基准。后续性能结论必须补同设备、同文档与明确采样时点的测量。

## Alternatives considered

- **保留并优化 Tauri/TipTap**：成熟 PM 事务、浏览器输入与已有 45 项回归能显著减少实现工作，也是当前功能较完整的参照。它继续依赖 WebView 核心编辑，不满足用户新约束，因此保留作基线，不作为新的主开发路线。
- **C++20 + Qt 6**：成熟文字排版、QTextDocument/QTextCursor 与桌面/移动平台覆盖是强项，资源生命周期也更直接。复杂块事务、可见投影和统一历史仍需单独实现；本次用户认可 C#，AvaloniaEdit 已支撑运行的 Windows 验证，尚无同负载证据证明承担 C++/QML 维护与分发成本能获得必要收益。若后续性能门槛无法达到，可重新做相同数据集的 Qt 原型比较。
- **C# + WinUI 3 / WPF**：Windows 集成、现成系统文字设施与无障碍基础是优势，若完全只支持 Windows 很有竞争力。当前仍保留 Android 方向，Avalonia 提供更直接的 UI 复用边界；这不免除各端 IME、触摸和无障碍验收。
- **Flutter**：不依赖 WebView，Windows/Android 共享界面与自绘制是强项；复杂桌面块编辑同样需要验证。当前用 C# 已串联 SQLite、原生文字设施与结构事务，没有证据表明再引入 Dart 能改善本次两项缺陷或降低占用。
- **Rust 原生 GUI 或复用 Rust 存储层**：原生编译、内存安全与可共享核心有价值；现有 Rust 只有少量 CRUD，桥接维护成本高于当前复用收益。未来有跨端重型核心时再评估，不用语言名称代替输入法与富文本能力验证。
- **待办只保留块菜单删除，继续复用普通字符退格**：入口最少、无需新增键盘规则；但复选框属于结构而非字符，空项仍无法按常见写作方式退出。键盘出口复用现有 `ConvertBlock`，菜单保留明确的整项删除，避免另造列表拆分和撤销路径。
- **开发和发布复用一份锁文件**：文件最少，普通自动还原也能重算依赖；但发布 RID 会传播到 Core，破坏后续无 RID 的锁定还原。把 Windows RID 固定到共享 Core 又会给 Linux 同步服务带入无关目标，因此按显式 RID 分开锁文件。代价是升级共享依赖时须同步审核开发与发布两种锁定结果。
- **继续复用平铺斜杠菜单**：所有命令一次可见、搜索实现最短；但用户明确要求分类再确认的流程，命令数量增加也使第一屏难扫读。保留全局关键词检索，浏览时使用同位置二级菜单，不增加并排弹窗的焦点与遮挡问题。
- **沿用文字的绝对横坐标判定拖动层级**：可以直接映射缩进栏；实际手柄位于文字左侧，三级按下后仅移动一档就会被判为退出两层。使用本次抓取点的相对位移，复用核心移动合法性判断。
- **重写光标或维护 AvaloniaEdit 分支**：能完全控制外形与闪烁，也可公开几何扩展点；本次只需排除块留白，重新维护焦点、闪烁和编辑生命周期没有必要。裁剪原有层保留其行为，但依赖固定版本的层名称，升级必须回归。

## Consequences

- 分类菜单增加目录、查询与焦点快照的维护，区域浮层还须处理卸载顺序；一次确认仍只提交一次文档事务。光标复用原生闪烁，不增加持续运行的定时器，但包升级时需要验证层名称与字体度量。

- 收益：核心编辑、层级和历史都可在不启动浏览器的 C# 模型中验证；可运行 Windows 原生版复用旧数据契约，并通过独立库支持回退。悬停手柄减少视觉干扰，结构拖动不再经过纯文本搬运。
- 代价：原生版维护自有富文本范围映射、可见投影、中文输入边界和历史，不再直接获得 PM 的全部编辑行为；每次事务仍重建可见投影，保存仍是整篇 JSON。原生绘制和无浏览器不能保证低占用，应以明确负载下的实测决定后续增量优化。
- 当前边界：普通 listItem/taskItem 的独立指针拖动尚未支持；剪贴板以纯文本为主，正文 UIA 目前是 custom，尚无完整屏幕阅读器 TextPattern。M3–M6 的标签、双链、收藏、资料库、页面、资产和同步已实现，具体约束留在各自笔记；任意高亮取色和正文嵌套页面/卡片仍未实现。
- 原生客户端当前仅实际构建和验证 Windows；同步服务另通过 Linux ARM64 Docker 验证。Android/macOS/Linux 客户端、长文档响应基准及完整 Craft 功能仍有工作，不能将此笔记的 implemented 解读为完整产品迁移完成。
