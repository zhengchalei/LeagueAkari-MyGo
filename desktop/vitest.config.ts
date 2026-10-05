import vue from '@vitejs/plugin-vue'
import vueJsx from '@vitejs/plugin-vue-jsx'
import { resolve } from 'path'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [
    vue(),
    vueJsx({
      tsTransform: 'built-in',
      babelPlugins: [['@babel/plugin-syntax-decorators', { legacy: true }]]
    })
  ],
  resolve: {
    alias: {
      '@main-window': resolve('src/renderer/src-main-window'),
      '@aux-window': resolve('src/renderer/src-aux-window'),
      '@opgg-window': resolve('src/renderer/src-opgg-window'),
      '@ongoing-game-window': resolve('src/renderer/src-ongoing-game-window'),
      '@cd-timer-window': resolve('src/renderer/src-cd-timer-window'),
      '@shared': resolve('src/shared'),
      '@renderer-shared': resolve('src/renderer-shared'),
      'vitest-environment-vue-client-renderer': resolve(
        'src/renderer/src-main-window/views/ongoing-game/test-support/client-environment.ts'
      ),
      '@main': resolve('src/main')
    }
  },
  test: {
    globals: true,
    environment: 'node'
  }
})
