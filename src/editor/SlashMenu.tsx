import type { Editor } from "@tiptap/core";
import { closeHistory } from "@tiptap/pm/history";
import { TextSelection, type EditorState, type Transaction } from "@tiptap/pm/state";
import { useEffect, useId, useRef, useState } from "react";
import Icon from "../components/Icon";
import { applyBlockKind, filterBlockTypes, type BlockKind } from "./blockCommands";
import { floatingPosition } from "./floatingPosition";
import { toggleTitle } from "./toggleCommands";

// Note: 搜索范围、IME 与菜单关闭语义 — 见 .agents/notes/implemented/feature/2026-09-09-slash-menu.md

export function slashMatch(state: EditorState) {
  const { selection } = state;
  if (!(selection instanceof TextSelection) || !selection.empty) return null;
  const { $from } = selection;
  if (!$from.parent.isTextblock || $from.parent.type.spec.code) return null;
  const before = $from.parent.textBetween(0, $from.parentOffset, "\n", "\ufffc");
  const match = /^\/([^/\n\ufffc]{0,48})$/u.exec(before);
  return match ? { from: $from.start(), to: selection.from, query: match[1] } : null;
}

type Match = NonNullable<ReturnType<typeof slashMatch>>;
interface MenuState {
  match: Match;
  items: ReturnType<typeof filterBlockTypes>;
  active: number;
  position: ReturnType<typeof floatingPosition>;
}

