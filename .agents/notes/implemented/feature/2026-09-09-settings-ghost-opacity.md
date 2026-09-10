# Agent Note: 设置项——可调的原生拖动幽灵块不透明度

Status: implemented

## Problem

用户觉得反复调整默认幽灵块透明度的差别不明显，要求通过设置自行调节，避免每次编译和重启。原生版还需要让设置确实作用于跟随指针的预览，而不只是修改一个未使用的数值。

## Decision

- `MainWindow.Appearance.cs` 的设置窗口提供 40–100%、步进 5% 的“幽灵块不透明度”滑块，默认 85%。滑动即时更新旁边的预览及 `BlockEditor.GhostOpacity`。
- `BlockEditor` 使用原生 Border 绘制实际跟随指针的拖动预览，开始拖动时显示，取消或释放后隐藏；不透明度不作用于正文、落点线或其他界面。
- 数值由 `AppPreferences` 写入当前资料库的本机 `library_state`，启动时恢复。偏好不进入正文历史，不同步到其他设备，也不包含在资料库 ZIP 中。主题设置共用这套本机偏好机制，见 [页面与资料可携带性](2026-09-10-page-assets-and-portability.md)。

## Verification

- `KnowledgeLibraryTests.cs` 验证偏好重开恢复；`WorkspaceInteractionTests.cs` 实际拖动原生块，检查幽灵块显隐与滑块数值生效，保存 `drag-ghost-opacity.png` 供渲染检查。
- `npm run native:test` 全套 153 项通过。

## Alternatives considered

- **不做设置，继续调整默认值出包**：没有设置界面和持久化成本；但无法适应主观偏好，也重复用户已经指出的重启成本。
- **只复用 CSS 变量或配置文件**：Web 基线容易接入且便于开发者调整；当前主路线是原生编辑，用户也明确需要可操作的滑块，因此设置连接原生预览和本机偏好。

## Consequences

- 收益：用户可以立即比较和保存视觉偏好，数值与真实拖动预览一致。
- 代价与上限：需要维护原生预览、设置预览与持久化三处状态的连接；范围固定为 40–100%，不包含多块预览或跨文档拖动。旧 Web 基线继续沿用它的既有拖动样式。
