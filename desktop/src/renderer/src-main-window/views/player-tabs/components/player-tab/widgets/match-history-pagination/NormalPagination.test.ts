import { createPinia, setActivePinia } from 'pinia'
import { describe, expect, it, vi } from 'vitest'
import { createSSRApp, h } from 'vue'
import { renderToString } from 'vue/server-renderer'

import NormalPagination from './NormalPagination.vue'

vi.mock('i18next-vue', () => ({ useTranslation: () => ({ t: (key: string) => key }) }))
vi.mock('naive-ui', async () => {
  const { defineComponent, h } = await import('vue')
  const control = (tag: string, role?: string) =>
    defineComponent({
      setup(_, { attrs, slots }) {
        return () => h(tag, { ...attrs, role }, [slots.icon?.(), slots.default?.()])
      }
    })
  return {
    NButton: control('button'),
    NIcon: control('span'),
    NSelect: control('div', 'combobox'),
    NInputNumber: control('input'),
    NPopover: defineComponent({
      setup(_, { slots }) {
        return () => h('div', slots.trigger?.())
      }
    })
  }
})
vi.mock('@main-window/shards/player-tabs', () => ({
  usePageSizeOptions: () => [{ label: '20', value: 20 }]
}))
vi.mock('../../context', async () => {
  const { ref } = await import('vue')
  return {
    usePlayerTab: () => ({
      preferredSource: ref('sgp'),
      isCrossRegion: ref(false),
      sgpApiStatus: ref({ canUse: true, isReady: true })
    })
  }
})
vi.mock('../../data/match-history', async () => {
  const { ref } = await import('vue')
  return {
    useMatchHistory: () => ({
      page: ref({ queryParams: { startIndex: 0, count: 20 } }),
      isLoading: ref(false),
      collectState: ref(null),
      loadMatchHistory: vi.fn()
    })
  }
})
vi.mock('./QueueSelect.vue', () => ({ default: { render: () => null } }))
vi.mock('./FilterButton.vue', () => ({ default: { render: () => null } }))
vi.mock('./ClearFiltersButton.vue', () => ({ default: { render: () => null } }))

describe('match history pagination in the compact window', () => {
  it('renders the next-page and previous-page controls alongside filters', async () => {
    setActivePinia(createPinia())
    const app = createSSRApp({
      render: () =>
        h(NormalPagination, { horizontal: true, isFloating: false, filterActive: false })
    })
    const html = await renderToString(app)

    expect(html).toContain('title="playerTabs.profile.nextPage"')
    expect(html).toContain('title="playerTabs.profile.prevPage"')
    expect(html).toContain('role="combobox"')
  })
})
