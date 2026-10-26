import { expect, test } from "@playwright/test";

// Against the bridge API in demo mode (emulated Dynamics 365, simulated platform, sales activity),
// serving this console. Nothing is stubbed: what the page shows came through the bridge.

test("an operator follows a merchant from the overview to its events", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Where events are" })).toBeVisible();

  await page.getByRole("link", { name: "Merchants" }).first().click();
  await expect(page.getByRole("heading", { name: "Merchants", level: 1 })).toBeVisible();
  await page.getByLabel("Stage").selectOption("live");
  const first = page.locator('a[href^="/merchants/M-"]:visible').first();
  await expect(first).toBeVisible({ timeout: 30_000 });
  const href = await first.getAttribute("href");
  await first.click();

  await expect(page).toHaveURL(new RegExp(`${href}$`));
  await expect(page.getByRole("table", { name: "CRM and platform side by side" })).toBeVisible();
  await expect(page.getByText("Dynamics 365", { exact: true })).toBeVisible();
  await expect(page.getByRole("list", { name: "Events, newest first" })).toContainText(
    "Onboarding requested",
  );
  await expect(page.getByText(/•••• \d{4}/)).toBeVisible(); // the IBAN is masked
});

test("search finds a merchant by its id", async ({ page }) => {
  await page.goto("/merchants");
  const id = await page.locator('a[href^="/merchants/M-"]:visible').first().getAttribute("href");
  await page.getByLabel("Find a merchant").fill(id!.split("/").pop()!);
  await expect(page.locator('a[href^="/merchants/M-"]:visible')).toHaveCount(1, { timeout: 10_000 });
  await page.getByLabel("Find a merchant").fill("no such merchant");
  await expect(page.getByRole("heading", { name: "No merchant matches" })).toBeVisible();
});

test("dead letters and drift are reachable and reconciliation runs", async ({ page }) => {
  await page.goto("/dead-letters");
  await expect(page.getByRole("heading", { name: "Dead letters", level: 1 })).toBeVisible();
  await expect(
    page
      .getByRole("list")
      .or(page.getByRole("heading", { name: "No dead letters" }))
      .first(),
  ).toBeVisible();

  await page.getByRole("link", { name: "Drift" }).first().click();
  await page.getByRole("button", { name: "Reconcile now" }).click();
  await expect(page.getByText(/Checked \d+ merchants?/)).toBeVisible({ timeout: 30_000 });
});

test("an unknown merchant is explained", async ({ page }) => {
  await page.goto("/merchants/M-999999");
  await expect(page.getByRole("heading", { name: "No merchant M-999999" })).toBeVisible();
});
