import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { closeHistory } from "@tiptap/pm/history";
import { NodeSelection, Selection, TextSelection, type Command, type EditorState } from "@tiptap/pm/state";
import { indentDestination, moveBlockTransaction, outdentDestination, selectedBlock } from "./blockTree";

// Note: 原生标题、回车下沉与无损取消折叠 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md

export function toggleTitle(state: EditorState) {
  const { $from, $to } = state.selection;
  const depth = $from.depth - 1;
  if (depth < 1 || !$from.sameParent($to)) return null;
  const node = $from.node(depth);
  if (node.type.name !== "toggleBlock" || $from.index(depth) !== 0) return null;
  return { node, pos: $from.before(depth), title: $from.parent };
}

export const wrapInToggle: Command = (state, dispatch) => {
  if (toggleTitle(state)) return true;
  const { $from, $to } = state.selection;
  if (state.selection instanceof NodeSelection) {
    const block = selectedBlock(state);
    if (!block) return false;
    if (block.node.type.name === "toggleBlock") return true;
    if (dispatch) {
      const title = state.schema.nodes.paragraph.create(null, block.node.isTextblock ? block.node.content : undefined);
      const content = block.node.isTextblock ? [title] : [title, block.node];
      const node = state.schema.nodes.toggleBlock.create({ collapsed: true }, content);
      const tr = state.tr.replaceWith(block.pos, block.pos + block.node.nodeSize, node);
      tr.setSelection(TextSelection.create(tr.doc, block.pos + 2));
      dispatch(closeHistory(tr).scrollIntoView());
    }
    return true;
  }
  if (!$from.sameParent($to) || !$from.parent.isTextblock || $from.depth < 1) return false;
  const pos = $from.before();
  const parent = $from.node($from.depth - 1);
  const index = $from.index($from.depth - 1);
  const type = state.schema.nodes.toggleBlock;
  if (!parent.canReplaceWith(index, index + 1, type)) return false;
  if (dispatch) {
    // 保留 marks / hardBreak / links；不能经 textContent 把富文本压成纯文字。
    const title = state.schema.nodes.paragraph.create(null, $from.parent.content);
    const toggle = type.create({ collapsed: true }, title);
    const tr = state.tr.replaceWith(pos, pos + $from.parent.nodeSize, toggle);
    tr.setSelection(TextSelection.create(tr.doc, pos + 2 + $from.parentOffset));
    dispatch(closeHistory(tr).scrollIntoView());
  }
  return true;
};

export function unwrapToggleAt(state: EditorState, pos: number, dispatch?: Parameters<Command>[1], dropEmptyTitle = false) {
  const node = state.doc.nodeAt(pos);
  if (node?.type.name !== "toggleBlock") return false;
  let content = node.content;
  if (dropEmptyTitle && node.firstChild?.content.size === 0 && node.childCount > 1) content = content.cut(node.firstChild.nodeSize);
  const $pos = state.doc.resolve(pos);
  if (!$pos.parent.canReplace($pos.index(), $pos.index() + 1, content)) return false;
  if (dispatch) {
    const tr = state.tr.replaceWith(pos, pos + node.nodeSize, content);
    tr.setSelection(Selection.near(tr.doc.resolve(Math.min(pos + 1, tr.doc.content.size))));
    dispatch(closeHistory(tr).scrollIntoView());
  }
  return true;
}

export const unwrapToggle: Command = (state, dispatch) => {
  const block = toggleTitle(state) ?? selectedBlock(state);
  return !!block && unwrapToggleAt(state, block.pos, dispatch);
};

export const indentBlock: Command = (state, dispatch) => {
  const block = selectedBlock(state);
  const destination = block && indentDestination(state, block);
  if (!block || !destination) return false;
  if (!dispatch) return true;
  const tr = moveBlockTransaction(state, block.pos, destination, "preserve");
  if (!tr) return false;
  dispatch(tr);
  return true;
};

export const outdentBlock: Command = (state, dispatch) => {
  const block = selectedBlock(state);
  const destination = block && outdentDestination(state, block);
  if (!block || !destination) return false;
  if (!dispatch) return true;
  const tr = moveBlockTransaction(state, block.pos, destination, "preserve");
  if (!tr) return false;
  dispatch(tr);
  return true;
};

export const enterToggle: Command = (state, dispatch) => {
  const current = toggleTitle(state);
  if (!current) return false;
  const { node, pos, title } = current;
  if (title.content.size === 0 && node.childCount === 1) return outdentBlock(state, dispatch) || unwrapToggleAt(state, pos, dispatch);
  if (!dispatch) return true;
  const { $from, $to } = state.selection;
  const before = title.copy(title.content.cut(0, $from.parentOffset));
  const after = title.copy(title.content.cut($to.parentOffset));
  const child = node.type.create({ collapsed: true }, after);
  const children: ProseMirrorNode[] = [];
  node.forEach((item, _offset, index) => { if (index > 0) children.push(item); });
  // 由明确的输入操作替换旧版唯一空正文占位，不异步清理文档。
  if (children.length === 1 && children[0].type.name === "paragraph" && !children[0].content.size) children.length = 0;
  const replacement = node.type.create({ ...node.attrs, collapsed: false }, [before, child, ...children]);
  const tr = state.tr.replaceWith(pos, pos + node.nodeSize, replacement);
  tr.setSelection(TextSelection.create(tr.doc, pos + before.nodeSize + 3));
  dispatch(closeHistory(tr).scrollIntoView());
  return true;
};

export const backspaceToggle: Command = (state, dispatch) => {
  const title = toggleTitle(state);
  if (!title || !state.selection.empty || state.selection.$from.parentOffset !== 0) return false;
  return unwrapToggleAt(state, title.pos, dispatch, true);
};

export const insertToggleSibling: Command = (state, dispatch) => {
  const current = toggleTitle(state);
  if (!current) return false;
  if (dispatch) {
    const pos = current.pos + current.node.nodeSize;
    const sibling = current.node.type.create({ collapsed: true }, state.schema.nodes.paragraph.create());
    const tr = state.tr.insert(pos, sibling);
    tr.setSelection(TextSelection.create(tr.doc, pos + 2));
    dispatch(closeHistory(tr).scrollIntoView());
  }
  return true;
};

export function collapsedSelectionPosition(state: EditorState): number | null {
  const { $from } = state.selection;
  for (let depth = 1; depth <= $from.depth; depth++) {
    const node = $from.node(depth);
    if (node.type.name === "toggleBlock" && node.attrs.collapsed && $from.index(depth) > 0) {
      return $from.before(depth) + 2 + (node.firstChild?.content.size ?? 0);
    }
  }
  return null;
}

export const toggleAllBlocks: Command = (state, dispatch) => {
  let count = 0;
  let collapse = false;
  state.doc.descendants((node) => {
    if (node.type.name === "toggleBlock") {
      count++;
      if (!node.attrs.collapsed) collapse = true;
    }
  });
  if (!count) return false;
  if (dispatch) {
    const tr = state.tr;
    state.doc.descendants((node, pos) => {
      if (node.type.name === "toggleBlock") tr.setNodeAttribute(pos, "collapsed", collapse);
    });
    dispatch(closeHistory(tr));
  }
  return true;
};
