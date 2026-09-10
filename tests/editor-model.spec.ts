import { expect, test } from "@playwright/test";
import { getSchema } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import { EditorState, TextSelection } from "@tiptap/pm/state";
import { history, undo } from "@tiptap/pm/history";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import ToggleBlock from "../src/editor/Toggle";
import { blockAt, moveBlockTransaction, type BlockDestination } from "../src/editor/blockTree";
import { backspaceToggle, enterToggle, wrapInToggle } from "../src/editor/toggleCommands";
import { parseContent } from "../src/editor/doc";
import { document, paragraph, toggle } from "./fixtures";

const schema = getSchema([StarterKit, ToggleBlock]);
const stateOf = (doc: ReturnType<typeof document>) => EditorState.create({ schema, doc: schema.nodeFromJSON(doc), plugins: [history()] });
const find = (doc: ProseMirrorNode, title: string) => {
  let found = -1;
  doc.descendants((node, pos) => {
    if ((node.type.name === "toggleBlock" ? node.firstChild?.textContent : node.textContent) === title && blockAt(doc, pos)) found = pos;
  });
  if (found < 0) throw new Error(`找不到块：${title}`);
  return found;
};

test("旧版空标题不能吃掉第一段正文，迁移可重复执行", () => {
  const old = document({ type: "toggleBlock", attrs: { title: "", collapsed: true }, content: [paragraph("需要保留的正文"),
    { type: "toggleBlock", attrs: { title: "子标题", collapsed: false } }] });
  const first = parseContent(JSON.stringify(old));
  expect(first.changed).toBe(true);
  expect(first.doc.content?.[0].content?.map((node) => node.type)).toEqual(["paragraph", "paragraph", "toggleBlock"]);
  expect(first.doc.content?.[0].content?.[0]).toEqual(paragraph());
  expect(first.doc.content?.[0].content?.[1]).toEqual(paragraph("需要保留的正文"));
  expect(schema.nodeFromJSON(first.doc).check()).toBeUndefined();
  expect(parseContent(JSON.stringify(first.doc))).toEqual({ doc: first.doc, changed: false });
});

test("标题中途回车分割富文本，保留已有子树", () => {
  const doc = document(toggle("父标题", [toggle("旧子项", [paragraph("旧正文")], true)]));
  doc.content![0].content![0] = { type: "paragraph", content: [
    { type: "text", text: "前半", marks: [{ type: "bold" }] },
    { type: "text", text: "后半", marks: [{ type: "italic" }] },
  ] };
  let state = stateOf(doc);
  state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, 4)));
  expect(enterToggle(state, (tr) => { state = state.apply(tr); })).toBe(true);
  expect(state.doc.firstChild?.firstChild?.textContent).toBe("前半");
  expect(state.doc.firstChild?.child(1).firstChild?.textContent).toBe("后半");
  expect(state.doc.firstChild?.child(1).firstChild?.firstChild?.marks[0].type.name).toBe("italic");
  expect(state.doc.firstChild?.child(2).textContent).toBe("旧子项旧正文");
  expect(state.doc.check()).toBeUndefined();
});

test("转换标题保留粗体、链接和软换行，退格取消折叠不丢正文", () => {
  let state = stateOf(document({ type: "paragraph", content: [
    { type: "text", text: "标题", marks: [{ type: "bold" }, { type: "link", attrs: { href: "https://example.com" } }] },
    { type: "hardBreak" }, { type: "text", text: "第二行" },
  ] }));
  const original = state.doc.toJSON();
  expect(wrapInToggle(state, (tr) => { state = state.apply(tr); })).toBe(true);
  expect(state.doc.firstChild?.firstChild?.toJSON()).toEqual(original.content[0]);
  state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, 2)));
  expect(backspaceToggle(state, (tr) => { state = state.apply(tr); })).toBe(true);
  expect(state.doc.toJSON()).toEqual(original);
});

test("首块 pos=0 可移入后方折叠块，一次撤销恢复原树", () => {
  let state = stateOf(document(paragraph("首块"), toggle("目标", [paragraph("原子项")], true), paragraph("尾块")));
  const original = state.doc.toJSON();
  const destination = find(state.doc, "目标");
  const tr = moveBlockTransaction(state, 0, { parentPos: destination, index: 1 });
  expect(tr).not.toBeNull();
  state = state.apply(tr!);
  expect(state.doc.firstChild?.attrs.collapsed).toBe(false);
  expect(state.doc.firstChild?.child(1).textContent).toBe("首块");
  expect(state.doc.firstChild?.child(2).textContent).toBe("原子项");
  expect(undo(state, (transaction) => { state = state.apply(transaction); })).toBe(true);
  expect(state.doc.toJSON()).toEqual(original);
});

test("移出最后一个子项后父级保持合法，尾部空段落完整保留", () => {
  const state = stateOf(document(toggle("父级", [toggle("子级", [paragraph("内容")], true)]), paragraph(), paragraph()));
  const tr = moveBlockTransaction(state, find(state.doc, "子级"), { parentPos: -1, index: 1 });
  expect(tr).not.toBeNull();
  expect(tr!.doc.firstChild?.childCount).toBe(1);
  expect(tr!.doc.child(1).attrs.collapsed).toBe(true);
  expect(tr!.doc.childCount).toBe(4);
  expect(tr!.doc.check()).toBeUndefined();
});

test("拒绝父级拖进后代和拖走标题，不修改原树", () => {
  const state = stateOf(document(toggle("父级", [toggle("子级", [paragraph("内容")])]), paragraph("结尾")));
  expect(moveBlockTransaction(state, 0, { parentPos: find(state.doc, "子级"), index: 1 })).toBeNull();
  expect(moveBlockTransaction(state, 1, { parentPos: -1, index: 1 })).toBeNull();
  expect(moveBlockTransaction(state, find(state.doc, "子级"), { parentPos: 0, index: 0 })).toBeNull();
});

test("跨三个层级的所有合法移动都保持文字、格式、空行和节点数量", () => {
  const rich = { type: "paragraph", content: [{ type: "text", text: "富文本", marks: [{ type: "bold" }] }] };
  const state = stateOf(document(paragraph("开头"), toggle("甲", [paragraph("甲一"),
    toggle("甲二", [rich, toggle("甲三", [paragraph("深层")], true)]), paragraph()]),
    toggle("乙", [paragraph("乙一")], true), paragraph("结尾"), paragraph()));
  const sources: number[] = [];
  const destinations: BlockDestination[] = [];
  for (let index = 0; index <= state.doc.childCount; index++) destinations.push({ parentPos: -1, index });
  state.doc.descendants((node, pos) => {
    if (blockAt(state.doc, pos)) sources.push(pos);
    if (node.type.name === "toggleBlock") {
      for (let index = 1; index <= node.childCount; index++) destinations.push({ parentPos: pos, index });
    }
  });
  const inventory = (doc: ProseMirrorNode) => {
    const leaves: string[] = [];
    doc.descendants((node) => {
      if (node.isTextblock) { leaves.push(JSON.stringify(node.toJSON())); return false; }
    });
    return leaves.sort();
  };
  let moved = 0;
  for (const source of sources) for (const destination of destinations) {
    const tr = moveBlockTransaction(state, source, destination);
    if (!tr) continue;
    moved++;
    expect(tr.doc.check()).toBeUndefined();
    expect(inventory(tr.doc)).toEqual(inventory(state.doc));
  }
  expect(moved).toBeGreaterThan(100);
});
