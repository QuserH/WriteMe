import { InputRule, mergeAttributes, Node } from "@tiptap/core";
import { closeHistory } from "@tiptap/pm/history";
import { Plugin, TextSelection, type Command } from "@tiptap/pm/state";
import {
  backspaceToggle, collapsedSelectionPosition, enterToggle, indentBlock,
  insertToggleSibling, outdentBlock, toggleAllBlocks, unwrapToggle, wrapInToggle,
} from "./toggleCommands";

// Note: 稳定 contentDOM 与独立折叠状态 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md

declare module "@tiptap/core" {
  interface Commands<ReturnType> {
    toggleBlock: {
      setToggleBlock: () => ReturnType;
      unsetToggleBlock: () => ReturnType;
      toggleAllBlocks: () => ReturnType;
    };
  }
}

let contentId = 0;

export const ToggleBlock = Node.create({
  name: "toggleBlock",
  group: "block",
  content: "paragraph block*",
  defining: true,
  priority: 1000,

  addAttributes() {
    return {
      collapsed: {
        default: true,
        parseHTML: (el) => el.getAttribute("data-collapsed") === "true",
        renderHTML: (attrs) => ({ "data-collapsed": String(!!attrs.collapsed) }),
      },
    };
  },

  parseHTML() {
    return [{
      tag: 'div[data-type="toggle-block"]',
      contentElement: (element) => element.querySelector<HTMLElement>(":scope > .wm-toggle-content") ?? element,
    }];
  },

  renderHTML({ HTMLAttributes }) {
    return ["div", mergeAttributes(HTMLAttributes, { "data-type": "toggle-block", class: "wm-toggle" }),
      ["div", { class: "wm-toggle-content" }, 0]];
  },

  addCommands() {
    return {
      setToggleBlock: () => ({ state, dispatch }) => wrapInToggle(state, dispatch),
      unsetToggleBlock: () => ({ state, dispatch }) => unwrapToggle(state, dispatch),
      toggleAllBlocks: () => ({ state, dispatch }) => toggleAllBlocks(state, dispatch),
    };
  },

  addKeyboardShortcuts() {
    const run = (command: Command) => !this.editor.view.composing &&
      command(this.editor.state, this.editor.view.dispatch, this.editor.view);
    const inList = () => this.editor.isActive("listItem") || this.editor.isActive("taskItem");
    return {
      Enter: () => run(enterToggle),
      Backspace: () => run(backspaceToggle),
      Tab: () => !inList() && run(indentBlock),
      "Shift-Tab": () => !inList() && run(outdentBlock),
      "Mod-Enter": () => run(insertToggleSibling),
      "Mod-Shift-7": () => run(wrapInToggle),
      "Mod-Alt-t": () => run(toggleAllBlocks),
    };
  },

  addInputRules() {
    return [new InputRule({
      find: /^\+\s$/,
      handler: ({ range, chain }) => {
        chain().deleteRange(range).setToggleBlock().run();
      },
    })];
  },

  addProseMirrorPlugins() {
    return [new Plugin({
      appendTransaction: (transactions, _old, state) => {
        if (!transactions.some((tr) => tr.selectionSet || tr.docChanged) || !state.selection.empty) return null;
        const pos = collapsedSelectionPosition(state);
        return pos === null ? null : state.tr.setSelection(TextSelection.create(state.doc, pos));
      },
    })];
  },

  addNodeView() {
    return ({ node, editor, getPos }) => {
      let current = node;
      const dom = document.createElement("div");
      dom.className = "wm-toggle";
      dom.dataset.type = "toggle-block";
      const button = document.createElement("button");
      button.type = "button";
      button.className = "wm-toggle-caret";
      button.contentEditable = "false";
      button.innerHTML = '<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="M4 2.25 8.25 6 4 9.75Z" fill="currentColor"/></svg>';
      const contentDOM = document.createElement("div");
      contentDOM.className = "wm-toggle-content";
      contentDOM.id = `wm-toggle-content-${++contentId}`;
      button.setAttribute("aria-controls", contentDOM.id);
      dom.append(button, contentDOM);

      const update = () => {
        const collapsed = !!current.attrs.collapsed;
        dom.dataset.collapsed = String(collapsed);
        dom.dataset.empty = String(!current.firstChild?.content.size);
        dom.dataset.hasChildren = String(current.childCount > 1);
        button.setAttribute("aria-expanded", String(!collapsed));
        button.setAttribute("aria-label", collapsed ? "展开折叠块" : "收起折叠块");
        button.title = collapsed ? "展开" : "收起";
      };
      update();
      const onMouseDown = (event: MouseEvent) => event.preventDefault();
      const onClick = (event: MouseEvent) => {
        event.preventDefault();
        const pos = getPos();
        if (typeof pos !== "number" || !editor.isEditable) return;
        const { state } = editor;
        const collapsed = !current.attrs.collapsed;
        const tr = state.tr.setNodeAttribute(pos, "collapsed", collapsed);
        const titleEnd = pos + 2 + (current.firstChild?.content.size ?? 0);
        if (collapsed && state.selection.to > titleEnd && state.selection.from < pos + current.nodeSize) {
          tr.setSelection(TextSelection.create(tr.doc, titleEnd));
        }
        editor.view.dispatch(closeHistory(tr));
      };
      button.addEventListener("mousedown", onMouseDown);
      button.addEventListener("click", onClick);

      return {
        dom,
        contentDOM,
        update(next) {
          if (next.type !== current.type) return false;
          current = next;
          update();
          return true;
        },
        // 子树始终由 ProseMirror 持有。只忽略箭头和外壳自身的展示属性。
        ignoreMutation: (mutation) => mutation.type !== "selection" && !contentDOM.contains(mutation.target),
        stopEvent: (event) => event.target instanceof globalThis.Node && button.contains(event.target),
        destroy() {
          button.removeEventListener("mousedown", onMouseDown);
          button.removeEventListener("click", onClick);
        },
      };
    };
  },
});

export default ToggleBlock;
