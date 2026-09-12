import { test, expect, type APIRequestContext, type Page } from "@playwright/test";
import type { Editor, JSONContent } from "@tiptap/core";
import { mkdir } from "node:fs/promises";
import type { Profile, SharedDocData, Workspace } from "../../src/shared/api";

const password = process.env.WRITEME_QA_PASSWORD || "qa-only-test-password-2026";
// APIRequestContext does not add a browser Origin. Keep the server's cookie CSRF check enabled.
const sameOrigin = { Origin: new URL(process.env.WRITEME_QA_URL || "http://127.0.0.1:8791").origin };
test.use({ actionTimeout: 15000 });
const androidChrome = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36";
async function bootstrap(request: APIRequestContext) {
  const response = await request.post("/api/session", { headers: sameOrigin, data: { username: "qa-admin", password } }); expect(response.ok()).toBeTruthy();
  const profile = await response.json() as Profile;
  if (profile.needsSetup) expect((await request.post("/api/profile", { headers: sameOrigin, data: { publicId: "qa-admin", displayName: "林然", password } })).ok()).toBeTruthy();
}
async function login(page: Page, username: string, secret: string) {
  await page.goto("/team"); await page.getByLabel("登录账号", { exact: true }).fill(username); await page.getByLabel("密码", { exact: true }).fill(secret);
  await page.getByRole("button", { name: "登录", exact: true }).click();
}
async function fits(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
}
async function visibleComposer(page: Page) {
  const dialog = page.getByRole("dialog", { name: /段落评论|全部评论/ }); await expect(dialog).toBeVisible();
  const box = await dialog.boundingBox(); const area = await page.getByRole("textbox", { name: "评论内容" }).boundingBox();
  const send = await dialog.getByRole("button", { name: "发送", exact: true }).boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(8); expect(box!.x + box!.width).toBeLessThanOrEqual(page.viewportSize()!.width - 8);
  expect(area!.y).toBeGreaterThanOrEqual(box!.y); expect(send!.y + send!.height).toBeLessThanOrEqual(page.viewportSize()!.height - 4);
  expect((await dialog.locator(".shared-comment-list").boundingBox())!.height).toBeGreaterThan(40);
}

