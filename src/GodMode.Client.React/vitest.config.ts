import { defineConfig } from 'vitest/config'

// Unit tests run in Node: the store's tests stub localStorage and fake the hub (src/test/setup.ts)
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
    setupFiles: ['src/test/setup.ts'],
    silent: true,
  },
})
