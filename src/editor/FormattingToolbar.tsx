import { posToDOMRect, type Editor } from "@tiptap/core";
import { closeHistory } from "@tiptap/pm/history";
import { TextSelection, type Transaction } from "@tiptap/pm/state";
import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import Icon from "../components/Icon";
import { floatingPosition } from "./floatingPosition";

// Note: 浮动工具栏的选区、链接草稿与焦点管理 — 见 .agents/notes/implemented/feature/2026-09-09-text-formatting-toolbar.md

const FORMATS = [
  { mark: "bold", label: "粗体", shortcut: "Ctrl+B" },
  { mark: "italic", label: "斜体", shortcut: "Ctrl+I" },
  { mark: "underline", label: "下划线", shortcut: "Ctrl+U" },
  { mark: "strike", label: "删除线", shortcut: "Ctrl+Shift+S" },
  { mark: "code", label: "行内代码", shortcut: "Ctrl+E" },
] as const;
type FormatMark = (typeof FORMATS)[number]["mark"];
const HIGHLIGHTS = [
  { label: "黄色", color: "#fff0a6" },
  { label: "绿色", color: "#d8efdf" },
  { label: "蓝色", color: "#dceafb" },
  { label: "粉色", color: "#f8dce8" },
  { label: "紫色", color: "#e9e0f6" },
] as const;
type Panel = "link" | "highlight" | null;
interface TextRange { from: number; to: number }
interface ToolbarState extends TextRange {
  position: NonNullable<ReturnType<typeof floatingPosition>>;
  formats: { mark: FormatMark; active: boolean; enabled: boolean }[];
  highlight: boolean;
  highlightColor: string;
  canHighlight: boolean;
  link: boolean;
  canLink: boolean;
  hasMarks: boolean;
  hasHighlight: boolean;
  hasLink: boolean;
  text: string;
}
const sameRange = (a: TextRange | null, b: TextRange) => a?.from === b.from && a.to === b.to;

export function normalizeLink(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed || /[\s\u0000-\u001f\u007f]/u.test(trimmed)) return null;
  const href = /^[a-z][a-z\d+.-]*:/i.test(trimmed) ? trimmed : `https://${trimmed}`;
  try {
    const url = new URL(href);
    if (url.protocol === "http:" || url.protocol === "https:") return url.hostname ? url.href : null;
    if (url.protocol === "mailto:" && /^[^@]+@[^@]+$/.test(url.pathname)) return url.href;
    if (url.protocol === "tel:" && /^\+?[\d().-]+$/.test(url.pathname)) return url.href;
  } catch { /* 无效地址留在表单内修正，不修改文档。 */ }
  return null;
}

