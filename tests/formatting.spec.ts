import { expect, test } from "@playwright/test";
import { document, openFixture, paragraph, savedDocument, selectText, titleOf, toggle, toggleByTitle } from "./fixtures";

test("格式按钮只作用于所选文字，连续格式、撤销与清除保留正文", async ({ page }) => {
  await openFixture(page, document(paragraph("前缀 需要格式 后缀")));
  const line = page.locator(".tiptap > p").first();
  await selectText(page, line, "需要格式");
  const toolbar = page.getByRole("toolbar", { name: "文字格式" });
  await expect(toolbar).toBeVisible();
  for (const label of ["粗体", "斜体", "下划线", "删除线"]) {
    await toolbar.getByRole("button", { name: label, exact: true }).click();
    await expect(toolbar.getByRole("button", { name: label, exact: true })).toHaveAttribute("aria-pressed", "true");
  }
  for (const tag of ["strong", "em", "u", "s"]) await expect(line.locator(tag)).toHaveText("需要格式");
  await expect(line).toHaveText("前缀 需要格式 后缀");
  await page.keyboard.press("Control+z");
  await expect(toolbar.getByRole("button", { name: "删除线", exact: true })).toHaveAttribute("aria-pressed", "false");
  await expect(line.locator("strong")).toHaveText("需要格式");
  await toolbar.getByRole("button", { name: "清除文字格式" }).click();
  await expect(line.locator("strong, em, u, s, code")).toHaveCount(0);
  await expect.poll(() => savedDocument(page)).toEqual(document(paragraph("前缀 需要格式 后缀")));
});

test("折叠标题的多色高亮可保存、刷新与清除，子树状态保持", async ({ page }) => {
  await openFixture(page, document(toggle("会议重点", [toggle("子项", [paragraph("详情")], true)]), paragraph("正文")));
  const title = titleOf(toggleByTitle(page, "会议重点"));
  await selectText(page, title, "重点");
  await page.getByRole("button", { name: "高亮颜色", exact: true }).click();
  await expect(page.getByRole("group", { name: "高亮颜色选择" })).toBeVisible();
  await page.screenshot({ path: test.info().outputPath("highlight-palette.png") });
  await page.getByRole("button", { name: "蓝色高亮", exact: true }).click();
  await expect(title.locator("mark")).toHaveText("重点");
  await expect(title.locator("mark")).toHaveAttribute("data-color", "#dceafb");
  await expect.poll(async () => (await savedDocument(page)).content?.[0]?.content?.[0]?.content?.[1]?.marks?.[0])
    .toEqual({ type: "highlight", attrs: { color: "#dceafb" } });
  await page.reload();
  await expect(title.locator("mark")).toHaveCSS("background-color", "rgb(220, 234, 251)");
  await expect(toggleByTitle(page, "子项")).toHaveAttribute("data-collapsed", "true");
  await selectText(page, title, "重点");
  await page.getByRole("button", { name: "高亮颜色", exact: true }).click();
  await expect(page.getByRole("button", { name: "蓝色高亮" })).toHaveAttribute("aria-pressed", "true");
  await page.getByRole("button", { name: "移除高亮" }).click();
  await expect(title.locator("mark")).toHaveCount(0);
});

test("链接输入保留选区，无效地址不写入，应用和移除只影响所选片段", async ({ page }) => {
  const fixture = document(paragraph("前往 Craft 文档查看"));
  await openFixture(page, fixture);
  const line = page.locator(".tiptap > p").first();
  await selectText(page, line, "Craft 文档");
  await page.keyboard.press("Control+k");
  const input = page.getByRole("textbox", { name: "链接地址" });
  await expect(input).toBeFocused();
  await input.fill("javascript:alert(1)");
  await page.getByRole("button", { name: "应用链接" }).click();
  await expect(page.getByRole("alert")).toContainText("请输入有效");
  await expect(line.locator("a")).toHaveCount(0);
  expect(await savedDocument(page)).toEqual(fixture);
  await input.fill("support.craft.do/zh-Hans/");
  await input.press("Enter");
  await expect(input).toBeHidden();
  await expect(line.locator("a")).toHaveText("Craft 文档");
  await expect(line.locator("a")).toHaveAttribute("href", "https://support.craft.do/zh-Hans/");
  await expect(line).toHaveText("前往 Craft 文档查看");
  await expect.poll(async () => (await savedDocument(page)).content?.[0]?.content?.[1]?.marks?.[0]?.attrs?.href)
    .toBe("https://support.craft.do/zh-Hans/");
  await page.reload();
  await selectText(page, line, "Craft 文档");
  await page.getByRole("button", { name: "链接", exact: true }).click();
  await expect(input).toHaveValue("https://support.craft.do/zh-Hans/");
  await page.getByRole("button", { name: "移除链接" }).click();
  await expect(line.locator("a")).toHaveCount(0);
  await expect(line).toHaveText("前往 Craft 文档查看");
});

