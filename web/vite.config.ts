/// <reference types="vitest/config" />
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// The console is served by the bridge API (src/Bridge.Api/wwwroot) in production; in development
// Vite proxies the API, which runs in demo mode: `dotnet run --project src/Bridge.Api`.
const api = process.env["API_URL"] ?? "http://localhost:5080";
const proxy = Object.fromEntries(["/v1", "/graphql", "/dev", "/healthz"].map((p) => [p, { target: api, changeOrigin: true }]));

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy },
  preview: { port: 4173, proxy },
  build: { sourcemap: true },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./test/setup.ts"],
    css: true,
    include: ["test/**/*.test.{ts,tsx}"],
  },
});
