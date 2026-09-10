import { expect, test } from "@playwright/test";
import { beginDrag, document, dragInto, openFixture, paragraph, savedDocument, titleOf, toggle, toggleByTitle } from "./fixtures";

test("父子折叠独立，连续点击和刷新后仍恢复各自状态", async ({ page }) => {
  await openFixture(page, document(toggle("项目", [toggle("计划", [paragraph("隐藏细节")], true),
    toggle("执行", [paragraph("可见细节")])]), paragraph("文档结尾")));
  const root = toggleByTitle(page, "项目");
  const child = toggleByTitle(page, "计划");
  const caret = root.locator(":scope > button");
  await expect(page.getByText("隐藏细节", { exact: true })).toBeHidden();
  for (let i = 0; i < 4; i++) {
    await caret.click();
    await expect(page.getByText("可见细节", { exact: true })).toBeHidden();
    await caret.click();
    await expect(page.getByText("可见细节", { exact: true })).toBeVisible();
    await expect(child).toHaveAttribute("data-collapsed", "true");
  }
  await child.locator(":scope > button").click();
  await expect.poll(async () => (await savedDocument(page)).content?.[0].content?.[1].attrs?.collapsed).toBe(false);
  await page.reload();
  await expect(page.getByText("隐藏细节", { exact: true })).toBeVisible();
  await expect(caret).toHaveAttribute("aria-expanded", "true");
});

test("Craft 的加号快捷方式、回车下沉和空标题退出", async ({ page }) => {
  await openFixture(page, document(paragraph()));
  const editor = page.getByRole("textbox", { name: "文档正文" });
  await editor.pressSequentially("+ ");
  await expect(page.locator(".writeme-editor .wm-toggle")).toHaveCount(1);
  await page.keyboard.insertText("父标题");
  await page.keyboard.press("Enter");
  await page.keyboard.insertText("子标题");
  await page.keyboard.press("Enter");
  await expect(page.locator(".writeme-editor .wm-toggle")).toHaveCount(3);
  await page.keyboard.press("Enter");
  await expect(toggleByTitle(page, "子标题").locator(".wm-toggle")).toHaveCount(0);
  await expect(toggleByTitle(page, "父标题").locator(".wm-toggle")).toHaveCount(2);
  await page.keyboard.press("Backspace");
  await expect(page.locator(".writeme-editor .wm-toggle")).toHaveCount(2);
});

test("折叠块内通过斜杠菜单创建子折叠块，标题可以输入中文", async ({ page }) => {
  await openFixture(page, document(toggle("父级", [paragraph()]), paragraph("文档结尾")));
  const childParagraph = toggleByTitle(page, "父级").locator(":scope > .wm-toggle-content > p").nth(1);
  await childParagraph.click();
  await page.keyboard.type("/");
  await page.getByRole("option", { name: "折叠块 可收起" }).click();
  await page.keyboard.insertText("新子级中文标题");
  await expect(titleOf(toggleByTitle(page, "新子级中文标题"))).toBeVisible();
  await expect(toggleByTitle(page, "父级").locator(".wm-toggle")).toHaveCount(1);
});

test("Tab/Shift-Tab 调整整个折叠子树且保留子内容", async ({ page }) => {
  await openFixture(page, document(toggle("甲", [], true), toggle("乙", [paragraph("子树内容")]), paragraph("文档结尾")));
  await titleOf(toggleByTitle(page, "乙")).click();
  // 浏览器 selectionchange 异步送达；等到编辑器确实接收到这次点击再按快捷键。
  await expect.poll(() => page.locator(".tiptap").evaluate((element) =>
    (element as HTMLElement & { editor: import("@tiptap/core").Editor }).editor.state.selection.$from.parent.textContent,
  )).toBe("乙");
  await page.keyboard.press("Tab");
  await expect(toggleByTitle(page, "甲").locator(".wm-toggle")).toHaveCount(1);
  await expect(page.getByText("子树内容", { exact: true })).toBeVisible();
  await page.keyboard.press("Shift+Tab");
  await expect(page.locator(".writeme-editor > .wm-toggle")).toHaveCount(2);
  await expect(toggleByTitle(page, "甲").locator(".wm-toggle")).toHaveCount(0);
});

test("拖动首块进入收起的折叠块，落点即插入位置，撤销恢复", async ({ page }) => {
  const fixture = document(paragraph("要移入的首块"), toggle("目标", [paragraph("原有内容")], true), paragraph("文档结尾"));
  await openFixture(page, fixture);
  await dragInto(page, page.getByText("要移入的首块", { exact: true }), toggleByTitle(page, "目标"));
  const target = toggleByTitle(page, "目标");
  await expect(target).toHaveAttribute("data-collapsed", "false");
  await expect(target.locator(":scope > .wm-toggle-content > p")).toHaveText(["目标", "要移入的首块", "原有内容"]);
  await expect(page.locator(".wm-drag-ghost")).toHaveCount(0);
  await expect.poll(async () => (await savedDocument(page)).content?.[0].content?.[1]).toEqual(paragraph("要移入的首块"));
  await page.keyboard.press("Control+z");
  await expect.poll(() => savedDocument(page)).toEqual(fixture);
});

