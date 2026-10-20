import { test, expect } from "@playwright/test";
import { mountToolbar } from "./helpers/toolbar.mjs";
test("planner, actual stored science pages, map and complete exports work together in a test host", async ({
  page,
  request,
}) => {
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  const external = [];
  page.on("request", (r) => {
    if (!r.url().startsWith("http://127.0.0.1:5080")) external.push(r.url());
  });
  await page.goto("/");
  await expect(page.locator("#altitude")).not.toHaveText("—");
  await expect(
    page.getByText("Ready for your next mission.", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("link", { name: "Mission Planner", exact: true })
    .click();
  await page
    .getByLabel("Plan name", { exact: true })
    .fill("Browser atmospheric plan");
  await page
    .getByLabel("Template", { exact: true })
    .selectOption("atmosphericProfile");
  await page.getByText("Advanced mission details & export settings", {exact:true}).click();
  await page
    .getByLabel("Estimated duration (minutes)", { exact: true })
    .fill("10");
  await page
    .getByRole("button", { name: "Estimate scientific storage", exact: true })
    .click();
  await expect(page.locator("#plan-form")).toContainText(
    "Estimated science storage:",
  );
  await page.getByRole("button", { name: "Add waypoint", exact: true }).click();
  await page
    .getByLabel("Latitude (degrees north)", { exact: true })
    .fill("34.9");
  await page
    .getByLabel("Longitude (degrees east)", { exact: true })
    .fill("-117.9");
  await page
    .getByRole("button", { name: "Save Waypoint", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Save mission plan", exact: true })
    .click();
  await expect(page.locator(".plan-row").first()).toContainText(
    "Browser atmospheric plan",
  );
  await page.screenshot({
    path: "artifacts/research-planner-browser-fixture.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Select for Flight", exact: true })
    .first()
    .click();
  await page.getByRole("button",{name:"Start mission",exact:true}).click();
  await expect(page.locator("#active-name")).toHaveText(
    "Browser atmospheric plan",
  );
  await page.getByLabel("Notes", { exact: true }).fill("Weather observation");
  await page
    .getByRole("button", { name: "Add observation marker", exact: true })
    .click();
  await page.goto("/#science");
  await expect(page.locator("#instrument-values")).toContainText(
    "Simulator-reported",
  );
  await expect
    .poll(async () => await page.locator("#instrument-values").textContent())
    .toMatch(/[1-9]\d* measurement values/);
  await expect(page.locator("#science-charts canvas").first()).toBeVisible({
    timeout: 10000,
  });
  await page.screenshot({
    path: "artifacts/research-instruments-browser-fixture.png",
    fullPage: true,
  });
  await page
    .getByRole("link", { name: "Mission Control", exact: true })
    .click();
  await expect(page.locator("#flight-guidance")).toContainText("HDG");
  await expect(page.locator("#mach")).toHaveText("Mach 0.82");
  await page.getByRole("button", { name: "End mission", exact: true }).click();
  await expect(page.locator("#mission-idle")).toBeVisible();
  await page
    .getByRole("link", { name: "Mission History", exact: true })
    .click();
  await page.getByRole("button", { name: "View mission" }).first().click();
  await expect(page.locator("#detail-content")).toContainText(
    "Research review",
  );
  await expect(page.locator("#detail-content")).toContainText(
    "Weather observation",
  );
  await expect(
    page.getByRole("link", {
      name: "Scientific observations CSV",
      exact: true,
    }),
  ).toBeVisible();
  await page
    .getByText("Research review", { exact: true })
    .scrollIntoViewIfNeeded();
  await page.screenshot({
    path: "artifacts/research-review-browser-fixture.png",
    fullPage: true,
  });
  expect(errors).toEqual([]);
  expect(external).toEqual([]);
});
test("planner remains usable at iPad and narrow browser widths", async ({
  page,
}) => {
  for (const width of [744, 375]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto("/#planner");
    await expect(page.locator("#plan-form")).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    await page.goto("/#science");
    await expect(page.locator("#live-instrument-config")).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
  }
});
test("toolbar container has no focus border while controls retain keyboard focus styling", async ({
  page,
}) => {
  await mountToolbar(page, { autoReply: true });
  await page.locator("#as-scroll").focus();
  expect(
    await page
      .locator("#as-scroll")
      .evaluate((e) => getComputedStyle(e).outlineStyle),
  ).toBe("none");
  await page.locator("#as-name").focus();
  expect(
    await page
      .locator("#as-name")
      .evaluate((e) => getComputedStyle(e).outlineStyle),
  ).not.toBe("none");
});
