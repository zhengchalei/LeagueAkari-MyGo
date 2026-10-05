<template>
  <div
    class="app-sidebar"
    ref="app-sidebar"
    :class="{
      collapsed: mui.frontendSettings.sidebarCollapsed
    }"
    :style="{
      '--la-sidebar-width-collapsed': as.isMacOS ? '76px' : '64px',
      '--la-sidebar-width-expanded': '180px',
      '--la-sidebar-icon-height': '42px',
      '--la-sidebar-icon-horizontal-padding': '8px'
    }"
  >
    <!-- macOS 的 logo 区留给交通等 -->
    <div class="app-sidebar__head-safe-area" v-if="as.isMacOS"></div>
    <div class="app-sidebar__head" v-else>
      <div class="app-sidebar__logo">
        <span class="app-sidebar__logo-icon">L</span>
      </div>
      <div class="app-sidebar__logo-text">
        <span>LeagueAkari</span>
        <span class="app-sidebar__edition">MyGo</span>
      </div>
    </div>

    <SidebarMenu
      class="app-sidebar__menu"
      :items="menu.slice(0, 2)"
      :current="currentMenu"
      @update:current="(key) => handleMenuChange(key)"
      :is-collapsed="mui.frontendSettings.sidebarCollapsed"
    />

    <div class="app-sidebar__padding"></div>

    <div class="app-sidebar__secondary-label" v-if="!mui.frontendSettings.sidebarCollapsed">
      {{ t('timo.navigation.more') }}
    </div>
    <SidebarMenu
      class="app-sidebar__secondary"
      :items="menu.slice(2)"
      :current="currentMenu"
      @update:current="(key) => handleMenuChange(key)"
      :is-collapsed="mui.frontendSettings.sidebarCollapsed"
    />

    <SidebarFixed
      class="app-sidebar__fixed"
      :is-collapsed="mui.frontendSettings.sidebarCollapsed"
    />

    <!-- 一个展开和缩小的按钮 -->
    <div class="app-sidebar__expand-line" ref="expandLineEl" @mousedown="toggleCollapse">
      <div class="app-sidebar__expand-line-inner"></div>
    </div>
  </div>
</template>

<script setup lang="tsx">
import { useInstance } from '@renderer-shared/shards'
import { useAppCommonStore } from '@renderer-shared/shards/app-common/store'
import { useLeagueClientStore } from '@renderer-shared/shards/league-client/store'
import { useOngoingGameStore } from '@renderer-shared/shards/ongoing-game/store'
import { WindowManagerRenderer } from '@renderer-shared/shards/window-manager'
import { LoggerRenderer } from '@renderer-shared/shards/logger'
import { ToolFilled as ToolFilledIcon } from '@vicons/antd'
import { AiStatus as AiStatusIcon } from '@vicons/carbon'
import {
  AnimalRabbit28Filled as AnimalRabbit28FilledIcon,
  Games24Filled as Games24FilledIcon
} from '@vicons/fluent'
import { AnalyticsRound as AnalyticsRoundIcon } from '@vicons/material'
import { useTranslation } from 'i18next-vue'
import { NIcon } from 'naive-ui'
import {
  Component as ComponentC,
  computed,
  nextTick,
  ref,
  useTemplateRef,
  watch,
  watchEffect
} from 'vue'
import { useRoute, useRouter } from 'vue-router'

import { useMainWindowUiStore } from '@main-window/shards/main-window-ui/store'

import SidebarFixed from './SidebarFixed.vue'
import SidebarMenu from './SidebarMenu.vue'

const { t } = useTranslation()

const as = useAppCommonStore()
const ogs = useOngoingGameStore()
const mui = useMainWindowUiStore()
const lcs = useLeagueClientStore()
const wm = useInstance(WindowManagerRenderer)
const logger = useInstance(LoggerRenderer).createLogger('timo:sidebar')
const sidebarEl = useTemplateRef('app-sidebar')

const MAIN_WINDOW_TRAFFIC_LIGHT_DEFAULT_POSITION = {
  x: 4,
  y: 12
}
const MAIN_WINDOW_TRAFFIC_LIGHT_EXPANDED_FALLBACK_X = 8

const renderIcon = (icon: ComponentC) => {
  const Icon = icon as any
  return () => (
    <NIcon>
      <Icon />
    </NIcon>
  )
}

const router = useRouter()
const route = useRoute()

const shouldShowOngoingGameBadge = ref(false)
const isInCombatPhase = computed(() => {
  return ogs.queryStage.phase !== 'unavailable' && ogs.queryStage.phase !== 'lobby'
})

const toggleCollapse = () => {
  mui.frontendSettings.sidebarCollapsed = !mui.frontendSettings.sidebarCollapsed
}

const getSidebarPxProperty = (propertyName: string, fallback: number) => {
  if (!sidebarEl.value) {
    return fallback
  }

  const value = Number.parseFloat(getComputedStyle(sidebarEl.value).getPropertyValue(propertyName))
  return Number.isFinite(value) ? value : fallback
}

const syncMainWindowTrafficLightPosition = () => {
  if (!as.isMacOS) {
    return
  }

  if (mui.frontendSettings.sidebarCollapsed) {
    void wm.mainWindow.setTrafficLightPosition(
      MAIN_WINDOW_TRAFFIC_LIGHT_DEFAULT_POSITION.x,
      MAIN_WINDOW_TRAFFIC_LIGHT_DEFAULT_POSITION.y
    )
    return
  }

  const x = getSidebarPxProperty(
    '--la-sidebar-icon-horizontal-padding',
    MAIN_WINDOW_TRAFFIC_LIGHT_EXPANDED_FALLBACK_X
  )

  void wm.mainWindow.setTrafficLightPosition(
    Math.round(x),
    MAIN_WINDOW_TRAFFIC_LIGHT_DEFAULT_POSITION.y
  )
}

