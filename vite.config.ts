// Note: devUrl 仅在 tauri dev 生效；debug exe 单独运行连不上 1420 — 见 .agents/notes/implemented/process/2026-09-08-release-vs-debug-launch.md
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const host = process.env.TAURI_DEV_HOST;

// base: "./" 使构建产物可用 file:// 直接预览（浏览器演示模式）
export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  base: process.env.WRITEME_WEB_BASE || "./",
  server: {
    port: 1420,
    strictPort: true,
    host: host || false,
    hmr: host
      ? { protocol: "ws", host, port: 1421 }
      : undefined,
    watch: { ignored: ["**/src-tauri/**"] },
    proxy: { "/api": { target: process.env.WRITEME_DEV_SERVER || "http://127.0.0.1:8787", ws: true } },
  },
});
