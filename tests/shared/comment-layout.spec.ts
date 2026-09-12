import { test, expect } from "@playwright/test";
import type { Editor, JSONContent } from "@tiptap/core";
import { mkdir } from "node:fs/promises";
import type { Profile, SharedDocData, Workspace } from "../../src/shared/api";

const password = process.env.WRITEME_QA_PASSWORD || "qa-only-test-password-2026";
const sameOrigin = { Origin: new URL(process.env.WRITEME_QA_URL || "http://127.0.0.1:8791").origin };

for (const touch of [false, true]) test(`Toggle comment footer keeps text geometry and deletion targets (${touch ? "Android" : "desktop"})`, async ({ browser, request }) => {
  await mkdir("artifacts/shared-qa/screens", { recursive: true });
  const login = await request.post("/api/session", { headers: sameOrigin, data: { username: "qa-admin", password } }); expect(login.ok()).toBeTruthy();
  if ((await login.json() as Profile).needsSetup)
    expect((await request.post("/api/profile", { headers: sameOrigin, data: { publicId: "qa-admin", displayName: "林然", password } })).ok()).toBeTruthy();
  const workspace = await (await request.post("/api/workspaces", { headers: sameOrigin, data: { name: "评论排版验证 " + Date.now() } })).json() as Workspace;
  const data = await (await request.post(`/api/workspaces/${workspace.id}/documents`, { headers: sameOrigin, data: { title: "折叠评论的光标与删除" } })).json() as SharedDocData;
  const context = await browser.newContext({ storageState: await request.storageState(), viewport: touch ? { width: 390, height: 844 } : { width: 1440, height: 960 },
    isMobile: touch, hasTouch: touch, ...(touch ? { userAgent: "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36" } : {}) });
  const page = await context.newPage();
  try {
    await page.goto("/team");
    if (touch) await page.getByRole("button", { name: "显示工作区导航" }).click();
    await page.getByLabel("切换共享工作区").selectOption(workspace.id);
    await page.getByRole("button", { name: data.document.title, exact: true }).first().click();
    const body = page.getByRole("textbox", { name: "共享文档正文" });
    await body.evaluate(element => {
      const editor = (element as HTMLElement & { editor: Editor }).editor;
      const p = (text: string): JSONContent => ({ type: "paragraph", content: text ? [{ type: "text", text }] : [] });
      editor.commands.setContent({ type: "doc", content: [
        { type: "toggleBlock", attrs: { collapsed: false }, content: [p("折叠标题保持完整"), p(""), p("正文内容保持完整")] }, p("折叠块外的下一段"),
      ] });
      editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    });
    const toggle = body.locator(".wm-toggle").first(); const title = toggle.locator(":scope > .wm-toggle-content > p").first();
    const titleId = await title.getAttribute("data-block-id"); const beforeHeight = (await title.boundingBox())!.height;
    await title.click({ position: { x: 45, y: 10 } }); await expect(body.locator(".shared-comment-anchor")).toHaveCount(0);
    if (touch) { await page.getByRole("button", { name: "文档与工作区操作" }).click(); await page.getByRole("button", { name: "评论当前段落", exact: true }).click(); }
    else await body.press("Control+Alt+m");
    await page.getByRole("textbox", { name: "评论内容" }).fill("评论贴在标题下，不产生可编辑的假空行。");
    await page.getByRole("button", { name: "发送", exact: true }).click(); await page.getByRole("button", { name: "关闭评论", exact: true }).click();
    const footer = body.locator(`[data-comment-block="${titleId}"]`);
    const empty = toggle.locator(":scope > .wm-toggle-content > p").nth(1); const emptyId = await empty.getAttribute("data-block-id");
    const footerBox = (await footer.boundingBox())!; const emptyBox = (await empty.boundingBox())!;
    await page.screenshot({ path: `artifacts/shared-qa/screens/toggle-comment-footer-${touch ? "android" : "desktop"}.png` });
    expect(emptyBox.y - footerBox.y - footerBox.height, "评论后不能再生成一整行空白").toBeLessThanOrEqual(8);
    expect((await title.boundingBox())!.height, "评论不能成为标题里的文字行").toBeCloseTo(beforeHeight, 0);
    const selection = () => body.evaluate(element => (element as HTMLElement & { editor: Editor }).editor.state.selection.$from.parent.attrs.writemeId as string);
    await empty.click({ position: { x: 40, y: 10 } }); await expect.poll(selection).toBe(emptyId);
    await page.keyboard.press("ArrowUp"); await expect.poll(selection).toBe(titleId);
    const caret = await body.evaluate(() => { const range = getSelection()?.getRangeAt(0); const rect = range?.getBoundingClientRect(); return rect && { top: rect.top, bottom: rect.bottom, height: rect.height }; });
    const titleBox = (await title.boundingBox())!;
    expect(caret?.height).toBeGreaterThan(0); expect(caret!.top).toBeGreaterThanOrEqual(titleBox.y - 1); expect(caret!.bottom).toBeLessThanOrEqual(titleBox.y + titleBox.height + 1);
    await page.keyboard.press("ArrowDown"); await expect.poll(selection).toBe(emptyId);
    await page.keyboard.press("Backspace");
    await expect(title).toHaveText("折叠标题保持完整"); await expect(toggle.locator(":scope > .wm-toggle-content > p")).toHaveCount(2);
    await expect(toggle).toContainText("正文内容保持完整");
    await toggle.getByRole("button", { name: "收起折叠块", exact: true }).click(); await expect(footer).toBeVisible();
    const collapsedBox = (await toggle.boundingBox())!; const collapsedFooter = (await footer.boundingBox())!;
    expect(collapsedBox.y + collapsedBox.height - collapsedFooter.y - collapsedFooter.height).toBeLessThanOrEqual(4);
    await expect(title).toHaveText("折叠标题保持完整");
    await footer.click(); await expect(page.getByText("评论贴在标题下，不产生可编辑的假空行。", { exact: true })).toBeVisible();
  } finally { await context.close(); }
});
