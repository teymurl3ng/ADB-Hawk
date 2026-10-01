import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  retries: 0,
  workers: 1,
  reporter: 'list',
  use: {
    trace: 'off',
    baseURL: 'http://localhost:5173',
  },
  projects: [
    {
      name: 'centering',
      testMatch: /visual\/centering\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        colorScheme: 'dark',
        viewport: { width: 376, height: 600 },
      },
    },
    {
      name: 'chromium-dark',
      testMatch: /visual\/fixtures\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        colorScheme: 'dark',
        viewport: { width: 376, height: 600 },
      },
    },
    {
      name: 'chromium-light',
      testMatch: /visual\/fixtures\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        colorScheme: 'light',
        viewport: { width: 376, height: 600 },
      },
    },
    {
      name: 'pairing',
      testMatch: /pairing\/.*\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        colorScheme: 'dark',
        viewport: { width: 376, height: 600 },
      },
    },
    {
      name: 'motion',
      testMatch: /motion\/.*\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        colorScheme: 'dark',
        viewport: { width: 376, height: 600 },
      },
    },
  ],
  webServer: {
    // Direct local Vite entry (avoid `npx`, which breaks when the repo path has spaces).
    // Scripts that launch Playwright put the pinned node on PATH first.
    command: 'node ./node_modules/vite/bin/vite.js',
    url: 'http://localhost:5173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
