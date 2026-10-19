import { test, expect } from '@playwright/test';
import { mountToolbar } from './helpers/toolbar.mjs';

const activeMission = () => ({
  id: '00000000-0000-4000-8000-000000000002', name: 'Toolbar layout recording', mode: 'universal',
  startedUtc: new Date(Date.now() - 12000).toISOString(), sampleCount: 9
});

test('one responsive interface retains navigation, mission and mocked CommBus state', async ({ page }) => {
  await mountToolbar(page, { autoReply: true, activeMission: activeMission() });
  await expect(page.getByRole('checkbox')).toHaveCount(0);
  await expect(page.locator('nav new-push-button')).toHaveText(['Mission', 'Payload', 'Map']);
  await page.locator('#as-payload-page').click();
  await expect(page.locator('#as-payload-view')).toBeVisible();
  await expect(page.locator('#as-mission-view')).toBeHidden();
  await page.evaluate(() => {
    window.bus.state.activeMission.sampleCount = 12;
    window.bus.state.telemetry.altitudeMslFeet = 60250;
  });
  await expect(page.locator('#as-samples')).toHaveText('12 samples');
  await expect(page.locator('#as-altitude')).toHaveText('60,250');
  await expect(page.locator('#as-payload-view')).toBeVisible();
  await page.locator('#as-mission-page').click();
  await expect(page.locator('#as-active-name')).toHaveText('Toolbar layout recording');
  await expect(page.locator('#as-active-mode')).toContainText('Universal');
  expect(await page.evaluate(() => window.listenerRegistrations)).toBe(1);
  expect(await page.evaluate(() => window.bus.sent.filter(x => x.command.op !== 'state' && !x.command.op.startsWith('map-')))).toEqual([]);
});

test('content follows supplied host bounds (browser fixture, not native dragging)', async ({ page }) => {
  await mountToolbar(page, { autoReply: true });
  for (const dimensions of [{ width: 320, height: 360 }, { width: 640, height: 720 }, { width: 900, height: 900 }]) {
    await page.locator('#aeroscience-frame').evaluate((frame, size) => {
      // Model bounds supplied by the host; do not fake native pointer handlers.
      Object.assign(frame.style, { left: '30px', top: '20px', width: `${size.width}px`, height: `${size.height}px` });
    }, dimensions);
    const geometry = await page.evaluate(() => {
      const frame = document.getElementById('aeroscience-frame').getBoundingClientRect();
      const root = document.getElementById('as-root').getBoundingClientRect();
      const scroll = document.getElementById('as-scroll');
      const content = scroll.getBoundingClientRect();
      return { x: frame.x, y: frame.y, width: frame.width, height: frame.height,
        right: frame.right, bottom: frame.bottom, rootRight: root.right, contentBottom: content.bottom,
        scrollHeight: scroll.clientHeight, horizontalOverflow: scroll.scrollWidth > scroll.clientWidth };
    });
    expect(geometry.x).toBe(30); expect(geometry.y).toBe(20);
    expect(geometry.width).toBe(dimensions.width); expect(geometry.height).toBe(dimensions.height);
    expect(geometry.rootRight).toBeLessThanOrEqual(geometry.right + 1);
    expect(geometry.contentBottom).toBeLessThanOrEqual(geometry.bottom + 1);
    expect(geometry.scrollHeight).toBeGreaterThanOrEqual(48);
    expect(geometry.horizontalOverflow).toBe(false);
  }
});

test('browser wheel scrolls bounded content and exposes mission controls', async ({ page }) => {
  await mountToolbar(page, { autoReply: true, activeMission: activeMission() });
  await page.locator('#aeroscience-frame').evaluate(frame => {
    Object.assign(frame.style, { width: '320px', height: '360px' });
  });
  const scroll = page.getByRole('region', { name: 'Mission and payload content' });
  expect(await scroll.evaluate(node => node.scrollHeight > node.clientHeight)).toBe(true);
  await scroll.hover(); await page.mouse.wheel(0, 800);
  await expect.poll(() => scroll.evaluate(node => node.scrollTop)).toBeGreaterThan(0);
  await page.locator('#as-end').scrollIntoViewIfNeeded();
  await expect(page.locator('#as-end')).toBeInViewport();
  await expect(page.locator('header')).toBeInViewport();
  expect(await page.evaluate(() => window.scrollY)).toBe(0);
  await page.locator('#as-end').click();
  expect(await page.evaluate(() => window.bus.sent.find(x => x.command.op === 'end').command.missionId))
    .toBe('00000000-0000-4000-8000-000000000002');
});

test('hiding and reconnecting the mocked panel retains the central mission', async ({ page }) => {
  await mountToolbar(page, { autoReply: true, activeMission: activeMission() });
  await page.locator('#aeroscience-frame').evaluate(frame => frame.classList.add('panelInvisible'));
  const beforeHidden = await page.evaluate(() => window.bus.sent.length);
  await page.waitForTimeout(1100); // Cover one real one-second poll interval while hidden.
  expect(await page.evaluate(() => window.bus.sent.length)).toBe(beforeHidden);
  await page.locator('#aeroscience-frame').evaluate(frame => frame.classList.remove('panelInvisible'));
  await expect.poll(() => page.evaluate(() => window.bus.sent.length)).toBeGreaterThan(beforeHidden);
  await page.evaluate(() => {
    window.panel = document.querySelector('ingamepanel-aeroscience');
    window.panel.remove();
    window.bus.state.activeMission.sampleCount = 18;
    document.body.append(window.panel);
  });
  await expect(page.locator('#as-samples')).toHaveText('18 samples');
  await expect(page.locator('#as-active-name')).toHaveText('Toolbar layout recording');
  expect(await page.evaluate(() => window.bus.unregistered)).toBe(1);
  expect(await page.evaluate(() => window.listenerRegistrations)).toBe(2);
  expect(await page.evaluate(() => window.bus.sent.filter(x => x.command.op !== 'state' && !x.command.op.startsWith('map-')))).toEqual([]);
});
