import { defineConfig } from "@playwright/test";
const endpoint = process.env.WRITEME_QA_URL;
export default defineConfig({
  testDir: "./tests/shared", fullyParallel: false, workers: 1, timeout: 60_000, expect: { timeout: 10000 },
  use: { baseURL: endpoint || "http://127.0.0.1:8791", viewport: { width: 1440, height: 960 }, trace: "retain-on-failure", screenshot: "only-on-failure" },
  // Override only for a disposable QA server: this suite creates accounts and workspaces.
  webServer: endpoint ? undefined : { command: "powershell -NoProfile -File scripts/Start-SharedQa.ps1", url: "http://127.0.0.1:8791/health", timeout: 30_000, reuseExistingServer: !process.env.CI },
});
