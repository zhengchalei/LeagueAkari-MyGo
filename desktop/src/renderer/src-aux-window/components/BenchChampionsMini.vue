<template>
  <NCard
    size="small"
    v-if="combinedChampions && gameMode"
    class="mini-champion-panel"
    :content-style="{ padding: '12px' }"
  >
    <div class="current-champion">
      <ChampionIcon class="current-icon" :champion-id="lcs.champSelect.currentChampion || -1" />
      <div class="current-info">
        <div class="current-name">
          {{
            lcs.champSelect.currentChampion
              ? lcs.gameData.championName(lcs.champSelect.currentChampion)
              : t('auxWindow.championBench.chooseChampion')
          }}
        </div>
        <div class="section-label">{{ t('auxWindow.championBench.currentChampion') }}</div>
      </div>
      <span v-if="lcs.champSelect.currentChampion" class="chosen-state">
        <NIcon><CheckmarkIcon /></NIcon>{{ t('auxWindow.champSelect.actions.picked') }}
      </span>
      <div v-if="shouldShowRerollButton" class="reroll-actions">
        <NButton
          v-if="shouldShowRerollButton"
          @click="() => handleReroll()"
          :disabled="rerollsRemaining === 0 || isRerolling"
          size="tiny"
          :title="
            t('auxWindow.championBench.reroll', {
              count: rerollsRemaining
            })
          "
          secondary
          type="primary"
        >
          <template #icon>
            <NIcon><RefreshOutlineIcon /></NIcon>
          </template>
        </NButton>
        <NButton
          v-if="shouldShowRerollButton"
          :disabled="rerollsRemaining === 0 || isRerolling"
          @click="() => handleReroll(true)"
          :title="
            t('auxWindow.championBench.charity', {
              count: rerollsRemaining
            })
          "
          secondary
          size="tiny"
        >
          <template #icon>
            <NIcon><ShareIcon /></NIcon>
          </template>
        </NButton>
      </div>
    </div>
    <div class="section-label">{{ t('auxWindow.championBench.availableChampions') }}</div>
    <div
      class="champion-grid"
      :style="{
        gridTemplateColumns: `repeat(${Math.min(availableChampionIds.length, 5) || 1}, minmax(0, 1fr))`
      }"
    >
      <NTooltip
        :show-arrow="false"
        :duration="100"
        :delay="300"
        v-for="championId of availableChampionIds"
        :key="championId"
        :keep-alive-on-hover="false"
        :disabled="!championAdjustment(championId)"
      >
        <template #trigger>
          <button
            type="button"
            class="champion-card"
            :aria-label="lcs.gameData.championName(championId)"
            :aria-pressed="championId === lcs.champSelect.currentChampion"
            :title="lcs.gameData.championName(championId)"
            :disabled="
              !canUseBench ||
              isSwappingOrPicking ||
              (championId !== lcs.champSelect.currentChampion && !isChampionSwappable(championId))
            "
            @click="handleBenchSwapOrPick(championId)"
            @contextmenu.prevent="handleBenchSwapOrPick(championId, false)"
          >
            <ChampionIcon class="choice-icon" :champion-id="championId" />
            <span class="champion-name">{{ lcs.gameData.championName(championId) }}</span>
            <span
              class="champion-summary"
              :data-effect="championAdjustment(championId)?.overallEffect"
            >
              {{ adjustmentSummary(championId) }}
            </span>
            <NIcon v-if="championId === lcs.champSelect.currentChampion" class="selected-check">
              <CheckmarkIcon />
            </NIcon>
          </button>
        </template>
        <div class="choice-tooltip">
          <div
            class="choice-tooltip-row"
            v-for="b of championAdjustment(championId)?.sortedAdjustments"
            :key="b.type"
          >
            <span>{{ b.name }}</span
            ><span>{{ b.changeValue }}</span>
          </div>
          <div v-if="!championAdjustment(championId)?.adjustments.length">
            {{ t('timo.selection.sourceNoChanges') }}
          </div>
          <div v-for="note in championAdjustment(championId)?.notes" :key="note">{{ note }}</div>
        </div>
      </NTooltip>
    </div>
    <section v-if="lcs.champSelect.currentChampion" class="current-balance" aria-live="polite">
      <div class="balance-heading">
        <span>{{ t('auxWindow.championBench.adjustments') }}</span>
        <span>{{ t('auxWindow.championBench.changeValue') }}</span>
      </div>
      <template v-if="currentAdjustment">
        <div
          v-for="entry in currentAdjustment.sortedAdjustments"
          :key="entry.type"
          class="balance-entry"
          :data-effect="entry.effect"
        >
          <span class="balance-category">
            {{
              t(
                entry.effect === 'buffed'
                  ? 'timo.selection.buffs'
                  : entry.effect === 'nerfed'
                    ? 'timo.selection.debuffs'
                    : 'timo.selection.notes'
              )
            }}
          </span>
          <span class="balance-label">{{ entry.name }}</span>
          <NTooltip :disabled="entry.display !== 'percentage'">
            <template #trigger
              ><span class="balance-value">{{ entry.changeValue }}</span></template
            >
            {{ t('auxWindow.championBench.sourceValue', { value: entry.formattedValue }) }}
          </NTooltip>
        </div>
        <div v-if="!currentAdjustment.adjustments.length" class="balance-empty">
          {{ t('timo.selection.sourceNoChanges') }}
        </div>
        <div v-for="note in currentAdjustment.notes" :key="note" class="balance-note">
          {{ note }}
        </div>
        <NTooltip :disabled="!currentAdjustment.sourceUrl">
          <template #trigger>
            <div class="balance-source">
              {{ currentAdjustment.source }} {{ currentAdjustment.version }}
              <template v-if="currentAdjustment.cached"
                >· {{ t('timo.selection.cached') }}</template
              >
            </div>
          </template>
          {{ currentAdjustment.sourceUrl }}
        </NTooltip>
      </template>
      <div v-else class="balance-empty">{{ t('timo.selection.noBalance') }}</div>
    </section>
  </NCard>
