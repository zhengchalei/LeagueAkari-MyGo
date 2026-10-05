<template>
  <section class="selection-panel" v-if="isSelecting">
    <div class="champion-section">
      <div class="section-heading">
        <span class="section-title">{{ t('timo.selection.title') }}</span>
        <span class="section-hint">{{ t('timo.selection.hint') }}</span>
      </div>

      <div class="champion-cards" v-if="championIds.length">
        <NTooltip v-for="championId in championIds" :key="championId" :delay="300">
          <template #trigger>
            <button
              type="button"
              class="champion-card"
              :class="{ selected: championId === currentChampion }"
              :aria-label="leagueClient.gameData.championName(championId)"
              :aria-pressed="championId === currentChampion"
              :disabled="championId !== currentChampion && !canSelectChampion(championId)"
              @click="selectChampion(championId)"
            >
              <ChampionIcon :champion-id="championId" class="champion-image" />
              <span>{{ leagueClient.gameData.championName(championId) }}</span>
            </button>
          </template>
          <div class="balance-tooltip">
            <span>{{ leagueClient.gameData.championName(championId) }}</span>
            <span
              v-for="adjustment in championAdjustments(championId)"
              :key="adjustment.type"
              :class="adjustment.effect"
            >
              {{ adjustmentLabel(adjustment) }} {{ formatAdjustment(adjustment) }}
            </span>
            <span v-if="!balanceData[championId]?.modes[gameMode]">
              {{ t('timo.selection.noBalance') }}
            </span>
          </div>
        </NTooltip>
      </div>
      <div v-else class="empty-message">{{ t('timo.selection.waiting') }}</div>

      <template v-if="currentChampion">
        <div class="current-champion">
          <span class="current-label">{{ t('timo.selection.current') }}</span>
          <strong>{{ leagueClient.gameData.championName(currentChampion) }}</strong>
        </div>
        <div class="balance-groups" v-if="currentBalance">
          <div class="balance-group buffed">
            <span class="balance-label">{{ t('timo.selection.buffs') }}</span>
            <span v-for="adjustment in buffs" :key="adjustment.type" class="balance-value">
              {{ adjustmentLabel(adjustment) }} {{ formatAdjustment(adjustment) }}
            </span>
            <span v-if="!buffs.length" class="balance-empty">—</span>
          </div>
          <div class="balance-group nerfed">
            <span class="balance-label">{{ t('timo.selection.debuffs') }}</span>
            <span v-for="adjustment in debuffs" :key="adjustment.type" class="balance-value">
              {{ adjustmentLabel(adjustment) }} {{ formatAdjustment(adjustment) }}
            </span>
            <span v-if="!debuffs.length" class="balance-empty">—</span>
          </div>
          <span class="balance-source">{{ t('timo.selection.source', { source: 'OP.GG' }) }}</span>
        </div>
        <span v-else class="empty-message">{{ t('timo.selection.noBalance') }}</span>
      </template>
    </div>

    <div class="skin-section">
      <div class="section-heading">
        <span class="section-title">{{ t('timo.selection.ownedSkins') }}</span>
        <span class="section-hint" v-if="skinOptions.length">
          {{ t('timo.selection.skinCount', { count: skinOptions.length }) }}
        </span>
        <NButton
          v-if="currentChampion"
          size="tiny"
          text
          :loading="isLoadingSkins"
          @click="loadSkins(currentChampion)"
        >
          {{ t('timo.selection.refresh') }}
        </NButton>
      </div>
      <div class="skin-cards" v-if="skinOptions.length">
        <button
          v-for="skin in skinOptions"
          :key="skin.id"
          type="button"
          class="skin-card"
          :class="{ selected: skin.id === selectedSkinId }"
          :aria-pressed="skin.id === selectedSkinId"
          :title="skin.name"
          :disabled="!canSelectSkin || isSettingSkin || isLoadingSkins"
          @click="selectSkin(skin.id)"
        >
          <LcuImage :src="skin.imagePath" class="skin-image" />
          <span>{{ skin.name }}</span>
        </button>
      </div>
      <div v-else class="empty-message">
        {{
          !currentChampion
            ? t('timo.selection.waiting')
            : isLoadingSkins
              ? t('timo.selection.skinLoading')
              : skinsLoadFailed
                ? t('timo.selection.skinLoadFailed')
                : t('timo.selection.noSkins')
        }}
      </div>
      <span v-if="skinOptions.length && !canSelectSkin" class="empty-message">
        {{ t('timo.selection.skinDisabled') }}
      </span>
    </div>
  </section>