test("嵌套子块可以拖到另一折叠块中，原兄弟块和自身折叠状态保留", async ({ page }) => {
  await openFixture(page, document(toggle("来源", [toggle("要移动的子树", [paragraph("子树详情")], true), paragraph("留下的兄弟")]),
    toggle("目标", [paragraph("目标正文")], true), paragraph("文档结尾")));
  await dragInto(page, titleOf(toggleByTitle(page, "要移动的子树")), toggleByTitle(page, "目标"));
  await expect(toggleByTitle(page, "来源").getByText("留下的兄弟", { exact: true })).toBeVisible();
  await expect(toggleByTitle(page, "来源").locator(".wm-toggle")).toHaveCount(0);
  const moved = toggleByTitle(page, "目标").locator(".wm-toggle");
  await expect(moved).toHaveAttribute("data-collapsed", "true");
  await moved.locator(":scope > button").click();
  await expect(page.getByText("子树详情", { exact: true })).toBeVisible();
});

test("向左拖出子项，空父级不会消失或留下无效节点", async ({ page }) => {
  await openFixture(page, document(toggle("父级", [toggle("子级", [paragraph("详情")], true)]), paragraph("文档结尾")));
  const root = toggleByTitle(page, "父级");
  const rect = await root.boundingBox();
  if (!rect) throw new Error("父级不可见");
  await beginDrag(page, titleOf(toggleByTitle(page, "子级")));
  await page.mouse.move(rect.x - 4, rect.y + rect.height - 10, { steps: 12 });
  await expect(page.locator(".wm-drop-line")).toBeVisible();
  await page.mouse.up();
  await expect(page.locator(".writeme-editor > .wm-toggle")).toHaveCount(2);
  await expect(root.locator(".wm-toggle")).toHaveCount(0);
  await expect(toggleByTitle(page, "子级")).toHaveAttribute("data-collapsed", "true");
});

test("同级排序能够跨过展开的整棵子树", async ({ page }) => {
  await openFixture(page, document(paragraph("首块"), toggle("父级", [toggle("子级", [paragraph("详情")])]), paragraph("尾块")));
  const tail = await page.getByText("尾块", { exact: true }).boundingBox();
  if (!tail) throw new Error("尾块不可见");
  await beginDrag(page, page.getByText("首块", { exact: true }));
  await page.mouse.move(tail.x - 8, tail.y + tail.height - 1, { steps: 12 });
  await page.mouse.up();
  await expect(page.locator(".writeme-editor > p")).toHaveText(["尾块", "首块"]);
  await expect(page.getByText("详情", { exact: true })).toBeVisible();
});

test("父级不能拖进自己的子级，Esc 取消不修改文档", async ({ page }) => {
  const fixture = document(toggle("父级", [toggle("子级", [paragraph("内容")])]), toggle("另一组"), paragraph("文档结尾"));
  await openFixture(page, fixture);
  const child = await titleOf(toggleByTitle(page, "子级")).boundingBox();
  if (!child) throw new Error("子级不可见");
  await beginDrag(page, titleOf(toggleByTitle(page, "父级")));
  await page.mouse.move(child.x + 90, child.y + child.height / 2, { steps: 10 });
  await expect(page.locator(".wm-drop-line")).toBeHidden();
  await page.mouse.up();
  const another = await titleOf(toggleByTitle(page, "另一组")).boundingBox();
  if (!another) throw new Error("另一组不可见");
  await beginDrag(page, titleOf(toggleByTitle(page, "父级")));
  await page.mouse.move(another.x + 80, another.y + another.height / 2, { steps: 10 });
  await expect(page.locator(".wm-drop-line")).toBeVisible();
  await page.keyboard.press("Escape");
  await page.mouse.up();
  await expect(page.locator(".wm-drag-ghost, .wm-drop-line")).toHaveCount(0);
  expect(await savedDocument(page)).toEqual(fixture);
});

test("块菜单取消折叠保留全部内容，并支持一次撤销", async ({ page }) => {
  const fixture = document(toggle("父级", [toggle("子级", [paragraph("内容")], true)], true), paragraph("文档结尾"));
  await openFixture(page, fixture);
  await titleOf(toggleByTitle(page, "父级")).hover();
  await page.getByRole("button", { name: "拖动块或打开块菜单" }).click();
  await page.getByRole("menuitem", { name: "取消折叠，保留内容" }).click();
  await expect(page.locator(".writeme-editor > p")).toHaveText(["父级", "文档结尾"]);
  await expect(toggleByTitle(page, "子级")).toHaveAttribute("data-collapsed", "true");
  await page.keyboard.press("Control+z");
  await expect(toggleByTitle(page, "父级")).toHaveAttribute("data-collapsed", "true");
  await expect.poll(() => savedDocument(page)).toEqual(fixture);
});

