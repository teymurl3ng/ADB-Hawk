import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./vitest.setup.ts'],
    exclude: [
      'tests/visual/**',
      'tests/motion/**',
      'tests/pairing/**',
      'node_modules/**',
    ],
  },
});