</template>

<script setup lang="ts">
import {
  type BalanceAdjustment,
  useChampionBalanceData
} from '@aux-window/composables/useFandomBalanceData'
import LcuImage from '@renderer-shared/components/LcuImage.vue'
import ChampionIcon from '@renderer-shared/components/widgets/ChampionIcon.vue'
import { useInstance } from '@renderer-shared/shards'
import { LeagueClientRenderer } from '@renderer-shared/shards/league-client'
import { useLeagueClientStore } from '@renderer-shared/shards/league-client/store'
import { LoggerRenderer } from '@renderer-shared/shards/logger'
import { type CarouselSkins } from '@shared/types/league-client/champ-select'
import { type ChampDetails } from '@shared/types/league-client/game-data'
import { useTranslation } from 'i18next-vue'
import { NButton, NTooltip, useMessage } from 'naive-ui'
import { computed, ref, shallowRef, watch } from 'vue'

const { t } = useTranslation()
const leagueClient = useLeagueClientStore()
const client = useInstance(LeagueClientRenderer)
const logger = useInstance(LoggerRenderer).createLogger('timo:champion-selection')
const message = useMessage()
const { data: balanceData } = useChampionBalanceData('opgg')

const currentChampion = computed(() => leagueClient.champSelect.currentChampion || 0)
const gameMode = computed(() => leagueClient.gameflow.session?.gameData.queue.gameMode || '')
const isSelecting = computed(() =>
  Boolean(leagueClient.champSelect.session && !leagueClient.champSelect.session.isSpectating)
)
const isSelectingChampion = ref(false)

const championIds = computed(() => {
  const session = leagueClient.champSelect.session
  const champions = currentChampion.value ? [currentChampion.value] : []
  if (!session?.benchEnabled) {
    return champions
  }

  if (session.timer.phase === 'BAN_PICK') {
    champions.push(...leagueClient.lobbyTeamBuilder.champSelect.subsetChampionList)
  }
  champions.push(...session.benchChampions.map((champion) => champion.championId))
  return [...new Set(champions)].filter(Boolean)
})

const canSelectChampion = (championId: number) => {
  const session = leagueClient.champSelect.session
  if (!session?.benchEnabled || isSelectingChampion.value || championId === currentChampion.value) {
    return false
  }
  const canUseBench =
    session.timer.phase === 'FINALIZATION' ||
    (session.allowSubsetChampionPicks && session.timer.phase === 'BAN_PICK')
  const isWaitingForBench =
    session.timer.phase === 'BAN_PICK' &&
    !leagueClient.lobbyTeamBuilder.champSelect.subsetChampionList.includes(championId)
  return (
    canUseBench &&
    !isWaitingForBench &&
    leagueClient.champSelect.currentPickableChampionIds.has(championId)
  )
}

const selectChampion = async (championId: number) => {
  if (!canSelectChampion(championId)) {
    return
  }
  const session = leagueClient.champSelect.session!
  isSelectingChampion.value = true
  try {
    if (session.timer.phase === 'BAN_PICK' && !currentChampion.value) {
      const action = session.actions
        .flat()
        .find(
          (item) =>
            item.type === 'pick' &&
            !item.completed &&
            item.actorCellId === session.localPlayerCellId
        )
      if (action) {
        await client.api.champSelect.pickOrBan(championId, true, 'pick', action.id)
      }
    } else {
      await client.api.champSelect.benchSwap(championId)
    }
  } catch (error: any) {
    logger.warn('Champion selection failed', error)
    message.warning(t('timo.selection.failed', { reason: error.message }))
  } finally {
    isSelectingChampion.value = false
  }
}

