import { expect, test } from "@playwright/test";
import { document, openFixture, paragraph, savedDocument, titleOf, toggle, toggleByTitle } from "./fixtures";

test("中文搜索折叠块，只移除命令范围并保留标题后方的富文本", async ({ page }) => {
  await openFixture(page, document(toggle("父级", [{ type: "paragraph", content: [
    { type: "text", text: "保留粗体内容", marks: [{ type: "bold" }] },
  ] }]), paragraph("末尾")));
  const line = toggleByTitle(page, "父级").locator(":scope > .wm-toggle-content > p").nth(1);
  await line.click();
  await page.keyboard.press("Home");
  await page.keyboard.insertText("/折叠");
  const menu = page.getByRole("listbox", { name: "斜杠菜单" });
  await expect(menu.getByRole("option")).toHaveCount(1);
  await expect(menu.getByRole("option", { name: "折叠块 可收起" })).toHaveAttribute("aria-selected", "true");
  await page.keyboard.press("Enter");
  const title = titleOf(toggleByTitle(page, "保留粗体内容"));
  await expect(title.locator("strong")).toHaveText("保留粗体内容");
  await expect(toggleByTitle(page, "父级").locator(".wm-toggle")).toHaveCount(1);
  await expect(page.locator(".tiptap")).not.toContainText("/折叠");
  await page.keyboard.press("Control+z");
  await expect(line).toHaveText("/折叠保留粗体内容");
  await expect(toggleByTitle(page, "父级").locator(".wm-toggle")).toHaveCount(0);
});

test("英文搜索过滤后上下循环选择，转换为指定标题等级且一次撤销", async ({ page }) => {
  await openFixture(page, document(paragraph("保留正文")));
  const line = page.locator(".tiptap > p").first();
  await line.click();
  await page.keyboard.press("Home");
  await page.keyboard.type("/heading");
  const menu = page.getByRole("listbox", { name: "斜杠菜单" });
  await expect(menu.getByRole("option")).toHaveCount(3);
  await page.keyboard.press("ArrowUp");
  await expect(menu.getByRole("option", { name: "标题3 小标题" })).toHaveAttribute("aria-selected", "true");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await expect(menu.getByRole("option", { name: "标题2 中标题" })).toHaveAttribute("aria-selected", "true");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 2, name: "保留正文" })).toBeVisible();
  await page.keyboard.press("Control+z");
  await expect(page.locator(".tiptap > p").first()).toHaveText("/heading保留正文");
  await expect(page.locator(".tiptap h2")).toHaveCount(0);
});

test("搜索变化重置高亮，列表关键词和精确等级共用命令", async ({ page }) => {
  await openFixture(page, document(paragraph()));
  await page.getByRole("textbox", { name: "文档正文" }).pressSequentially("/list");
  const menu = page.getByRole("listbox", { name: "斜杠菜单" });
  await expect(menu.getByRole("option")).toHaveCount(3);
  await page.keyboard.press("ArrowUp");
  await expect(menu.getByRole("option", { name: "待办清单 任务" })).toHaveAttribute("aria-selected", "true");
  for (let i = 0; i < 4; i++) await page.keyboard.press("Backspace");
  await page.keyboard.type("h2");
  await expect(menu.getByRole("option")).toHaveCount(1);
  await expect(menu.getByRole("option", { name: "标题2 中标题" })).toHaveAttribute("aria-selected", "true");
  await page.keyboard.press("Enter");
  await page.keyboard.insertText("新标题");
  await expect(page.getByRole("heading", { level: 2, name: "新标题" })).toBeVisible();
});

test("在已有列表内选择相同类型时保留列表与相邻项", async ({ page }) => {
  await openFixture(page, document({ type: "bulletList", content: [
    { type: "listItem", content: [paragraph("第一项")] }, { type: "listItem", content: [paragraph("第二项")] },
  ] }));
  await page.locator(".tiptap li p").first().click();
  await page.keyboard.press("Home");
  await page.keyboard.type("/bullet");
  await page.getByRole("option", { name: "项目符号列表 无序" }).click();
  await expect(page.locator(".tiptap > ul > li")).toHaveText(["第一项", "第二项"]);
});