test("Android touch: paragraph threads, nested replies, drafts, keyboard viewport and nested layouts", async ({ browser, request }) => {
  test.setTimeout(100000); await mkdir("artifacts/shared-qa/screens", { recursive: true }); await bootstrap(request);
  const stamp = Date.now().toString(36); const username = "android-" + stamp;
  expect((await request.post("/api/admin/accounts", { headers: sameOrigin, data: { username, password: "123456" } })).ok()).toBeTruthy();
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, userAgent: androidChrome });
  const phone = await context.newPage();
  const desktop = await browser.newContext({ storageState: await request.storageState(), viewport: { width: 1440, height: 960 } }); const peer = await desktop.newPage();
  const errors: string[] = []; phone.on("pageerror", error => errors.push(error.message));
  try {
    await login(phone, username, "123456");
    await phone.getByLabel("你的名字", { exact: true }).fill("许舟"); await phone.getByRole("textbox", { name: "个人 ID" }).fill(username);
    await phone.getByLabel("设置新密码", { exact: true }).fill("654321"); await phone.getByRole("button", { name: "进入工作区" }).tap();
    await expect(phone.getByRole("heading", { name: "你好，许舟" })).toBeVisible();
    const space = await (await request.post("/api/workspaces", { headers: sameOrigin, data: { name: "手机上的共同工作 " + stamp } })).json() as Workspace;
    expect((await request.put("/api/workspaces/" + space.id + "/members", { headers: sameOrigin, data: { publicId: username, role: "editor" } })).ok()).toBeTruthy();
    const data = await (await request.post("/api/workspaces/" + space.id + "/documents", { headers: sameOrigin, data: { title: "手机编辑与段落讨论 " + stamp } })).json() as SharedDocData;
    await peer.goto("/team");
    await peer.getByLabel("切换共享工作区").selectOption(space.id); await peer.getByRole("button", { name: data.document.title, exact: true }).first().click();
    const peerBody = peer.getByRole("textbox", { name: "共享文档正文" });
    await peerBody.evaluate(element => {
      const editor = (element as HTMLElement & { editor: Editor }).editor;
      const p = (text: string): JSONContent => ({ type: "paragraph", content: [{ type: "text", text }] });
      editor.commands.setContent({ type: "doc", content: [
        p("把想法留在这一段，手机上也能接着讨论。"),
        { type: "toggleBlock", attrs: { collapsed: false }, content: [p("折叠标题上的评论"), p("收起后，标题评论仍然可见。")] },
        { type: "columnList", content: ["计划", "进展"].map(text => ({ type: "column", content: [p(text)] })) },
        { type: "table", content: [0, 1].map(row => ({ type: "tableRow", content: [0, 1, 2, 3].map(col => ({ type: "tableCell", attrs: { colwidth: [160] }, content: [p(row + "-" + col)] })) })) },
      ] }); editor.commands.setTextSelection(2);
    });
    await peerBody.press("Control+Alt+m");
    await peer.getByRole("textbox", { name: "评论内容" }).fill("这一段在手机上可以看到完整回复。"); await peer.getByRole("button", { name: "发送", exact: true }).click();
    await peer.getByRole("button", { name: "回复", exact: true }).click(); await peer.getByRole("textbox", { name: "评论内容" }).fill("这条是段落评论里的第一条回复。"); await peer.getByRole("button", { name: "发送", exact: true }).click();
    await expect(peer.getByRole("status")).toHaveText("所有更改已保存");
    await phone.reload(); await phone.getByRole("button", { name: data.document.title }).tap();
    const body = phone.getByRole("textbox", { name: "共享文档正文" }); await expect(body).toContainText("把想法留在这一段");
    await fits(phone); await expect(body.locator(".shared-columns")).toHaveCSS("flex-direction", "column");
    expect(await body.locator(".tableWrapper").evaluate(element => element.scrollWidth > element.clientWidth)).toBeTruthy();
    await phone.getByRole("button", { name: "显示工作区导航" }).tap();
    const navigation = phone.getByRole("complementary", { name: "工作区导航" }); await expect(navigation).toBeVisible();
    const topbar = (await phone.locator(".shared-topbar").boundingBox())!;
    expect((await navigation.boundingBox())!.y).toBeCloseTo(topbar.y + topbar.height, 0);
    expect((await navigation.locator(".shared-space-picker").boundingBox())!.y - (await navigation.boundingBox())!.y).toBeLessThanOrEqual(18);
    await phone.getByRole("button", { name: "关闭工作区导航", exact: true }).tap();
    await body.locator(".shared-paragraph-comment.has-comments").first().tap();
    await visibleComposer(phone); await expect(phone.locator(".shared-message")).toHaveCount(2);
    await phone.getByRole("button", { name: "回复", exact: true }).last().tap(); await phone.getByRole("textbox", { name: "评论内容" }).fill("回复的回复草稿，关闭后继续。");
    await phone.getByRole("button", { name: "关闭评论", exact: true }).tap(); await body.locator(".shared-paragraph-comment.has-comments").first().tap();
    await expect(phone.getByRole("textbox", { name: "评论内容" })).toHaveValue("回复的回复草稿，关闭后继续。");
    await phone.setViewportSize({ width: 390, height: 400 }); await visibleComposer(phone);
    const composer = phone.getByRole("textbox", { name: "评论内容" });
    await composer.dispatchEvent("compositionstart"); await phone.getByRole("button", { name: "发送", exact: true }).tap(); await expect(phone.locator(".shared-message")).toHaveCount(2);
    await composer.dispatchEvent("compositionend"); await phone.getByRole("button", { name: "发送", exact: true }).tap(); await expect(phone.locator(".shared-message")).toHaveCount(3);
    await phone.screenshot({ path: "artifacts/shared-qa/screens/android-keyboard-viewport.png" });
    await expect(peer.locator(".shared-comment-list")).toContainText("回复的回复草稿，关闭后继续。");
    await phone.setViewportSize({ width: 390, height: 844 });
    await phone.getByRole("button", { name: "回复", exact: true }).last().tap(); await composer.fill("继续回复手机发出的那条消息。"); await phone.getByRole("button", { name: "发送", exact: true }).tap();
    const parent = phone.locator(".shared-message").filter({ has: phone.getByText("回复的回复草稿，关闭后继续。", { exact: true }) });
    const deletedId = await parent.getAttribute("data-message-id");
    await parent.getByRole("button", { name: "删除", exact: true }).tap(); await phone.getByRole("button", { name: "确认删除", exact: true }).tap();
    for (const device of [phone, peer]) {
      await expect(device.locator('[data-message-id="' + deletedId + '"]')).toHaveCount(0);
      await expect(device.locator(".shared-message")).toHaveCount(3);
      await expect(device.locator(".shared-comments-heading>div>span")).toHaveText("3");
      await expect(device.locator(".shared-comment-list")).not.toContainText("已删除");
      const remaining = device.locator(".shared-message").filter({ has: device.getByText("继续回复手机发出的那条消息。", { exact: true }) });
      await expect(remaining).toBeVisible(); await expect(remaining.locator(".shared-reply-context")).toHaveCount(0);
    }
    await phone.getByRole("button", { name: "回复", exact: true }).last().tap(); await composer.fill("前一条删除后，讨论仍可以继续。");
    await phone.getByRole("button", { name: "发送", exact: true }).tap();
    await expect(peer.locator(".shared-message")).toHaveCount(4);
    for (const width of [360, 390, 412]) {
      await phone.setViewportSize({ width, height: 844 }); await visibleComposer(phone); await fits(phone);
      await phone.screenshot({ path: "artifacts/shared-qa/screens/android-comments-" + width + ".png" });
    }
    await phone.getByRole("button", { name: "关闭评论" }).tap();
    await body.getByText("折叠标题上的评论", { exact: true }).tap();
    await phone.getByRole("button", { name: "文档与工作区操作" }).tap(); await phone.getByRole("button", { name: "评论当前段落", exact: true }).tap();
    await composer.fill("折叠标题的段落评论"); await phone.getByRole("button", { name: "发送", exact: true }).tap(); await phone.getByRole("button", { name: "关闭评论" }).tap();
    await body.getByRole("button", { name: "收起折叠块", exact: true }).tap(); await body.locator(".wm-toggle .shared-paragraph-comment.has-comments").tap();
    await expect(phone.getByText("折叠标题的段落评论", { exact: true })).toBeVisible(); await phone.getByRole("button", { name: "关闭评论" }).tap();
    await body.locator("td").first().tap();
    await phone.getByRole("button", { name: "文档与工作区操作" }).tap(); await phone.getByRole("button", { name: "评论当前段落", exact: true }).tap();
    await composer.fill("单元格里的评论也能打开"); await phone.getByRole("button", { name: "发送", exact: true }).tap(); await phone.getByRole("button", { name: "关闭评论" }).tap();
    await body.locator("td").first().locator(".shared-paragraph-comment.has-comments").tap(); await expect(phone.getByText("单元格里的评论也能打开", { exact: true })).toBeVisible();
    await phone.getByRole("button", { name: "关闭评论" }).tap();
    await phone.getByRole("button", { name: "文档与工作区操作" }).tap(); await phone.getByRole("button", { name: "个人设置", exact: true }).tap();
    const settings = phone.getByRole("dialog", { name: "个人设置" });
    await settings.getByLabel("头像文字", { exact: true }).fill("舟"); await settings.getByRole("button", { name: "头像颜色 #497BE0" }).tap();
    await settings.getByRole("button", { name: "保存个人资料" }).tap(); await expect(settings.getByRole("status")).toHaveText("个人资料已保存");
    await phone.screenshot({ path: "artifacts/shared-qa/screens/android-profile.png" });
    await settings.getByRole("tab", { name: "修改密码" }).tap();
    await settings.getByLabel("当前密码", { exact: true }).fill("654321"); await settings.getByLabel("新密码", { exact: true }).fill("new123"); await settings.getByLabel("确认新密码", { exact: true }).fill("new123");
    await phone.setViewportSize({ width: 360, height: 400 }); await settings.getByRole("button", { name: "更新密码" }).tap();
    await expect(settings.getByRole("status")).toHaveText("密码已更新，其他设备需要重新登录");
    await phone.setViewportSize({ width: 390, height: 844 }); await settings.getByRole("button", { name: "关闭", exact: true }).tap();
    await expect(body).toContainText("把想法留在这一段"); expect((await context.request.get("/api/me")).ok()).toBeTruthy();
    await fits(phone); expect(errors).toEqual([]);
  } finally { await context.close(); await desktop.close(); }
});

