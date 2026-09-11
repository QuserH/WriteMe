import { Extension, Mark, Node as TiptapNode, mergeAttributes, type Editor, type JSONContent } from "@tiptap/core";
import { EditorContent, useEditor } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import Highlight from "@tiptap/extension-highlight";
import Placeholder from "@tiptap/extension-placeholder";
import TaskList from "@tiptap/extension-task-list";
import TaskItem from "@tiptap/extension-task-item";
import { TableKit } from "@tiptap/extension-table";
import { TextStyleKit } from "@tiptap/extension-text-style";
import { Fragment, Slice, type Node as PMNode } from "@tiptap/pm/model";
import { Plugin, TextSelection } from "@tiptap/pm/state";
import * as Y from "yjs";
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import ToggleBlock from "../editor/Toggle";
import BlockHandle from "../editor/BlockHandle";
import FormattingToolbar from "../editor/FormattingToolbar";
import { slashMatch } from "../editor/SlashMenu";
import { applyBlockKind, BLOCK_TYPES, type BlockKind } from "../editor/blockCommands";
import Icon from "../components/Icon";
import type { LiveDocument } from "./live";
import { containerTypes, textTypes } from "./replica";
import { uuid } from "./api";

const types = [...containerTypes, ...textTypes, "horizontalRule", "sharedUnsupported"];
const Identity = Extension.create({
  name: "sharedIdentity",
  addGlobalAttributes() { return [{ types, attributes: {
    writemeId: { default: null, renderHTML: attrs => attrs.writemeId ? { "data-block-id": attrs.writemeId } : {} },
    writemeCommentIds: { default: null, rendered: false },
    textAlign: { default: null, renderHTML: attrs => attrs.textAlign ? { style: `text-align:${["left", "right", "center", "justify"].includes(String(attrs.textAlign)) ? attrs.textAlign : "left"}` } : {} },
    writemeComments: { default: null, rendered: false },
  } }]; },
  addProseMirrorPlugins() { return [new Plugin({ appendTransaction(transactions, _old, state) {
    if (!transactions.some(transaction => transaction.docChanged)) return null;
    const seen = new Set<string>(); const tr = state.tr;
    state.doc.descendants((node, pos) => {
      if (!node.isBlock) return;
      const id = node.attrs.writemeId as string | null;
      if (!id || seen.has(id)) { const next = uuid(); tr.setNodeMarkup(pos, undefined, { ...node.attrs, writemeId: next, writemeCommentIds: id ? null : node.attrs.writemeCommentIds }); seen.add(next); }
      else seen.add(id);
    });
    return tr.docChanged ? tr : null;
  } })]; },
});
const CommentMark = Mark.create({ name: "comment", inclusive: false, addAttributes: () => ({ ids: { default: [], rendered: false } }), parseHTML: () => [{ tag: "span[data-comment]" }],
  renderHTML: ({ HTMLAttributes }) => ["span", mergeAttributes(HTMLAttributes, { "data-comment": "true", class: "shared-annotation" }), 0] });
const NoteLink = Mark.create({ name: "noteLink", addAttributes: () => ({ documentId: { default: "" }, label: { default: "" } }),
  parseHTML: () => [{ tag: "span[data-note-link]" }], renderHTML: () => ["span", { "data-note-link": "true", class: "shared-note-link" }, 0] });
const Columns = TiptapNode.create({ name: "columnList", group: "block", content: "column{2,3}", defining: true,
  parseHTML: () => [{ tag: "div[data-columns]" }], renderHTML: ({ HTMLAttributes }) => ["div", mergeAttributes(HTMLAttributes, { "data-columns": "true", class: "shared-columns" }), 0] });
const Column = TiptapNode.create({ name: "column", content: "block+", isolating: true, addAttributes: () => ({ width: { default: null } }),
  parseHTML: () => [{ tag: "div[data-column]" }], renderHTML: ({ HTMLAttributes }) => ["div", mergeAttributes(HTMLAttributes, { "data-column": "true" }), 0] });
const Unsupported = TiptapNode.create({ name: "sharedUnsupported", group: "block", atom: true, addAttributes: () => ({ raw: { default: null, rendered: false } }),
  parseHTML: () => [{ tag: "div[data-preserved-block]" }], renderHTML: ({ HTMLAttributes }) => ["div", mergeAttributes(HTMLAttributes, { "data-preserved-block": "true", class: "shared-preserved" }), "此内容已保留，可在原生端查看"] });
