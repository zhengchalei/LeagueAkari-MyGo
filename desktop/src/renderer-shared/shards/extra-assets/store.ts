import { BalanceType } from '@shared/data-sources/fandom'
import { GtimgHeroListJs, GtimgKiwiAugments, Hero } from '@shared/data-sources/gtimg'
import type { KiwiChampionBalance } from '@shared/types/champion-balance'
import type { OpggAramBalanceItem } from '@shared/types/opgg'
import { defineStore } from 'pinia'
import { computed, shallowReactive } from 'vue'

export const useExtraAssetsStore = defineStore('shard:extra-assets-renderer', () => {
  const gtimg = shallowReactive({
    heroList: null as GtimgHeroListJs | null,
    kiwiAugments: null as GtimgKiwiAugments[] | null
  })

  const kiwiAugmentsMap = computed(() => {
    if (!gtimg.kiwiAugments) return {}

    try {
      return gtimg.kiwiAugments.reduce(
        (acc, augment) => {
          acc[augment.augmentID] = augment
          return acc
        },
        {} as Record<number, GtimgKiwiAugments>
      )
    } catch {
      return {}
    }
  })

  const fandom = shallowReactive({
    balance: null as Record<string, BalanceType> | null
  })
  const opgg = shallowReactive({
    balance: {} as Record<number, OpggAramBalanceItem>,
    lastUpdate: 0,
    cached: true
  })
  const kiwi = shallowReactive({
    balance: {} as Record<number, KiwiChampionBalance>,
    version: '',
    sourceUrl: '',
    lastUpdate: 0,
    cached: true
  })

  const heroListMap = computed(() => {
    if (!gtimg.heroList) return {}

    try {
      return gtimg.heroList.hero.reduce(
        (acc, hero) => {
          acc[Number(hero.heroId)] = hero
          return acc
        },
        {} as Record<string, Hero>
      )
    } catch {
      return {}
    }
  })

  return {
    gtimg,
    fandom,
    opgg,
    kiwi,

    // computed
    heroListMap,
    kiwiAugmentsMap
  }
})
