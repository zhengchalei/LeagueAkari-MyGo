import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { type Component, createRenderer, nextTick, reactive, ssrContextKey } from 'vue'

import { usePlayerTabsStore } from '@main-window/shards/player-tabs/store'

import PlayerTabs from './PlayerTabs.vue'

function renderComponentSetup(component: Component) {
  const renderer = createRenderer<object, object>({
    createElement: () => ({}),
    createText: () => ({}),
    createComment: () => ({}),
    setText: () => {},
    setElementText: () => {},
    patchProp: () => {},
    insert: () => {},
    remove: () => {},
    parentNode: () => null,
    nextSibling: () => null
  })
  // Exercise the component's real route watchers without a DOM or SSR-only template.
  const app = renderer.createApp({ ...component, render: () => null })
  app.provide(ssrContextKey, { modules: new Set() })
  app.mount({})
  return { unmount: () => app.unmount() }
}

const mocks = vi.hoisted(() => ({
  route: null as any,
  replace: vi.fn(),
  playerTabs: null as any
}))

vi.mock('vue-router', () => ({
  useRoute: () => mocks.route,
  useRouter: () => ({ replace: mocks.replace })
}))
vi.mock('@renderer-shared/shards', () => ({ useInstance: () => mocks.playerTabs }))
vi.mock('@main-window/shards/player-tabs', () => ({ PlayerTabsRenderer: class {} }))
vi.mock('@renderer-shared/composables/useKeyboardCombo', () => ({
  useKeyboardCombo: () => ({ start: vi.fn(), stop: vi.fn() })
}))
vi.mock('naive-ui', () => ({ useMessage: () => ({ success: vi.fn() }) }))
vi.mock('i18next-vue', () => ({ useTranslation: () => ({ t: (key: string) => key }) }))
vi.mock('./components/StartupPane.vue', () => ({ default: { render: () => null } }))
vi.mock('./components/player-tab/PlayerTab.vue', () => ({ default: { render: () => null } }))

describe('player history navigation', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    mocks.replace.mockReset()
    mocks.route = reactive({ name: 'player-tabs', params: {}, query: {} })
    mocks.playerTabs = {
      createTabAndSetCurrent: vi.fn(),
      parseUnionId: (id: string) => {
        const [sgpServerId, puuid] = id.split(':')
        return { sgpServerId, puuid }
      }
    }
  })

  it('does not redirect away from a match when the client disconnects or account data updates', async () => {
    const store = usePlayerTabsStore()
    const view = renderComponentSetup(PlayerTabs)
    mocks.replace.mockClear()
    mocks.route.name = 'ongoing-game'
    store.currentTabId = 'TENCENT_HN1:logged-in-player'
    await nextTick()
    expect(mocks.replace).not.toHaveBeenCalled()

    store.closeAllTabs()
    await nextTick()
    expect(mocks.replace).not.toHaveBeenCalled()
    view.unmount()
  })

  it('restores the selected history tab when the user manually returns from the match', async () => {
    const store = usePlayerTabsStore()
    mocks.route.name = 'ongoing-game'
    store.currentTabId = 'TENCENT_HN1:logged-in-player'
    const view = renderComponentSetup(PlayerTabs)
    expect(mocks.replace).not.toHaveBeenCalled()

    mocks.route.name = 'player-tabs'
    await nextTick()
    expect(mocks.replace).toHaveBeenLastCalledWith({
      name: 'player-tabs',
      params: { sgpServerId: 'TENCENT_HN1', puuid: 'logged-in-player' }
    })
    view.unmount()
  })
})