test("链接草稿可 Esc 取消，切换文档后不会保留旧选区", async ({ page }) => {
  const fixture = document(paragraph("这段文字保持原样"));
  await openFixture(page, fixture);
  const line = page.locator(".tiptap > p").first();
  await selectText(page, line, "文字");
  await page.getByRole("button", { name: "链接", exact: true }).click();
  await page.getByRole("textbox", { name: "链接地址" }).fill("https://example.com/draft");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("form", { name: "编辑链接" })).toBeHidden();
  await expect(line.locator("a")).toHaveCount(0);
  await page.keyboard.press("Control+k");
  await page.getByRole("textbox", { name: "链接地址" }).fill("https://example.com/another");
  await page.getByRole("button", { name: /^另一篇文档/ }).click();
  await expect(page.getByRole("toolbar", { name: "文字格式" })).toBeHidden();
  await page.getByRole("button", { name: /^折叠块回归用例/ }).click();
  expect(await savedDocument(page)).toEqual(fixture);
  await expect(line).toHaveText("这段文字保持原样");
});

test("光标位于已有链接时 Ctrl+K 编辑整条链接，点击文字不跳离编辑器", async ({ page }) => {
  await openFixture(page, document({ type: "paragraph", content: [
    { type: "text", text: "原链接文字", marks: [{ type: "link", attrs: { href: "https://example.com/old" } }] },
    { type: "text", text: "后续正文" },
  ] }));
  const link = page.locator(".tiptap a");
  await link.click();
  await expect.poll(() => page.locator(".tiptap").evaluate((el) =>
    (el as HTMLElement & { editor: import("@tiptap/core").Editor }).editor.isActive("link"))).toBe(true);
  await page.keyboard.press("Control+k");
  await page.getByRole("textbox", { name: "链接地址" }).fill("https://example.com/new");
  await page.getByRole("button", { name: "应用链接" }).click();
  await expect(link).toHaveText("原链接文字");
  await expect(link).toHaveAttribute("href", "https://example.com/new");
  await expect(page.locator(".tiptap > p").first()).toHaveText("原链接文字后续正文");
  await expect(page).toHaveURL("http://127.0.0.1:1420/");
});

test("键盘选择后 Alt+F10 进入工具栏，方向键操作，Esc 后重新选择可再次显示", async ({ page }) => {
  await openFixture(page, document(paragraph("键盘编辑文字")));
  const line = page.locator(".tiptap > p").first();
  await line.click();
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  const toolbar = page.getByRole("toolbar", { name: "文字格式" });
  await expect(toolbar).toBeVisible();
  await page.keyboard.press("Alt+F10");
  await expect(toolbar.getByRole("button", { name: "粗体", exact: true })).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(toolbar.getByRole("button", { name: "斜体", exact: true })).toBeFocused();
  await page.keyboard.press("Space");
  await expect(line.locator("em")).toHaveText("键盘编辑文字");
  await page.keyboard.press("Escape");
  await expect(toolbar).toBeHidden();
  await line.click();
  await expect.poll(() => page.locator(".tiptap").evaluate((el) =>
    (el as HTMLElement & { editor: import("@tiptap/core").Editor }).editor.state.selection.empty)).toBe(true);
  await selectText(page, line, "键盘编辑文字");
  await expect(toolbar).toBeVisible();
  await page.keyboard.press("Alt+F10");
  await page.keyboard.press("Tab");
  await expect(toolbar).toBeHidden();
});

test("选中折叠标题时 Tab 仍调整层级，格式工具栏不抢占缩进快捷键", async ({ page }) => {
  await openFixture(page, document(toggle("甲"), toggle("乙", [paragraph("子内容")]), paragraph("结尾")));
  await selectText(page, titleOf(toggleByTitle(page, "乙")), "乙");
  await expect(page.getByRole("toolbar", { name: "文字格式" })).toBeVisible();
  await page.keyboard.press("Tab");
  await expect(toggleByTitle(page, "甲").locator(".wm-toggle")).toHaveCount(1);
  await expect(page.getByText("子内容", { exact: true })).toBeVisible();
});

