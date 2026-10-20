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
async function post(request, path, data) {
  const csrf = await session(request);
  const r = await request.post(path, {
    data,
    headers: { Origin: origin, "X-AeroScience-CSRF": csrf },
  });
  expect(r.ok()).toBeTruthy();
  return r.json();
}
test.afterEach(async ({ request }) => {
  await post(request, "/api/maps/settings", {
    onlineEnabled: false,
    layer: "offline",
    privacyAccepted: false,
  });
});
test("offline basemap and export preview preserve route precision without external requests", async ({
  page,
  request,
}) => {
  const plan = await post(request, "/api/plans", {
    id: crypto.randomUUID(),
    name: "Geographic export <&>",
    cruisingAltitudeFeet: 7500,
    waypoints: [
      { name: "Custom A", latitude: 36.0716666667, longitude: -75.9521666667 },
      {
        name: "Custom B",
        latitude: 36.08,
        longitude: -75.94,
        targetAltitudeFeet: 8500,
      },
    ],
  });
  const external = [];
  page.on("request", (r) => {
    if (!r.url().startsWith(origin)) external.push(r.url());
  });
  await page.goto("/#planner");
  await expect(page.locator("#plan-form")).toBeVisible();
  await expect(page.getByLabel("Map Layers").last()).toHaveValue("offline");
  const row = page.locator(".plan-row").filter({ hasText: plan.name });
  await row.getByRole("button", { name: "Export / Share Route" }).click();
  const dialog = page.locator("dialog[open]");
  await dialog.getByRole("button", { name: "Preview export" }).click();
  await expect(dialog.getByLabel("Export text preview")).toHaveValue(
    /<AppVersionMajor>12<\/AppVersionMajor>/,
  );
  await expect(dialog.getByLabel("Export text preview")).toHaveValue(
    /AS000001/,
  );
  await expect(dialog.getByLabel("Export text preview")).not.toHaveValue(
    /\+000000\.00/,
  );
  const download = page.waitForEvent("download");
  await dialog.getByRole("button", { name: "Download", exact: true }).click();
  expect((await download).suggestedFilename()).toBe(
    `AeroScience-plan-${plan.id.replaceAll("-", "")}.pln`,
  );
  await dialog.getByLabel("Export format").selectOption("coordinates");
  await dialog.getByLabel("Coordinate format").selectOption("ddm");
  await dialog.getByRole("button", { name: "Preview export" }).click();
  await expect(dialog.getByLabel("Export text preview")).toHaveValue(
    /N3604\.3000 W07557\.1300/,
  );
  await dialog.getByLabel("Export format").selectOption("route");
  await expect(dialog.getByLabel("Coordinate format")).toHaveValue("simbrief");
  await dialog.getByRole("button", { name: "Preview export" }).click();
  await expect(dialog.getByLabel("Export text preview")).toHaveValue(
    /360418N0755708W/,
  );
  await expect(
    dialog.getByText("Values round to whole arcseconds", { exact: false }),
  ).toBeVisible();
  expect(external).toEqual([]);
  await page.screenshot({ path: "artifacts/v0.2.2-export-browser.png" });
});
test("provider settings require deliberate consent and a key while offline geometry stays available", async ({
  page,
}) => {
  await page.goto("/#settings/maps");
  await expect(
    page.getByRole("heading", { name: "Maps & Basemaps" }),
  ).toBeVisible();
  await page.getByLabel("Enable online maps").check();
  await page.getByLabel("Preferred map layer").selectOption("satellite");
  await page.getByRole("button", { name: "Save map settings" }).click();
  await expect(
    page.getByText(
      "Review and accept the online-map privacy and billing notice first.",
    ),
  ).toBeVisible();
  await page
    .getByLabel(
      "I understand the privacy disclosure and provider billing/usage conditions.",
    )
    .check();
  await page.getByRole("button", { name: "Save map settings" }).click();
  await expect(
    page.getByText(
      "Configure your own public Esri basemap API key for satellite imagery, or use Offline/Streets.",
    ),
  ).toBeVisible();
  await page.getByLabel("Preferred map layer").selectOption("offline");
  await page.getByLabel("Enable online maps").uncheck();
  await page.getByRole("button", { name: "Save map settings" }).click();
  await expect(
    page.getByText("Map settings saved.", { exact: false }),
  ).toBeVisible();
  await page.reload();
  await expect(page.getByLabel("Preferred map layer")).toHaveValue("offline");
  await expect(page.getByLabel("Enable online maps")).not.toBeChecked();
});
test("online tile failures fall back without changing the saved plan", async ({
  page,
  request,
}) => {
  const saved = await post(request, "/api/plans", {
    id: crypto.randomUUID(),
    name: "Fallback geographic route",
    waypoints: [{ name: "Keep", latitude: 0, longitude: 179.99 }],
  });
  await post(request, "/api/maps/settings", {
    onlineEnabled: true,
    layer: "streets",
    privacyAccepted: true,
  });
  await page.route("https://tile.openstreetmap.org/**", (route) =>
    route.abort(),
  );
  await page.goto("/#planner");
  await expect(page.getByLabel("Map Layers").last()).toHaveValue("streets");
  await expect(
    page
      .getByText(
        "Online provider unavailable or quota/authentication failed.",
        { exact: false },
      )
      .last(),
  ).toBeVisible();
  const data = await (await request.get(`/api/plans/${saved.id}`)).json();
  expect(data.waypoints[0].longitude).toBe(179.99);
  await page.getByLabel("Map Layers").last().selectOption("offline");
  await expect(
    page.getByLabel("Map Layers").last(),
  ).toHaveValue("offline");
});
test("hidden browser maps do not load online tiles", async ({
  page,
  request,
}) => {
  await post(request, "/api/maps/settings", {
    onlineEnabled: true,
    layer: "streets",
    privacyAccepted: true,
  });
  const tiles = [];
  page.on("request", (r) => {
    if (r.url().startsWith("https://tile.openstreetmap.org/"))
      tiles.push(r.url());
  });
  await page.route("https://tile.openstreetmap.org/**", (route) =>
    route.abort(),
  );
  await page.goto("/#settings/maps");
  await expect(
    page.getByRole("heading", { name: "Maps & Basemaps" }),
  ).toBeVisible();
  await page.waitForTimeout(1100);
  expect(tiles).toEqual([]);
  await page
    .getByRole("link", { name: "Mission Control", exact: true })
    .click();
  await expect.poll(() => tiles.length).toBeGreaterThan(0);
});
test("native Map browser fixture synchronizes cached chunks, pan/follow and bounded stale track", async ({
  page,
}) => {
  await mountToolbar(page, { autoReply: true });
  await page.evaluate(() => {
    const original = window.bus.callSimConnect.bind(window.bus);
    window.bus.callSimConnect = function (event, payload) {
      const cmd = JSON.parse(payload);
      if (!cmd.op.startsWith("map-")) return original(event, payload);
      this.sent.push({ event, command: cmd });
      let map;
      if (cmd.op === "map-state")
        map = {
          revision: "fixture-route-1",
          name: "Mocked 1000 point route",
          missionId: "fixture",
          items: 1000,
          waypoints: 1000,
          nextWaypoint: 0,
          aircraft: {
            latitude: 36,
            longitude: -76,
            headingDegrees: 90,
            timestampUtc: new Date().toISOString(),
          },
          track: [
            {
              sequence: 1,
              latitude: 36,
              longitude: -76,
              timestampUtc: new Date().toISOString(),
            },
          ],
          nextSequence: 1,
          markers: [],
          progressExplanation:
            "Browser fixture only; native simulator unverified.",
        };
      else if (cmd.op === "map-chunk") {
        const end = Math.min(1000, cmd.offset + 40);
        map = {
          revision: "fixture-route-1",
          offset: cmd.offset,
          items: Array.from({ length: end - cmd.offset }, (_, i) => ({
            kind: "waypoint",
            index: cmd.offset + i,
            id: "fixture-" + (cmd.offset + i),
            name: "Survey " + (cmd.offset + i + 1),
            latitude: 36 + (cmd.offset + i) * 0.0001,
            longitude: -76,
          })),
          nextOffset: end,
          complete: end === 1000,
        };
      } else map = [];
      setTimeout(
        () =>
          this.handlers["AeroScience.Reply.v1"]?.(
            JSON.stringify({
              protocol: 1,
              requestId: cmd.requestId,
              ok: true,
              map,
            }),
          ),
        0,
      );
    };
  });
  await page.locator("#as-map-page").click();
  await expect(page.locator("#as-map-view")).toBeVisible();
  await expect(page.locator("#as-map-waypoint option")).toHaveCount(1000, {
    timeout: 15000,
  });
  await expect(page.locator("#as-map-waypoint-info")).toContainText("Survey 1");
  await page.locator("#as-map-waypoint").selectOption("999");
  await expect(page.locator("#as-map-waypoint-info")).toContainText(
    "Survey 1000",
  );
  await page.locator("#as-map-free").click();
  await expect(page.locator("#as-map-status")).toContainText("Free Pan");
  await page.locator("#as-map-in").click();
  await page.locator("#as-map-fit").click();
  const sent = await page.evaluate(
    () => window.bus.sent.filter((s) => s.command.op === "map-chunk").length,
  );
  await page.waitForTimeout(1300);
  expect(
    await page.evaluate(
      () => window.bus.sent.filter((s) => s.command.op === "map-chunk").length,
    ),
  ).toBe(sent);
  expect(
    await page.evaluate(() =>
      document.getElementById("aeroscience-frame").getAttribute("resize"),
    ),
  ).toBe("both");
  await page.evaluate(() => {
    const panel = document.querySelector("ingamepanel-aeroscience");
    panel.remove();
    document.body.appendChild(panel);
  });
  await expect(page.locator("#as-map-waypoint option")).toHaveCount(1000, {
    timeout: 15000,
  });
  await expect(page.locator("#as-map-canvas canvas")).toHaveCount(2);
  await expect
    .poll(() =>
      page.evaluate(() => {
        const panel = document.querySelector("ingamepanel-aeroscience");
        return panel.loadingRevision === null && panel.mapRenderer.items.length;
      }),
    )
    .toBe(1000);
  await page.evaluate(() => {
    const frame = document.getElementById("aeroscience-frame");
    frame.style.width = "960px";
    frame.style.height = "960px";
  });
  await page.locator("#as-map-fit").click();
  await page.locator("#aeroscience-frame").screenshot({
    path: "artifacts/v0.2.2-toolbar-browser-fixture.png",
  });
  await page.locator("#as-mission-page").click();
  await expect(page.locator("#as-map-view")).toBeHidden();
  const wrongTransport = await page.evaluate(() =>
    window.bus.sent.some((s) => !s.event.startsWith("AeroScience.Command.")),
  );
  expect(wrongTransport).toBe(false);
});