test("五级长标题在窄窗口不越界，隐藏子树不占空间", async ({ page }) => {
  await page.setViewportSize({ width: 860, height: 720 });
  const long = "一个很长的折叠标题用于检查自动换行、箭头对齐以及内容区域边界";
  await openFixture(page, document(toggle("一级", [toggle("二级", [toggle("三级", [toggle("四级", [toggle(long, [paragraph("底层正文")])])])])]), paragraph("文档结尾")));
  const size = await page.locator(".editor").evaluate((el) => ({ client: el.clientWidth, scroll: el.scrollWidth }));
  expect(size.scroll).toBeLessThanOrEqual(size.client + 1);
  const root = toggleByTitle(page, "一级");
  const before = (await root.boundingBox())!.height;
  await root.locator(":scope > button").click();
  await expect(page.getByText("底层正文", { exact: true })).toBeHidden();
  expect((await root.boundingBox())!.height).toBeLessThan(before / 2);
});

test("切换文档保存折叠状态，全局快捷键可展开所有级别", async ({ page }) => {
  await openFixture(page, document(toggle("父级", [toggle("子级", [paragraph("内容")], true)]), paragraph("文档结尾")));
  await toggleByTitle(page, "父级").locator(":scope > button").click();
  await page.getByRole("button", { name: /^另一篇文档/ }).click();
  await page.getByRole("button", { name: /^折叠块回归用例/ }).click();
  await expect(toggleByTitle(page, "父级")).toHaveAttribute("data-collapsed", "true");
  await titleOf(toggleByTitle(page, "父级")).click();
  await page.keyboard.press("Control+Alt+t");
  await expect(page.getByText("内容", { exact: true })).toBeVisible();
});

test("光标在子内容时收起父级，继续输入写入可见标题", async ({ page }) => {
  await openFixture(page, document(toggle("父级", [paragraph("保留的正文")]), paragraph("文档结尾")));
  await page.getByText("保留的正文", { exact: true }).click();
  await page.keyboard.press("End");
  await toggleByTitle(page, "父级").locator(":scope > button").click();
  await page.keyboard.insertText("追加标题");
  await expect(titleOf(toggleByTitle(page, "父级追加标题"))).toBeVisible();
  await expect.poll(async () => (await savedDocument(page)).content?.[0].content?.[1]).toEqual(paragraph("保留的正文"));
});

test("输入法确认候选时 Enter 不新增折叠子项", async ({ page }) => {
  await openFixture(page, document(toggle("中文标题"), paragraph("文档结尾")));
  const editor = page.getByRole("textbox", { name: "文档正文" });
  await titleOf(toggleByTitle(page, "中文标题")).click();
  await editor.dispatchEvent("compositionstart", { data: "中" });
  await editor.dispatchEvent("keydown", { key: "Enter", code: "Enter", keyCode: 229, isComposing: true });
  await expect(page.locator(".writeme-editor .wm-toggle")).toHaveCount(1);
  await editor.dispatchEvent("compositionend", { data: "中" });
});

test("折叠 HTML 往返保留嵌套、标记和每级折叠状态", async ({ page }) => {
  const fixture = document(toggle("父级", [toggle("子级", [
    { type: "paragraph", content: [{ type: "text", text: "粗体内容", marks: [{ type: "bold" }] }] },
  ], true)], true), paragraph("文档结尾"));
  await openFixture(page, fixture);
  await page.locator(".tiptap").evaluate((element) => {
    const editor = (element as HTMLElement & { editor: import("@tiptap/core").Editor }).editor;
    editor.commands.setContent(editor.getHTML());
  });
  await expect.poll(() => savedDocument(page)).toEqual(fixture);
  await toggleByTitle(page, "父级").locator(":scope > button").click();
  await toggleByTitle(page, "子级").locator(":scope > button").click();
  await expect(page.locator(".writeme-editor strong")).toHaveText("粗体内容");
});

test("拖动停留在编辑区边缘会持续滚动，取消后文档不变", async ({ page }) => {
  const fixture = document(paragraph("拖动源"), ...Array.from({ length: 45 }, (_, index) => paragraph(`长文档第 ${index + 1} 段`)));
  await openFixture(page, fixture);
  const rect = await page.locator(".editor").boundingBox();
  if (!rect) throw new Error("编辑区不可见");
  await beginDrag(page, page.getByText("拖动源", { exact: true }));
  await page.mouse.move(rect.x + 100, rect.y + rect.height - 18, { steps: 10 });
  await expect.poll(() => page.locator(".editor").evaluate((el) => el.scrollTop)).toBeGreaterThan(100);
  await page.keyboard.press("Escape");
  await page.mouse.up();
  await expect(page.locator(".wm-drag-ghost, .wm-drop-line")).toHaveCount(0);
  expect(await savedDocument(page)).toEqual(fixture);
});

test("冷启动只创建一篇欢迎文档，侧栏可收起后恢复", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("textbox", { name: "文档标题" })).toHaveValue("欢迎使用 WriteME");
  await expect(page.locator(".doc-list > li")).toHaveCount(1);
  await page.getByRole("button", { name: "收起侧栏", exact: true }).click();
  await expect(page.getByRole("complementary", { name: "文档侧栏" })).toBeHidden();
  await page.getByRole("button", { name: "展开侧栏", exact: true }).click();
  await expect(page.locator(".doc-list > li")).toHaveCount(1);
});
