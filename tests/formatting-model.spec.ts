import { expect, test } from "@playwright/test";
import { getSchema } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import Highlight from "@tiptap/extension-highlight";
import { EditorState, TextSelection } from "@tiptap/pm/state";
import ToggleBlock from "../src/editor/Toggle";
import { normalizeLink } from "../src/editor/FormattingToolbar";
import { slashMatch } from "../src/editor/SlashMenu";
import { parseContent, serializeDoc } from "../src/editor/doc";
import { document, paragraph } from "./fixtures";

const schema = getSchema([StarterKit, Highlight.configure({ multicolor: true }), ToggleBlock]);

test("链接接受网址、邮箱和电话，拒绝脚本、空地址与畸形链接", () => {
  for (const [input, result] of [
    [" support.craft.do/zh-Hans/ ", "https://support.craft.do/zh-Hans/"],
    ["http://localhost:1420/test", "http://localhost:1420/test"],
    ["mailto:hello@example.com", "mailto:hello@example.com"],
    ["tel:+8613800000000", "tel:+8613800000000"],
    ["javascript:alert(1)", null], ["data:text/html,hello", null],
    ["https://", null], ["hello world", null], ["mailto:", null], ["", null],
  ]) expect(normalizeLink(input!)).toBe(result);
});

test("斜杠匹配按块内实际位置读取，保留后方内容，不在代码或正文中部触发", () => {
  for (const [text, offset, type, query] of [
    ["/折叠后方正文", 3, "paragraph", "折叠"],
    ["/heading 2", 10, "paragraph", "heading 2"],
    ["正文/todo", 7, "paragraph", null],
    ["/code", 5, "codeBlock", null],
    ["//", 2, "paragraph", null],
  ] as const) {
    let state = EditorState.create({ schema, doc: schema.nodeFromJSON(document({ ...paragraph(text), type })) });
    state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, offset + 1)));
    const match = slashMatch(state);
    expect(match?.query ?? null).toBe(query);
    if (query !== null) expect(match).toEqual({ from: 1, to: offset + 1, query });
  }
});

test("高亮与链接在旧折叠迁移、JSON 保存和再次解析后保持", () => {
  const rich = { type: "paragraph", content: [{ type: "text", text: "有格式的原正文", marks: [
    { type: "bold" }, { type: "highlight", attrs: { color: "#dceafb" } },
    { type: "link", attrs: { href: "https://example.com" } },
  ] }] };
  const migrated = parseContent(JSON.stringify(document({ type: "toggleBlock", attrs: { title: "", collapsed: true }, content: [rich] })));
  expect(migrated.changed).toBe(true);
  expect(migrated.doc.content?.[0]?.content?.[1]).toEqual(rich);
  const node = schema.nodeFromJSON(migrated.doc);
  expect(node.check()).toBeUndefined();
  const saved = serializeDoc(node.toJSON());
  expect(parseContent(saved)).toEqual({ changed: false, doc: node.toJSON() });
});