export default function FormattingToolbar({ editor }: { editor: Editor | null }) {
  const [view, setView] = useState<ToolbarState | null>(null);
  const [panel, setPanel] = useState<Panel>(null);
  const [url, setUrl] = useState("");
  const [error, setError] = useState("");
  const [tabStop, setTabStop] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);
  const rowRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const panelRef = useRef<Panel>(null);
  const rangeRef = useRef<TextRange | null>(null);
  const dismissedRef = useRef<TextRange | null>(null);
  const refreshRef = useRef<() => void>(() => {});
  const openLinkRef = useRef<() => void>(() => {});
  const viewRef = useRef(view);
  viewRef.current = view;
  const panelId = useId();
  const changePanel = useCallback((next: Panel) => {
    panelRef.current = next;
    setPanel(next);
    if (!next) rangeRef.current = null;
  }, []);

  useEffect(() => {
    if (!editor) return;
    const editorDom = editor.view.dom;
    let frame = 0;
    let selecting = false;
    let composing = false;
    const hide = () => { setView(null); changePanel(null); };
    const refresh = () => {
      if (editor.isDestroyed) return;
      const { selection, doc } = editor.state;
      if (!sameRange(dismissedRef.current, selection)) dismissedRef.current = null;
      const inside = editor.view.hasFocus() || rootRef.current?.contains(document.activeElement);
      if (!inside || selecting || composing || editor.view.composing || !(selection instanceof TextSelection)
        || selection.empty || !doc.textBetween(selection.from, selection.to).trim()) { hide(); return; }
      if (sameRange(dismissedRef.current, selection)) { hide(); return; }
      dismissedRef.current = null;
      if (rangeRef.current && !sameRange(rangeRef.current, selection)) changePanel(null);
      const formats = FORMATS.map(({ mark }) => ({ mark, active: editor.isActive(mark), enabled: editor.can().toggleMark(mark) }));
      if (!formats.some((format) => format.enabled)) { hide(); return; }
      const anchor = posToDOMRect(editor.view, selection.from, selection.to);
      const position = floatingPosition(editor, anchor,
        rootRef.current?.offsetWidth ?? 320, rootRef.current?.offsetHeight ?? 42, "above", true);
      if (!position) { hide(); return; }
      let hasMarks = false;
      let hasHighlight = false;
      let hasLink = false;
      doc.nodesBetween(selection.from, selection.to, (node) => {
        if (!node.isText) return;
        if (node.marks.length) hasMarks = true;
        if (node.marks.some((mark) => mark.type.name === "highlight")) hasHighlight = true;
        if (node.marks.some((mark) => mark.type.name === "link")) hasLink = true;
      });
      setView({ from: selection.from, to: selection.to, position, formats, hasMarks, hasHighlight, hasLink,
        highlight: editor.isActive("highlight"), highlightColor: editor.getAttributes("highlight").color ?? HIGHLIGHTS[0].color,
        canHighlight: editor.can().toggleHighlight(), link: editor.isActive("link"),
        canLink: editor.can().setLink({ href: "https://example.com" }),
        text: doc.textBetween(selection.from, selection.to, " ").slice(0, 40),
      });
    };
    const schedule = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { frame = 0; refresh(); });
    };
    refreshRef.current = refresh;
    const onTransaction = ({ transaction }: { transaction: Transaction }) => {
      if (transaction.docChanged && panelRef.current) changePanel(null);
      schedule();
    };
    const onPointerDown = (event: PointerEvent) => {
      if (rootRef.current?.contains(event.target as Node)) return;
      selecting = editorDom.contains(event.target as Node);
      hide();
    };
    const onPointerUp = () => { selecting = false; schedule(); };
    const onCompositionStart = () => { composing = true; hide(); };
    const onCompositionEnd = () => { composing = false; schedule(); };
    const onScroll = (event: Event) => {
      if (!rootRef.current?.contains(event.target as Node)) schedule();
    };
    const onKey = (event: KeyboardEvent) => {
      if (composing || editor.view.composing || event.isComposing || event.keyCode === 229) return;
      const inEditor = editorDom.contains(event.target as Node);
      const inToolbar = rootRef.current?.contains(event.target as Node);
      if (!inEditor && !inToolbar) return;
      if (inEditor && (event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === "k") {
        if ((!editor.state.selection.empty || editor.isActive("link")) && editor.can().setLink({ href: "https://example.com" })) {
          event.preventDefault(); event.stopPropagation(); openLinkRef.current();
        }
      } else if (inEditor && viewRef.current && event.altKey && event.key === "F10") {
        event.preventDefault(); event.stopPropagation();
        const target = panelRef.current ? rootRef.current?.querySelector<HTMLElement>(".wm-format-panel button:not(:disabled)")
          : rowRef.current?.querySelector<HTMLElement>("button:not(:disabled)");
        target?.focus({ preventScroll: true });
      } else if (event.key === "Escape" && viewRef.current) {
        event.preventDefault(); event.stopPropagation();
        if (panelRef.current) {
          changePanel(null);
          editor.commands.focus(undefined, { scrollIntoView: false });
        } else {
          dismissedRef.current = { from: editor.state.selection.from, to: editor.state.selection.to };
          hide();
          if (inToolbar) editor.commands.focus(undefined, { scrollIntoView: false });
        }
      }
    };
    editor.on("transaction", onTransaction);
    editor.on("focus", schedule);
    editor.on("blur", schedule);
    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("pointerup", onPointerUp, true);
    document.addEventListener("pointercancel", onPointerUp, true);
    document.addEventListener("keydown", onKey, true);
    document.addEventListener("scroll", onScroll, true);
    document.addEventListener("focusin", schedule, true);
    editorDom.addEventListener("compositionstart", onCompositionStart);
    editorDom.addEventListener("compositionend", onCompositionEnd);
    window.addEventListener("resize", schedule);
    window.addEventListener("blur", hide);
    return () => {
      cancelAnimationFrame(frame);
      editor.off("transaction", onTransaction);
      editor.off("focus", schedule);
      editor.off("blur", schedule);
      document.removeEventListener("pointerdown", onPointerDown, true);
      document.removeEventListener("pointerup", onPointerUp, true);
      document.removeEventListener("pointercancel", onPointerUp, true);
      document.removeEventListener("keydown", onKey, true);
      document.removeEventListener("scroll", onScroll, true);
      document.removeEventListener("focusin", schedule, true);
      editorDom.removeEventListener("compositionstart", onCompositionStart);
      editorDom.removeEventListener("compositionend", onCompositionEnd);
      window.removeEventListener("resize", schedule);
      window.removeEventListener("blur", hide);
    };
  }, [editor, changePanel]);

  useEffect(() => {
    if (!view || !rootRef.current) return;
    const observer = new ResizeObserver(() => refreshRef.current());
    observer.observe(rootRef.current);
    return () => observer.disconnect();
  }, [Boolean(view)]);

  useEffect(() => {
    if (panel === "link") { inputRef.current?.focus(); inputRef.current?.select(); }
    refreshRef.current();
  }, [panel]);

  const openLink = () => {
    if (!editor) return;
    if (editor.state.selection.empty && editor.isActive("link")) editor.chain().focus().extendMarkRange("link").run();
    const { selection } = editor.state;
    if (!(selection instanceof TextSelection) || selection.empty) return;
    dismissedRef.current = null;
    rangeRef.current = { from: selection.from, to: selection.to };
    setUrl(editor.isActive("link") ? editor.getAttributes("link").href ?? "" : "");
    setError("");
    changePanel("link");
    refreshRef.current();
  };
  openLinkRef.current = openLink;

  const selectedChain = () => {
    if (!editor || !view || !sameRange(rangeRef.current ?? view, editor.state.selection)) return null;
    const range = rangeRef.current ?? view;
    changePanel(null);
    return editor.chain().focus(undefined, { scrollIntoView: false }).setTextSelection({ from: range.from, to: range.to })
      .command(({ tr }) => { closeHistory(tr); return true; });
  };
  const applyLink = () => {
    const href = normalizeLink(url);
    if (!href) { setError("请输入有效的网址、邮箱链接或电话链接。"); inputRef.current?.focus(); return; }
    selectedChain()?.setLink({ href }).run();
  };
  const closePanel = () => {
    changePanel(null);
    editor?.commands.focus(undefined, { scrollIntoView: false });
  };
  const onRowKey = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    const buttons = Array.from(rowRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (index < 0) return;
    event.preventDefault();
    const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
      : (index + (event.key === "ArrowRight" ? 1 : -1) + buttons.length) % buttons.length;
    buttons[next]?.focus();
  };

  if (!view) return null;
  return (
    <div ref={rootRef} className="wm-format-popover" style={view.position}
      onMouseDown={(event) => { if ((event.target as Element).closest("button")) event.preventDefault(); }}>
      <div ref={rowRef} className="wm-format-toolbar" role="toolbar" aria-label="文字格式" onKeyDown={onRowKey}>
        {FORMATS.map((format, index) => (
          <button key={format.mark} type="button" className="wm-format-button"
            aria-label={format.label} title={`${format.label} · ${format.shortcut}`} aria-keyshortcuts={format.shortcut.replace("Ctrl", "Control")}
            aria-pressed={view.formats[index].active} disabled={!view.formats[index].enabled}
            tabIndex={tabStop === index ? 0 : -1} onFocus={() => setTabStop(index)}
            onClick={() => selectedChain()?.toggleMark(format.mark).run()}>
            <Icon name={format.mark} size={18} />
          </button>
        ))}
        <span className="wm-format-separator" />
        <button type="button" className="wm-format-button" aria-label="高亮颜色" title="高亮颜色"
          aria-pressed={view.highlight} aria-expanded={panel === "highlight"} aria-controls={panel === "highlight" ? panelId : undefined}
          disabled={!view.canHighlight} tabIndex={tabStop === 5 ? 0 : -1} onFocus={() => setTabStop(5)}
          onClick={() => {
            if (panel === "highlight") closePanel();
            else { rangeRef.current = { from: view.from, to: view.to }; changePanel("highlight"); }
          }}><Icon name="highlight" size={18} /><span className="wm-highlight-indicator" style={{ backgroundColor: view.highlightColor }} /></button>
        <button type="button" className="wm-format-button" aria-label="链接" title="链接 · Ctrl+K" aria-keyshortcuts="Control+k"
          aria-pressed={view.link} aria-expanded={panel === "link"} aria-controls={panel === "link" ? panelId : undefined}
          disabled={!view.canLink} tabIndex={tabStop === 6 ? 0 : -1} onFocus={() => setTabStop(6)}
          onClick={openLink}><Icon name="link" size={18} /></button>
        <span className="wm-format-separator" />
        <button type="button" className="wm-format-button" aria-label="清除文字格式" title="清除文字格式"
          disabled={!view.hasMarks} tabIndex={tabStop === 7 ? 0 : -1} onFocus={() => setTabStop(7)}
          onClick={() => selectedChain()?.unsetAllMarks().run()}><Icon name="eraser" size={18} /></button>
      </div>
      {panel === "highlight" && (
        <div id={panelId} className="wm-format-panel wm-highlight-panel" role="group" aria-label="高亮颜色选择">
          <div className="wm-format-panel-title">高亮颜色</div>
          <div className="wm-highlight-colors">
            {HIGHLIGHTS.map(({ label, color }) => (
              <button type="button" key={color} className="wm-highlight-swatch" style={{ backgroundColor: color }}
                title={`${label}高亮`} aria-label={`${label}高亮`} aria-pressed={view.highlight && view.highlightColor === color}
                onClick={() => selectedChain()?.setHighlight({ color }).run()}>
                {view.highlight && view.highlightColor === color && <Icon name="check" size={16} />}
              </button>
            ))}
            <button type="button" className="wm-highlight-swatch wm-highlight-clear" title="移除高亮" aria-label="移除高亮"
              disabled={!view.hasHighlight} onClick={() => selectedChain()?.unsetHighlight().run()}><Icon name="eraser" size={16} /></button>
          </div>
        </div>
      )}
      {panel === "link" && (
        <form id={panelId} className="wm-format-panel wm-link-panel" aria-label="编辑链接"
          onSubmit={(event) => { event.preventDefault(); applyLink(); }}>
          <div className="wm-link-heading"><label htmlFor={`${panelId}-url`}>链接地址</label>
            <button type="button" aria-label="取消编辑链接" title="取消 · Esc" onClick={closePanel}><Icon name="close" size={16} /></button>
          </div>
          <div className="wm-link-selection" title={view.text}>所选文字：{view.text}</div>
          <input ref={inputRef} id={`${panelId}-url`} value={url} onChange={(event) => { setUrl(event.target.value); setError(""); }}
            placeholder="粘贴或输入网址" autoComplete="off" spellCheck={false} aria-invalid={Boolean(error)}
            aria-describedby={error ? `${panelId}-error` : undefined}
            onKeyDown={(event) => { if (event.key === "Enter" && (event.nativeEvent.isComposing || event.keyCode === 229)) event.preventDefault(); }} />
          {error && <div className="wm-link-error" id={`${panelId}-error`} role="alert">{error}</div>}
          <div className="wm-link-actions">
            {view.hasLink && <button type="button" className="wm-link-remove" onClick={() => selectedChain()?.unsetLink().run()}><Icon name="unlink" size={14} />移除链接</button>}
            <button type="submit" className="wm-link-apply">应用链接<Icon name="check" size={14} /></button>
          </div>
        </form>
      )}
    </div>
  );
}