// TODO: 临时方案：折叠状态仍由前端拥有，因此先在这里同步 macOS 交通灯位置。
watch(
  () => [as.isMacOS, mui.frontendSettings.sidebarCollapsed] as const,
  () => {
    void nextTick(syncMainWindowTrafficLightPosition)
  },
  { immediate: true, flush: 'post' }
)

watch(
  () => isInCombatPhase.value,
  (yes) => {
    if (yes && currentMenu.value !== 'ongoing-game') {
      shouldShowOngoingGameBadge.value = true
    } else {
      shouldShowOngoingGameBadge.value = false
    }
  }
)

const currentMenu = ref('match-history')
const menu = computed(() => {
  return [
    {
      key: 'player-tabs',
      icon: renderIcon(AnalyticsRoundIcon),
      name: t('timo.navigation.myHistory')
    },
    {
      key: 'ongoing-game',
      icon: renderIcon(Games24FilledIcon),
      name: t('navigation.sidebar.menu.ongoing-game'),
      inProgress: shouldShowOngoingGameBadge.value,
      isDisabled: !lcs.isConnected
    },
    {
      key: 'automation',
      icon: renderIcon(AiStatusIcon),
      name: t('navigation.sidebar.menu.automation')
    },
    {
      key: 'toolkit',
      icon: renderIcon(ToolFilledIcon),
      name: t('navigation.sidebar.menu.toolkit')
    },
    {
      key: 'test',
      icon: renderIcon(AnimalRabbit28FilledIcon),
      name: t('navigation.sidebar.menu.test'),
      show: mui.frontendSettings.showTestPage
    }
  ]
})

const handleMenuChange = async (val: string | undefined) => {
  try {
    await router.replace({ name: val })
  } catch (error) {
    logger.warn('Navigation failed', error)
  }
}

watchEffect(() => {
  currentMenu.value = route.name as string

  if (route.name === 'ongoing-game') {
    shouldShowOngoingGameBadge.value = false
  }
})

watch(
  () => lcs.isConnected,
  (isConnected) => {
    if (!isConnected && route.name === 'ongoing-game') {
      router.replace({ name: 'player-tabs' })
    }
  }
)
</script>

<style scoped>
.app-sidebar {
  --la-sidebar-macos-safe-top: 42px;

  display: flex;
  flex-direction: column;
  height: 100%;
  width: var(--la-sidebar-width-expanded);
  transition: width 0.3s cubic-bezier(0, 0.9, 0.1, 1);
  position: relative;

  &.collapsed {
    width: var(--la-sidebar-width-collapsed);
  }

  .app-sidebar__head-safe-area {
    height: var(--la-sidebar-macos-safe-top);
  }

  .app-sidebar__head {
    position: relative;
    display: flex;
    align-items: center;
    padding-inline: var(--la-sidebar-icon-horizontal-padding);
    padding-block: 12px 8px;
    gap: 4px;
    overflow: hidden;
  }

  .app-sidebar__logo {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    width: calc(var(--la-sidebar-width-collapsed) - var(--la-sidebar-icon-horizontal-padding) * 2);
    height: var(--la-sidebar-icon-height);
    font-size: 20px;
    flex-shrink: 0;
    -webkit-app-region: drag;
  }

  .app-sidebar__logo-icon {
    color: var(--la-color-link);
    font-size: 24px;
    font-weight: 700;
  }

  .app-sidebar__logo-text {
    display: flex;
    flex-direction: column;
    font-size: 13px;
    line-height: 18px;
    font-weight: bold;
    font-family: 'Comfortaa', sans-serif;
    text-wrap-mode: nowrap;
    transition: opacity 0.2s;

    .collapsed & {
      opacity: 0;
    }
  }

  .app-sidebar__edition {
    font-size: 10px;
    font-weight: normal;
    opacity: 0.55;
  }

  .app-sidebar__logo-toggle {
    position: absolute;
    color: rgba(0, 0, 0, 1);
    transition:
      opacity 0.2s ease,
      color 0.2s ease,
      transform 0.2s ease;
    opacity: 0;

    &:active {
      transform: scale(0.9);
    }

    [data-theme='dark'] & {
      color: rgba(255, 255, 255, 1);
    }

    [data-theme-id]:not([data-theme-id='light']):not([data-theme-id='dark']) & {
      color: color-mix(in oklch, var(--la-color-text-themed) 92%, transparent);
    }
  }

  .app-sidebar__padding {
    flex: 1;
  }

  .app-sidebar__secondary-label {
    margin: 12px 20px 6px;
    font-size: 10px;
    opacity: 0.45;
  }

  .app-sidebar__secondary {
    padding-bottom: 12px;
    margin-bottom: 8px;
    border-bottom: 1px solid rgb(var(--la-card-border-rgb) / 0.08);
  }

  .app-sidebar__fixed {
    margin-bottom: var(--la-sidebar-icon-horizontal-padding);
  }

  .app-sidebar__expand-line {
    position: absolute;
    right: 0;
    top: 0;
    height: 100%;
    padding: 0 4px;
    transform: translateX(50%);
    cursor: ew-resize;

    &:hover .app-sidebar__expand-line-inner {
      background-color: #f83f6f;
    }

    .app-sidebar__expand-line-inner {
      width: 2px;
      height: 100%;
      background-color: transparent;
      transition: background-color 0.4s;
    }
  }
}
</style>
