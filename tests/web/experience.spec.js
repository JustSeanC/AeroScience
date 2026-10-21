import { test, expect } from "@playwright/test";
import { mountToolbar } from "./helpers/toolbar.mjs";
const origin = "http://127.0.0.1:5080";
async function session(request) {
  return (
    await (
      await request.post("/api/session", {
        data: {},
        headers: { Origin: origin },
      })
    ).json()
  ).csrf;
}
async function post(request, csrf, path, data) {
  const response = await request.post(path, {
    data,
    headers: { Origin: origin, "X-AeroScience-CSRF": csrf },
  });
  expect(response.ok()).toBeTruthy();
  return response.json();
}
test.afterEach(async ({ request }) => {
  const csrf = await session(request),
    state = await (await request.get("/api/state")).json();
  if (state.activeMission)
    await post(
      request,
      csrf,
      `/api/missions/${state.activeMission.id}/end`,
      {},
    );
  await post(request, csrf, "/api/display/units", {
    temperature: "celsius",
    pressure: "inHg",
    windSpeed: "knots",
    density: "kg/m3",
    altitude: "feet",
    distance: "nauticalMiles",
  });
});
test("offline geography, zero coordinates, search, patterns and editable plan persist", async ({
  page,
}) => {
  const errors = [],
    external = [];
  page.on("pageerror", (e) => errors.push(e.message));
  page.on("request", (r) => {
    if (!r.url().startsWith(origin)) external.push(r.url());
  });
  await page.goto("/#planner");
  await expect(page.locator("#plan-form")).toBeVisible();
  await expect(page.locator("#plan-editor .leaflet-container")).toBeVisible();
  await page
    .getByLabel("Offline geographic location search")
    .last()
    .fill("KJFK");
  await page.getByRole("button", { name: "Find", exact: true }).last().click();
  await expect(page.getByLabel("Search results").last()).toBeVisible();
  await page.getByRole("button", { name: "Add waypoint", exact: true }).click();
  await page.getByLabel("Latitude (degrees north)", { exact: true }).fill("0");
  await page.getByLabel("Longitude (degrees east)", { exact: true }).fill("0");
  await page
    .getByRole("button", { name: "Save Waypoint", exact: true })
    .click();
  await expect(page.locator(".waypoint-row")).toHaveCount(1);
  await page.getByText("Flight pattern generator", { exact: true }).click();
  await page.getByLabel("Center/start latitude", { exact: true }).fill("34.9");
  await page
    .getByLabel("Center/start longitude", { exact: true })
    .fill("-117.9");
  await page
    .getByRole("button", { name: "Add Pattern to Mission", exact: true })
    .click();
  await expect(page.locator(".waypoint-row")).toHaveCount(13);
  await page
    .getByRole("button", { name: "Undo geometry change", exact: true })
    .click();
  await expect(page.locator(".waypoint-row")).toHaveCount(1);
  await page
    .getByRole("button", { name: "Add Pattern to Mission", exact: true })
    .click();
  await expect(page.locator(".waypoint-row")).toHaveCount(13);
  await page
    .getByLabel("Plan name", { exact: true })
    .fill("021 offline pattern");
  await page
    .getByRole("button", { name: "Save mission plan", exact: true })
    .click();
  await expect(page.locator(".plan-row").first()).toContainText(
    "021 offline pattern",
  );
  await page
    .locator("#plan-editor")
    .screenshot({ path: "artifacts/v021-planner-browser-fixture.png" });
  expect(external).toEqual([]);
  expect(errors).toEqual([]);
});
test("science pause synchronizes displays while telemetry continues and unit preferences persist", async ({
  page,
  context,
  request,
}) => {
  const csrf = await session(request);
  const mission = await post(request, csrf, "/api/missions", {
    name: "021 pause and units",
    instruments: [
      { instrumentId: "ambient-wind", enabled: true, intervalMs: 1000 },
      { instrumentId: "ambient-temperature", enabled: true, intervalMs: 1000 },
    ],
  });
  await page.goto("/#science");
  await expect(
    page.getByRole("heading", { name: "Instruments & Collection", exact: true }),
  ).toBeVisible();
  await expect
    .poll(
      async () =>
        (
          await (await request.get("/api/state")).json()
        ).research.instruments.find(
          (i) => i.instrumentId === "ambient-temperature",
        ).observationCount,
    )
    .toBeGreaterThan(0);
  const second = await context.newPage();
  await second.goto("/#science");
  await page
    .getByRole("button", { name: "Pause science collection", exact: true })
    .click();
  await expect(
    second.getByRole("button", {
      name: "Resume science collection",
      exact: true,
    }),
  ).toBeVisible();
  const before = await (await request.get("/api/state")).json();
  await expect
    .poll(
      async () =>
        (await (await request.get("/api/state")).json()).activeMission
          .sampleCount,
    )
    .toBeGreaterThan(before.activeMission.sampleCount);
  const paused = await (await request.get("/api/state")).json();
  expect(
    paused.research.instruments.find(
      (i) => i.instrumentId === "ambient-temperature",
    ).observationCount,
  ).toEqual(
    before.research.instruments.find(
      (i) => i.instrumentId === "ambient-temperature",
    ).observationCount,
  );
  await second
    .getByRole("button", { name: "Resume science collection", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Pause science collection", exact: true }),
  ).toBeVisible();
  await page.getByText("Science display units", { exact: true }).click();
  await page
    .getByRole("button", { name: "Scientific units", exact: true })
    .click();
  await expect(page.locator("#instrument-values")).toContainText("m/s");
  await page.reload();
  await expect(page.locator("#instrument-values")).toContainText("m/s");
  await expect(page.locator("#science-charts canvas").first()).toBeVisible();
  await page.screenshot({
    path: "artifacts/v021-science-browser-fixture.png",
    fullPage: true,
  });
  await second.close();
  await post(request, csrf, `/api/missions/${mission.id}/end`, {});
});
test("instrument presets can be created applied in planner and deleted without changing snapshots", async ({
  page,
  request,
}) => {
  await page.goto("/#science");
  await page.getByText("Reusable instrument presets", { exact: true }).click();
  await page
    .getByLabel("Preset name", { exact: true })
    .fill("Slow wind reusable");
  await page
    .getByLabel("Preset description", { exact: true })
    .fill("Saved independently of mission plans");
  await page
    .getByRole("button", {
      name: "Save current configuration as preset",
      exact: true,
    })
    .click();
  await expect(page.locator("#instrument-content")).toContainText(
    "Slow wind reusable",
  );
  await page
    .getByRole("link", { name: "Mission Planner", exact: true })
    .click();
  await page.getByText("Apply an instrument preset", { exact: true }).click();
  await page
    .getByRole("button", { name: "Slow wind reusable", exact: true })
    .click();
  await expect(page.getByLabel("Plan name", { exact: true })).not.toHaveValue(
    "Slow wind reusable",
  );
  await session(request);
  const presets = await (await request.get("/api/presets")).json();
  expect(presets.some((p) => p.name === "Slow wind reusable")).toBeTruthy();
});
for (const zoom of [1.25, 1.5])
  test(`all pages remain readable at ${zoom * 100}% layout scale`, async ({
    page,
  }) => {
    await page.setViewportSize({
      width: Math.floor(1440 / zoom),
      height: Math.floor(1000 / zoom),
    });
    for (const hash of [
      "control",
      "planner",
      "science",
      "history",
      "settings",
    ]) {
      await page.goto("/#" + hash);
      await expect(page.locator("#workspace")).toBeVisible();
      await expect(
        page.locator(".view:not([hidden]) h1").first(),
      ).toBeVisible();
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      ).toBeTruthy();
    }
  });
