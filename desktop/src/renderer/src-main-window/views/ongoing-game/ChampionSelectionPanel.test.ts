// @vitest-environment vue-client-renderer
import type { CarouselSkins } from '@shared/types/league-client/champ-select'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, nextTick, reactive, shallowRef, ssrContextKey } from 'vue'

import ChampionSelectionPanel from './ChampionSelectionPanel.vue'

const mocks = vi.hoisted(() => ({
  store: null as any,
  balance: null as any,
  pickOrBan: vi.fn(),
  benchSwap: vi.fn(),
  getCarouselSkins: vi.fn(),
  getChampDetails: vi.fn(),
  setSkin: vi.fn(),
  warning: vi.fn(),
  loggerWarn: vi.fn()
}))

vi.mock('@renderer-shared/shards/league-client/store', () => ({
  useLeagueClientStore: () => mocks.store
}))
vi.mock('@renderer-shared/shards/league-client', () => ({ LeagueClientRenderer: class {} }))
vi.mock('@renderer-shared/shards/logger', () => ({ LoggerRenderer: class LoggerRenderer {} }))
vi.mock('@renderer-shared/shards', () => ({
  useInstance: (instance: { name: string }) =>
    instance.name === 'LoggerRenderer'
      ? { createLogger: () => ({ warn: mocks.loggerWarn }) }
      : {
          api: {
            champSelect: {
              pickOrBan: mocks.pickOrBan,
              benchSwap: mocks.benchSwap,
              getCarouselSkins: mocks.getCarouselSkins,
              setSkin: mocks.setSkin
            },
            gameData: { getChampDetails: mocks.getChampDetails }
          }
        }
}))
vi.mock('@aux-window/composables/useFandomBalanceData', () => ({
  useChampionBalanceData: () => ({ data: mocks.balance })
}))
vi.mock('i18next-vue', () => ({
  useTranslation: () => ({ t: (key: string) => key })
}))
vi.mock('naive-ui', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    useMessage: () => ({ warning: mocks.warning }),
    NTooltip: defineComponent({
      setup:
        (_, { slots }) =>
        () =>
          slots.trigger?.()
    }),
    NButton: defineComponent({
      setup:
        (_, { slots }) =>
        () =>
          h('button', {}, slots.default?.())
    })
  }
})
vi.mock('@renderer-shared/components/widgets/ChampionIcon.vue', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    default: defineComponent({
      props: ['championId'],
      setup: (props) => () => h('img', { alt: `Champion ${props.championId}` })
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

function renderPanel() {
  // Run the actual compiled template and its click handlers without a browser dependency.
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
  const app = renderer.createApp(ChampionSelectionPanel)
  app.provide(ssrContextKey, { modules: new Set() })
  app.mount(root)
  mountedPanels.push(() => app.unmount())
  return root
}

function nodesMatching(root: TestNode, predicate: (node: TestNode) => boolean): TestNode[] {
  return [
    ...(predicate(root) ? [root] : []),
    ...root.children.flatMap((node) => nodesMatching(node, predicate))
  ]
}

function championButton(root: TestNode, championId: number) {
  return nodesMatching(
    root,
    (node) => node.type === 'button' && node.props['aria-label'] === `Champion ${championId}`
  )[0]
}

function skinButtons(root: TestNode) {
  return nodesMatching(root, (node) => node.type === 'button' && Boolean(node.props.title))
}

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
    tilePath: `/skins/${id}.jpg`,
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

const mountedPanels: (() => void)[] = []

describe('champion and owned skin selection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.balance = shallowRef({})
    mocks.store = reactive({
      gameData: { championName: (championId: number) => `Champion ${championId}` },
      champSelect: {
        currentChampion: 103,
        currentPickableChampionIds: new Set([103, 127, 711]),
        session: {
          isSpectating: false,
          benchEnabled: true,
          benchChampions: [{ championId: 127 }, { championId: 711 }],
          timer: { phase: 'FINALIZATION' },
          allowSubsetChampionPicks: true,
          allowSkinSelection: true,
          localPlayerCellId: 2,
          actions: [[{ id: 42, type: 'pick', completed: false, actorCellId: 2 }]]
        },
        skinSelectorInfo: { selectedSkinId: 103000, skinSelectionDisabled: false }
      },
      gameflow: { session: { gameData: { queue: { gameMode: 'ARAM' } } } },
      lobbyTeamBuilder: { champSelect: { subsetChampionList: [103, 127] } }
    })
    mocks.getCarouselSkins.mockReset().mockResolvedValue({ data: [skin(103000)] })
    mocks.getChampDetails.mockReset().mockResolvedValue({ data: { skins: [] } })
    mocks.pickOrBan.mockReset().mockResolvedValue(undefined)
    mocks.benchSwap.mockReset().mockResolvedValue(undefined)
    mocks.setSkin.mockReset().mockResolvedValue(undefined)
  })

  afterEach(() => mountedPanels.splice(0).forEach((unmount) => unmount()))

  it('locks the local initial pick immediately with one champion click', async () => {
    mocks.store.champSelect.currentChampion = null
    mocks.store.champSelect.session.timer.phase = 'BAN_PICK'
    mocks.store.champSelect.session.actions[0].unshift({
      id: 19,
      type: 'pick',
      completed: false,
      actorCellId: 0
    })
    const root = renderPanel()
    await settleRender()

    expect(championButton(root, 127).props.disabled).toBe(false)
    await championButton(root, 127).props.onClick()
    expect(mocks.pickOrBan).toHaveBeenCalledExactlyOnceWith(127, true, 'pick', 42)
    expect(mocks.benchSwap).not.toHaveBeenCalled()

    // A bench champion outside the current initial subset cannot be selected yet.
    expect(championButton(root, 711).props.disabled).toBe(true)
    await championButton(root, 711).props.onClick()
    expect(mocks.pickOrBan).toHaveBeenCalledTimes(1)
  })

  it('exchanges a bench champion and prevents duplicate requests while the exchange is pending', async () => {
    const exchange = deferred<void>()
    mocks.benchSwap.mockReturnValueOnce(exchange.promise)
    const root = renderPanel()
    await settleRender()
    const switching = championButton(root, 127).props.onClick()
    await championButton(root, 127).props.onClick()
    expect(mocks.benchSwap).toHaveBeenCalledExactlyOnceWith(127)
    expect(mocks.pickOrBan).not.toHaveBeenCalled()
    exchange.resolve()
    await switching
  })

  it('shows only unlocked enabled skins and chromas and sends a card click to the client', async () => {
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
              chromaPreviewPath: '/chroma.jpg'
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
        skins: [
          { id: 103014, name: 'Star Guardian', chromas: [{ id: 103015, name: 'Pearl chroma' }] }
        ]
      }
    })
    const root = renderPanel()
    await settleRender()
    expect(skinButtons(root).map((node) => node.props.title)).toEqual([
      'Skin 103000',
      'Star Guardian',
      'Pearl chroma'
    ])

    await skinButtons(root)[2].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledExactlyOnceWith(103015)
    mocks.store.champSelect.skinSelectorInfo.skinSelectionDisabled = true
    await nextTick()
    expect(skinButtons(root).every((node) => node.props.disabled)).toBe(true)
    await skinButtons(root)[1].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledTimes(1)
  })

  it('keeps the new champion skins when an earlier champion request finishes late', async () => {
    const oldSkins = deferred<{ data: CarouselSkins[] }>()
    const newSkins = deferred<{ data: CarouselSkins[] }>()
    mocks.getCarouselSkins
      .mockReturnValueOnce(oldSkins.promise)
      .mockReturnValueOnce(newSkins.promise)
    const root = renderPanel()
    mocks.store.champSelect.currentChampion = 127
    await nextTick()
    newSkins.resolve({ data: [skin(127003)] })
    await settleRender()
    expect(skinButtons(root).map((node) => node.props.title)).toEqual(['Skin 127003'])
    oldSkins.resolve({ data: [skin(103014)] })
    await settleRender()
    expect(skinButtons(root).map((node) => node.props.title)).toEqual(['Skin 127003'])
    await skinButtons(root)[0].props.onClick()
    expect(mocks.setSkin).toHaveBeenCalledExactlyOnceWith(127003)
  })
})
