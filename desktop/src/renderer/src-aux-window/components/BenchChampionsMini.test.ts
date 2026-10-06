// @vitest-environment vue-client-renderer
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, markRaw, nextTick, shallowReactive, shallowRef, ssrContextKey } from 'vue'

import BenchChampionsMini from './BenchChampionsMini.vue'

const mocks = vi.hoisted(() => ({
  store: null as any,
  balance: null as any,
  pickOrBan: vi.fn(),
  benchSwap: vi.fn(),
  reroll: vi.fn(),
  warning: vi.fn()
}))

vi.mock('@renderer-shared/shards/league-client/store', () => ({
  useLeagueClientStore: () => mocks.store
}))
vi.mock('@renderer-shared/shards/league-client', () => ({ LeagueClientRenderer: class {} }))
vi.mock('@renderer-shared/shards', () => ({
  useInstance: () => ({
    api: {
      champSelect: {
        pickOrBan: mocks.pickOrBan,
        benchSwap: mocks.benchSwap,
        reroll: mocks.reroll
      }
    }
  })
}))
vi.mock('@aux-window/composables/useFandomBalanceData', () => ({
  useChampionBalanceData: () => ({ data: mocks.balance })
}))
vi.mock('i18next-vue', () => ({
  useTranslation: () => ({ t: (key: string) => key })
}))
vi.mock('naive-ui', async () => {
  const { defineComponent, h } = await import('vue')
  const wrapper = defineComponent({
    setup:
      (_, { slots }) =>
      () =>
        h('div', slots.default?.())
  })
  return {
    useMessage: () => ({ warning: mocks.warning }),
    NCard: wrapper,
    NIcon: wrapper,
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
          h('button', slots.default?.())
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
const mountedPanels: (() => void)[] = []

function renderPanel() {
  // Tooltip mocks render only their triggers, so these assertions prove the values stay visible.
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
      if (node.parent) node.parent.children.splice(node.parent.children.indexOf(node), 1)
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
  const app = renderer.createApp(BenchChampionsMini)
  app.provide(ssrContextKey, { modules: new Set() })
  app.mount(root)
  mountedPanels.push(() => app.unmount())
  return root
}

function matching(root: TestNode, predicate: (node: TestNode) => boolean): TestNode[] {
  return [
    ...(predicate(root) ? [root] : []),
    ...root.children.flatMap((node) => matching(node, predicate))
  ]
}

function text(root: TestNode): string {
  return root.type === 'comment' ? '' : [root.text, ...root.children.map(text)].join(' ')
}

function withClass(root: TestNode, name: string) {
  return matching(root, (node) =>
    String(node.props.class || '')
      .split(/\s+/)
      .includes(name)
  )
}

function visibleAdjustments(root: TestNode) {
  return withClass(root, 'balance-entry').map((row) => ({
    effect: row.props['data-effect'],
    category: text(withClass(row, 'balance-category')[0]).trim(),
    value: text(withClass(row, 'balance-value')[0]).trim()
  }))
}

function championButton(root: TestNode, championId: number) {
  return matching(
    root,
    (node) => node.type === 'button' && node.props['aria-label'] === `Champion ${championId}`
  )[0]
}

describe('visible Mini champion balance and selection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.balance = shallowRef({})
    mocks.store = shallowReactive({
      gameData: markRaw({ championName: (id: number) => `Champion ${id}` }),
      champSelect: shallowReactive({
        currentChampion: 103,
        currentPickableChampionIds: new Set([103, 127, 711]),
        session: markRaw({
          benchEnabled: true,
          benchChampions: [{ championId: 127 }, { championId: 711 }],
          timer: { phase: 'FINALIZATION' },
          allowSubsetChampionPicks: true,
          allowRerolling: false,
          rerollsRemaining: 0,
          localPlayerCellId: 2,
          actions: [[{ id: 42, type: 'pick', completed: false, actorCellId: 2 }]]
        })
      }),
      gameflow: shallowReactive({
        session: markRaw({ gameData: { queue: { gameMode: 'KIWI' } } })
      }),
      lobbyTeamBuilder: shallowReactive({ champSelect: markRaw({ subsetChampionList: [] }) })
    })
    mocks.pickOrBan.mockResolvedValue(undefined)
    mocks.benchSwap.mockResolvedValue(undefined)
  })

  afterEach(() => mountedPanels.splice(0).forEach((unmount) => unmount()))

  it('keeps benefit categories, signed values and source visible without opening a tooltip', async () => {
    mocks.balance.value = {
      103: {
        modes: {
          KIWI: {
            source: 'Bilibili RESG',
            sourceUrl: 'https://www.bilibili.com/toy/resg/index.html',
            version: '16.19',
            cached: true,
            adjustments: [
              { type: 'damage-dealt', value: 0.95, display: 'percentage', effect: 'nerfed' },
              { type: 'ability-haste', value: -20, display: 'literal', effect: 'nerfed' },
              { type: 'damage-taken', value: 0.9, display: 'percentage', effect: 'buffed' },
              {
                type: 'attack-speed-growth',
                value: 2.5,
                display: 'literal',
                effect: 'buffed',
                formattedValue: '+2.5%'
              }
            ]
          },
          ARAM: {
            source: 'OP.GG',
            adjustments: [
              { type: 'damage-dealt', value: 1.2, display: 'percentage', effect: 'buffed' }
            ]
          }
        }
      }
    }
    const root = renderPanel()
    await nextTick()
    expect(visibleAdjustments(root)).toEqual([
      { effect: 'buffed', category: 'timo.selection.buffs', value: '−10%' },
      { effect: 'buffed', category: 'timo.selection.buffs', value: '+2.5%' },
      { effect: 'nerfed', category: 'timo.selection.debuffs', value: '−5%' },
      { effect: 'nerfed', category: 'timo.selection.debuffs', value: '−20' }
    ])
    expect(text(root)).toContain('Bilibili RESG')
    expect(text(root)).toContain('16.19')
    expect(text(root)).toContain('timo.selection.cached')
    expect(text(root)).not.toContain('OP.GG')

    mocks.store.gameflow.session = markRaw({ gameData: { queue: { gameMode: 'ARAM' } } })
    await nextTick()
    expect(visibleAdjustments(root)).toEqual([
      { effect: 'buffed', category: 'timo.selection.buffs', value: '+20%' }
    ])
    expect(text(root)).toContain('OP.GG')
    expect(text(root)).not.toContain('Bilibili RESG')
  })

  it('replaces live rows and distinguishes an unlisted adjustment from missing mode data', async () => {
    mocks.balance.value = {
      103: {
        modes: {
          KIWI: {
            source: 'Bilibili RESG',
            version: '16.19',
            adjustments: [{ type: 'healing', value: 0.8, display: 'percentage', effect: 'nerfed' }]
          }
        }
      }
    }
    const root = renderPanel()
    await nextTick()
    expect(visibleAdjustments(root)[0].value).toBe('−20%')

    mocks.balance.value = {
      103: {
        modes: {
          KIWI: {
            source: 'Bilibili RESG',
            version: '16.20',
            adjustments: [
              { type: 'healing', value: 0.9, display: 'percentage', effect: 'nerfed' },
              { type: 'special', value: 0, effect: 'neutral', description: '特殊调整：保留原文' }
            ]
          }
        }
      },
      127: { modes: { KIWI: { source: 'Bilibili RESG', version: '16.20', adjustments: [] } } }
    }
    await nextTick()
    expect(visibleAdjustments(root)[0].value).toBe('−10%')
    expect(text(root)).toContain('特殊调整：保留原文')
    expect(text(root)).toContain('16.20')
    expect(text(root)).not.toContain('16.19')

    mocks.store.champSelect.currentChampion = 127
    await nextTick()
    expect(visibleAdjustments(root)).toEqual([])
    expect(text(root)).toContain('timo.selection.sourceNoChanges')
    expect(text(root)).not.toContain('timo.selection.noBalance')
    expect(text(root)).not.toContain('特殊调整：保留原文')

    mocks.store.champSelect.currentChampion = 711
    await nextTick()
    expect(visibleAdjustments(root)).toEqual([])
    expect(text(root)).toContain('timo.selection.noBalance')
    expect(text(root)).not.toContain('timo.selection.sourceNoChanges')
  })

  it('retains the three-choice pick and bench swap while unavailable buttons cannot send actions', async () => {
    mocks.store.champSelect.currentChampion = null
    mocks.store.champSelect.currentPickableChampionIds = new Set([23, 421, 202, 67])
    mocks.store.champSelect.session = markRaw({
      ...mocks.store.champSelect.session,
      timer: { phase: 'BAN_PICK' },
      benchChampions: [{ championId: 67 }]
    })
    mocks.store.lobbyTeamBuilder.champSelect = markRaw({ subsetChampionList: [23, 421, 202] })
    const root = renderPanel()
    await nextTick()
    for (const id of [23, 421, 202]) expect(championButton(root, id).props.disabled).toBe(false)
    expect(championButton(root, 67).props.disabled).toBe(true)
    await championButton(root, 67).props.onClick()
    expect(mocks.pickOrBan).not.toHaveBeenCalled()
    expect(mocks.benchSwap).not.toHaveBeenCalled()

    await championButton(root, 421).props.onClick()
    expect(mocks.pickOrBan).toHaveBeenCalledExactlyOnceWith(421, true, 'pick', 42)
    mocks.store.champSelect.currentChampion = 421
    mocks.store.champSelect.session = markRaw({
      ...mocks.store.champSelect.session,
      timer: { phase: 'FINALIZATION' }
    })
    await nextTick()
    expect(championButton(root, 421).props['aria-pressed']).toBe(true)
    await championButton(root, 421).props.onClick()
    expect(mocks.benchSwap).not.toHaveBeenCalled()
    expect(championButton(root, 67).props.disabled).toBe(false)
    await championButton(root, 67).props.onClick()
    expect(mocks.benchSwap).toHaveBeenCalledExactlyOnceWith(67)

    mocks.store.champSelect.session = markRaw({
      ...mocks.store.champSelect.session,
      timer: { phase: 'PLANNING' }
    })
    await nextTick()
    expect(championButton(root, 67).props.disabled).toBe(true)
    await championButton(root, 67).props.onClick()
    expect(mocks.benchSwap).toHaveBeenCalledTimes(1)
  })
})