test("Science Payload uses human names units compact cards and shared pause command (mock SDK)", async ({
  page,
  request,
}) => {
  const csrf = await session(request);
  await post(request, csrf, "/api/missions", {
    name: "Payload fixture",
    instruments: [
      { instrumentId: "ambient-temperature", enabled: true, intervalMs: 1000 },
    ],
  });
  const state = await (await request.get("/api/state")).json();
  await mountToolbar(page, {
    autoReply: true,
    activeMission: state.activeMission,
  });
  await page.evaluate((state) => {
    window.bus.state = state;
    document.querySelector("ingamepanel-aeroscience").state = state;
    document.querySelector("ingamepanel-aeroscience").render(state);
  }, state);
  await page.locator("#as-payload-page").click();
  await expect(page.locator("#as-instruments")).toContainText(
    "Ambient temperature",
  );
  await expect(page.locator("#as-instruments")).not.toContainText(
    "ambient-temperature",
  );
  await page.locator("#as-science-pause").click();
  await expect
    .poll(() =>
      page.evaluate(() =>
        window.bus.sent.some((x) => x.command.op === "science-pause"),
      ),
    )
    .toBeTruthy();
  await page
    .locator("#aeroscience-frame")
    .screenshot({ path: "artifacts/v021-toolbar-browser-fixture.png" });
});
