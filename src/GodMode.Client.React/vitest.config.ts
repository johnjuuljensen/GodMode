import { defineConfig } from 'vitest/config'

// Unit tests run in Node: the store's tests stub localStorage and fake the hub (src/test/setup.ts).
// A component test marks itself `// @vitest-environment jsdom` and renders with src/test/render.tsx, as
// the page the app hosts: at the app's address, with the app's shell around it (src/test/appShell.ts)
export default defineConfig({
  test: {
    include: ['src/**/*.test.{ts,tsx}'],
    environment: 'node',
    environmentOptions: { jsdom: { url: 'https://0.0.0.1/' } },
    setupFiles: ['src/test/setup.ts'],
    silent: true,
  },
})