test("无结果按回车保留原文，Esc 关闭后继续输入不会重新弹出", async ({ page }) => {
  await openFixture(page, document(paragraph()));
  const body = page.getByRole("textbox", { name: "文档正文" });
  await body.pressSequentially("/notfound");
  await expect(page.getByRole("status")).toContainText("没有匹配的内容");
  await expect(page.getByRole("option")).toHaveCount(0);
  await page.keyboard.press("Enter");
  await expect(page.locator(".tiptap > p").first()).toHaveText("/notfound");
  await page.keyboard.type("/todo");
  await expect(page.getByRole("option", { name: "待办清单 任务" })).toBeVisible();
  await page.keyboard.press("Escape");
  await page.keyboard.type(" more");
  await expect(page.getByRole("listbox", { name: "斜杠菜单" })).toBeHidden();
  await expect(page.locator(".tiptap > p").nth(1)).toHaveText("/todo more");
  await expect.poll(async () => (await savedDocument(page)).content?.[1]?.content?.[0]?.text).toBe("/todo more");
});

test("中文输入法确认不执行搜索项，结束输入后回车才转换", async ({ page }) => {
  await openFixture(page, document(paragraph()));
  const body = page.getByRole("textbox", { name: "文档正文" });
  await body.pressSequentially("/");
  await body.dispatchEvent("compositionstart", { data: "zhedie" });
  await page.keyboard.insertText("折叠");
  await body.dispatchEvent("keydown", { key: "Enter", code: "Enter", keyCode: 229, isComposing: true, bubbles: true });
  await expect(page.locator(".tiptap .wm-toggle")).toHaveCount(0);
  await body.dispatchEvent("compositionend", { data: "折叠" });
  await expect(page.getByRole("option", { name: "折叠块 可收起" })).toBeVisible();
  await expect.poll(() => page.locator(".tiptap").evaluate((el) =>
    (el as HTMLElement & { editor: import("@tiptap/core").Editor }).editor.view.composing)).toBe(false);
  await page.keyboard.press("Enter");
  await expect(page.locator(".tiptap .wm-toggle")).toHaveCount(1);
  await expect(page.locator(".tiptap")).not.toContainText("/折叠");
});

test("窄窗口底部菜单向上展开，内部导航不滚动正文，点击外部关闭", async ({ page }) => {
  await page.setViewportSize({ width: 860, height: 560 });
  await openFixture(page, document(...Array.from({ length: 30 }, (_, i) => paragraph(`第 ${i + 1} 段内容`)), paragraph()));
  const last = page.locator(".tiptap > p").last();
  await last.scrollIntoViewIfNeeded();
  await last.click();
  await page.keyboard.type("/");
  const menu = page.getByRole("listbox", { name: "斜杠菜单" });
  await expect(menu).toBeVisible();
  const scrollBefore = await page.locator(".editor").evaluate((el) => el.scrollTop);
  await page.keyboard.press("ArrowUp");
  await expect(page.getByRole("option", { name: "折叠块 可收起" })).toBeVisible();
  expect(await page.locator(".editor").evaluate((el) => el.scrollTop)).toBe(scrollBefore);
  const rect = (await page.locator(".wm-slash-menu").boundingBox())!;
  expect(rect.x).toBeGreaterThanOrEqual(216);
  expect(rect.x + rect.width).toBeLessThanOrEqual(860);
  expect(rect.y).toBeGreaterThanOrEqual(52);
  expect(rect.y + rect.height).toBeLessThanOrEqual(560);
  await page.screenshot({ path: test.info().outputPath("slash-menu.png"), animations: "disabled" });
  await page.getByRole("textbox", { name: "文档标题" }).click();
  await expect(menu).toBeHidden();
  await page.keyboard.press("ArrowDown");
  await expect(last).toHaveText("/");
});