test("Independent admin: rename, six-character password, account sync controls and custom avatar", async ({ browser, request }) => {
  test.setTimeout(100000); await mkdir("artifacts/shared-qa/screens", { recursive: true }); await bootstrap(request);
  const stamp = Date.now().toString(36); const username = "manage-" + stamp;
  expect((await request.post("/api/admin/accounts", { headers: sameOrigin, data: { username, password: "123456", isAdmin: true } })).ok()).toBeTruthy();
  const context = await browser.newContext({ viewport: { width: 1440, height: 960 }, userAgent: androidChrome.replace("Chrome/128.0.0.0", "Chrome/128.0.0.0 EdgA/128.0.0.0") }); const page = await context.newPage();
  const extra = await browser.newContext(); const errors: string[] = []; page.on("pageerror", error => errors.push(error.message));
  try {
    const login = await context.request.post("/api/session", { headers: sameOrigin, data: { username, password: "123456" } }); expect(login.ok()).toBeTruthy();
    const profile = await (await context.request.post("/api/profile", { headers: sameOrigin, data: { publicId: username, displayName: "我的管理账号", password: "123456" } })).json() as Profile;
    await page.goto("/admin"); await expect(page.getByRole("heading", { name: "账号与同步" })).toBeVisible();
    const row = page.locator('[data-account-id="' + profile.id + '"]'); await row.getByRole("button", { name: "编辑账号", exact: true }).click();
    const edit = page.getByRole("dialog", { name: "编辑我的管理员账号" }); await edit.getByLabel("登录账号", { exact: true }).fill("renamed-" + stamp);
    await edit.getByLabel("显示名字", { exact: true }).fill("林然的工作台"); await edit.getByLabel("新密码", { exact: true }).fill("654321"); await edit.getByRole("button", { name: "保存账号" }).click();
    await expect(edit).not.toBeVisible(); await expect(row).toContainText("renamed-" + stamp); expect((await context.request.get("/api/me")).ok()).toBeTruthy();
    await page.getByRole("button", { name: "个人设置", exact: true }).click(); const settings = page.getByRole("dialog", { name: "个人设置" });
    await settings.getByLabel("头像文字", { exact: true }).fill("林");
    await settings.getByRole("button", { name: "头像颜色 #8170AE", exact: true }).click();
    const picture = await page.evaluate(() => {
      const canvas = document.createElement("canvas"); canvas.width = 400; canvas.height = 280; const context = canvas.getContext("2d")!;
      const fill = context.createLinearGradient(0, 0, 400, 280); fill.addColorStop(0, "#d8e9ff"); fill.addColorStop(1, "#8e8bd0"); context.fillStyle = fill; context.fillRect(0, 0, 400, 280);
      context.fillStyle = "#fff"; context.beginPath(); context.arc(230, 110, 42, 0, Math.PI * 2); context.fill(); context.beginPath(); context.ellipse(230, 236, 80, 62, 0, 0, Math.PI * 2); context.fill();
      return canvas.toDataURL("image/png").split(",")[1];
    });
    await settings.getByLabel("上传头像图片", { exact: true }).setInputFiles({ name: "avatar.png", mimeType: "image/png", buffer: Buffer.from(picture, "base64") });
    await expect(settings.getByLabel("头像缩放", { exact: true })).toBeVisible(); await settings.getByLabel("头像缩放", { exact: true }).focus(); await page.keyboard.press("ArrowRight");
    await settings.getByRole("button", { name: "保存个人资料" }).click(); await expect(settings.getByRole("status")).toHaveText("个人资料已保存");
    await page.screenshot({ path: "artifacts/shared-qa/screens/avatar-designer.png" }); await settings.getByRole("button", { name: "关闭", exact: true }).click();
    await expect(page.locator(".admin-profile-button img")).toBeVisible(); await page.reload(); await expect(page.locator(".admin-profile-button img")).toBeVisible();
    const updated = await (await context.request.get("/api/me")).json() as Profile; expect(updated.avatar?.text).toBe("林"); expect(updated.avatar?.color).toBe("#8170AE"); expect(updated.avatar?.imageVersion).toMatch(/^[A-F0-9]{64}$/);
    await row.getByRole("button", { name: "同步详情", exact: true }).click(); const detail = page.getByRole("dialog", { name: /同步详情/ });
    await detail.getByRole("button", { name: "暂停同步写入", exact: true }).click(); await detail.getByRole("button", { name: "暂停写入", exact: true }).click(); await expect(detail.locator(".admin-state")).toHaveText("已暂停写入");
    await detail.getByRole("button", { name: "恢复同步写入", exact: true }).click(); await expect(detail.locator(".admin-state")).toHaveText("正常");
    expect((await extra.request.post("/api/session", { headers: sameOrigin, data: { username: "renamed-" + stamp, password: "654321" } })).ok()).toBeTruthy();
    await detail.getByRole("button", { name: "关闭", exact: true }).click(); await row.getByRole("button", { name: "同步详情", exact: true }).click();
    await detail.getByRole("tab", { name: /登录设备/ }).click(); await expect(detail.getByText("Android · Edge", { exact: false })).toBeVisible();
    await detail.getByRole("button", { name: "退出其他设备", exact: true }).click(); await detail.locator(".admin-confirm").getByRole("button", { name: "退出其他设备", exact: true }).click();
    await expect(detail.locator(".admin-confirm")).not.toBeVisible();
    await expect(detail.getByRole("button", { name: "退出其他设备", exact: true })).toBeDisabled();
    expect((await extra.request.get("/api/me")).status()).toBe(401); expect((await context.request.get("/api/me")).ok()).toBeTruthy();
    await page.screenshot({ path: "artifacts/shared-qa/screens/admin-account-data.png" }); await detail.getByRole("button", { name: "关闭", exact: true }).click();
    await page.getByLabel("搜索账号").fill("renamed-" + stamp); await page.screenshot({ path: "artifacts/shared-qa/screens/admin-desktop.png" });
    await page.setViewportSize({ width: 360, height: 800 }); await fits(page); await expect(row).toBeVisible(); await page.screenshot({ path: "artifacts/shared-qa/screens/admin-mobile.png" });
    await row.getByRole("button", { name: "同步详情", exact: true }).click(); await detail.getByRole("tab", { name: /登录设备/ }).click(); await fits(page);
    await page.screenshot({ path: "artifacts/shared-qa/screens/admin-mobile-data.png" }); expect(errors).toEqual([]);
  } finally { await context.close(); await extra.close(); }
});
