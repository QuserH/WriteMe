import type { JSONContent } from "@tiptap/core";
import { expect, type Locator, type Page } from "@playwright/test";

export const paragraph = (text = ""): JSONContent => ({
  type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}),
});
export const toggle = (title: string, children: JSONContent[] = [], collapsed = false): JSONContent => ({
  type: "toggleBlock", attrs: { collapsed }, content: [paragraph(title), ...children],
});
export const document = (...content: JSONContent[]): JSONContent => ({ type: "doc", content });

/** 每个测试使用隔离的浏览器存储，不读写用户桌面 SQLite。刷新保留当前保存结果。 */
export async function openFixture(page: Page, doc: JSONContent) {
  page.on("pageerror", (error) => { throw error; });
  await page.addInitScript((content) => {
    if (localStorage.getItem("writeme-test-seeded")) return;
    const now = Date.now();
    localStorage.setItem("writeme-demo-v1", JSON.stringify([
      { id: "fixture", title: "折叠块回归用例", content: JSON.stringify(content), created_at: now, updated_at: now },
      { id: "other", title: "另一篇文档", content: JSON.stringify({ type: "doc", content: [{ type: "paragraph" }] }), created_at: now - 1000, updated_at: now - 1000 },
    ]));
    localStorage.setItem("writeme-test-seeded", "true");
  }, doc);
  await page.goto("/");
  await expect(page.getByRole("textbox", { name: "文档正文" })).toBeVisible();
}

export async function savedDocument(page: Page): Promise<JSONContent> {
  return page.evaluate(() => {
    const docs = JSON.parse(localStorage.getItem("writeme-demo-v1") ?? "[]");
    return JSON.parse(docs.find((doc: { id: string }) => doc.id === "fixture").content);
  });
}

export const toggleByTitle = (page: Page, title: string) =>
  page.locator(".writeme-editor .wm-toggle").filter({
    has: page.locator(":scope > .wm-toggle-content > p:first-child")
      .filter({ hasText: new RegExp(`^${title.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`) }),
  });

export const titleOf = (node: Locator) => node.locator(":scope > .wm-toggle-content > p:first-child");

/** 使用浏览器真实 Selection，等待异步 selectionchange 进入 ProseMirror。 */
export async function selectText(page: Page, element: Locator, text: string) {
  await element.scrollIntoViewIfNeeded();
  await element.evaluate((el, value) => {
    (el.closest('[contenteditable="true"]') as HTMLElement).focus();
    const offset = el.textContent?.indexOf(value) ?? -1;
    if (offset < 0) throw new Error(`找不到待选择文字：${value}`);
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    const range = document.createRange();
    let position = 0;
    let started = false;
    while (walker.nextNode()) {
      const node = walker.currentNode;
      const length = node.textContent?.length ?? 0;
      if (!started && offset < position + length) { range.setStart(node, offset - position); started = true; }
      if (started && offset + value.length <= position + length) { range.setEnd(node, offset + value.length - position); break; }
      position += length;
    }
    const selection = window.getSelection();
    selection?.removeAllRanges();
    selection?.addRange(range);
  }, text);
  await expect.poll(() => page.locator(".tiptap").evaluate((el) => {
    const { state } = (el as HTMLElement & { editor: import("@tiptap/core").Editor }).editor;
    return state.doc.textBetween(state.selection.from, state.selection.to);
  })).toBe(text);
}

export async function beginDrag(page: Page, source: Locator) {
  await source.hover();
  const handle = page.getByRole("button", { name: "拖动块或打开块菜单" });
  await expect(handle).toHaveCSS("opacity", "1");
  const rect = await handle.boundingBox();
  if (!rect) throw new Error("拖动手柄不可见");
  await page.mouse.move(rect.x + rect.width / 2, rect.y + rect.height / 2);
  await page.mouse.down();
}

export async function dragInto(page: Page, source: Locator, destination: Locator) {
  const rect = await titleOf(destination).boundingBox();
  if (!rect) throw new Error("目标折叠块不可见");
  await beginDrag(page, source);
  await page.mouse.move(rect.x + 80, rect.y + rect.height / 2, { steps: 12 });
  await expect(page.locator(".wm-drop-line")).toHaveAttribute("data-kind", "inside");
  await page.mouse.up();
}