test("optional map renderer failure preserves Mission and Payload", async ({
  page,
}) => {
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await mountToolbar(page, { autoReply: true, mapFails: true });
  await page.locator("#as-map-page").click();
  await expect(page.locator("#as-map-status")).toContainText(
    "map renderer is unavailable",
  );
  await page.locator("#as-payload-page").click();
  await expect(page.locator("#as-payload-view")).toBeVisible();
  await page.locator("#as-mission-page").click();
  await expect(page.locator("#as-mission-view")).toBeVisible();
  await expect(page.locator("#as-source")).toHaveText("SIMULATED TEST DATA");
  expect(errors).toEqual([]);
});

test("selected native saved plan is included in Start mission", async ({
  page,
}) => {
  await mountToolbar(page, { autoReply: true });
  await page.evaluate(() => {
    const original = window.bus.callSimConnect.bind(window.bus);
    window.bus.callSimConnect = function (event, payload) {
      const cmd = JSON.parse(payload);
      if (!cmd.op.startsWith("map-")) return original(event, payload);
      this.sent.push({ event, command: cmd });
      const map =
        cmd.op === "map-plans"
          ? [
              {
                id: "aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa",
                name: "Saved native survey",
                mode: "realistic",
              },
            ]
          : {
              revision: "none",
              name: "",
              items: 0,
              waypoints: 0,
              nextWaypoint: 0,
              aircraft: null,
              track: [],
              markers: [],
              nextSequence: 0,
              progressExplanation: "Browser fixture only.",
            };
      setTimeout(
        () =>
          this.handlers["AeroScience.Reply.v1"]?.(
            JSON.stringify({
              protocol: 1,
              requestId: cmd.requestId,
              ok: true,
              map,
            }),
          ),
        0,
      );
    };
  });
  await page.locator("#as-map-page").click();
  await page.locator("#as-mission-page").click();
  await page.locator("#as-map-plans").click();
  await expect(page.locator("#as-map-plan option")).toHaveCount(2);
  await page
    .locator("#as-map-plan")
    .selectOption("aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa");
  await page.locator("#as-mission-page").click();
  await expect(page.locator("#as-start-plan")).toContainText(
    "Saved native survey",
  );
  await expect(page.locator("#as-mode")).toHaveValue("realistic");
  await expect(page.locator("#as-start")).not.toHaveAttribute("disabled", "");
  await page.locator("#as-start").click();
  const command = await page.evaluate(
    () => window.bus.sent.find((s) => s.command.op === "start").command,
  );
  expect(command.planId).toBe("aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa");
  expect(command.mode).toBe("realistic");
});