test("混合选区可以移除局部高亮或链接，并保留其他文字格式", async ({ page }) => {
  await openFixture(page, document({ type: "paragraph", content: [
    { type: "text", text: "局部格式", marks: [{ type: "bold" }, { type: "highlight", attrs: { color: "#fff0a6" } },
      { type: "link", attrs: { href: "https://example.com" } }] }, { type: "text", text: "与普通文字" },
  ] }));
  const line = page.locator(".tiptap > p").first();
  await selectText(page, line, "局部格式与普通文字");
  await page.getByRole("button", { name: "高亮颜色", exact: true }).click();
  await page.getByRole("button", { name: "移除高亮" }).click();
  await expect(line.locator("mark")).toHaveCount(0);
  await expect(line.locator("strong")).toHaveText("局部格式");
  await page.getByRole("button", { name: "链接", exact: true }).click();
  await page.getByRole("button", { name: "移除链接" }).click();
  await expect(line.locator("a")).toHaveCount(0);
  await expect(line.locator("strong")).toHaveText("局部格式");
});

test("选区跨过整个可见区域时，工具栏和链接输入仍可操作", async ({ page }) => {
  const lines = Array.from({ length: 40 }, (_, i) => `很长文档的第 ${i + 1} 段`);
  await openFixture(page, document(...lines.map((line) => paragraph(line))));
  await selectText(page, page.locator(".tiptap"), lines.join(""));
  const toolbar = page.getByRole("toolbar", { name: "文字格式" });
  await expect(toolbar).toBeVisible();
  await page.getByRole("button", { name: "链接", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "链接地址" })).toBeVisible();
  const rect = (await page.locator(".wm-format-popover").boundingBox())!;
  const bounds = (await page.locator(".editor").boundingBox())!;
  expect(rect.y).toBeGreaterThanOrEqual(bounds.y);
  expect(rect.y + rect.height).toBeLessThanOrEqual(bounds.y + bounds.height);
});

test("行内代码可切换，代码块和整块选择不显示文字工具栏", async ({ page }) => {
  await openFixture(page, document(paragraph("inline code"), { type: "codeBlock", content: [{ type: "text", text: "const value = 1;" }] }));
  const line = page.locator(".tiptap > p").first();
  const toolbar = page.getByRole("toolbar", { name: "文字格式" });
  await expect(toolbar).toBeHidden();
  await selectText(page, line, "inline code");
  await toolbar.getByRole("button", { name: "行内代码", exact: true }).click();
  await expect(line.locator("code")).toHaveText("inline code");
  await toolbar.getByRole("button", { name: "行内代码", exact: true }).click();
  await expect(line.locator("code")).toHaveCount(0);
  await selectText(page, page.locator(".tiptap pre"), "const value = 1;");
  await expect(toolbar).toBeHidden();
  await line.hover();
  await page.getByRole("button", { name: "拖动块或打开块菜单" }).click();
  await expect(page.getByRole("menu", { name: "块操作" })).toBeVisible();
  await expect(toolbar).toBeHidden();
});

test("跨段格式保留块结构，浮层在窄窗口与底部选区中保持可见", async ({ page }) => {
  await page.setViewportSize({ width: 860, height: 560 });
  await openFixture(page, document(...Array.from({ length: 24 }, (_, i) => paragraph(`段落 ${i + 1}：记录一点想法。`)),
    paragraph("最后两段"), paragraph("一起格式化")));
  await selectText(page, page.locator(".tiptap"), "最后两段一起格式化");
  // 原生选区 API 不自动滚动末尾，显式滚到所选块再检查浮层。
  await page.locator(".tiptap > p").last().scrollIntoViewIfNeeded();
  const toolbar = page.getByRole("toolbar", { name: "文字格式" });
  await expect(toolbar).toBeVisible();
  await toolbar.getByRole("button", { name: "下划线", exact: true }).click();
  await expect(page.locator(".tiptap > p u")).toHaveText(["最后两段", "一起格式化"]);
  await expect(page.locator(".tiptap > p")).toHaveCount(26);
  await page.getByRole("button", { name: "链接", exact: true }).click();
  await expect(page.getByRole("textbox", { name: "链接地址" })).toBeVisible();
  const rect = (await page.locator(".wm-format-popover").boundingBox())!;
  const bounds = (await page.locator(".editor").boundingBox())!;
  expect(rect.x).toBeGreaterThanOrEqual(bounds.x);
  expect(rect.x + rect.width).toBeLessThanOrEqual(860);
  expect(rect.y).toBeGreaterThanOrEqual(bounds.y);
  expect(rect.y + rect.height).toBeLessThanOrEqual(560);
  await page.screenshot({ path: test.info().outputPath("formatting-link.png") });
  await page.locator(".editor").hover();
  await page.mouse.wheel(0, -2200);
  await expect(toolbar).toBeHidden();
});
