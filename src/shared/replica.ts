import * as Y from "yjs";
import type { JSONContent } from "@tiptap/core";
import type { Thread, Message } from "./api";
import { uuid } from "./api";

// Note: 与 C# SharedDocumentReplica 共享稳定块、UTF-16 文字和逐条回复 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
export const textTypes = new Set(["paragraph", "heading", "codeBlock"]);
export const containerTypes = new Set(["doc", "toggleBlock", "blockquote", "bulletList", "orderedList", "taskList", "listItem", "taskItem", "table", "tableRow", "tableCell", "tableHeader", "columnList", "column"]);
export const localOrigin = "writeme-local";
export const remoteOrigin = "writeme-remote";
const map = (value: unknown): Y.Map<unknown> | undefined => value instanceof Y.Map ? value : undefined;
const array = (value: unknown): Y.Array<string> | undefined => value instanceof Y.Array ? value as Y.Array<string> : undefined;
const text = (value: unknown): Y.Text | undefined => value instanceof Y.Text ? value : undefined;
function nested<T>(parent: Y.Map<unknown>, key: string, create: () => T): T { if (!parent.has(key)) parent.set(key, create()); return parent.get(key) as T; }
function put(parent: Y.Map<unknown>, key: string, value: string) { if (parent.get(key) !== value) parent.set(key, value); }
function replaceMap(parent: Y.Map<unknown>, values: Record<string, string>) {
  for (const key of parent.keys()) if (!(key in values)) parent.delete(key);
  for (const [key, value] of Object.entries(values)) put(parent, key, value);
}
function replaceArray(target: Y.Array<string>, next: string[]) {
  const old = target.toArray(); let start = 0; let suffix = 0;
  while (start < old.length && start < next.length && old[start] === next[start]) start++;
  while (suffix < old.length - start && suffix < next.length - start && old[old.length - suffix - 1] === next[next.length - suffix - 1]) suffix++;
  if (old.length - start - suffix > 0) target.delete(start, old.length - start - suffix);
  if (next.length - start - suffix > 0) target.insert(start, next.slice(start, next.length - suffix));
}
export function replaceText(target: Y.Text, value: string) {
  const old = target.toString(); if (old === value) return;
  let start = 0; let suffix = 0;
  while (start < old.length && start < value.length && old[start] === value[start]) start++;
  if (start > 0 && /[\uD800-\uDBFF]/.test(old[start - 1]) && /[\uDC00-\uDFFF]/.test(old[start] ?? "")) start--;
  while (suffix < old.length - start && suffix < value.length - start && old[old.length - suffix - 1] === value[value.length - suffix - 1]) suffix++;
  if (suffix > 0 && /[\uDC00-\uDFFF]/.test(old[old.length - suffix]) && /[\uD800-\uDBFF]/.test(old[old.length - suffix - 1] ?? "")) suffix--;
  if (old.length - start - suffix > 0) target.delete(start, old.length - start - suffix);
  if (value.length - start - suffix > 0) target.insert(start, value.slice(start, value.length - suffix));
}
const runText = (run: JSONContent) => run.type === "text" ? run.text ?? "" : run.type === "hardBreak" ? "\u2028" : "\uFFFC";
function runAttrs(run: JSONContent): Record<string, string> {
  const attrs: Record<string, string> = {};
  for (const mark of run.marks ?? []) {
    if (mark.type === "comment") for (const id of (mark.attrs?.ids ?? []) as string[]) attrs[`c:${id}`] = "true";
    else attrs[`m:${mark.type}`] = JSON.stringify({ attrs: mark.attrs ?? {}, extra: {} });
  }
  if (run.type !== "text" && run.type !== "hardBreak") attrs.inline = JSON.stringify(run);
  return attrs;
}
function replaceRichText(target: Y.Text, node: JSONContent) {
  replaceText(target, (node.content ?? []).map(runText).join(""));
  let offset = 0;
  const actual = (target.toDelta() as { insert: unknown; attributes?: Record<string, unknown> }[]).map(chunk => { const value = { start: offset, end: offset + String(chunk.insert).length, attrs: chunk.attributes ?? {} }; offset = value.end; return value; });
  offset = 0;
  for (const run of node.content ?? []) {
    const end = offset + runText(run).length; const desired = runAttrs(run);
    for (const current of actual.filter(chunk => chunk.end > offset && chunk.start < end)) {
      const changes: Record<string, string | null> = {};
      for (const key of Object.keys(current.attrs)) if (!(key in desired)) changes[key] = null;
      for (const [key, value] of Object.entries(desired)) if (current.attrs[key] !== value) changes[key] = value;
      if (Object.keys(changes).length) { const start = Math.max(offset, current.start); target.format(start, Math.min(end, current.end) - start, changes); }
    }
    offset = end;
  }
}
function placeholderId(parent: string): string {
  let hex = "";
  for (let seed = 0; seed < 4; seed++) { let hash = (2166136261 ^ seed) >>> 0; for (const char of `${parent}:empty:v1`) hash = Math.imul(hash ^ char.charCodeAt(0), 16777619) >>> 0; hex += hash.toString(16).padStart(8, "0"); }
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
function jsonAttrs(source: Y.Map<unknown> | undefined): Record<string, unknown> {
  return Object.fromEntries([...(source?.entries() ?? [])].map(([key, value]) => [key, JSON.parse(String(value)) as unknown]));
}
export class WebReplica {
  readonly doc = new Y.Doc();
  readonly blocks = this.doc.getMap<Y.Map<unknown>>("blocks");
  readonly threads = this.doc.getMap<Y.Map<unknown>>("threads");
  readonly title = this.doc.getText("title");
  readonly meta = this.doc.getMap<string>("meta");
  readonly undo = new Y.UndoManager([this.blocks, this.threads, this.title], { trackedOrigins: new Set([localOrigin]), captureTimeout: 750 });
  constructor(state?: Uint8Array) { if (state) Y.applyUpdate(this.doc, state, remoteOrigin); }
  apply(update: Uint8Array) { Y.applyUpdate(this.doc, update, remoteOrigin); }
  state() { return Y.encodeStateAsUpdate(this.doc); }
  vector() { return Y.encodeStateVector(this.doc); }
  difference(vector?: Uint8Array) { return Y.encodeStateAsUpdate(this.doc, vector); }
  setTitle(value: string) { if (value.length <= 500) this.doc.transact(() => replaceText(this.title, value), localOrigin); }
  blockText(id: string) { return text(this.blocks.get(id)?.get("text")); }
  writeRoot(root: JSONContent) {
    const seen = new Set<string>();
    const prepare = (node: JSONContent): JSONContent => {
      if (node.type === "sharedUnsupported") return { ...node.attrs?.raw as JSONContent, attrs: { ...(node.attrs?.raw as JSONContent)?.attrs, writemeId: node.attrs?.writemeId } };
      const result: JSONContent = { ...node, attrs: { ...node.attrs } };
      if (node.type !== "doc") {
        const id = typeof node.attrs?.writemeId === "string" && !seen.has(node.attrs.writemeId) ? node.attrs.writemeId : uuid(); seen.add(id); result.attrs!.writemeId = id;
      }
      if (containerTypes.has(node.type ?? "")) result.content = (node.content ?? []).map(prepare);
      return result;
    };
    const normalized = prepare(root); seen.clear();
    this.doc.transact(() => {
      this.meta.set("version", "1");
      const visit = (node: JSONContent, key: string, parent: string) => {
        seen.add(key); const block = nested(this.blocks as unknown as Y.Map<unknown>, key, () => new Y.Map<unknown>());
        put(block, "type", node.type ?? "paragraph"); put(block, "parent", parent); put(block, "deleted", "false");
        const attrs = Object.fromEntries(Object.entries(node.attrs ?? {}).filter(([key, value]) => !["writemeId", "writemeComments", "writemeCommentIds"].includes(key) && value !== null && value !== undefined).map(([key, value]) => [key, JSON.stringify(value)]));
        replaceMap(nested(block, "attrs", () => new Y.Map()), attrs);
        replaceMap(nested(block, "anchors", () => new Y.Map()), Object.fromEntries(((node.attrs?.writemeCommentIds ?? []) as string[]).map(id => [id, "true"])));
        if (!block.has("extra")) block.set("extra", "{}");
        if (textTypes.has(node.type ?? "")) replaceRichText(nested(block, "text", () => new Y.Text()), node);
        else if (containerTypes.has(node.type ?? "")) {
          replaceArray(nested(block, "children", () => new Y.Array<string>()), (node.content ?? []).map(child => child.attrs!.writemeId as string));
          for (const child of node.content ?? []) visit(child, child.attrs!.writemeId as string, key);
        } else put(block, "raw", JSON.stringify(node));
      };
      visit(normalized, "root", "");
      for (const [key, block] of this.blocks) if (!seen.has(key)) put(block, "deleted", "true");
    }, localOrigin);
  }
  readThreads(): Thread[] {
    return [...this.threads.values()].map(thread => {
      const info = JSON.parse(String(thread.get("info"))) as Omit<Thread, "messages"> & { firstMessageId: string };
      const remaining = [...(map(thread.get("messages"))?.values() ?? [])].map(value => JSON.parse(String(value)) as Message).sort((a, b) => a.createdAt - b.createdAt || a.id.localeCompare(b.id));
      const first = remaining.find(message => message.id === info.firstMessageId); if (!first) throw new Error("评论数据不完整");
      remaining.splice(remaining.indexOf(first), 1); const messages = [first]; const seen = new Set([first.id]);
      while (remaining.length) { const index = remaining.findIndex(message => message.replyTo && seen.has(message.replyTo)); if (index < 0) throw new Error("回复关系无效"); const [next] = remaining.splice(index, 1); messages.push(next); seen.add(next.id); }
      return { ...info, messages };
    }).sort((a, b) => a.messages[0].createdAt - b.messages[0].createdAt || a.id.localeCompare(b.id));
  }
  writeThreads(threads: Thread[]) {
    this.undo.stopCapturing();
    this.doc.transact(() => {
      const ids = new Set(threads.map(thread => thread.id)); for (const id of this.threads.keys()) if (!ids.has(id)) this.threads.delete(id);
      for (const thread of threads) {
        const block = nested(this.threads as unknown as Y.Map<unknown>, thread.id, () => new Y.Map<unknown>());
        put(block, "info", JSON.stringify({ id: thread.id, quote: thread.quote, anchored: thread.anchored, wholeBlock: thread.wholeBlock, firstMessageId: thread.messages[0].id }));
        replaceMap(nested(block, "messages", () => new Y.Map()), Object.fromEntries(thread.messages.map(message => [message.id, JSON.stringify(message)])));
      }
    }, localOrigin);
  }
  readRoot(): JSONContent {
    if (this.meta.get("version") !== "1") throw new Error("请更新 WriteME，共享文档版本不受支持");
    const live = new Map([...this.blocks.entries()].filter(([, block]) => block.get("deleted") !== "true"));
    const parents = new Map([...live.entries()].filter(([id]) => id !== "root").map(([id, block]) => [id, String(block.get("parent") ?? "root")]));
    for (const key of parents.keys()) {
      const chain: string[] = []; let current = key;
      while (parents.has(current) && parents.get(current) !== "root") {
        if (chain.includes(current)) { parents.set(chain.slice(chain.indexOf(current)).sort()[0], "root"); break; }
        if (chain.length > 80) throw new Error("文档层级超过限制");
        const parent = parents.get(current)!; if (!live.has(parent)) break; chain.push(current); current = parent;
      }
    }
    const visit = (key: string): JSONContent => {
      const block = live.get(key); if (!block) throw new Error("文档内容缺失"); const type = String(block.get("type"));
      const attrs = jsonAttrs(map(block.get("attrs"))); if (key !== "root") attrs.writemeId = key;
      const anchors = [...(map(block.get("anchors"))?.entries() ?? [])].filter(([, value]) => value === "true").map(([id]) => id);
      if (anchors.length) attrs.writemeCommentIds = anchors;
      const node: JSONContent = { type, attrs };
      if (textTypes.has(type)) {
        node.content = [];
        for (const chunk of text(block.get("text"))?.toDelta() ?? []) {
          const marks: NonNullable<JSONContent["marks"]> = []; const ids: string[] = [];
          for (const [key, value] of Object.entries(chunk.attributes ?? {}).sort(([a], [b]) => a.localeCompare(b))) {
            if (key.startsWith("c:") && value === "true") ids.push(key.slice(2));
            else if (key.startsWith("m:")) { const parsed = JSON.parse(String(value)) as { attrs: Record<string, unknown> }; marks.push({ type: key.slice(2), attrs: parsed.attrs }); }
          }
          if (ids.length) marks.push({ type: "comment", attrs: { ids } });
          const value = String(chunk.insert); const inline = chunk.attributes?.inline;
          if (inline) { for (let i = 0; i < value.length; i++) node.content.push({ ...JSON.parse(String(inline)) as JSONContent, marks }); }
          else value.split("\u2028").forEach((part, i) => { if (i) node.content!.push({ type: "hardBreak", marks }); if (part) node.content!.push({ type: "text", text: part, marks }); });
        }
      } else if (containerTypes.has(type)) {
        const ordered = array(block.get("children"))?.toArray() ?? [];
        const ids = [...new Set([...ordered, ...[...parents].filter(([, parent]) => parent === key).map(([id]) => id).sort()])].filter(id => id !== key && live.has(id) && parents.get(id) === key);
        node.content = ids.map(visit);
        if ((!node.content.length && ["doc", "toggleBlock", "listItem", "taskItem", "tableCell", "tableHeader", "column"].includes(type)) || (["toggleBlock", "listItem", "taskItem"].includes(type) && node.content[0]?.type !== "paragraph"))
          node.content.unshift({ type: "paragraph", attrs: { writemeId: placeholderId(key) } });
        if (["bulletList", "orderedList", "taskList", "blockquote"].includes(type) && !node.content.length) node.content = [];
      } else if (block.get("raw")) Object.assign(node, JSON.parse(String(block.get("raw"))), { attrs });
      return node;
    };
    const root = visit("root"); root.attrs = { ...root.attrs, writemeComments: { version: 1, threads: this.readThreads() } }; return root;
  }
  dispose() { this.undo.destroy(); this.doc.destroy(); }
}
