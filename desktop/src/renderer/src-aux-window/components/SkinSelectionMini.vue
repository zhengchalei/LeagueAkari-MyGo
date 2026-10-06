<template>
  <NCard
    size="small"
    v-if="aws.settings.showSkinSelector && currentChampion"
    :content-style="{ padding: '12px' }"
  >
    <div class="skin-heading">
      <span>{{ t('auxWindow.skinSelection.title') }}</span>
      <span v-if="skinOptions.length" class="skin-count">
        {{ t('auxWindow.skinSelection.ownedCount', { count: skinOptions.length }) }}
      </span>
    </div>
    <div v-if="skinOptions.length" class="skin-grid">
      <button
        v-for="skin in skinOptions"
        :key="skin.id"
        type="button"
        class="skin-card"
        :aria-label="t('auxWindow.skinSelection.chooseSkin', { name: skin.name })"
        :aria-pressed="skin.id === selectedSkin?.id"
        :title="skin.name"
        :disabled="!canSelectSkin || isLoadingSkins || Boolean(pendingSkin)"
        @click="handleSetSkin(skin.id)"
      >
        <LcuImage :src="skin.imagePath" class="skin-image" alt="" />
        <span class="skin-name">{{ skin.name }}</span>
        <span v-if="skin.id === selectedSkin?.id" class="skin-check" aria-hidden="true">
          <svg viewBox="0 0 16 16"><path d="m4 8 2.5 2.5L12 5" /></svg>
        </span>
      </button>
    </div>
    <div v-else class="skin-empty" role="status">
      {{
        t(
          isLoadingSkins
            ? 'auxWindow.skinSelection.loading'
            : skinsLoadFailed
              ? 'auxWindow.skinSelection.loadFailed'
              : 'auxWindow.skinSelection.empty'
        )
      }}
    </div>
    <div v-if="skinOptions.length" class="skin-status" role="status" aria-live="polite">
      {{ skinStatus }}
    </div>
  </NCard>
</template>

<script setup lang="ts">
import LcuImage from '@renderer-shared/components/LcuImage.vue'
import { useInstance } from '@renderer-shared/shards'
import { LeagueClientRenderer } from '@renderer-shared/shards/league-client'
import { useLeagueClientStore } from '@renderer-shared/shards/league-client/store'
import { useAuxWindowStore } from '@renderer-shared/shards/window-manager/store'
import { CarouselSkins } from '@shared/types/league-client/champ-select'
import { ChampDetails } from '@shared/types/league-client/game-data'
import { useTranslation } from 'i18next-vue'
import { NCard, useMessage } from 'naive-ui'
import { computed, ref, shallowRef, watch } from 'vue'

const { t } = useTranslation()
const aws = useAuxWindowStore()
const lcs = useLeagueClientStore()
const lc = useInstance(LeagueClientRenderer)
const message = useMessage()

const currentChampion = computed(() => lcs.champSelect.currentChampion)
const carouselSkins = shallowRef<CarouselSkins[]>([])
const championDetails = shallowRef<ChampDetails | null>(null)
const isLoadingSkins = ref(false)
const skinsLoadFailed = ref(false)
const skinApplyFailed = ref(false)
const pendingSkin = shallowRef<{ championId: number; id: number; name: string } | null>(null)

const canSelectSkin = computed(() => {
  const session = lcs.champSelect.session
  return Boolean(
    currentChampion.value &&
    session?.allowSkinSelection &&
    !session.isSpectating &&
    !lcs.champSelect.skinSelectorInfo?.skinSelectionDisabled
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
            imagePath: skin.splashPath || skin.tilePath
          }
        ]
    for (const chroma of skin.childSkins) {
      if (chroma.unlocked && !chroma.disabled) {
        options.push({
          id: chroma.id,
          name: names.get(chroma.id) || chroma.name,
          imagePath: chroma.chromaPreviewPath || chroma.splashPath || chroma.tilePath
        })
      }
    }
    return options
  })
})

// 选中标记以客户端回传为准，点击和请求成功都不会提前更改它。
const selectedSkin = computed(() =>
  skinOptions.value.find((skin) => skin.id === lcs.champSelect.skinSelectorInfo?.selectedSkinId)
)

