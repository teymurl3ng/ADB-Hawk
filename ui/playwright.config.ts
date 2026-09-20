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
      name: 'chromium-dark',
      testMatch: /visual\/.*\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        colorScheme: 'dark',
        viewport: { width: 376, height: 600 },
      },
    },
    {
      name: 'chromium-light',
      testMatch: /visual\/.*\.spec\.ts/,
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
    command: 'npx vite',
    url: 'http://localhost:5173',
    reuseExistingServer: true,
  },
});