function supported(root: JSONContent): JSONContent {
  if (!types.includes(root.type ?? "") && root.type !== "text" && root.type !== "hardBreak") return { type: "sharedUnsupported", attrs: { writemeId: root.attrs?.writemeId, raw: root } };
  return { ...root, content: root.content?.filter(node => !(containerTypes.has(node.type ?? "") && !node.content?.length && ["bulletList", "orderedList", "taskList", "blockquote"].includes(node.type ?? ""))).map(supported) };
}
function cleanPaste(slice: Slice): Slice {
  const fragment = (value: Fragment): Fragment => Fragment.fromArray(Array.from({ length: value.childCount }, (_, i) => {
    const node = value.child(i); if (node.isText) return node.mark(node.marks.filter(mark => mark.type.name !== "comment"));
    return node.type.create({ ...node.attrs, writemeId: null, writemeCommentIds: null }, fragment(node.content), node.marks.filter(mark => mark.type.name !== "comment"));
  }));
  return new Slice(fragment(slice.content), slice.openStart, slice.openEnd);
}
export interface ParagraphTarget { id: string; text: string }
function paragraph(editor: Editor): ParagraphTarget | null {
  const { $from } = editor.state.selection;
  for (let depth = $from.depth; depth > 0; depth--) { const node = $from.node(depth); if (node.isTextblock && node.attrs.writemeId) return { id: String(node.attrs.writemeId), text: node.textContent }; }
  return null;
}
export default function SharedEditor({ live, onComment, onReady }: { live: LiveDocument; onComment: (target: ParagraphTarget | null) => void; onReady?: (editor: Editor) => void }) {
  const scope = useRef<HTMLDivElement>(null); const writing = useRef(false); const comment = useRef(onComment); comment.current = onComment;
  const [, redraw] = useState(0); const [menu, setMenu] = useState<{ pos: number; x: number; y: number } | null>(null);
  const initial = useMemo(() => supported(live.replica.readRoot()), [live]);
  const editor = useEditor({ extensions: [StarterKit.configure({ undoRedo: false, heading: { levels: [1, 2, 3] }, link: { openOnClick: false, defaultProtocol: "https" } }),
    Highlight.configure({ multicolor: true }), Placeholder.configure({ placeholder: "从这里开始，输入 / 探索更多…" }), TaskList, TaskItem.configure({ nested: true }),
    TableKit.configure({ table: { resizable: true } }), TextStyleKit, ToggleBlock, Columns, Column, Unsupported, Identity, CommentMark, NoteLink,
    Extension.create({ name: "sharedKeys", priority: 2000, addKeyboardShortcuts: () => ({
      "Mod-z": () => { if (live.canWrite) live.replica.undo.undo(); return true; }, "Mod-Shift-z": () => { if (live.canWrite) live.replica.undo.redo(); return true; },
      "Mod-y": () => { if (live.canWrite) live.replica.undo.redo(); return true; }, "Mod-Alt-m": ({ editor }) => { comment.current(paragraph(editor)); return true; },
      Tab: ({ editor }) => { const type = editor.isActive("taskItem") ? "taskItem" : "listItem"; return editor.isActive(type) && editor.commands.sinkListItem(type); },
      "Shift-Tab": ({ editor }) => { const type = editor.isActive("taskItem") ? "taskItem" : "listItem"; return editor.isActive(type) && editor.commands.liftListItem(type); },
    }) }),
  ], content: initial, editable: live.canWrite,
  editorProps: { attributes: { class: "writeme-editor shared-editor", spellcheck: "false", role: "textbox", "aria-label": "共享文档正文", "aria-multiline": "true" }, transformPasted: cleanPaste,
    handleDOMEvents: { compositionstart: () => { live.setComposing(true); return false; }, compositionend: view => { const release = () => { if (!view.isDestroyed && view.composing) setTimeout(release, 20); else live.setComposing(false); }; setTimeout(release, 20); return false; } } },
  onUpdate: ({ editor, transaction }) => { if (transaction.getMeta("shared-remote") || !live.canWrite) return; writing.current = true; try { live.replica.writeRoot(editor.getJSON()); } finally { writing.current = false; } setMenu(null); },
  onSelectionUpdate: ({ editor }) => { live.presence(paragraph(editor)?.id ?? null); redraw(value => value + 1); },
  });
  useEffect(() => {
    if (!editor) return;
    onReady?.(editor);
    const capture = () => {
      const { $anchor, $head } = editor.state.selection;
      const point = (resolved: typeof $anchor) => { const id = resolved.parent.attrs.writemeId as string; const text = live.replica.blockText(id); return { id, offset: resolved.parentOffset, position: text ? Y.createRelativePositionFromTypeIndex(text, Math.min(resolved.parentOffset, text.length)) : null }; };
      return { anchor: point($anchor), head: point($head) };
    };
    let selection = capture();
    const before = () => { if (!writing.current && !editor.isDestroyed) selection = capture(); };
    const refresh = () => {
      if (writing.current || editor.isDestroyed || editor.view.composing) return;
      editor.setEditable(!!live.canWrite, false);
      const root = supported(live.replica.readRoot()); const next = editor.schema.nodeFromJSON(root);
      if (editor.state.doc.eq(next)) { redraw(value => value + 1); return; }
      const locate = (point: typeof selection.anchor) => {
        const offset = point.position ? Y.createAbsolutePositionFromRelativePosition(point.position, live.replica.doc)?.index ?? point.offset : point.offset;
        let found: number | undefined;
        next.descendants((node: PMNode, pos: number) => { if (node.isTextblock && node.attrs.writemeId === point.id) { found = pos + 1 + Math.min(offset, node.content.size); return false; } });
        return found ?? 1;
      };
      const tr = editor.state.tr.replaceWith(0, editor.state.doc.content.size, next.content).setMeta("shared-remote", true).setMeta("addToHistory", false);
      tr.setDocAttribute("writemeComments", root.attrs?.writemeComments ?? null);
      tr.setSelection(TextSelection.create(tr.doc, Math.min(locate(selection.anchor), tr.doc.content.size), Math.min(locate(selection.head), tr.doc.content.size)));
      editor.view.dispatch(tr); redraw(value => value + 1);
    };
    live.replica.doc.on("beforeTransaction", before); live.replica.doc.on("afterTransaction", refresh);
    const unsubscribe = live.subscribe(() => { editor.setEditable(!!live.canWrite, false); });
    const close = (event: MouseEvent) => { if (!(event.target as HTMLElement).closest(".wm-block-menu,.wm-handle")) setMenu(null); };
    document.addEventListener("mousedown", close);
    return () => { live.replica.doc.off("beforeTransaction", before); live.replica.doc.off("afterTransaction", refresh); unsubscribe(); document.removeEventListener("mousedown", close); live.setComposing(false); };
  }, [editor, live, onReady]);
  if (!editor) return null;
  return <div className="shared-editor-wrap wm-editor-scope" ref={scope}>
    <EditorContent editor={editor} />
    {live.canWrite && <><BlockHandle editor={editor} scopeRef={scope} onOpenMenu={(pos, point) => setMenu({ pos, x: point.left, y: point.top })} /><SharedSlash editor={editor} /><FormattingToolbar editor={editor} /></>}
    <ParagraphBadges editor={editor} live={live} scope={scope} onComment={onComment} />
    {menu && <div className="wm-block-menu" style={{ left: Math.max(10, Math.min(menu.x - 25, innerWidth - 230)), top: Math.min(menu.y + 30, innerHeight - 260) }} onMouseDown={event => event.preventDefault()} role="menu">
      <button role="menuitem" onClick={() => { editor.commands.setTextSelection(menu.pos + 1); onComment(paragraph(editor)); setMenu(null); }}><BubbleIcon />评论此段落</button>
      <button role="menuitem" onClick={() => { editor.chain().focus().setNodeSelection(menu.pos).deleteSelection().run(); setMenu(null); }}><Icon name="trash" />删除此块</button>
      <span className="wm-block-menu-sep" /><div className="wm-menu-title">转换为</div>
      {(["paragraph", "h2", "toggle", "taskList"] as const).map(kind => <button role="menuitem" key={kind} onClick={() => { editor.commands.setTextSelection(menu.pos + 1); applyBlockKind(editor.chain().focus(), kind).run(); setMenu(null); }}>{BLOCK_TYPES.find(item => item.kind === kind)?.label}</button>)}
    </div>}
    {editor.isActive("table") && live.canWrite && <div className="shared-table-actions" aria-label="表格操作">{[
      ["插入行", () => editor.chain().focus().addRowAfter().run()], ["插入列", () => editor.chain().focus().addColumnAfter().run()],
      ["删除行", () => editor.chain().focus().deleteRow().run()], ["删除列", () => editor.chain().focus().deleteColumn().run()],
    ].map(([label, action]) => <button key={label as string} onMouseDown={event => event.preventDefault()} onClick={action as () => void}>{label as string}</button>)}</div>}
  </div>;
}
function ParagraphBadges({ editor, live, scope, onComment }: { editor: Editor; live: LiveDocument; scope: RefObject<HTMLDivElement | null>; onComment: (target: ParagraphTarget) => void }) {
  const [badges, setBadges] = useState<Array<ParagraphTarget & { top: number; left: number; count: number; current: boolean }>>([]);
  useLayoutEffect(() => {
    let frame = 0;
    const measure = () => {
      frame = 0; if (editor.isDestroyed || !scope.current) return;
      const bounds = scope.current.getBoundingClientRect(); const current = paragraph(editor);
      const counts = new Map(live.replica.readThreads().map(thread => [thread.id, thread.messages.filter(message => !message.deleted).length]));
      const next: typeof badges = [];
      editor.state.doc.descendants((node, pos) => {
        if (!node.isTextblock || !node.attrs.writemeId) return;
        const id = String(node.attrs.writemeId); const count = (node.attrs.writemeCommentIds as string[] | null)?.reduce((sum, id) => sum + (counts.get(id) ?? 0), 0) ?? 0;
        if (id !== current?.id && !count) return;
        const element = editor.view.nodeDOM(pos); if (!(element instanceof HTMLElement) || !element.getClientRects().length) return;
        const rect = element.getBoundingClientRect();
        // Match the actual paragraph, including paragraphs inside cells, columns and toggles.
        // No sticky footer: scrolling, wrapping and remote changes all remeasure the same node.
        next.push({ id, text: node.textContent, count, current: id === current?.id, top: rect.top - bounds.top, left: Math.min(rect.right - bounds.left + 8, bounds.width + 9) });
      });
      setBadges(next);
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
    const observer = new ResizeObserver(schedule); observer.observe(editor.view.dom);
    if (scope.current) observer.observe(scope.current);
    editor.on("transaction", schedule); editor.on("selectionUpdate", schedule); const unsubscribe = live.subscribe(schedule);
    window.addEventListener("scroll", schedule, true); window.addEventListener("resize", schedule); measure();
    return () => { cancelAnimationFrame(frame); observer.disconnect(); editor.off("transaction", schedule); editor.off("selectionUpdate", schedule); unsubscribe(); window.removeEventListener("scroll", schedule, true); window.removeEventListener("resize", schedule); };
  }, [editor, live, scope]);
  return <>{badges.map(badge => <button key={badge.id} data-comment-block={badge.id} className={`shared-paragraph-comment${badge.count ? " has-comments" : ""}`} style={{ top: badge.top, left: badge.left, right: "auto" }} title={badge.count ? `${badge.count} 条评论` : "评论当前段落 · Ctrl+Alt+M"} aria-label={badge.current ? "评论当前段落" : `查看段落评论：${badge.text.slice(0, 24) || "空白段落"}`} onMouseDown={event => event.preventDefault()} onClick={() => onComment({ id: badge.id, text: badge.text })}><BubbleIcon />{badge.count > 0 && <span>{badge.count}</span>}</button>)}</>;
}
export function BubbleIcon() { return <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" strokeWidth="1.35" aria-hidden="true"><path d="M17 9.3a6.8 6.8 0 0 1-7 6.5c-1 0-2-.2-2.9-.6L3 17l.8-4A6.1 6.1 0 0 1 3 9.3a7 7 0 0 1 14 0Z" /><path d="M7 8h6M7 11h4" /></svg>; }
const categories = [
  { id: "list", label: "列表", description: "待办、折叠与编号", icon: "bulletList", kinds: ["taskList", "toggle", "bulletList", "orderedList"] },
  { id: "text", label: "文本样式", description: "正文与多级标题", icon: "text", kinds: ["paragraph", "h1", "h2", "h3"] },
  { id: "layout", label: "表格与分栏", description: "让信息整齐排列", icon: "panel", kinds: ["table", "columns"] },
  { id: "decoration", label: "装饰", description: "引用、代码与分隔线", icon: "quote", kinds: ["blockquote", "codeBlock", "divider"] },
] as const;
type SlashKind = BlockKind | "table" | "columns" | "divider";
const extras = [{ kind: "table", label: "表格", hint: "3 × 3", icon: "panel", keywords: "biaoge table" }, { kind: "columns", label: "两栏", hint: "并排内容", icon: "panel", keywords: "fenlan columns" }, { kind: "divider", label: "分隔线", hint: "区分段落", icon: "text", keywords: "fengexian divider" }] as const;
function SharedSlash({ editor }: { editor: Editor }) {
  const [match, setMatch] = useState(() => slashMatch(editor.state)); const [category, setCategory] = useState<string | null>(null); const [active, setActive] = useState(0); const [dismissed, setDismissed] = useState<number | null>(null);
  useEffect(() => { const update = () => { const next = slashMatch(editor.state); setMatch(next); if (!next) { setCategory(null); setDismissed(null); } }; editor.on("transaction", update); return () => { editor.off("transaction", update); }; }, [editor]);
  const selected = categories.find(item => item.id === category); const all = [...BLOCK_TYPES, ...extras]; const query = match?.query.toLowerCase().trim() ?? "";
  const items = query ? all.filter(item => `${item.label} ${item.keywords}`.toLowerCase().includes(query)) : selected ? all.filter(item => (selected.kinds as readonly string[]).includes(item.kind)) : [];
  const open = !!match && dismissed !== match.from && editor.isFocused;
  const length = items.length || (!query && !selected ? categories.length : 0);
  useEffect(() => { setActive(0); }, [category, query]);
  const run = (kind: SlashKind) => {
    const current = slashMatch(editor.state); if (!current || editor.view.composing || current.from !== match?.from || current.query !== match.query) return;
    const chain = editor.chain().focus().deleteRange({ from: current.from, to: current.to });
    if (kind === "table") chain.insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run();
    else if (kind === "columns") chain.insertContent({ type: "columnList", content: [0, 1].map(() => ({ type: "column", content: [{ type: "paragraph" }] })) }).run();
    else if (kind === "divider") chain.setHorizontalRule().run(); else applyBlockKind(chain, kind).run();
    setMatch(null); setCategory(null);
  };
  useEffect(() => {
    if (!open) return;
    const key = (event: KeyboardEvent) => {
      if (event.isComposing || editor.view.composing || event.ctrlKey || event.metaKey || event.altKey) return;
      if (["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape", "ArrowRight", "ArrowLeft"].includes(event.key)) { event.preventDefault(); event.stopPropagation(); }
      if (event.key === "ArrowDown" || event.key === "ArrowUp") setActive(value => (value + (event.key === "ArrowDown" ? 1 : -1) + Math.max(1, length)) % Math.max(1, length));
      else if (event.key === "Escape" || event.key === "ArrowLeft" || event.key === "Tab" && event.shiftKey) { if (category) setCategory(null); else setDismissed(match!.from); }
      else if (["Enter", "Tab", "ArrowRight"].includes(event.key)) { if (!selected && !query) setCategory(categories[active]?.id ?? null); else if (items[active]) run(items[active].kind); }
    };
    const close = (event: PointerEvent) => { if (!(event.target as Element).closest(".shared-slash")) setDismissed(match!.from); };
    document.addEventListener("keydown", key, true); document.addEventListener("pointerdown", close, true);
    return () => { document.removeEventListener("keydown", key, true); document.removeEventListener("pointerdown", close, true); };
  });
  if (!open || !match) return null;
  const point = editor.view.coordsAtPos(match.from);
  return <div className="wm-slash-menu shared-slash" style={{ left: Math.max(12, Math.min(point.left, innerWidth - 304)), top: Math.max(12, Math.min(point.bottom + 8, innerHeight - 352)) }} onMouseDown={event => event.preventDefault()}>
    <div className="wm-slash-title">{selected ? <button aria-label="返回插入分类" onClick={() => setCategory(null)}>‹</button> : <Icon name="plus" size={15} />}<span>{query ? "搜索内容" : selected?.label ?? "插入内容"}</span></div>
    <div role="listbox" aria-label="插入菜单" className="wm-slash-options">{!selected && !query ? categories.map((item, i) => <button key={item.id} role="option" aria-selected={i === active} className={i === active ? "active" : ""} onMouseEnter={() => setActive(i)} onClick={() => setCategory(item.id)}><Icon name={item.icon} /><span className="shared-slash-label">{item.label}<small>{item.description}</small></span><Icon name="chevron" size={14} /></button>)
      : items.map((item, i) => <button key={item.kind} role="option" aria-selected={i === active} className={i === active ? "active" : ""} onMouseEnter={() => setActive(i)} onClick={() => run(item.kind)}><Icon name={item.icon} /><span>{item.label}</span><small>{item.hint}</small></button>)}
      {length === 0 && <p className="shared-empty-small">没有找到匹配的内容</p>}
    </div><div className="wm-slash-footer"><span>↑ ↓ 选择</span><span>↵ 进入 / 插入</span><span>Esc 返回</span></div>
  </div>;
}
