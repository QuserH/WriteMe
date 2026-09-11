// Real Chromium counterpart for SharedWindowTests; no test hooks are added to the product.
import { chromium, expect } from "@playwright/test";
import { createInterface } from "node:readline";
import { mkdir } from "node:fs/promises";

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
const page = await context.newPage();
page.setDefaultTimeout(12000);
const body = page.getByRole("textbox", { name: "共享文档正文" });
let title;
async function saved() { await expect(page.getByRole("status")).toHaveText("所有更改已保存"); }
async function execute(command) {
  switch (command.operation) {
    case "open":
      title = command.title;
      await page.goto(`${command.endpoint}team`);
      await page.getByLabel("登录账号", { exact: true }).fill(command.username);
      await page.getByLabel("密码", { exact: true }).fill(command.password);
      await page.getByRole("button", { name: "登录", exact: true }).click();
      await page.getByRole("button", { name: title, exact: true }).first().click();
      await saved(); return {};
    case "offline": await context.setOffline(command.value); return {};
    case "insert":
      await body.click(); await page.keyboard.press(command.end ? "Control+End" : "Control+Home");
      for (let i = 0; i < (command.offset ?? 0); i++) await page.keyboard.press("ArrowRight");
      if (command.italic) await page.keyboard.press("Control+i");
      await page.keyboard.insertText(command.text);
      if (command.italic) await page.keyboard.press("Control+i");
      return { text: await body.innerText() };
    case "expect":
      for (const text of command.contains ?? []) await expect(body).toContainText(text);
      for (const text of command.absent ?? []) await expect(body).not.toContainText(text);
      if (command.bold) await expect(body.locator("strong")).toContainText(command.bold);
      if (command.italic) await expect(body.locator("em")).toContainText(command.italic);
      await saved(); return { text: await body.innerText() };
    case "undo": await body.click(); await page.keyboard.press("Control+z"); await saved(); return {};
    case "reply":
      if (!await page.locator(".shared-comments").count()) await page.locator(".shared-top-actions").getByRole("button", { name: /^评论/ }).click();
      await expect(page.locator(".shared-message")).toContainText([command.parent]);
      await page.locator(".shared-message").filter({ hasText: command.parent }).getByRole("button", { name: "回复", exact: true }).click();
      await page.getByRole("textbox", { name: "评论内容" }).fill(command.text);
      await page.getByRole("button", { name: /发送 ↑/ }).click(); await saved(); return {};
    case "comments":
      if (!await page.locator(".shared-comments").count()) await page.locator(".shared-top-actions").getByRole("button", { name: /^评论/ }).click();
      for (const text of command.contains) await expect(page.locator(".shared-comment-list")).toContainText(text);
      await mkdir("artifacts/shared-qa/screens", { recursive: true });
      await page.screenshot({ path: "artifacts/shared-qa/screens/shared-mixed-browser.png" }); return {};
    case "reload": await page.reload(); await page.getByRole("button", { name: title, exact: true }).first().click(); await saved(); return {};
    case "quit": return {};
    default: throw new Error(`Unknown operation ${command.operation}`);
  }
}
try {
  for await (const line of createInterface({ input: process.stdin, crlfDelay: Infinity })) {
    let command;
    try { command = JSON.parse(line); const result = await execute(command); process.stdout.write(JSON.stringify({ ok: true, ...result }) + "\n"); }
    catch (error) { process.stdout.write(JSON.stringify({ ok: false, error: error.stack ?? String(error) }) + "\n"); }
    if (command?.operation === "quit") break;
  }
} finally { await browser.close(); process.stdin.destroy(); }