</template>

<script setup lang="ts">
import {
  BalanceAdjustment,
  useChampionBalanceData
} from '@aux-window/composables/useFandomBalanceData'
import ChampionIcon from '@renderer-shared/components/widgets/ChampionIcon.vue'
import { useInstance } from '@renderer-shared/shards'
import { LeagueClientRenderer } from '@renderer-shared/shards/league-client'
import { useLeagueClientStore } from '@renderer-shared/shards/league-client/store'
import {
  Checkmark as CheckmarkIcon,
  RefreshOutline as RefreshOutlineIcon,
  Share as ShareIcon
} from '@vicons/ionicons5'
import { useTranslation } from 'i18next-vue'
import { NButton, NCard, NIcon, NTooltip, useMessage } from 'naive-ui'
import { computed, ref } from 'vue'

const { t } = useTranslation()

const lcs = useLeagueClientStore()
const lc = useInstance(LeagueClientRenderer)

const gameMode = computed(() => {
  if (!lcs.gameflow.session) {
    return null
  }

  return lcs.gameflow.session.gameData.queue.gameMode
})

const source = computed(() => (gameMode.value === 'ARAM' ? 'opgg' : 'fandom'))
const { data } = useChampionBalanceData(source)

const fandomBalanceTypes = computed(() => {
  return {
    'damage-dealt': {
      name: t('auxWindow.championBench.balanceTypes.damage-dealt'),
      order: 0
    },
    'damage-taken': {
      name: t('auxWindow.championBench.balanceTypes.damage-taken'),
      order: 1
    },
    healing: {
      name: t('auxWindow.championBench.balanceTypes.healing'),
      order: 2
    },
    shielding: {
      name: t('auxWindow.championBench.balanceTypes.shielding'),
      order: 3
    },
    'ability-haste': {
      name: t('auxWindow.championBench.balanceTypes.ability-haste'),
      order: 4
    },
    'mana-regen': {
      name: t('auxWindow.championBench.balanceTypes.mana-regen'),
      order: 5
    },
    'energy-regen': {
      name: t('auxWindow.championBench.balanceTypes.energy-regen'),
      order: 6
    },
    'attack-speed': {
      name: t('auxWindow.championBench.balanceTypes.attack-speed'),
      order: 7
    },
    'attack-speed-growth': {
      name: t('auxWindow.championBench.balanceTypes.attack-speed-growth'),
      order: 7
    },
    'resource-regen': {
      name: t('auxWindow.championBench.balanceTypes.resource-regen'),
      order: 6
    },
    'movement-speed': {
      name: t('auxWindow.championBench.balanceTypes.movement-speed'),
      order: 8
    },
    tenacity: {
      name: t('auxWindow.championBench.balanceTypes.tenacity'),
      order: 9
    }
  }
})

