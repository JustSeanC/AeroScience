import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests/web', fullyParallel: false, workers: 1,
  timeout: 30000, reporter: [['list'], ['json', { outputFile: 'artifacts/web-results.json' }]],
  use: { baseURL: 'http://127.0.0.1:5080', headless: true, screenshot: 'only-on-failure',
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {} },
  outputDir: 'artifacts/browser-tests',
  webServer: { command: 'node scripts/build/test-server.mjs', url: 'http://127.0.0.1:5080/api/health', reuseExistingServer: false, timeout: 30000 }
});
