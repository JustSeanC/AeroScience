import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { mountToolbar } from './helpers/toolbar.mjs';

const capture = readFileSync('scripts/debug/capture-toolbar-window.js', 'utf8');

test('opt-in capture observes targets, bounds and scrolling without consuming input or mission state', async ({ page }) => {
  await mountToolbar(page, { autoReply: true });
  await page.locator('#aeroscience-frame').evaluate(frame => {
    Object.assign(frame.style, { width: '320px', height: '360px' });
  });
  await page.addScriptTag({ content: capture });
  await page.locator('#as-payload-page').click();
  await expect(page.locator('#as-payload-view')).toBeVisible();
  await page.locator('#as-mission-page').click();
  const scroll = page.locator('#as-scroll');
  await scroll.hover(); await page.mouse.wheel(0, 800);
  await expect.poll(() => scroll.evaluate(node => node.scrollTop)).toBeGreaterThan(0);
  const report = await page.evaluate(() => {
    window.AeroScienceWindowDebug.snapshot('after-wheel');
    window.AeroScienceWindowDebug.stop();
    return JSON.parse(window.AeroScienceWindowDebug.report());
  });
  expect(report.stopped).toBe(true);
  expect(report.entries.some(e => e.kind === 'input' && e.type === 'mousedown' && e.target.includes('as-payload-page'))).toBe(true);
  const wheel = report.entries.find(e => e.kind === 'input' && e.type === 'wheel');
  expect(wheel.path).toContain('div#as-scroll.as-scroll');
  expect(wheel.defaultPreventedAtCapture).toBe(false);
  expect(wheel.frame).toEqual({ x: 0, y: 0, width: 320, height: 360 });
  const last = report.entries.at(-1);
  expect(last.elements.find(e => e.element === 'div#as-scroll.as-scroll').scrollTop).toBeGreaterThan(0);
  expect(await page.evaluate(() => window.bus.sent.filter(x => x.command.op !== 'state' && !x.command.op.startsWith('map-')))).toEqual([]);
  // Fixture observations verify the capture utility, not simulator title-bar/resize handlers.
});

test('capture is bounded, replaceable and detaches all observation on stop', async ({ page }) => {
  await mountToolbar(page, { autoReply: true });
  await page.addScriptTag({ content: capture });
  await page.addScriptTag({ content: capture });
  const report = await page.evaluate(() => {
    const frame = document.getElementById('aeroscience-frame');
    for (let i = 0; i < 200; i++) frame.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, clientX: 20, clientY: 20 }));
    window.AeroScienceWindowDebug.stop();
    return JSON.parse(window.AeroScienceWindowDebug.report());
  });
  expect(report.entries).toHaveLength(120);
  const before = await page.evaluate(() => window.AeroScienceWindowDebug.report());
  await page.locator('#as-payload-page').click();
  await expect(page.locator('#as-payload-view')).toBeVisible();
  await page.waitForTimeout(150); // Stopping also clears delayed post-input observations.
  expect(await page.evaluate(() => window.AeroScienceWindowDebug.report())).toBe(before);
});