const skinStatus = computed(() => {
  if (pendingSkin.value?.championId === currentChampion.value) {
    return t('auxWindow.skinSelection.applying', { name: pendingSkin.value.name })
  }
  if (skinApplyFailed.value) {
    return t('auxWindow.skinSelection.failed')
  }
  if (!canSelectSkin.value) {
    return t('auxWindow.skinSelection.disabled')
  }
  return selectedSkin.value
    ? t('auxWindow.skinSelection.selected', { name: selectedSkin.value.name })
    : t('auxWindow.skinSelection.waitingSelection')
})

watch(
  currentChampion,
  async (championId, _, onCleanup) => {
    let stale = false
    onCleanup(() => (stale = true))
    carouselSkins.value = []
    championDetails.value = null
    skinsLoadFailed.value = false
    skinApplyFailed.value = false
    isLoadingSkins.value = Boolean(championId)
    if (!championId) {
      return
    }

    const [skins, details] = await Promise.allSettled([
      lc.api.champSelect.getCarouselSkins(),
      lc.api.gameData.getChampDetails(championId)
    ])
    if (stale || currentChampion.value !== championId) {
      return
    }
    if (skins.status === 'fulfilled') {
      carouselSkins.value = skins.value.data
    } else {
      skinsLoadFailed.value = true
    }
    if (details.status === 'fulfilled') {
      championDetails.value = details.value.data
    }
    isLoadingSkins.value = false
  },
  { immediate: true }
)

const handleSetSkin = async (skinId: number) => {
  const championId = currentChampion.value
  const skin = skinOptions.value.find((option) => option.id === skinId)
  if (
    !championId ||
    !skin ||
    !canSelectSkin.value ||
    pendingSkin.value ||
    isLoadingSkins.value ||
    selectedSkin.value?.id === skinId
  ) {
    return
  }

  pendingSkin.value = { championId, id: skinId, name: skin.name }
  skinApplyFailed.value = false
  try {
    await lc.api.champSelect.setSkin(skinId)
  } catch (error) {
    if (currentChampion.value === championId) {
      skinApplyFailed.value = true
      message.warning(t('auxWindow.skinSelection.failed'))
    }
  } finally {
    pendingSkin.value = null
  }
}
</script>

<style scoped>
.skin-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 9px;
  color: var(--la-color-text-primary);
  font-size: 12px;
  font-weight: 500;
}

.skin-count,
.skin-empty,
.skin-status {
  color: var(--la-color-text-primary);
  font-size: 11px;
  font-weight: 400;
  opacity: 0.65;
}

.skin-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 7px;
  max-height: 228px;
  padding: 1px;
  overflow-y: auto;
}

.skin-card {
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
  margin: 0;
  padding: 0;
  overflow: hidden;
  border: 1px solid rgb(var(--la-card-border-rgb) / 0.14);
  border-radius: 5px;
  background: var(--la-card-surface-95);
  color: var(--la-color-text-primary);
  font: inherit;
  text-align: center;
  cursor: pointer;
  transition: border-color 0.15s;
}

.skin-card:hover:not(:disabled),
.skin-card[aria-pressed='true'] {
  border-color: var(--la-color-link);
}

.skin-card[aria-pressed='true'] {
  box-shadow: 0 0 0 1px var(--la-color-link);
}

.skin-card:focus-visible {
  outline: 2px solid var(--la-color-link);
  outline-offset: -2px;
}

.skin-card:disabled {
  cursor: default;
  opacity: 0.7;
}

.skin-image {
  width: 100%;
  height: 51px;
  object-fit: cover;
  flex-shrink: 0;
}

.skin-name {
  display: block;
  width: 100%;
  box-sizing: border-box;
  padding: 6px 3px;
  overflow: hidden;
  font-size: 11px;
  line-height: 16px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.skin-check {
  position: absolute;
  top: 4px;
  right: 4px;
  display: grid;
  width: 16px;
  height: 16px;
  place-items: center;
  border-radius: 50%;
  background: var(--la-color-link);
  color: var(--la-card-surface-95);
}

.skin-check svg {
  width: 12px;
  height: 12px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.skin-empty {
  padding: 8px 0;
}

.skin-status {
  margin-top: 9px;
  min-height: 16px;
  line-height: 16px;
}
</style>