const STATUS_SORT_ORDER = {
  buffed: 0,
  nerfed: 1,
  neutral: 2
}

const formatValue = (item: BalanceAdjustment) => {
  if (item.display === 'percentage') {
    return `${(100 * item.value).toFixed()}%`
  } else {
    return item.value > 0 ? `+${item.value}` : item.value
  }
}

const formatChange = (item: BalanceAdjustment) => {
  if (item.type === 'attack-speed-growth' && item.formattedValue) return item.formattedValue
  const value = Number(
    (item.display === 'percentage' ? (item.value - 1) * 100 : item.value).toFixed(2)
  )
  return `${value > 0 ? '+' : value < 0 ? '−' : ''}${Math.abs(value)}${item.display === 'percentage' ? '%' : ''}`
}

const championAdjustment = (championId: number) => {
  if (!gameMode.value) {
    return null
  }

  const champion = data.value[championId]

  if (!champion) {
    return null
  }

  const modeAdjustment = champion.modes[gameMode.value]

  if (!modeAdjustment) {
    return null
  }

  return {
    ...modeAdjustment,
    notes: modeAdjustment.adjustments
      .filter((item) => item.type === 'special' && item.description)
      .map((item) => item.description!),
    sortedAdjustments: modeAdjustment.adjustments
      .filter((item) => item.type !== 'special')
      .toSorted((a, b) => {
        const statusOrder = STATUS_SORT_ORDER[a.effect] - STATUS_SORT_ORDER[b.effect]
        if (statusOrder) return statusOrder
        const aBalanceOrder = fandomBalanceTypes.value[a.type]?.order ?? 0
        const bBalanceOrder = fandomBalanceTypes.value[b.type]?.order ?? 0

        if (aBalanceOrder !== bBalanceOrder) {
          return aBalanceOrder - bBalanceOrder
        }

        return 0
      })

      .map((item) => ({
        ...item,
        name: fandomBalanceTypes.value[item.type]?.name || item.type,
        formattedValue: item.formattedValue || formatValue(item),
        changeValue: formatChange(item)
      }))
  }
}

const currentAdjustment = computed(() => championAdjustment(lcs.champSelect.currentChampion || -1))
const adjustmentSummary = (championId: number) => {
  const adjustment = championAdjustment(championId)
  if (!adjustment) return '—'
  const buffs = adjustment.adjustments.filter((entry) => entry.effect === 'buffed').length
  const nerfs = adjustment.adjustments.filter((entry) => entry.effect === 'nerfed').length
  return (
    [
      buffs ? t('auxWindow.championBench.buffCount', { count: buffs }) : '',
      nerfs ? t('auxWindow.championBench.nerfCount', { count: nerfs }) : ''
    ]
      .filter(Boolean)
      .join(' · ') || t('auxWindow.championBench.unlisted')
  )
}

// lcux 中按照如下逻辑隐藏 bench. 在隐藏 bench 的时候, 通常也不能继续进行选择
const canUseBench = computed(() => {
  if (!lcs.champSelect.session) {
    return false
  }

  if (!lcs.champSelect.session.benchEnabled) {
    return false
  }

  const isInFinalizationPhase = lcs.champSelect.session.timer.phase === 'FINALIZATION'
  const isInBanPickPhase = lcs.champSelect.session.timer.phase === 'BAN_PICK'

  if (lcs.champSelect.session.allowSubsetChampionPicks) {
    return isInFinalizationPhase || isInBanPickPhase
  }

  return isInFinalizationPhase
})