export default function SlashMenu({ editor }: { editor: Editor | null }) {
  const [menu, setMenu] = useState<MenuState | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const optionsRef = useRef<HTMLDivElement>(null);
  const runRef = useRef<(kind: BlockKind) => void>(() => {});
  const highlightRef = useRef<(index: number) => void>(() => {});
  const listId = useId();

  useEffect(() => {
    if (!editor) return;
    const editorDom = editor.view.dom;
    let current: MenuState | null = null;
    let dismissedAt: number | null = null;
    let composing = false;
    let frame = 0;
    const publish = (next: MenuState | null) => {
      current = next;
      setMenu(next);
    };
    const close = () => {
      dismissedAt = current?.match.from ?? dismissedAt;
      publish(null);
    };
    const place = (match: Match, count: number) => floatingPosition(
      editor, editor.view.coordsAtPos(match.from), 280, Math.min(358, 84 + count * 37), "below",
    );
    const refresh = (allowOpen: boolean) => {
      if (editor.isDestroyed) return;
      const match = slashMatch(editor.state);
      if (!match) {
        dismissedAt = null;
        publish(null);
        return;
      }
      if (!editor.view.hasFocus() || match.from === dismissedAt || (!current && !allowOpen)) return;
      const items = filterBlockTypes(match.query);
      const sameQuery = current?.match.from === match.from && current.match.query === match.query;
      publish({ match, items, active: sameQuery ? Math.min(current!.active, Math.max(0, items.length - 1)) : 0,
        position: place(match, items.length) });
    };
    const schedule = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { frame = 0; refresh(false); });
    };
    const highlight = (index: number) => {
      if (current && current.items.length) publish({ ...current, active: index });
    };
    const run = (kind: BlockKind) => {
      if (!current || composing || editor.view.composing) return;
      const match = slashMatch(editor.state);
      // 点击前重新验证整个触发范围，不能删掉另一个块或新光标附近的文字。
      if (!match || match.from !== current.match.from || match.to !== current.match.to || match.query !== current.match.query) {
        close();
        return;
      }
      close();
      const chain = editor.chain().focus().command(({ tr }) => { closeHistory(tr); return true; })
        .deleteRange({ from: match.from, to: match.to });
      if (kind !== "toggle" && toggleTitle(editor.state)) chain.unsetToggleBlock();
      applyBlockKind(chain, kind).run();
    };
    runRef.current = run;
    highlightRef.current = highlight;

    const onTransaction = ({ transaction }: { transaction: Transaction }) => refresh(transaction.docChanged);
    const onKey = (event: KeyboardEvent) => {
      if (!current?.position || !editorDom.contains(event.target as Node)
        || composing || editor.view.composing || event.isComposing || event.keyCode === 229) return;
      if (event.key === "Escape") {
        event.preventDefault(); event.stopPropagation(); close();
      } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        if (event.shiftKey || event.ctrlKey || event.metaKey || event.altKey) return;
        if (!current.items.length) return;
        event.preventDefault(); event.stopPropagation();
        highlight((current.active + (event.key === "ArrowDown" ? 1 : -1) + current.items.length) % current.items.length);
      } else if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey) {
        const item = current.items[current.active];
        if (!item) { close(); return; }
        event.preventDefault(); event.stopPropagation(); run(item.kind);
      }
    };
    const onPointer = (event: PointerEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) close();
    };
    const onScroll = (event: Event) => {
      if (current && !menuRef.current?.contains(event.target as Node)) schedule();
    };
    const onBlur = () => close();
    const onCompositionStart = () => { composing = true; };
    const onCompositionEnd = () => {
      composing = false;
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { frame = 0; refresh(true); });
    };
    editor.on("transaction", onTransaction);
    editor.on("blur", onBlur);
    editorDom.addEventListener("compositionstart", onCompositionStart);
    editorDom.addEventListener("compositionend", onCompositionEnd);
    document.addEventListener("keydown", onKey, true);
    document.addEventListener("pointerdown", onPointer, true);
    document.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", schedule);
    return () => {
      cancelAnimationFrame(frame);
      editor.off("transaction", onTransaction);
      editor.off("blur", onBlur);
      editorDom.removeEventListener("compositionstart", onCompositionStart);
      editorDom.removeEventListener("compositionend", onCompositionEnd);
      document.removeEventListener("keydown", onKey, true);
      document.removeEventListener("pointerdown", onPointer, true);
      document.removeEventListener("scroll", onScroll, true);
      window.removeEventListener("resize", schedule);
    };
  }, [editor]);

  useEffect(() => {
    if (!editor) return;
    const input = editor.view.dom;
    if (menu?.position) {
      input.setAttribute("aria-controls", listId);
      input.setAttribute("aria-haspopup", "listbox");
      const selected = menu.items[menu.active];
      if (selected) input.setAttribute("aria-activedescendant", `${listId}-${selected.kind}`);
      else input.removeAttribute("aria-activedescendant");
    } else {
      input.removeAttribute("aria-controls");
      input.removeAttribute("aria-haspopup");
      input.removeAttribute("aria-activedescendant");
    }
    const list = optionsRef.current;
    const active = list?.querySelector<HTMLElement>('[aria-selected="true"]');
    if (list && active) {
      const listRect = list.getBoundingClientRect();
      const itemRect = active.getBoundingClientRect();
      if (itemRect.top < listRect.top) list.scrollTop -= listRect.top - itemRect.top;
      else if (itemRect.bottom > listRect.bottom) list.scrollTop += itemRect.bottom - listRect.bottom;
    }
    return () => {
      input.removeAttribute("aria-controls");
      input.removeAttribute("aria-haspopup");
      input.removeAttribute("aria-activedescendant");
    };
  }, [editor, menu, listId]);

  if (!menu?.position) return null;
  return (
    <div ref={menuRef} className="wm-slash-menu" style={{ ...menu.position, maxHeight: Math.min(358, menu.position.maxHeight) }}
      onMouseDown={(event) => event.preventDefault()}>
      <div className="wm-slash-title">
        <Icon name={menu.match.query ? "search" : "plus"} size={15} />
        <span>{menu.match.query ? `搜索：${menu.match.query}` : "插入内容"}</span>
        <span className="wm-slash-count">{menu.items.length}</span>
      </div>
      <div ref={optionsRef} id={listId} className="wm-slash-options" role="listbox" aria-label="斜杠菜单">
        {menu.items.map((item, index) => (
          <button key={item.kind} id={`${listId}-${item.kind}`} type="button" role="option" tabIndex={-1}
            aria-selected={index === menu.active} className={index === menu.active ? "active" : ""}
            onMouseEnter={() => highlightRef.current(index)} onClick={() => runRef.current(item.kind)}>
            <span className="wm-slash-label"><Icon name={item.icon} size={18} />{item.label}</span>
            <span className="wm-slash-hint">{item.hint}</span>
          </button>
        ))}
        {!menu.items.length && <div className="wm-slash-empty" role="status">没有匹配的内容<span>试试「标题」「折叠」或 heading</span></div>}
      </div>
      <div className="wm-slash-footer"><span><kbd>↑</kbd><kbd>↓</kbd> 选择</span><span><kbd>↵</kbd> 确认</span><span><kbd>Esc</kbd> 关闭</span></div>
    </div>
  );
}