const championAdjustments = (championId: number) => {
  return (balanceData.value[championId]?.modes[gameMode.value]?.adjustments || []).filter(
    (adjustment) => adjustment.effect !== 'neutral'
  )
}
const currentBalance = computed(
  () => balanceData.value[currentChampion.value]?.modes[gameMode.value]
)
const buffs = computed(() =>
  championAdjustments(currentChampion.value).filter((item) => item.effect === 'buffed')
)
const debuffs = computed(() =>
  championAdjustments(currentChampion.value).filter((item) => item.effect === 'nerfed')
)
const adjustmentLabel = (adjustment: BalanceAdjustment) => {
  return t(`auxWindow.championBench.balanceTypes.${adjustment.type}`)
}
const formatAdjustment = (adjustment: BalanceAdjustment) => {
  return adjustment.display === 'percentage'
    ? `${(adjustment.value * 100).toFixed()}%`
    : `${adjustment.value > 0 ? '+' : ''}${adjustment.value}`
}

const carouselSkins = shallowRef<CarouselSkins[]>([])
const championDetails = shallowRef<ChampDetails | null>(null)
const isLoadingSkins = ref(false)
const skinsLoadFailed = ref(false)
const isSettingSkin = ref(false)
let skinLoadSequence = 0

const selectedSkinId = computed(() => leagueClient.champSelect.skinSelectorInfo?.selectedSkinId)
const canSelectSkin = computed(() => {
  const session = leagueClient.champSelect.session
  return Boolean(
    session?.allowSkinSelection &&
    currentChampion.value &&
    !leagueClient.champSelect.skinSelectorInfo?.skinSelectionDisabled
  )
})

const skinOptions = computed(() => {
  const names = new Map<number, string>()
  for (const skin of championDetails.value?.skins || []) {
    names.set(skin.id, skin.name)
    for (const chroma of skin.chromas || []) {
      names.set(chroma.id, chroma.name)
    }
  }

  return carouselSkins.value.flatMap((skin) => {
    if (!skin.unlocked || skin.championId !== currentChampion.value) {
      return []
    }
    const options = skin.disabled
      ? []
      : [
          {
            id: skin.id,
            name: names.get(skin.id) || skin.name,
            imagePath: skin.tilePath || skin.splashPath
          }
        ]
    for (const chroma of skin.childSkins) {
      if (chroma.unlocked && !chroma.disabled) {
        options.push({
          id: chroma.id,
          name: names.get(chroma.id) || chroma.name,
          imagePath: chroma.chromaPreviewPath || chroma.tilePath || chroma.splashPath
        })
      }
    }
    return options
  })
})

const loadSkins = async (championId: number) => {
  const sequence = ++skinLoadSequence
  carouselSkins.value = []
  championDetails.value = null
  skinsLoadFailed.value = false
  isLoadingSkins.value = Boolean(championId)
  if (!championId) {
    return
  }
  try {
    const [skins, details] = await Promise.all([
      client.api.champSelect.getCarouselSkins(),
      client.api.gameData.getChampDetails(championId)
    ])
    // 英雄快速切换时，只展示最后一次选择的皮肤。
    if (sequence === skinLoadSequence && currentChampion.value === championId) {
      carouselSkins.value = skins.data
      championDetails.value = details.data
    }
  } catch (error) {
    if (sequence === skinLoadSequence) {
      skinsLoadFailed.value = true
      logger.warn('Owned skins could not be loaded', error)
    }
  } finally {
    if (sequence === skinLoadSequence) {
      isLoadingSkins.value = false
    }
  }
}

