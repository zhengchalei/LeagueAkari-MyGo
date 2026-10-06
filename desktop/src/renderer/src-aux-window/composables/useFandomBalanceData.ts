import { useExtraAssetsStore } from '@renderer-shared/shards/extra-assets/store'
import type { BalanceAdjustment } from '@shared/types/champion-balance'
import { MaybeRefOrGetter, computed, readonly, toRef } from 'vue'

export type { BalanceAdjustment } from '@shared/types/champion-balance'

const ADJUSTMENT_EFFECT = {
  'damage-dealt': 'buff',
  'damage-taken': 'nerf',
  shielding: 'buff',
  healing: 'buff',
  'ability-haste': 'buff',
  'attack-speed': 'buff',
  'energy-regen': 'buff',
  tenacity: 'buff',
  'movement-speed': 'buff',
  'area-of-effect-damage': 'buff',
  special: 'neutral'
} as const

const ADJUSTMENT_DISPLAY = {
  'damage-dealt': 'percentage',
  'damage-taken': 'percentage',
  shielding: 'percentage',
  healing: 'percentage',
  'ability-haste': 'literal',
  'attack-speed': 'percentage',
  'energy-regen': 'percentage',
  tenacity: 'percentage',
  'movement-speed': 'percentage',
  'area-of-effect-damage': 'percentage',
  special: 'literal'
}

const FANDOM_TYPE_MAP = {
  dmg_dealt: 'damage-dealt',
  dmg_taken: 'damage-taken',
  shielding: 'shielding',
  healing: 'healing',
  ability_haste: 'ability-haste',
  attack_speed: 'attack-speed',
  energy_regen: 'energy-regen',
  tenacity: 'tenacity',
  movement_speed: 'movement-speed'
}

// for reference
export const ALL_MODES = [
  'ARAM',
  'ASCENSION',
  'CLASSIC',
  'FIRSTBLOOD',
  'KINGPORO',
  'ODIN',
  'ONEFORALL',
  'TUTORIAL',
  'TUTORIAL_MODULE_1',
  'TUTORIAL_MODULE_2',
  'TUTORIAL_MODULE_3',
  'SIEGE',
  'ASSASSINATE',
  'DARKSTAR',
  'ARSR',
  'URF',
  'DOOMBOTSTEEMO',
  'STARGUARDIAN',
  'STRAWBERRY',
  'PROJECT',
  'OVERCHARGE',
  'SNOWURF',
  'PRACTICETOOL',
  'NEXUSBLITZ',
  'ODYSSEY',
  'ULTBOOK',
  'CHERRY',
  'WIPMODEWIP'
] as const

const FANDOM_MODE_MAP = {
  ofa: 'ONEFORALL',
  urf: 'URF',
  usb: 'ULTBOOK',
  nb: 'NEXUSBLITZ',
  aram: 'ARAM',
  ar: 'CHERRY'
}
export interface ChampionModeBalance {
  overallEffect: 'buffed' | 'nerfed' | 'mixed' | 'neutral'
  adjustments: BalanceAdjustment[]
  source?: string
  sourceUrl?: string
  version?: string
  cached?: boolean
  lastUpdate?: number
}

export interface ChampionBalance {
  id: number

  modes: Record<string, ChampionModeBalance>
}

/**
 * 组装英雄平衡性数据适配
 * @returns
 */
