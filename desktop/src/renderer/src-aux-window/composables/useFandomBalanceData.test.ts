import { syncExtraAssetsState } from '@renderer-shared/shards/extra-assets/state-sync'
import { useExtraAssetsStore } from '@renderer-shared/shards/extra-assets/store'
import { PiniaStateSync } from '@renderer-shared/shards/pinia-mobx-utils/pinia-state-sync'
import type { KiwiChampionBalance } from '@shared/types/champion-balance'
import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { computed, ref } from 'vue'

import { useChampionBalanceData } from './useFandomBalanceData'

const sourceUrl = 'https://www.bilibili.com/toy/resg/index.html'
const kiwiBalance: Record<number, KiwiChampionBalance> = {
  103: {
    id: 103,
    adjustments: [
      {
        type: 'damage-taken',
        value: 0.9,
        display: 'percentage',
        effectType: 'nerf',
        effect: 'buffed'
      },
      {
        type: 'healing',
        value: 0.8,
        display: 'percentage',
        effectType: 'buff',
        effect: 'nerfed'
      },
      {
        type: 'attack-speed-growth',
        value: 2.5,
        display: 'literal',
        effectType: 'buff',
        effect: 'buffed',
        formattedValue: '+2.5%'
      }
    ]
  },
  127: { id: 127, adjustments: [] }
}

describe('independent champion mode balance data', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('merges KIWI without overwriting ARAM and keeps provenance when the base provider changes', () => {
    const assets = useExtraAssetsStore()
    assets.opgg.balance = {
      103: {
        champion_id: 103,
        attack_speed: 100,
        damage_dealt: 110,
        damage_taken: 100,
        cooldown_reduction: 0,
        healing: 100,
        tenacity: 0,
        shield_amount: 100,
        energy_regen: 100,
        area_of_effect_damage: 100,
        default: false
      }
    }
    assets.fandom.balance = { Ahri: { id: 103, balance: { aram: { dmg_taken: 1.1 } } } }
    assets.kiwi.balance = kiwiBalance
    assets.kiwi.version = '16.19'
    assets.kiwi.sourceUrl = sourceUrl
    const provider = ref('opgg')
    const { data } = useChampionBalanceData(provider)

    expect(data.value[103].modes.ARAM.adjustments).toEqual([
      expect.objectContaining({ type: 'damage-dealt', value: 1.1, effect: 'buffed' })
    ])
    expect(data.value[103].modes.ARAM.source).toBe('OP.GG')
    expect(data.value[103].modes.KIWI).toEqual({
      adjustments: kiwiBalance[103].adjustments,
      overallEffect: 'mixed',
      source: 'Bilibili RESG',
      sourceUrl,
      version: '16.19',
      cached: true,
      lastUpdate: 0
    })
    expect(data.value[127].modes.KIWI.adjustments).toEqual([])
    expect(data.value[127].modes.ARAM).toBeUndefined()
    expect(data.value[103].modes.KIWI_JADE).toBeUndefined()

    provider.value = 'fandom'
    expect(data.value[103].modes.ARAM.adjustments[0]).toMatchObject({
      type: 'damage-taken',
      value: 1.1,
      effect: 'nerfed'
    })
    expect(data.value[103].modes.ARAM.source).toBe('Fandom Wiki')
    expect(data.value[103].modes.KIWI.source).toBe('Bilibili RESG')
  })

  it('updates the selected KIWI balance after initial synchronization and whole-record IPC replacement', async () => {
    const handlers = new Map<string, (...args: any[]) => void>()
    const stateSync = new PiniaStateSync({
      ipc: {
        onEvent: (_namespace: string, event: string, callback: (...args: any[]) => void) =>
          handlers.set(event, callback),
        call: async (_namespace: string, _call: string, _target: string, stateId: string) =>
          stateId === 'kiwi'
            ? {
                balance: { value: kiwiBalance, config: { raw: true } },
                version: { value: '16.19', config: { raw: false } },
                sourceUrl: { value: sourceUrl, config: { raw: false } }
              }
            : {}
      } as any
    })
    await syncExtraAssetsState({ piniaMobxUtils: stateSync as any })
    const { data } = useChampionBalanceData('fandom')
    const selected = computed(() => data.value[103]?.modes.KIWI)
    expect(selected.value?.adjustments[0].value).toBe(0.9)
    const update = handlers.get('update-state-prop/extra-assets-main:kiwi')!
    update('balance', { 103: { id: 103, adjustments: [] } }, { action: 'update', raw: true })
    update('version', '16.20', { action: 'update', raw: false })
    expect(selected.value?.adjustments).toEqual([])
    expect(selected.value?.version).toBe('16.20')
    expect(data.value[127]).toBeUndefined()
    expect(data.value[103].modes.ARAM).toBeUndefined()
  })
})
