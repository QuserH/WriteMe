import { Extension, type JSONContent } from "@tiptap/core";
import { Plugin, PluginKey, type EditorState } from "@tiptap/pm/state";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import type { LiveDocument } from "./live";
import { avatarUrl, relativeTime, type Member } from "./api";
import type { ParagraphTarget } from "./SharedEditor";

export function findNode(root: JSONContent, id: string): JSONContent | null {
  if (root.attrs?.writemeId === id) return root;
  for (const child of root.content ?? []) { const found = findNode(child, id); if (found) return found; }
  return null;
}
export function paragraphThreadIds(node: JSONContent | null): Set<string> {
  const ids = new Set<string>((node?.attrs?.writemeCommentIds ?? []) as string[]);
  for (const child of node?.content ?? []) for (const mark of child.marks ?? []) if (mark.type === "comment") for (const id of (mark.attrs?.ids ?? []) as string[]) ids.add(id);
  return ids;
}

// Note: 只有已有评论的段落展示摘要，装饰位于文字块外，避免正文尾部产生假空行 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
export function paragraphDecorations(live: LiveDocument, open: (target: ParagraphTarget) => void, people: () => Member[], selected: () => string | undefined) {
  const key = new PluginKey<DecorationSet>("shared-paragraph-comments");
  const decorations = (state: EditorState) => {
    const threads = new Map(live.replica.readThreads().map(thread => [thread.id, thread]));
    const current = state.selection.$from.parent.attrs.writemeId as string | undefined; const result: Decoration[] = [];
    state.doc.descendants((node, position, parent, index) => {
      if (!node.isTextblock || !node.attrs.writemeId) return;
      const id = String(node.attrs.writemeId); const ids = paragraphThreadIds(node.toJSON());
      const messages = [...ids].flatMap(id => threads.get(id)?.messages ?? []).filter(message => !message.deleted);
      if (selected() === id) result.push(Decoration.node(position, position + node.nodeSize, { class: "shared-comment-selected" }));
      if (!messages.length) return;
      result.push(Decoration.node(position, position + node.nodeSize, { class: "shared-commented-block" }));
      result.push(Decoration.widget(position + node.nodeSize, () => {
        const row = document.createElement("div"); row.className = "shared-comment-anchor" + (parent?.type.name === "toggleBlock" && index === 0 ? " toggle-title-comments" : ""); row.contentEditable = "false";
        row.dataset.commentFor = id;
        const button = document.createElement("button"); button.type = "button"; button.className = "shared-paragraph-comment has-comments"; button.dataset.commentBlock = id;
        button.setAttribute("aria-label", current === id ? "评论当前段落" : `查看段落评论：${node.textContent.slice(0, 24) || "空白段落"}`);
        button.title = "查看评论与回复";
        const authors = [...new Map(messages.map(message => [message.authorId ?? message.author, message])).values()].slice(0, 3);
        const avatars = document.createElement("span"); avatars.className = "shared-comment-avatars"; avatars.setAttribute("aria-hidden", "true");
        for (const message of authors) {
          const person = live.peers.find(person => person.accountId === message.authorId) ?? people().find(person => person.accountId === message.authorId);
          const slot = document.createElement("span"); slot.className = "shared-avatar"; const color = person?.avatar?.color ?? "#6C82AD"; slot.style.background = color + "18"; slot.style.color = color;
          const initial = person?.avatar?.text || [...(person?.displayName ?? message.author)][0] || "W"; slot.textContent = initial;
          const url = avatarUrl(message.authorId, person?.avatar);
          if (url) { const image = document.createElement("img"); image.src = url; image.alt = ""; image.onerror = () => { slot.textContent = initial; }; slot.replaceChildren(image); }
          avatars.append(slot);
        }
        if (authors.length) button.append(avatars);
        const label = document.createElement("span"); label.textContent = `${messages.length} 条评论`; button.append(label);
        const time = document.createElement("time"); time.textContent = relativeTime(Math.max(...messages.map(message => message.createdAt))); button.append(time);
        row.addEventListener("mousedown", event => event.preventDefault());
        button.addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); open({ id, text: node.textContent }); });
        row.append(button); return row;
      }, { side: -1, ignoreSelection: true, stopEvent: () => true }));
    });
    return DecorationSet.create(state.doc, result);
  };
  return Extension.create({ name: "sharedParagraphComments", addProseMirrorPlugins: () => [new Plugin({ key,
    state: { init: (_, state) => decorations(state), apply: (transaction, previous, _old, state) => transaction.docChanged || transaction.selectionSet || transaction.getMeta("comment-status") ? decorations(state) : previous },
    props: { decorations: state => key.getState(state) ?? null },
  })] });
}