// when in ban pick phase, the bench champions are the subset champions
const combinedChampions = computed(() => {
  if (!lcs.champSelect.session?.benchEnabled) {
    return null
  }

  const originalBenchChampions = lcs.champSelect.session.benchChampions || []

  if (lcs.champSelect.session.timer.phase === 'BAN_PICK') {
    const subsetChampionList = lcs.lobbyTeamBuilder.champSelect.subsetChampionList

    const newChampions = subsetChampionList
      .filter((championId) => !originalBenchChampions.some((c) => c.championId === championId))
      .filter((championId) => lcs.champSelect.currentChampion !== championId)
      .map((championId) => ({
        championId,
        isPriority: false
      }))

    return [...newChampions, ...originalBenchChampions]
  }

  return originalBenchChampions
})

const availableChampionIds = computed(() => [
  ...new Set([
    ...(lcs.champSelect.currentChampion ? [lcs.champSelect.currentChampion] : []),
    ...(combinedChampions.value || []).map((champion) => champion.championId)
  ])
])

const rerollsRemaining = computed(() => {
  if (!canUseBench.value) {
    return 0
  }

  return lcs.champSelect.session!.rerollsRemaining
})

// logic copied from lcux
const isChampionSwappable = (championId: number) => {
  if (!championId || !lcs.champSelect.session) {
    return false
  }

  const canPlay = lcs.champSelect.currentPickableChampionIds.has(championId)
  const waitingOnFinalizationPhase =
    lcs.champSelect.session.timer.phase === 'BAN_PICK' &&
    !lcs.lobbyTeamBuilder.champSelect.subsetChampionList.includes(championId)

  return canPlay && !waitingOnFinalizationPhase
}

// logic copied from lcux
const shouldShowRerollButton = computed(() => {
  if (!lcs.champSelect.session) {
    return false
  }

  return (
    lcs.champSelect.session.rerollsRemaining > 0 /* 特殊 hacky 情况 */ ||
    (lcs.champSelect.session.allowRerolling &&
      lcs.champSelect.session.timer.phase === 'FINALIZATION' &&
      !lcs.champSelect.session.allowSubsetChampionPicks)
  )
})

const message = useMessage()

const isRerolling = ref(false)
const isSwappingOrPicking = ref(false)

// complete takes effect only when in ban-pick phase
const handleBenchSwapOrPick = async (championId: number, complete = true) => {
  if (
    isSwappingOrPicking.value ||
    !canUseBench.value ||
    championId === lcs.champSelect.currentChampion
  ) {
    return
  }

  // isChampionSwappable makes sure lcs.champSelect.session is not null
  if (!isChampionSwappable(championId)) {
    return
  }

  isSwappingOrPicking.value = true
  try {
    if (lcs.champSelect.session!.timer.phase === 'BAN_PICK' && !lcs.champSelect.currentChampion) {
      const firstPickAction = lcs.champSelect
        .session!.actions.flat()
        .find(
          (a) =>
            a.type === 'pick' &&
            !a.completed &&
            a.actorCellId === lcs.champSelect.session!.localPlayerCellId
        )

      if (firstPickAction) {
        await lc.api.champSelect.pickOrBan(championId, complete, 'pick', firstPickAction.id)
      }
    } else {
      await lc.api.champSelect.benchSwap(championId)
    }
  } catch (error: any) {
    console.error(error)
    message.warning(
      t('auxWindow.championBench.swapFailed', {
        reason: error.message
      })
    )
  } finally {
    isSwappingOrPicking.value = false
  }
}

const handleReroll = async (grabBack = false) => {
  if (isRerolling.value) {
    return
  }

  isRerolling.value = true
  try {
    const prevId = lcs.champSelect.currentChampion

    await lc.api.champSelect.reroll()

    // 使用一个简短的延时来实现，simple workaround
    if (grabBack && prevId !== null) {
      window.setTimeout(async () => {
        if (combinedChampions.value) {
          await handleBenchSwapOrPick(prevId)
        }
      }, 25)
    }
  } catch (error: any) {
    message.warning(
      t('auxWindow.championBench.rerollFailed', {
        reason: error.message
      })
    )
  } finally {
    isRerolling.value = false
  }
}
</script>

