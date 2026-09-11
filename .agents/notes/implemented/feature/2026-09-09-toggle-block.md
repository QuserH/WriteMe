# Agent Note: 折叠块——稳定内容容器、独立层级与无损编辑

Status: implemented

## Problem

折叠块需要像 Craft 一样可编辑、独立收起并容纳嵌套内容。旧 React NodeView 在内容区添加额外包裹，CSS 却把标题和子块当成直接子元素；同时通过 domAtPos 的结果逐项写 display，容易隐藏错误的容器、干扰编辑器管理的 DOM。旧迁移逻辑忽略空 title 属性，把第一段正文误当标题。原生版的新节点曾缺少 collapsed 属性，空标题 Enter 又会退出折叠，违背用户指定的“初始右箭头，回车进入内部后父级下箭头并出现层级线”。用户进一步指出原生三角和标记列明显偏小；渲染检查还发现多级长标题的续行退到编辑区最左侧，穿过祖先线，仅检查原文保留和不溢出无法发现此问题。

## Decision

### Web 基线

- 以 [Craft 折叠块文档](https://support.craft.do/zh-Hans/write-and-edit/blocks-and-pages/toggles) 为交互依据：标题常驻、三角箭头旋转、每级独立折叠、加号空格创建、取消折叠保留内容。保留项目已采用的大纲输入约定：标题 Enter 创建子折叠项；Craft 文档只规定 Enter 进入内部内容，并未规定内部块必须是折叠项。
- `Toggle.tsx` 直接使用 ProseMirror NodeView：一个不可编辑的真实 button 与一个固定 contentDOM。标题和子内容均由 ProseMirror 管理，不另外创建 contentEditable 标题，不通过 domAtPos 或 React effect 修改子节点 display。此处指直接使用 PM 的 DOM 接口，仍运行于 WebView，不是 Windows 原生编辑控件。
- schema 为 `toggleBlock(paragraph block*)`。首个 paragraph 是标题，后续块是子树；只有标题也是合法节点，不强制制造空正文。已有文档中的空段落保留。
- `collapsed` 是持久化属性。CSS 只匹配本节点的直接内容容器与第 2 个及以后的直接子块；收起父级不改变后代的 collapsed。如果选区位于将隐藏的子内容，点击收起的同一事务把光标放回标题末尾；键盘选区也不能停留在隐藏正文中。
- 键盘事务集中于 `toggleCommands.ts`：标题中途 Enter 按光标分割富文本，新子项紧接标题，已有子树保留；空标题叶节点 Enter 退出一级，顶层空标题回到普通段落；Ctrl+Enter 新建同级；Tab / Shift+Tab 移入前一个折叠块 / 移出父级。标题段首 Backspace 取消包装，保留文字和子树。composition 期间不处理结构快捷键。
- Windows 快捷键对齐 Craft 文档：`+ 空格`、`Ctrl+Shift+7` 创建，`Ctrl+Alt+T` 展开或收起全部。加号规则优先于默认无序列表规则，减号与星号规则保留。
- 普通段落/标题转换时直接复用 inline content，保留粗体、链接和软换行。块菜单提供“取消折叠，保留内容”；转换回正文或标题先解除包装。列表等容器从块菜单转换时保留整个容器为子内容。
- `doc.ts#normalizeToggle` 根据旧 `attrs.title` 属性是否存在判断迁移，空标题也补独立标题段。缺失 content 或非段落首块补标题段；清理旧 title 属性，重复解析保持幂等。
- 视觉使用 24px 缩进、紧凑三角按钮、细层级线与正文阅读列。箭头与手柄分开，长标题可换行；隐藏内容不占布局空间。按钮提供 aria-expanded、aria-controls 与焦点样式。

### C# 原生版

- `BlockCommands.Enter/ConvertBlock` 与侧栏 `InsertBlock` 的新建折叠项显式保存 `collapsed: true`；Markdown 加号空格、斜杠菜单、快捷键、块菜单、格式面板与插入面板共用此规则。`NoteNode.Toggle` 的无子项工厂也默认收起，带子项的演示/夹具仍默认展开。解析旧 JSON 不统一改写 collapsed，已有展开状态和子树保持原样。
- 标题中按 Enter 分割富文本并创建紧接标题的子折叠项，光标进入新子项；父级同一事务设为展开，子项默认收起。空标题使用相同行为，不再退出或退化成正文。Ctrl+Enter 创建同级收起项；退出层级使用 Shift+Tab，取消包装使用段首 Backspace 或块样式命令，已有文字和子树仍保留。
- `BlockRow.IsExpanded` 同时检查展开属性与实际子内容；只有标题的节点始终显示右三角。拖走最后一个子项后，原父级回到右三角且不显示自身层级线。点击空节点箭头会创建一个可编辑子项并定位进去，可一次撤销；展开/收起全部跳过空叶，不额外制造内容。
- 指针拖动的层级以实际按下手柄的位置为起点，每 28px 对应一层；三级向左一档成为二级同级，整个子树与收起状态保留。插到展开块后方时，提示线位于最后一个可见后代之后。拖动预览使用 [幽灵块](2026-09-09-settings-ghost-opacity.md) 的只读原生排版，展开/收起分别反映可见子树/标题尺寸；预览和提交共用 `CanMove` 合法性判断，单次撤销恢复原层级。
- `DocumentProjection.GuideDepths` 记录实际展开的折叠祖先深度；`BlockBackgroundRenderer` 单处绘制从父级箭头下方到子内容末端的细线。普通列表/引用缩进不伪装成折叠祖先，长标题换行时延续层级线，收起和移出后随投影清理。删除前缀 Canvas 中重复的层级线绘制。
- `ToggleDisclosureButton` 使用原生绘制的 10×10px 矢量三角，按实际 Craft SVG 的 `viewBox="-1.5 -1.5 13 13"`、三角顶点和 1.5px 圆角描边缩放；收起时从下向三角旋转为右向。点击区域为 24×24px；普通状态使用 `#1F2225` 的 0.33 透明度，光标所在块或悬停块为 1。独立 Button 子类显式复用 Button 主题模板，避免有逻辑但没有可见内容。
- `BlockLayout` 统一 28px 标记列、28px 每级缩进及祖先线位置；顶层编辑区文字起点为 48px 基础前缀加层级缩进，折叠标题、待办和有序/无序列表另加标记列。独立编辑表面默认 15px，实际文档字号/行距由页面外观控制；折叠父子项起点间距至少 28px，普通顶层段落至少 36px。单元格/分栏通过 `PrefixInset/TextStart` 同步调整前缀、续行、祖先线、命中及拖放坐标，见 [复合区域](2026-09-11-native-tables-columns-and-editing.md)。待办复选框为 24×24px，列表标记以祖先线横坐标居中，避免与手柄重叠而无法打开删除菜单。
- `BlockTextFormatter` 只装配到本编辑器的 TextView。首行继续使用零文档长度的前缀控件，续行减去同一前缀宽度交给原生 TextFormatter 排版，再由 TextLine 包装同步平移绘制、光标命中、选区矩形与行宽；保留原生文字索引和断行缓存，不添加实际空格或换行、不修改模型，也不额外重排一遍。正文、标题、折叠内容和软换行复用此机制。
- 兼容层对应固定版本 AvaloniaEdit 11.4.1 / Avalonia 11.3.21：前者没有初始化 `firstLineInParagraph`，后者不实现 `TextParagraphProperties.Indent`，所以 `InheritWordWrapIndentation` 不能解决此问题。用三个集中声明的 `UnsafeAccessor` 访问当前 TextView 的 formatter 字段及选区边界构造函数，不替换全局 TextFormatter、不关闭依赖检查。升级任一排版包时必须重新核对这些签名与续行回归；上游提供有效公开缩进接口时删除此适配层。
- 原生版仍保留标题 Enter 创建子折叠项的层级输入约定；Craft 官方文档仅规定 Enter 进入内部，没有规定内部块必须是折叠项。普通折叠标题不是文档章节，不进入左侧目录。当前数据契约与历史边界见 [原生块编辑器](../architecture/2026-09-09-native-block-editor.md)，导航分工见 [左右工具与目录](2026-09-09-editor-sidebar.md)。

## Verification

`BlockDragInteractionTests` 另覆盖三级折叠向左一档成为二级同级（展开/收起两种状态）、完整子树及撤销、提示线位于可见子树末尾、展开预览的实际行高和长标题断行，以及单元格/分栏内取消和关闭清理。`SlashMenuInteractionTests` 通过 `/ → 列表 → 折叠列表` 的鼠标与键盘路径验证转换。

`npm run test:editor` 覆盖独立折叠、反复点击、刷新与文档切换、中文输入、composition 确认、斜杠创建子级、Tab 层级移动、富文本分割、取消折叠及撤销、HTML 往返、旧版空标题迁移，以及 860px 窗口中的五级长标题布局。与 [M2 块拖拽笔记](2026-09-08-m2-block-drag.md) 共享测试集。

原生 `npm run native:test` 共 208 项通过。`DocumentToolsTests` 与 `CraftInteractionTests` 覆盖各新建入口的初始右箭头、空标题和五级 Enter、父级下箭头与祖先线、同级/移出、旧状态解析、单步撤销、侧栏插入和父子状态保存。`DeepLongTitlesWrapWithIndentationAndKeepEveryCharacter` 在 780px / 360px 编辑窗口检查五级长标题续行真实起点、选区子矩形、鼠标定位、中文插入及撤销；窄宽度覆盖缩进超过视口一半的情况。`SoftBreaksAndWrappedRichTextKeepTheBlockIndentWithoutChangingSavedText` 覆盖正文/标题/折叠子正文、连续软换行、emoji、粗体高亮、预编辑光标与原 JSON 不变，并确认默认 TextFormatter 没被全局替换。`TaskEditingTests` 检查手柄与复选框点击区分离、待办退出时保留折叠子树，以及菜单删除与撤销。headless PNG 检查新建/Enter 后、五级长标题、左右导航和窄窗口的实际原生布局。

## Alternatives considered

- **复用文字绝对缩进阈值计算拖出层数**：落点栏直观且不用记录起点；手柄与文字起点的偏移会使三级向左一档被判为直接回到顶层。以真实抓取点的横向位移决定层级，标题中心与目标深度一起决定移入，避免只有像素轮廓正确而结构结果不同。

- **复用 React NodeView，只修 CSS**：改动小且与 React 宿主一致；但这里仅有一个按钮，额外内容包裹与 effect 生命周期没有收益。固定 contentDOM 使归属和样式边界直接，免去展示 DOM 与编辑器解析之间的竞争。
- **独立 toggleTitle / toggleContent 节点**：结构约束明确，后续标题支持更多块类型时有价值；当前 paragraph 足够，新增持久化节点的迁移与粘贴维护成本没有必要。
- **独立 contentEditable 标题**：便于排版，但中文输入、选区和撤销与正文割裂，无法满足标题像普通文本一样编辑的要求。
- **维持现状不修**：简单折叠尚可展示，但空标题迁移、错误隐藏与层级操作失败直接影响已有笔记，不能作为稳定交付。
- **只把原生箭头字符改为右向**：改动极小，但模型仍处于展开状态，空标题 Enter 仍会退出，也无法正确保存新行为。新建默认值、结构事务和绘制状态需要一起修正，旧文档状态则保持不变。
- **复用控件自带的换行继承缩进选项**：代码最少，升级成本最低；固定版本没有实际进入缩进计算，而且底层排版不支持该属性，实测续行起点为 0。保留原生断行与字形缓存，用局部适配统一所有文字坐标。
- **维护整个 AvaloniaEdit 源码分支**：可公开扩展排版接口，避免依赖内部签名，适合需要持续修改编辑器底层的产品；本次只缺少段落续行缩进，长期维护完整分支的成本高于三个已锁版本并有回归覆盖的访问点。若后续底层改动增加或升级频繁破坏适配，应重新评估源码分支。

## Consequences

- 收益：折叠只改变可见性及必要选区，不改变子树内容；每级状态独立持久化，取消折叠和移动可一次撤销，旧正文不会被吞作标题。
- 代价与上限：Web 的 PM NodeView 需管理按钮事件与销毁；C# 版需要维护祖先线、矢量标记和与排版包版本绑定的续行适配。选区边界需建立带偏移的矩形副本。折叠标题暂为 paragraph，不支持独立标题等级；连续 Enter 向内创建层级，同级通过 Ctrl+Enter 或 Shift+Tab 创建/调整。
- 数据契约沿用 [M1 编辑器笔记](2026-09-08-m1-tiptap-block-editor.md)，自动保存与桌面/浏览器双后端接口不变。
