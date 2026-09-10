import { Extension, type JSONContent } from "@tiptap/core";
import Highlight from "@tiptap/extension-highlight";
import Placeholder from "@tiptap/extension-placeholder";
import { Selection } from "@tiptap/pm/state";
import StarterKit from "@tiptap/starter-kit";
import TaskItem from "@tiptap/extension-task-item";
import TaskList from "@tiptap/extension-task-list";
import { EditorContent, useEditor } from "@tiptap/react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { parseContent, serializeDoc } from "./doc";
import ToggleBlock from "./Toggle";
import BlockHandle from "./BlockHandle";
import SlashMenu from "./SlashMenu";
import FormattingToolbar from "./FormattingToolbar";
import { applyBlockKind, BLOCK_TYPES, type BlockKind } from "./blockCommands";
import Icon from "../components/Icon";

// Note: 块拖拽为自研实现（无第三方 drag 扩展），见 .agents/notes/implemented/feature/2026-09-08-m2-block-drag.md
// 交互：悬停可见块 → 手柄拖动整个子树或单击打开菜单；落点同时支持同级、拖入和移出。
// Note: 内联格式与 highlight.color 的持久化 — 见 .agents/notes/implemented/feature/2026-09-08-m1-tiptap-block-editor.md

interface EditorBlockProps {
  contentRaw: string;
  onContentChange: (json: string) => void;
  placeholder?: string;
}

const TabIndent = Extension.create({
  name: "tabIndent",
  addKeyboardShortcuts() {
    return {
      // 折叠树由高优先级 ToggleBlock 处理；列表交给原生列表命令。
      Tab: ({ editor }) => {
        const type = editor.isActive("taskItem") ? "taskItem" : "listItem";
        if (editor.can().sinkListItem(type)) editor.commands.sinkListItem(type);
        return true;
      },
      "Shift-Tab": ({ editor }) => {
        const type = editor.isActive("taskItem") ? "taskItem" : "listItem";
        if (editor.can().liftListItem(type)) editor.commands.liftListItem(type);
        return true;
      },
    };
  },
});

interface BlockMenuState {
  pos: number;
  x: number;
  y: number;
}

export default function EditorBlock({
  contentRaw,
  onContentChange,
  placeholder = "开始书写，输入 / 插入内容…",
}: EditorBlockProps) {
  const { doc: initialDoc, changed } = useMemo(
    () => parseContent(contentRaw),
    [contentRaw],
  );

  const [blockMenu, setBlockMenu] = useState<BlockMenuState | null>(null);
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const editor = useEditor({
    extensions: [
      StarterKit.configure({ heading: { levels: [1, 2, 3] }, link: { openOnClick: false, defaultProtocol: "https" } }),
      Highlight.configure({ multicolor: true }),
      TaskList,
      TaskItem.configure({ nested: true }),
      Placeholder.configure({ placeholder }),
      TabIndent,
      ToggleBlock,
    ],
    content: initialDoc as JSONContent,
    editorProps: {
      attributes: { class: "writeme-editor", spellcheck: "false", role: "textbox", "aria-label": "文档正文", "aria-multiline": "true" },
    },
    onCreate: ({ editor: e }) => {
      if (changed) onContentChange(serializeDoc(e.getJSON()));
    },
    onUpdate: ({ editor: e }) => {
      onContentChange(serializeDoc(e.getJSON()));
      setBlockMenu(null);
    },
  });

  // 块菜单：Esc / 点击别处 → 关闭；滚动 → 跟随
  useEffect(() => {
    if (!blockMenu) return;
    const close = () => setBlockMenu(null);
    const onKey = (ev: KeyboardEvent) => {
      if (ev.key === "Escape") close();
    };
    const onPointerDown = (ev: PointerEvent) => {
      const t = ev.target as HTMLElement;
      if (t.closest(".wm-block-menu") || t.closest(".wm-handle")) return;
      close();
    };
    const onScroll = () => {
      if (!editor) return;
      const dom = editor.view.nodeDOM(blockMenu.pos);
      if (!(dom instanceof HTMLElement)) {
        close();
        return;
      }
      const rect = dom.getBoundingClientRect();
      setBlockMenu({ pos: blockMenu.pos, x: rect.left, y: rect.top });
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("scroll", onScroll, true);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onPointerDown, true);
      document.removeEventListener("scroll", onScroll, true);
    };
  }, [blockMenu, editor]);

  const convertBlock = useCallback(
    (kind: BlockKind) => {
      if (!editor || blockMenu === null) return;
      const node = editor.state.doc.nodeAt(blockMenu.pos);
      if (!node) return;
      const chain = editor.chain().focus().setNodeSelection(blockMenu.pos);
      if (kind === "toggle") {
        chain.setToggleBlock().run();
        setBlockMenu(null);
        return;
      }
      if (node.type.name === "toggleBlock") chain.unsetToggleBlock();
      chain.command(({ tr }) => {
        tr.setSelection(Selection.near(tr.doc.resolve(blockMenu.pos + 1)));
        return true;
      });
      applyBlockKind(chain, kind).run();
      setBlockMenu(null);
    },
    [editor, blockMenu],
  );

  const deleteBlock = useCallback(() => {
    if (!editor || blockMenu === null) return;
    editor
      .chain()
      .focus()
      .setNodeSelection(blockMenu.pos)
      .deleteSelection()
      .run();
    setBlockMenu(null);
  }, [editor, blockMenu]);

  return (
    <div className="wm-editor-scope" ref={wrapRef}>
      <EditorContent editor={editor} />
      <BlockHandle editor={editor} scopeRef={wrapRef} onOpenMenu={(pos, point) => {
        editor?.commands.setNodeSelection(pos);
        setBlockMenu({ pos, x: point.left, y: point.top });
      }} />
      <SlashMenu editor={editor} />
      <FormattingToolbar editor={editor} />

      {blockMenu && (
        <div
          className="wm-block-menu"
          style={{ left: Math.max(12, Math.min(blockMenu.x - 28, window.innerWidth - 236)),
            top: Math.max(12, Math.min(blockMenu.y + 30, window.innerHeight - 398)) }}
          role="menu"
          aria-label="块操作"
          onMouseDown={(event) => event.preventDefault()}
        >
          <div className="wm-menu-title">转换为</div>
          {editor?.state.doc.nodeAt(blockMenu.pos)?.type.name === "toggleBlock" && (
            <button type="button" role="menuitem" onClick={() => {
              editor.chain().focus().setNodeSelection(blockMenu.pos).unsetToggleBlock().run();
              setBlockMenu(null);
            }}><Icon name="toggle" size={16} /><span>取消折叠，保留内容</span></button>
          )}
          {BLOCK_TYPES.map((k) => (
            <button type="button" role="menuitem" key={k.kind} onClick={() => convertBlock(k.kind)}>
              <Icon name={k.icon} size={16} />
              {k.label}
            </button>
          ))}
          <span className="wm-block-menu-sep" />
          <button type="button" role="menuitem" className="danger" onClick={deleteBlock}>
            <Icon name="trash" size={16} />删除块
          </button>
        </div>
      )}

    </div>
  );
}
