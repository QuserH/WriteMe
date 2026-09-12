import { test, expect, type Page, type APIRequestContext } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import type { Editor } from "@tiptap/core";

const password = process.env.WRITEME_QA_PASSWORD || "qa-only-test-password-2026";
async function login(page: Page, user: string) {
  await page.goto("/team"); await page.getByLabel("登录账号", { exact: true }).fill(user); await page.getByLabel("密码", { exact: true }).fill(password); await page.getByRole("button", { name: "登录", exact: true }).click();
  await expect(page.getByLabel("登录账号", { exact: true })).toHaveCount(0);
}
async function setup(page: Page, id: string, name: string) {
  await expect(page.getByRole("heading", { name: "设置你的个人名片" })).toBeVisible(); await page.getByLabel("你的名字", { exact: true }).fill(name); await page.getByRole("textbox", { name: "个人 ID" }).fill(id); await page.getByLabel("设置新密码", { exact: true }).fill(password); await page.getByRole("button", { name: "进入工作区" }).click();
}
async function owner(request: APIRequestContext) {
  const login = await request.post("/api/login", { data: { username: "qa-admin", password } }); const result = await login.json() as { token: string; accountId: string };
  const headers = { Authorization: `Bearer ${result.token}` }; const profile = await (await request.get("/api/me", { headers })).json();
  if (profile.needsSetup) expect((await request.post("/api/profile", { headers, data: { publicId: "qa-admin", displayName: "林然", password } })).ok()).toBeTruthy();
  return headers;
}
test("admin, two accounts, shared paragraph comments, menus and durable reconnect", async ({ page, browser, request }) => {
  await mkdir("artifacts/shared-qa/screens", { recursive: true });
  await page.goto("/team"); await expect(page.getByRole("heading", { name: "登录你的工作区" })).toBeVisible(); await page.screenshot({ path: "artifacts/shared-qa/screens/web-login.png" });
  await owner(request); await login(page, "qa-admin"); await expect(page.getByRole("button", { name: "管理控制台" })).toHaveCount(0); await page.goto("/admin");
  await expect(page.getByRole("heading", { name: "账号与同步" })).toBeVisible(); await expect(page.locator(".shared-sidebar")).toHaveCount(0);
  await page.getByRole("button", { name: "创建账号", exact: true }).click(); const stamp = Date.now().toString(36); const member = `member-${stamp}`;
  const create = page.getByRole("dialog", { name: "为伙伴开通账号" }); await create.getByLabel("登录账号", { exact: true }).fill(member); await create.getByLabel("临时密码", { exact: true }).fill(password); await create.getByRole("button", { name: "创建账号", exact: true }).click();
  await expect(create).not.toBeVisible(); await expect(page.getByText("账号已创建，对方首次登录后可设置自己的名字、ID 和密码。")).toBeVisible(); await page.screenshot({ path: "artifacts/shared-qa/screens/web-admin.png" });
  const peerContext = await browser.newContext({ viewport: { width: 1440, height: 960 } }); const peer = await peerContext.newPage(); await login(peer, member); await setup(peer, member, "许舟");
  await page.goto("/team"); await page.getByRole("button", { name: "创建工作区", exact: true }).first().click(); const space = page.getByRole("dialog", { name: "创建工作区" }); await space.getByLabel("工作区名称").fill("产品设计 · 共同工作"); await space.getByRole("button", { name: /创建工作区/ }).click();
  await page.getByRole("button", { name: "工作区成员", exact: true }).click(); const members = page.getByRole("dialog"); await members.getByLabel("通过个人 ID 添加成员").fill(member); await members.getByRole("button", { name: "添加成员", exact: true }).click(); await expect(members.getByText("许舟", { exact: true })).toBeVisible(); await members.getByRole("button", { name: "关闭", exact: true }).click();
  await page.getByRole("button", { name: "新建文档", exact: true }).first().click(); const title = page.getByRole("textbox", { name: "共享文档标题" }); await expect(title).toBeVisible(); await title.fill("九月 · 产品工作手记");
  const body = page.getByRole("textbox", { name: "共享文档正文" }); await body.click(); await page.keyboard.insertText("把零散的想法，整理成共同的下一步。"); await expect(page.getByRole("status")).toHaveText("所有更改已保存");
  await peer.reload(); await peer.getByRole("button", { name: "九月 · 产品工作手记" }).first().click(); await expect(peer.getByRole("textbox", { name: "共享文档正文" })).toContainText("把零散的想法");
  await body.click(); await page.keyboard.press("Control+End"); await page.keyboard.insertText(" 桌面端"); const peerBody = peer.getByRole("textbox", { name: "共享文档正文" }); await peerBody.click(); await peer.keyboard.press("Control+End"); await peer.keyboard.insertText(" 网页端");
  await expect(body).toContainText("网页端"); await expect(peerBody).toContainText("桌面端");
  await page.getByRole("button", { name: "评论当前段落", exact: true }).click(); await page.getByRole("textbox", { name: "评论内容" }).fill("这段思路可以再补一个具体例子。"); await page.getByRole("button", { name: "发送", exact: true }).click();
  await peer.getByRole("button", { name: "查看全部评论" }).click(); await expect(peer.getByText("这段思路可以再补一个具体例子。", { exact: true })).toBeVisible();
  await peer.getByRole("button", { name: "回复", exact: true }).first().click(); await peer.getByRole("textbox", { name: "评论内容" }).fill("好，我来补上用户场景。"); await peer.getByRole("button", { name: "发送", exact: true }).click();
  await expect(page.getByText("好，我来补上用户场景。", { exact: true })).toBeVisible(); await page.getByRole("button", { name: "回复", exact: true }).last().click(); await page.getByRole("textbox", { name: "评论内容" }).fill("收到，我们接着这条回复讨论。"); await page.getByRole("button", { name: "发送", exact: true }).click(); await expect(peer.getByText("收到，我们接着这条回复讨论。", { exact: true })).toBeVisible();
  await page.screenshot({ path: "artifacts/shared-qa/screens/web-document-comments.png" });
  await page.getByRole("button", { name: "关闭评论" }).click(); await body.click(); await page.keyboard.press("Control+End"); await page.keyboard.press("Enter"); await page.keyboard.insertText("/");
  const menu = page.getByRole("listbox", { name: "插入菜单" }); await expect(menu.getByRole("option", { name: /列表/ })).toBeVisible(); await page.keyboard.press("Enter"); await expect(menu.getByRole("option", { name: /折叠块/ })).toBeVisible(); await page.screenshot({ path: "artifacts/shared-qa/screens/web-slash-list.png" }); await page.keyboard.press("Escape"); await menu.getByRole("option", { name: /表格与分栏/ }).click(); await menu.getByRole("option", { name: "表格 3 × 3" }).click(); await expect(body.locator("table")).toBeVisible();
  await expect(peerBody.locator("table")).toBeVisible(); await expect(page.getByRole("status")).toHaveText("所有更改已保存");
  await peerContext.setOffline(true); await peerBody.locator("td").first().click(); await peer.keyboard.insertText("离线保留的内容"); await peerContext.setOffline(false); await expect(body).toContainText("离线保留的内容", { timeout: 20000 });
  await peer.reload(); await peer.getByRole("button", { name: "九月 · 产品工作手记" }).first().click(); await expect(peerBody).toContainText("离线保留的内容");
  await body.evaluate(element => {
    const editor = (element as HTMLElement & { editor: Editor }).editor;
    editor.commands.insertContentAt(editor.state.doc.content.size, { type: "toggleBlock", attrs: { collapsed: true }, content: [
      { type: "paragraph", content: [{ type: "text", text: "阅读时也可以展开" }] }, { type: "paragraph", content: [{ type: "text", text: "仅此设备展开的内容" }] },
    ] });
  });
  await expect(page.getByRole("status")).toHaveText("所有更改已保存"); await expect(peerBody).toContainText("阅读时也可以展开");
  const headers = await owner(request); const spaces = await (await request.get("/api/workspaces", { headers })).json() as Array<{ id: string; name: string }>;
  const workspace = spaces.find(item => item.name === "产品设计 · 共同工作")!;
  expect((await request.put(`/api/workspaces/${workspace.id}/members`, { headers, data: { publicId: member, role: "viewer" } })).ok()).toBeTruthy();
  await expect(peerBody).toHaveAttribute("contenteditable", "false"); await peerBody.getByRole("button", { name: "展开折叠块", exact: true }).click();
  await expect(peerBody.getByText("仅此设备展开的内容", { exact: true })).toBeVisible();
  await expect(body.getByText("仅此设备展开的内容", { exact: true })).not.toBeVisible();
  await expect(peer.getByRole("button", { name: "删除当前文档" })).toBeDisabled();
  await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: "artifacts/shared-qa/screens/web-mobile.png" });
  await peerContext.close();
});
