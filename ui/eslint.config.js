import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  {
    ignores: ['dist/', 'node_modules/', 'playwright-report/', 'test-results/', 'baselines/'],
  },
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    plugins: {
      'react-hooks': reactHooks,
    },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      // DESIGN.md §16 / AGENTS.md R-02: there is exactly one DeviceCard. Alternate
      // compact/dense/mini/pinned variants are forbidden forever.
      'no-restricted-syntax': [
        'error',
        {
          selector:
            'Identifier[name=/^(CompactDeviceCard|DenseDeviceCard|MultiDeviceCard|MiniDeviceCard|PinnedDeviceCard)$/]',
          message:
            'Forbidden component identifier (DESIGN.md §16): when density increases, scroll — do not redesign. There is exactly one canonical DeviceCard.',
        },
      ],
    },
  },
);
