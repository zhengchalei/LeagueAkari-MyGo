// @vitest-environment vue-client-renderer
import type { CarouselSkins } from '@shared/types/league-client/champ-select'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, nextTick, reactive, ssrContextKey } from 'vue'

import SkinSelectionMini from './SkinSelectionMini.vue'

const mocks = vi.hoisted(() => ({
  leagueStore: null as any,
  auxStore: null as any,
  getCarouselSkins: vi.fn(),
  getChampDetails: vi.fn(),
  setSkin: vi.fn(),
  warning: vi.fn()
}))

vi.mock('@renderer-shared/shards/league-client/store', () => ({
  useLeagueClientStore: () => mocks.leagueStore
}))
vi.mock('@renderer-shared/shards/window-manager/store', () => ({
  useAuxWindowStore: () => mocks.auxStore
}))
vi.mock('@renderer-shared/shards/league-client', () => ({ LeagueClientRenderer: class {} }))
vi.mock('@renderer-shared/shards', () => ({
  useInstance: () => ({
    api: {
      champSelect: { getCarouselSkins: mocks.getCarouselSkins, setSkin: mocks.setSkin },
      gameData: { getChampDetails: mocks.getChampDetails }
    }
  })
}))
vi.mock('i18next-vue', () => ({
  useTranslation: () => ({
    t: (key: string, values?: { name?: string; count?: number }) =>
      values?.name ? `${key}: ${values.name}` : values?.count ? `${key}: ${values.count}` : key
  })
}))
vi.mock('naive-ui', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    useMessage: () => ({ warning: mocks.warning }),
    NCard: defineComponent({
      setup:
        (_, { slots }) =>
        () =>
          h('section', {}, slots.default?.())
    })
  }
})
vi.mock('@renderer-shared/components/LcuImage.vue', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    default: defineComponent({
      props: ['src'],
      setup: (props) => () => h('img', { src: props.src })
    })
  }
})

interface TestNode {
  type: string
  props: Record<string, any>
  children: TestNode[]
  text: string
  parent: TestNode | null
}

const createNode = (type: string, text = ''): TestNode => ({
  type,
  props: {},
  children: [],
  text,
  parent: null
})

function renderMini() {
  const renderer = createRenderer<TestNode, TestNode>({
    createElement: (type) => createNode(type),
    createText: (text) => createNode('text', text),
    createComment: (text) => createNode('comment', text),
    setText: (node, text) => (node.text = text),
    setElementText: (node, text) => {
      node.children = []
      node.text = text
    },
    patchProp: (node, key, _, value) => (node.props[key] = value),
    insert: (node, parent, anchor) => {
      if (node.parent) {
        node.parent.children.splice(node.parent.children.indexOf(node), 1)
      }
      node.parent = parent
      const index = anchor ? parent.children.indexOf(anchor) : -1
      parent.children.splice(index < 0 ? parent.children.length : index, 0, node)
    },
    remove: (node) => {
      if (node.parent) {
        node.parent.children.splice(node.parent.children.indexOf(node), 1)
        node.parent = null
      }
    },
    parentNode: (node) => node.parent,
    nextSibling: (node) => {
      const siblings = node.parent?.children || []
      return siblings[siblings.indexOf(node) + 1] || null
    }
  })
  const root = createNode('root')
  const app = renderer.createApp(SkinSelectionMini)
  app.provide(ssrContextKey, { modules: new Set() })
  app.mount(root)
  mountedMinis.push(() => app.unmount())
  return root
}

function nodesMatching(root: TestNode, predicate: (node: TestNode) => boolean): TestNode[] {
  return [
    ...(predicate(root) ? [root] : []),
    ...root.children.flatMap((node) => nodesMatching(node, predicate))
  ]
}

const skinButtons = (root: TestNode) => nodesMatching(root, (node) => node.type === 'button')
const visibleText = (root: TestNode) =>
  nodesMatching(root, (node) => node.type !== 'comment')
    .map((node) => node.text)
    .join(' ')

async function settleRender() {
  await Promise.resolve()
  await Promise.resolve()
  await nextTick()
}

function skin(id: number, values: Partial<CarouselSkins> = {}): CarouselSkins {
  return {
    id,
    championId: Math.floor(id / 1000),
    name: `Skin ${id}`,
    splashPath: `/skins/${id}.jpg`,
    unlocked: true,
    disabled: false,
    childSkins: [],
    ...values
  } as CarouselSkins
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => (resolve = done))
  return { promise, resolve }
}

const mountedMinis: (() => void)[] = []

