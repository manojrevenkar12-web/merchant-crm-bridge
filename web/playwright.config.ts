import { defineConfig, devices } from "@playwright/test";

// Runs against the bridge API in demo mode serving the built console (see .github/workflows/ci.yml).
export default defineConfig({
  testDir: "e2e",
  timeout: 45_000,
  retries: process.env["CI"] ? 1 : 0,
  reporter: process.env["CI"] ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: process.env["E2E_BASE_URL"] ?? "http://localhost:5080",
    trace: "retain-on-failure",
    locale: "en-GB",
    launchOptions: process.env["PW_CHROMIUM_PATH"] ? { executablePath: process.env["PW_CHROMIUM_PATH"] } : {},
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 960 } } },
    { name: "mobile", use: { ...devices["Pixel 7"] } },
  ],
});
