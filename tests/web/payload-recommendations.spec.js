import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdirSync } from 'node:fs';

// These assertions declare the intended research payload independently of /api/templates.
const primaryIds = ['terrain-scanner', 'multispectral-imager'];
const origin = 'http://127.0.0.1:5080';
const panel = page => page.getByLabel('Recommended instrument payload', { exact: true });
const instrument = (page, id) => page.locator(`#plan-editor [data-payload-instrument="${id}"]`);
const recommendation = (page, id) => panel(page).locator(`[data-instrument-id="${id}"]`);
async function post(request, path, data) {
  const session = await (await request.post('/api/session', { headers: { Origin: origin }, data: {} })).json();
  const response = await request.post(path, { headers: { Origin: origin, 'X-AeroScience-CSRF': session.csrf }, data });
  expect(response.ok(), await response.text()).toBeTruthy();
  return response.json();
}
async function save(page) {
  const response = page.waitForResponse(r => r.url().endsWith('/api/plans') && r.request().method() === 'POST' && r.ok());
  await page.getByRole('button', { name: 'Save mission plan', exact: true }).click();
  const saved = await (await response).json();
  await expect(page.locator('#plan-editor .planner-status')).toContainText('Mission plan saved');
  return saved;
}
async function editSaved(page, name) {
  await page.locator('.plan-row').filter({ hasText: name }).getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByLabel('Plan name', { exact: true })).toHaveValue(name);
}
function observeMutations(page) {
  const requests = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() !== 'GET' && request.method() !== 'HEAD' &&
      (path === '/api/plans' || path === '/api/instruments' || path === '/api/missions' ||
       /\/(approve|download|import|start|prepare)$/.test(path) || path.includes('/collection/')))
      requests.push(`${request.method()} ${path}`);
  });
  return requests;
}

test('Geographic Survey offers actual primary and optional science without automatically enabling optional instruments', async ({ page, request }) => {
  const templates = await (await request.get('/api/templates')).json();
  const survey = templates.find(t => t.id === 'geographicSurvey');
  expect(survey.instruments.filter(c => c.enabled).map(c => c.instrumentId).sort()).toEqual([...primaryIds].sort());
  expect(survey.optionalInstruments.map(c => c.instrumentId)).toEqual(expect.arrayContaining(['thermal-imager', 'hyperspectral-imager']));
  expect(survey.optionalInstruments.every(c => !c.enabled)).toBeTruthy();
  const writes = observeMutations(page);
  await page.goto('/#planner');
  await page.getByLabel('Template', { exact: true }).selectOption('geographicSurvey');
  await expect(panel(page).getByRole('heading', { name: 'Recommended Science Payload', exact: true })).toBeVisible();
  for (const id of primaryIds) {
    await expect(recommendation(page, id)).toHaveAttribute('data-recommendation', 'primary');
    await expect(recommendation(page, id)).toContainText('Enabled in draft');
    await expect(instrument(page, id).locator('[data-instrument]')).toBeChecked();
  }
  for (const id of ['thermal-imager', 'hyperspectral-imager']) {
    await expect(recommendation(page, id)).toHaveAttribute('data-recommendation', 'optional');
    await expect(instrument(page, id).locator('[data-instrument]')).not.toBeChecked();
    await expect(recommendation(page, id)).toContainText('Not enabled in draft');
  }
  await expect(recommendation(page, 'terrain-scanner')).toContainText(/DEM|DSM/);
  await expect(recommendation(page, 'multispectral-imager')).toContainText('Sentinel-2');
  await expect(recommendation(page, 'thermal-imager')).toContainText('Landsat');
  await expect(recommendation(page, 'hyperspectral-imager')).toContainText('ENVI');
  await expect(panel(page)).toContainText(/not.*(?:physical|hardware)|does not.*(?:physical|hardware)/i);
  const details = recommendation(page, 'terrain-scanner').locator('details');
  await expect(details).not.toHaveAttribute('open', '');
  const disclosure = details.locator(':scope > summary');
  await disclosure.focus(); await page.keyboard.press('Enter');
  await expect(details).toHaveAttribute('open', '');
  await expect(disclosure).toBeFocused();
  await expect(details).toContainText(/interval|cadence/i);
  await expect(details).toContainText('theoretical density unavailable without a supported effective measurement rate');
  await expect(details).toContainText('generic.terrain');
  await expect(details).toContainText('version 1');
  const cadence = instrument(page, 'terrain-scanner').locator('[data-interval]');
  await cadence.fill('9');
  await expect(cadence).toBeFocused();
  await expect(details).toHaveAttribute('open', '');
  await expect(details).toContainText('current configured interval 9 s');
  expect(writes).toEqual([]);
  expect((await (await request.get('/api/state')).json()).activeMission).toBeNull();
});

