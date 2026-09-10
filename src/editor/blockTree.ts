import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { closeHistory } from "@tiptap/pm/history";
import { NodeSelection, TextSelection, type EditorState, type Transaction } from "@tiptap/pm/state";

// Note: 折叠树中的拖入/移出使用节点位置与事务映射 — 见 .agents/notes/implemented/feature/2026-09-08-m2-block-drag.md

export interface BlockAddress {
  node: ProseMirrorNode;
  pos: number;
  /** 文档根为 -1；其余为父折叠块之前的位置。 */
  parentPos: number;
  index: number;
}

export interface BlockDestination {
  parentPos: number;
  index: number;
}

export function blockAt(doc: ProseMirrorNode, pos: number): BlockAddress | null {
  if (pos < 0 || pos >= doc.content.size) return null;
  const $pos = doc.resolve(pos);
  const node = $pos.nodeAfter;
  if (!node || !node.isBlock || $pos.textOffset) return null;
  const parent = $pos.parent;
  const index = $pos.index();
  if (parent.type.name !== "doc" && parent.type.name !== "toggleBlock") return null;
  // 标题属于折叠块，不允许把标题单独拖走。
  if (parent.type.name === "toggleBlock" && index === 0) return null;
  return { node, pos, parentPos: $pos.depth ? $pos.before() : -1, index };
}

export function selectedBlock(state: EditorState): BlockAddress | null {
  if (state.selection instanceof NodeSelection) return blockAt(state.doc, state.selection.from);
  const { $from, $to } = state.selection;
  for (let depth = $from.depth; depth > 0; depth--) {
    if ($to.pos > $from.after(depth)) continue;
    const block = blockAt(state.doc, $from.before(depth));
    if (block) return block;
  }
  return null;
}

export function childPosition(doc: ProseMirrorNode, destination: BlockDestination): number | null {
  const { parentPos, index } = destination;
  const parent = parentPos === -1 ? doc : doc.nodeAt(parentPos);
  if (!parent || (parent.type.name !== "doc" && parent.type.name !== "toggleBlock")) return null;
  const minimum = parent.type.name === "toggleBlock" ? 1 : 0;
  if (index < minimum || index > parent.childCount) return null;
  let pos = parentPos + 1;
  for (let i = 0; i < index; i++) pos += parent.child(i).nodeSize;
  return pos;
}

/** 一次事务移动完整子树；无整篇重写、异步删空行或临时非法树。 */
export function moveBlockTransaction(
  state: EditorState,
  sourcePos: number,
  destination: BlockDestination,
  select: "block" | "preserve" = "block",
): Transaction | null {
  const source = blockAt(state.doc, sourcePos);
  const insertAt = childPosition(state.doc, destination);
  if (!source || insertAt === null) return null;
  const end = sourcePos + source.node.nodeSize;
  if (destination.parentPos >= sourcePos && destination.parentPos < end) return null;
  if (insertAt >= sourcePos && insertAt <= end) return null;

  const tr = state.tr.delete(sourcePos, end);
  const mapped = tr.mapping.map(insertAt, -1);
  const $insert = tr.doc.resolve(mapped);
  if (!$insert.parent.canReplaceWith($insert.index(), $insert.index(), source.node.type)) return null;
  tr.insert(mapped, source.node);
  // 移入后展开容器及其祖先，保留被移动子树自身的折叠状态。
  for (let depth = 1; depth <= $insert.depth; depth++) {
    if ($insert.node(depth).type.name === "toggleBlock") tr.setNodeAttribute($insert.before(depth), "collapsed", false);
  }
  const { selection } = state;
  if (select === "preserve" && selection instanceof TextSelection &&
      selection.from > sourcePos && selection.to < end) {
    tr.setSelection(TextSelection.create(tr.doc,
      mapped + selection.anchor - sourcePos, mapped + selection.head - sourcePos));
  } else {
    tr.setSelection(NodeSelection.create(tr.doc, mapped));
  }
  return closeHistory(tr).scrollIntoView();
}

export function indentDestination(state: EditorState, block: BlockAddress): BlockDestination | null {
  const parent = block.parentPos === -1 ? state.doc : state.doc.nodeAt(block.parentPos);
  if (!parent || block.index < 1) return null;
  const previous = parent.child(block.index - 1);
  if (previous.type.name !== "toggleBlock") return null;
  return { parentPos: block.pos - previous.nodeSize, index: previous.childCount };
}

export function outdentDestination(state: EditorState, block: BlockAddress): BlockDestination | null {
  if (block.parentPos === -1) return null;
  const parent = blockAt(state.doc, block.parentPos);
  return parent ? { parentPos: parent.parentPos, index: parent.index + 1 } : null;
}
