<template>
  <div class="ongoing-game-page">
    <ConnectedMatchPreviewer
      v-model:show="showPreviewModal"
      :game-id="previewingGame.gameId"
      :source="previewingGame.source"
      :puuid="previewingGame.puuid"
      :summary="previewingGame.summary"
      :details="previewingGame.details"
      :hide-privacy="as.settings.streamerMode"
      can-dry-run-ongoing-game
      @navigate-to-summoner-by-puuid="navigateToTabByPuuid"
      @dry-run-ongoing-game="handleDryRunOngoingGame"
    />
    <ChampionSelectionPanel />
    <div class="match-section-heading" v-if="isSelecting">
      <span>{{ t('timo.match.title') }}</span>
      <span class="match-section-hint">{{ t('timo.match.hint') }}</span>
    </div>
    <div class="match-panel-container" ref="matchPanelContainer">
      <OngoingGameProvider :value="ongoingGame">
        <OngoingGamePanel
          :content-width="panelWidth"
          :content-height="panelHeight"
          @navigate-to-summoner-by-puuid="navigateToTabByPuuid"
          @preview-game="handlePreviewGame"
        />
      </OngoingGameProvider>
    </div>
  </div>
</template>

<script lang="ts" setup>
import ConnectedMatchPreviewer from '@renderer-shared/components/match-preview/ConnectedMatchPreviewer.vue'
import OngoingGamePanel from '@renderer-shared/components/ongoing-game-panel/OngoingGamePanel.vue'
import {
  createAkariOngoingGameProvider,
  OngoingGameProvider
} from '@renderer-shared/providers/ongoing-game'
import {
  type MatchPreviewPayload,
  type MatchPreviewState,
  toMatchPreviewState
} from '@renderer-shared/components/match-preview'
import { useInstance } from '@renderer-shared/shards'
import { useAppCommonStore } from '@renderer-shared/shards/app-common/store'
import { useLeagueClientStore } from '@renderer-shared/shards/league-client/store'
import { OngoingGameRenderer } from '@renderer-shared/shards/ongoing-game'
import { DraftOptions } from '@shared/shards/ongoing-game'
import { useElementSize } from '@vueuse/core'
import { useTranslation } from 'i18next-vue'
import { computed, ref, shallowRef, useTemplateRef } from 'vue'

import { PlayerTabsRenderer } from '@main-window/shards/player-tabs'
import ChampionSelectionPanel from './ChampionSelectionPanel.vue'

const { t } = useTranslation()
const matchPanelContainer = useTemplateRef('matchPanelContainer')
const { width: panelWidth, height: panelHeight } = useElementSize(matchPanelContainer)
const leagueClient = useLeagueClientStore()
const isSelecting = computed(() => Boolean(leagueClient.champSelect.session))

const pt = useInstance(PlayerTabsRenderer)
const og = useInstance(OngoingGameRenderer)
const ongoingGame = createAkariOngoingGameProvider()

const as = useAppCommonStore()

const { navigateToTabByPuuid } = pt.useNavigateToTab()

const showPreviewModal = ref(false)
const previewingGame = shallowRef<MatchPreviewState>({
  gameId: 0,
  source: 'sgp'
})

const handlePreviewGame = (payload: MatchPreviewPayload) => {
  previewingGame.value = toMatchPreviewState(payload, as.settings.preferredLolSource)
  showPreviewModal.value = true
}

const handleDryRunOngoingGame = async (draft: DraftOptions) => {
  await og.setDraft(draft)
  showPreviewModal.value = false
}
</script>

<style scoped>
.ongoing-game-page {
  height: 100%;
  display: flex;
  flex-direction: column;
  box-sizing: border-box;
  padding: 16px 16px 0;
  gap: 12px;
}
.match-panel-container {
  min-height: 0;
  flex: 1;
  width: 100%;
}
.match-section-heading {
  display: flex;
  align-items: baseline;
  gap: 12px;
  padding: 0 4px;
  font-size: 13px;
  font-weight: 600;
  color: var(--la-color-text-themed);
}
.match-section-hint {
  font-size: 11px;
  font-weight: 400;
  opacity: 0.55;
}
</style>
