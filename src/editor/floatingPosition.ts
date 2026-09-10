import type { Editor } from "@tiptap/core";

interface Anchor { left: number; right: number; top: number; bottom: number }

/** 浮层受实际编辑区裁剪边界约束；光标/选区滚出视野时不在页眉上悬空显示。 */
export function floatingPosition(
  editor: Editor, anchor: Anchor, width: number, height: number,
  preference: "above" | "below", centered = false,
) {
  const scrollRect = editor.view.dom.closest(".editor")?.getBoundingClientRect();
  const left = Math.max(12, (scrollRect?.left ?? 0) + 8);
  const right = Math.min(window.innerWidth - 12, (scrollRect?.right ?? window.innerWidth) - 8);
  const top = Math.max(12, (scrollRect?.top ?? 0) + 8);
  const bottom = Math.min(window.innerHeight - 12, (scrollRect?.bottom ?? window.innerHeight) - 8);
  if (anchor.bottom < top || anchor.top > bottom) return null;

  const gap = 8;
  const above = Math.max(0, anchor.top - top - gap);
  const below = Math.max(0, bottom - anchor.bottom - gap);
  const openUp = preference === "above"
    ? above >= height || above > below
    : below < height && above > below;
  const maxWidth = Math.max(0, right - left);
  const desiredX = centered ? (anchor.left + anchor.right - Math.min(width, maxWidth)) / 2 : anchor.left;
  const horizontal = { left: Math.max(left, Math.min(desiredX, right - Math.min(width, maxWidth))), maxWidth };
  // 跨屏选区上下都可能没有空位；工具栏在可见编辑区内落位，避免高度被压成 0。
  if (preference === "above" && Math.max(above, below) < Math.min(height, bottom - top)) {
    return { ...horizontal, top, bottom: undefined, maxHeight: bottom - top };
  }
  return {
    ...horizontal,
    top: openUp ? undefined : Math.max(top, anchor.bottom + gap),
    bottom: openUp ? window.innerHeight - Math.min(bottom, anchor.top - gap) : undefined,
    maxHeight: openUp ? above : below,
  };
}