test('template changes, merge, optional Add and Keep preserve saved custom geometry, reference and calibration until explicit Apply', async ({ page, request }) => {
  const id = crypto.randomUUID(), name = `Custom payload ${id}`;
  const profiles = await (await request.get('/api/instrument-profiles')).json();
  const profile = structuredClone(profiles.generic.find(p => p.instrumentId === 'terrain-scanner'));
  profile.horizontalFovDegrees = 43;
  const calibration = {
    version: 1, id: `user.${crypto.randomUUID()}`, revision: 1, instrumentId: 'terrain-scanner', status: 'userDefined',
    author: 'Synthetic browser fixture', definedUtc: '2026-01-02T03:04:05Z', parameters: [], references: [],
    limitations: ['Synthetic preservation fixture only; no scientific calibration certification.'], adjustment: null,
  };
  const payload = { profile, forwardDegrees: 7, rightDegrees: -3, custom: true, geometryAlgorithm: 'sensor-plane-2' };
  const original = await post(request, '/api/plans', {
    id, name, template: 'generalResearch', mode: 'realistic', objective: 'Retain this custom objective',
    instruments: [
      { instrumentId: 'terrain-scanner', enabled: true, intervalMs: 7000, payload, calibration },
      { instrumentId: 'thermal-imager', enabled: true, intervalMs: 11000 },
    ],
    waypoints: [{ name: 'Original research point', latitude: 40, longitude: -75, targetAltitudeFeet: 3500 }],
    surveyAreas: [{ name: 'Original area', vertices: [{ latitude: 40, longitude: -75 }, { latitude: 40, longitude: -74.99 }, { latitude: 40.01, longitude: -74.99 }] }],
  });
  const originalTerrain = original.instruments.find(c => c.instrumentId === 'terrain-scanner');
  await page.goto('/#planner'); await editSaved(page, name);
  const writes = observeMutations(page);
  await page.getByLabel('Template', { exact: true }).selectOption('geographicSurvey');
  await expect(page.getByLabel('Objective', { exact: true })).toHaveValue('Retain this custom objective');
  await expect(instrument(page, 'terrain-scanner').locator('[data-interval]')).toHaveValue('7');
  await panel(page).getByRole('button', { name: 'Keep Current Payload', exact: true }).click();
  await expect(instrument(page, 'multispectral-imager').locator('[data-instrument]')).not.toBeChecked();
  await panel(page).getByRole('button', { name: 'Merge Recommended Instruments', exact: true }).click();
  await expect(instrument(page, 'multispectral-imager').locator('[data-instrument]')).toBeChecked();
  await expect(instrument(page, 'thermal-imager').locator('[data-instrument]')).toBeChecked();
  await expect(instrument(page, 'thermal-imager').locator('[data-interval]')).toHaveValue('11');
  const add = recommendation(page, 'hyperspectral-imager').getByRole('button', { name: 'Add Hyperspectral Imager', exact: true });
  await panel(page).locator('.recommended-optional > summary').click(); await add.click();
  await expect(add).toBeDisabled();
  await expect(instrument(page, 'hyperspectral-imager').locator('[data-instrument]')).toBeChecked();
  await expect(instrument(page, 'terrain-scanner').locator('[data-interval]')).toHaveValue('7');
  expect(writes).toEqual([]);
  const unchanged = await (await request.get(`/api/plans/${id}`)).json();
  expect(unchanged).toEqual(original);
  const merged = await save(page);
  expect(merged.instruments.find(c => c.instrumentId === 'terrain-scanner')).toEqual(originalTerrain);
  expect(merged.instruments.find(c => c.instrumentId === 'thermal-imager').intervalMs).toBe(11000);
  expect(merged.instruments.filter(c => c.enabled).map(c => c.instrumentId)).toEqual(expect.arrayContaining([...primaryIds, 'thermal-imager', 'hyperspectral-imager']));
  expect(merged.waypoints).toEqual(original.waypoints); expect(merged.surveyAreas).toEqual(original.surveyAreas);
  await editSaved(page, name);
  await expect(instrument(page, 'terrain-scanner').locator('[data-interval]')).toHaveValue('7');
  const apply = panel(page).getByRole('button', { name: 'Apply Recommended Payload', exact: true });
  page.once('dialog', dialog => dialog.dismiss()); await apply.click();
  await expect(instrument(page, 'terrain-scanner').locator('[data-interval]')).toHaveValue('7');
  page.once('dialog', dialog => dialog.accept()); await apply.click();
  await expect(instrument(page, 'thermal-imager').locator('[data-instrument]')).not.toBeChecked();
  await expect(instrument(page, 'hyperspectral-imager').locator('[data-instrument]')).not.toBeChecked();
  expect((await (await request.get(`/api/plans/${id}`)).json()).instruments.find(c => c.instrumentId === 'terrain-scanner')).toEqual(originalTerrain);
  const applied = await save(page);
  expect(applied.instruments.find(c => c.instrumentId === 'terrain-scanner').payload.custom).toBe(false);
  expect(applied.instruments.find(c => c.instrumentId === 'terrain-scanner').calibration ?? null).toBeNull();
  expect(applied.instruments.filter(c => c.enabled).map(c => c.instrumentId).sort()).toEqual([...primaryIds].sort());
  expect(applied.waypoints).toEqual(original.waypoints); expect(applied.surveyAreas).toEqual(original.surveyAreas);
});