<style scoped>
.mini-champion-panel {
  --mini-buff: #167650;
  --mini-nerf: #bf3d46;
}
:global([data-theme='dark']) .mini-champion-panel {
  --mini-buff: #6dd6ac;
  --mini-nerf: #ff9099;
}
.current-champion {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
}
.current-icon {
  width: 42px;
  height: 42px;
  border-radius: 6px;
  flex-shrink: 0;
}
.current-info {
  min-width: 0;
  flex: 1;
}
.current-name {
  font-size: 17px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.section-label {
  color: var(--la-color-text-secondary, var(--la-color-text-primary));
  opacity: 0.65;
  font-size: 11px;
  margin-bottom: 6px;
}
.current-info .section-label {
  margin: 2px 0 0;
}
.chosen-state {
  color: var(--mini-buff);
  display: flex;
  align-items: center;
  gap: 3px;
  font-size: 11px;
  white-space: nowrap;
}
.reroll-actions {
  display: flex;
  gap: 4px;
}
.champion-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 5px;
}
.champion-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 6px 2px;
  min-width: 0;
  margin: 0;
  border: 1px solid rgb(var(--la-card-border-rgb) / 0.14);
  border-radius: 5px;
  background: var(--la-card-surface-95);
  color: var(--la-color-text-primary);
  font-family: inherit;
  line-height: normal;
  text-align: center;
  cursor: pointer;
}
.champion-card[aria-pressed='true'] {
  border-color: var(--la-color-link);
  box-shadow: inset 0 0 0 1px var(--la-color-link);
}
.champion-card:hover:not(:disabled) {
  border-color: var(--la-color-link);
}
.champion-card:focus-visible {
  outline: 2px solid var(--la-color-link);
  outline-offset: 2px;
}
.champion-card:disabled {
  cursor: default;
}
.champion-card:disabled:not([aria-pressed='true']) {
  opacity: 0.5;
  filter: grayscale(0.7);
}
.choice-icon {
  width: 30px;
  height: 30px;
  border-radius: 4px;
}
.champion-name {
  width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
}
.champion-summary {
  font-size: 11px;
  min-height: 12px;
}
.champion-summary[data-effect='buffed'] {
  color: var(--mini-buff);
}
.champion-summary[data-effect='nerfed'] {
  color: var(--mini-nerf);
}
.selected-check {
  position: absolute;
  top: 3px;
  right: 3px;
  color: var(--la-color-link);
  font-size: 12px;
}
.choice-tooltip {
  font-size: 12px;
}
.choice-tooltip-row {
  display: flex;
  justify-content: space-between;
  gap: 16px;
}
.current-balance {
  margin-top: 13px;
}
.balance-heading {
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  opacity: 0.65;
  margin-bottom: 7px;
}
.balance-entry {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 9px;
  margin-top: 5px;
  background: var(--la-card-muted-surface);
  border-left: 3px solid transparent;
  border-radius: 4px;
}
.balance-entry[data-effect='buffed'] {
  border-left-color: var(--mini-buff);
}
.balance-entry[data-effect='nerfed'] {
  border-left-color: var(--mini-nerf);
}
.balance-category {
  font-size: 11px;
  flex-shrink: 0;
}
.balance-label {
  flex: 1;
  min-width: 0;
  font-size: 12px;
}
.balance-value {
  font-size: 21px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}
.balance-entry[data-effect='buffed'] :is(.balance-category, .balance-value) {
  color: var(--mini-buff);
}
.balance-entry[data-effect='nerfed'] :is(.balance-category, .balance-value) {
  color: var(--mini-nerf);
}
.balance-source {
  font-size: 10px;
  opacity: 0.6;
  margin-top: 8px;
}
.balance-empty,
.balance-note {
  font-size: 12px;
  opacity: 0.7;
  padding: 6px 0;
}
</style>