export function useChampionBalanceData(_source: MaybeRefOrGetter<string>) {
  const eas = useExtraAssetsStore()
  const source = toRef(_source)

  const providerData = computed<Record<number, ChampionBalance>>(() => {
    if (!source.value) {
      return {}
    }

    if (source.value === 'fandom') {
      if (!eas.fandom.balance) {
        return {}
      }

      return Object.values(eas.fandom.balance).reduce(
        (acc, value) => {
          if (typeof value !== 'object' || value === null) {
            return acc
          }

          const id = Math.floor(value.id)
          const modeAdjustments = {
            id,
            modes: Object.entries(value.balance).reduce(
              (acc, [mode, balance]) => {
                const adjustments = Object.entries(balance).reduce((acc, [key, value]) => {
                  const type = FANDOM_TYPE_MAP[key]
                  const effectType = ADJUSTMENT_EFFECT[type]
                  const display = ADJUSTMENT_DISPLAY[type] || 'literal'

                  if (!type || !effectType || typeof value !== 'number') {
                    return acc
                  }

                  let effect: BalanceAdjustment['effect'] = 'neutral'
                  if (display === 'percentage') {
                    if (effectType === 'buff') {
                      effect = value > 1 ? 'buffed' : 'nerfed'
                    } else if (effectType === 'nerf') {
                      effect = value > 1 ? 'nerfed' : 'buffed'
                    }
                  } else {
                    if (effectType === 'buff') {
                      effect = value > 0 ? 'buffed' : 'nerfed'
                    } else if (effectType === 'nerf') {
                      effect = value > 0 ? 'nerfed' : 'buffed'
                    }
                  }

                  acc.push({ type, value, effectType, display, effect })

                  return acc
                }, [] as BalanceAdjustment[])

                if (adjustments.length) {
                  let nerfed = 0
                  let buffed = 0
                  let neutral = 0

                  for (const { effect } of adjustments) {
                    if (effect === 'nerfed') {
                      nerfed++
                    } else if (effect === 'buffed') {
                      buffed++
                    } else {
                      neutral++
                    }
                  }

                  let overallEffect: ChampionModeBalance['overallEffect'] = 'neutral'
                  if (nerfed && buffed) {
                    overallEffect = 'mixed'
                  } else if (nerfed) {
                    overallEffect = 'nerfed'
                  } else if (buffed) {
                    overallEffect = 'buffed'
                  }

                  acc[FANDOM_MODE_MAP[mode]] = {
                    overallEffect,
                    adjustments,
                    source: 'Fandom Wiki'
                  }
                }

                return acc
              },
              {} as Record<string, ChampionModeBalance>
            )
          }

          if (Object.keys(modeAdjustments.modes).length) {
            acc[id] = modeAdjustments
          }

          return acc
        },
        {} as Record<string, ChampionBalance>
      )
    }

    if (source.value === 'opgg') {
      const fields = [
        ['damage_dealt', 'damage-dealt', 100],
        ['damage_taken', 'damage-taken', 100],
        ['attack_speed', 'attack-speed', 100],
        ['cooldown_reduction', 'ability-haste', 0],
        ['healing', 'healing', 100],
        ['tenacity', 'tenacity', 0],
        ['shield_amount', 'shielding', 100],
        ['energy_regen', 'energy-regen', 100],
        ['area_of_effect_damage', 'area-of-effect-damage', 100]
      ] as const
      return Object.values(eas.opgg.balance).reduce(
        (acc, item) => {
          const adjustments: BalanceAdjustment[] = []
          for (const [key, type, baseline] of fields) {
            const raw = item[key]
            if (!Number.isFinite(raw) || raw === baseline) continue
            const effectType = ADJUSTMENT_EFFECT[type]
            const value = baseline === 100 ? raw / 100 : raw
            const positive = raw > baseline
            adjustments.push({
              type,
              value,
              effectType,
              display: baseline === 100 ? 'percentage' : 'literal',
              effect: positive === (effectType === 'buff') ? 'buffed' : 'nerfed'
            })
          }
          const hasBuff = adjustments.some((entry) => entry.effect === 'buffed')
          const hasNerf = adjustments.some((entry) => entry.effect === 'nerfed')
          acc[item.champion_id] = {
            id: item.champion_id,
            modes: {
              ARAM: {
                adjustments,
                source: 'OP.GG',
                overallEffect:
                  hasBuff && hasNerf ? 'mixed' : hasBuff ? 'buffed' : hasNerf ? 'nerfed' : 'neutral'
              }
            }
          }
          return acc
        },
        {} as Record<number, ChampionBalance>
      )
    }

    return {}
  })

  const data = computed<Record<number, ChampionBalance>>(() => {
    const champions = { ...providerData.value }
    for (const champion of Object.values(eas.kiwi.balance)) {
      const hasBuff = champion.adjustments.some((entry) => entry.effect === 'buffed')
      const hasNerf = champion.adjustments.some((entry) => entry.effect === 'nerfed')
      champions[champion.id] = {
        id: champion.id,
        modes: {
          ...champions[champion.id]?.modes,
          KIWI: {
            adjustments: champion.adjustments,
            overallEffect:
              hasBuff && hasNerf ? 'mixed' : hasBuff ? 'buffed' : hasNerf ? 'nerfed' : 'neutral',
            source: 'Bilibili RESG',
            sourceUrl: eas.kiwi.sourceUrl,
            version: eas.kiwi.version,
            cached: eas.kiwi.cached,
            lastUpdate: eas.kiwi.lastUpdate
          }
        }
      }
    }
    return champions
  })

  return { data, source: readonly(source) }
}