test('all six mission recommendations are objective appropriate and switching untouched templates causes no save or download', async ({ page, request }) => {
  const templates = await (await request.get('/api/templates')).json();
  expect(templates).toHaveLength(6);
  for (const id of ['atmosphericProfile', 'cloudStructureSurvey', 'atmosphericTransect']) {
    const selected = templates.find(t => t.id === id).instruments.filter(c => c.enabled).map(c => c.instrumentId);
    expect(selected).toEqual(expect.arrayContaining(['ambient-temperature', 'ambient-wind', 'ambient-pressure']));
    expect(selected.every(i => i.startsWith('ambient-'))).toBeTruthy();
  }
  expect(templates.find(t => t.id === 'generalResearch').instruments).toHaveLength(0);
  expect(templates.find(t => t.id === 'observationFlight').instruments.every(c => c.instrumentId.startsWith('ambient-'))).toBeTruthy();
  const writes = observeMutations(page);
  await page.goto('/#planner');
  await page.getByLabel('Plan name', { exact: true }).fill('Unsaved scientific objective draft');
  for (const template of ['atmosphericProfile', 'geographicSurvey', 'cloudStructureSurvey', 'atmosphericTransect', 'observationFlight', 'generalResearch']) {
    await page.getByLabel('Template', { exact: true }).selectOption(template);
    await expect(panel(page)).toContainText(templates.find(t => t.id === template).name);
    await expect(page.getByLabel('Plan name', { exact: true })).toHaveValue('Unsaved scientific objective draft');
  }
  await panel(page).getByRole('button', { name: 'Keep Current Payload', exact: true }).click();
  await page.getByLabel('Template', { exact: true }).selectOption('geographicSurvey');
  for (const id of primaryIds) await expect(instrument(page, id).locator('[data-instrument]')).not.toBeChecked();
  await expect(panel(page)).toContainText('Not enabled in draft');
  expect(writes).toEqual([]);
});

test('payload recommendations occupy a separate full-width responsive section with accessible disclosures', async ({ page }) => {
  await page.goto('/#planner');
  await page.getByLabel('Template', { exact: true }).selectOption('geographicSurvey');
  await page.getByLabel('Plan name', { exact: true }).fill('Geographic survey payload review');
  mkdirSync('artifacts/payload-ux', { recursive: true });
  const cases = [
    { width: 1920, zoom: 1 }, { width: 1366, zoom: 1 }, { width: 1024, zoom: 1 },
    { width: 768, zoom: 1 }, { width: 390, zoom: 1 }, { width: 1366, zoom: 1.25 }, { width: 1366, zoom: 1.5 },
  ];
  for (const { width, zoom } of cases) {
    await page.setViewportSize({ width, height: 1100 });
    // CSS page zoom exercises the reflow/zoom geometry; physical Safari/browser acceptance is separate.
    await page.evaluate(value => { document.documentElement.style.zoom = String(value); }, zoom);
    await panel(page).scrollIntoViewIfNeeded();
    const geometry = await panel(page).evaluate(element => {
      const grid = document.querySelector('#plan-form > .research-grid');
      const panel = element.getBoundingClientRect(), form = element.closest('form').getBoundingClientRect();
      return { inGrid: !!element.closest('.research-grid'), panelWidth: panel.width, formWidth: form.width,
        labels: [...grid.children].map(e => e.textContent), overflow: document.documentElement.scrollWidth > innerWidth + 2,
        cards: [...element.querySelectorAll('[data-recommendation="primary"]')].map(e => ({ width: e.getBoundingClientRect().width, height: e.getBoundingClientRect().height })) };
    });
    expect(geometry.inGrid).toBe(false);
    expect(geometry.panelWidth).toBeGreaterThan(geometry.formWidth * .94);
    expect(geometry.labels).toHaveLength(4);
    expect(geometry.overflow).toBe(false);
    expect(geometry.cards).toHaveLength(2);
    for (const card of geometry.cards) {
      expect(card.width).toBeGreaterThan(200 * zoom);
      expect(card.height).toBeLessThan(600 * zoom);
    }
    await expect(page.getByLabel('Plan name', { exact: true })).toHaveValue('Geographic survey payload review');
    await page.locator('#plan-editor').evaluate(element => element.scrollIntoView({ block: 'start', behavior: 'instant' }));
    await page.screenshot({ path: `artifacts/payload-ux/geographic-${width}-zoom-${Math.round(zoom * 100)}.png`, fullPage: false });
    if (width === 390 || width === 768 || zoom === 1.5)
      await panel(page).screenshot({ path: `artifacts/payload-ux/geographic-${width}-zoom-${Math.round(zoom * 100)}-payload-panel.png` });
  }
  await page.evaluate(() => { document.documentElement.style.zoom = ''; });
  await page.setViewportSize({ width: 390, height: 1100 });
  const findings = await new AxeBuilder({ page }).include('.recommended-payload').analyze();
  expect(findings.violations).toEqual([]);
});