watch(currentChampion, (championId) => void loadSkins(championId), { immediate: true })

const selectSkin = async (skinId: number) => {
  if (
    !canSelectSkin.value ||
    isSettingSkin.value ||
    !skinOptions.value.some((skin) => skin.id === skinId)
  ) {
    return
  }
  isSettingSkin.value = true
  try {
    await client.api.champSelect.setSkin(skinId)
  } catch (error) {
    logger.warn('Skin selection failed', error)
    message.warning(t('timo.selection.skinFailed'))
  } finally {
    isSettingSkin.value = false
  }
}
</script>

<style scoped>
.selection-panel {
  display: grid;
  grid-template-columns: minmax(280px, 1fr) minmax(240px, 1fr);
  gap: 20px;
  padding: 16px 20px;
  color: var(--la-color-text-themed);
  background: var(--la-card-surface-95);
  border: 1px solid rgb(var(--la-card-border-rgb) / 0.1);
  border-radius: 12px;
}

.section-heading {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}
.section-title {
  font-size: 13px;
  font-weight: 600;
}
.section-hint,
.empty-message,
.balance-source {
  font-size: 11px;
  opacity: 0.6;
}
.section-hint {
  flex: 1;
}
.champion-cards,
.skin-cards {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  max-height: 166px;
  overflow-y: auto;
}
.champion-card,
.skin-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 4px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 10px;
  line-height: 1.6;
  cursor: pointer;
  transition:
    background-color 0.15s,
    border-color 0.15s;
}
.champion-card {
  width: 58px;
}
.champion-image {
  width: 44px;
  height: 44px;
  border-radius: 6px;
  margin-bottom: 3px;
}
.skin-card {
  width: 104px;
}
.skin-image {
  width: 94px;
  height: 56px;
  object-fit: cover;
  border-radius: 4px;
  margin-bottom: 4px;
}
.champion-card span,
.skin-card span {
  max-width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.champion-card:hover:not(:disabled),
.skin-card:hover:not(:disabled) {
  background: rgb(var(--la-card-tint-rgb) / 0.05);
}
.champion-card.selected,
.skin-card.selected {
  border-color: var(--la-color-link);
  background: color-mix(in oklch, var(--la-color-link) 10%, transparent);
}
.champion-card:disabled:not(.selected),
.skin-card:disabled:not(.selected) {
  opacity: 0.4;
  cursor: not-allowed;
}
.champion-card:focus-visible,
.skin-card:focus-visible {
  outline: 2px solid var(--la-color-link);
  outline-offset: 2px;
}
.current-champion {
  display: flex;
  gap: 8px;
  align-items: baseline;
  margin-top: 12px;
  font-size: 12px;
}
.current-label {
  font-size: 10px;
  opacity: 0.5;
}
.balance-groups {
  display: flex;
  flex-direction: column;
  gap: 5px;
  margin-top: 6px;
}
.balance-group {
  display: flex;
  gap: 6px;
  align-items: baseline;
  flex-wrap: wrap;
  font-size: 11px;
}
.balance-label {
  font-weight: 600;
}
.balance-value {
  padding: 1px 5px;
  border-radius: 4px;
  background: currentColor;
  background: color-mix(in oklch, currentColor 8%, transparent);
}
.buffed {
  color: #22845c;
}
.nerfed {
  color: #b45555;
}
.balance-tooltip {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
}
:global([data-theme='dark']) .buffed {
  color: #73cda3;
}
:global([data-theme='dark']) .nerfed {
  color: #ee9393;
}
@media (max-width: 940px) {
  .selection-panel {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    gap: 12px;
    padding: 12px;
  }
  .section-hint {
    display: none;
  }
  .champion-cards,
  .skin-cards {
    max-height: 136px;
  }
  .skin-card {
    width: 86px;
  }
  .skin-image {
    width: 76px;
    height: 48px;
  }
}
</style>