describe('Mini owned skin cards', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.auxStore = reactive({ settings: { showSkinSelector: true } })
    mocks.leagueStore = reactive({
      champSelect: {
        currentChampion: 103,
        session: { allowSkinSelection: true, isSpectating: false },
        skinSelectorInfo: { selectedSkinId: 103000, skinSelectionDisabled: false }
      }
    })
    mocks.getCarouselSkins.mockReset().mockResolvedValue({
      data: [skin(103000), skin(103014)]
    })
    mocks.getChampDetails.mockReset().mockResolvedValue({ data: { skins: [] } })
    mocks.setSkin.mockReset().mockResolvedValue(undefined)
  })

  afterEach(() => mountedMinis.splice(0).forEach((unmount) => unmount()))

  it('shows owned enabled cards and chromas, selected status, and respects the visibility setting', async () => {
    mocks.getCarouselSkins.mockResolvedValueOnce({
      data: [
        skin(103000),
        skin(103014, {
          childSkins: [
            {
              id: 103015,
              name: 'Owned chroma',
              unlocked: true,
              disabled: false,
              chromaPreviewPath: '/pearl.jpg'
            },
            { id: 103016, name: 'Locked chroma', unlocked: false, disabled: false },
            { id: 103017, name: 'Disabled chroma', unlocked: true, disabled: true }
          ] as CarouselSkins['childSkins']
        }),
        skin(103027, { unlocked: false }),
        skin(103028, { disabled: true }),
        skin(127000)
      ]
    })
    mocks.getChampDetails.mockResolvedValueOnce({
      data: {
        skins: [{ id: 103014, name: 'Star Guardian', chromas: [{ id: 103015, name: 'Pearl' }] }]
      }
    })
    const root = renderMini()
    await settleRender()
    expect(skinButtons(root).map((node) => node.props.title)).toEqual([
      'Skin 103000',
      'Star Guardian',
      'Pearl'
    ])
    expect(visibleText(root)).toContain('auxWindow.skinSelection.ownedCount: 3')
    expect(visibleText(root)).toContain('auxWindow.skinSelection.selected: Skin 103000')
    expect(skinButtons(root).map((node) => node.props['aria-pressed'])).toEqual([
      true,
      false,
      false
    ])
    expect(nodesMatching(skinButtons(root)[2], (node) => node.type === 'img')[0].props.src).toBe(
      '/pearl.jpg'
    )

    mocks.auxStore.settings.showSkinSelector = false
    await nextTick()
    expect(skinButtons(root)).toHaveLength(0)
    mocks.auxStore.settings.showSkinSelector = true
    mocks.leagueStore.champSelect.skinSelectorInfo.selectedSkinId = 103015
    await nextTick()
    expect(skinButtons(root)[2].props['aria-pressed']).toBe(true)
    expect(visibleText(root)).toContain('auxWindow.skinSelection.selected: Pearl')
  })

  it('waits for the client selection, prevents duplicate requests, and retains the previous skin on failure', async () => {
    const applying = deferred<void>()
    mocks.setSkin.mockReturnValueOnce(applying.promise)
    const root = renderMini()
    await settleRender()
    const request = skinButtons(root)[1].props.onClick()
    await nextTick()
    expect(skinButtons(root).every((node) => node.props.disabled)).toBe(true)
    expect(skinButtons(root)[0].props['aria-pressed']).toBe(true)
    expect(visibleText(root)).toContain('auxWindow.skinSelection.applying: Skin 103014')
    await skinButtons(root)[1].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledExactlyOnceWith(103014)
    applying.resolve()
    await request
    await nextTick()
    expect(skinButtons(root)[0].props['aria-pressed']).toBe(true)
    mocks.leagueStore.champSelect.skinSelectorInfo.selectedSkinId = 103014
    await nextTick()
    expect(skinButtons(root)[1].props['aria-pressed']).toBe(true)

    mocks.setSkin.mockRejectedValueOnce(new Error('Skin unavailable'))
    await skinButtons(root)[0].props.onClick()
    await nextTick()
    expect(skinButtons(root)[1].props['aria-pressed']).toBe(true)
    expect(visibleText(root)).toContain('auxWindow.skinSelection.failed')
    expect(mocks.warning).toHaveBeenCalledExactlyOnceWith('auxWindow.skinSelection.failed')
    mocks.leagueStore.champSelect.skinSelectorInfo.skinSelectionDisabled = true
    await nextTick()
    await skinButtons(root)[0].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledTimes(2)
    expect(skinButtons(root).every((node) => node.props.disabled)).toBe(true)
  })

  it('clears the old hero immediately and ignores its late response after changing heroes', async () => {
    const oldSkins = deferred<{ data: CarouselSkins[] }>()
    const newSkins = deferred<{ data: CarouselSkins[] }>()
    mocks.getCarouselSkins
      .mockReturnValueOnce(oldSkins.promise)
      .mockReturnValueOnce(newSkins.promise)
    const root = renderMini()
    await settleRender()
    mocks.leagueStore.champSelect.currentChampion = 127
    await nextTick()
    expect(skinButtons(root)).toHaveLength(0)
    expect(visibleText(root)).toContain('auxWindow.skinSelection.loading')
    newSkins.resolve({ data: [skin(127000), skin(127003)] })
    await settleRender()
    expect(skinButtons(root).map((node) => node.props.title)).toEqual([
      'Skin 127000',
      'Skin 127003'
    ])
    oldSkins.resolve({ data: [skin(103014)] })
    await settleRender()
    expect(skinButtons(root).map((node) => node.props.title)).toEqual([
      'Skin 127000',
      'Skin 127003'
    ])
    await skinButtons(root)[1].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledExactlyOnceWith(127003)
  })

  it('keeps available cards if names cannot load and distinguishes an ownership request failure', async () => {
    mocks.getChampDetails.mockRejectedValueOnce(new Error('Names unavailable'))
    const root = renderMini()
    await settleRender()
    expect(skinButtons(root)).toHaveLength(2)
    await skinButtons(root)[1].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledExactlyOnceWith(103014)

    mocks.getCarouselSkins.mockRejectedValueOnce(new Error('Client disconnected'))
    mocks.leagueStore.champSelect.currentChampion = 127
    await settleRender()
    expect(skinButtons(root)).toHaveLength(0)
    expect(visibleText(root)).toContain('auxWindow.skinSelection.loadFailed')
    expect(visibleText(root)).not.toContain('auxWindow.skinSelection.empty')
  })
})
