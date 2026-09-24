import { defineConfig } from 'vitest/config'

// Unit tests run in Node: the store's tests stub localStorage and fake the hub (src/test/setup.ts).
// A component test marks itself `// @vitest-environment jsdom` and renders with src/test/render.tsx
export default defineConfig({
  test: {
    include: ['src/**/*.test.{ts,tsx}'],
    environment: 'node',
    setupFiles: ['src/test/setup.ts'],
    silent: true,
  },
})
