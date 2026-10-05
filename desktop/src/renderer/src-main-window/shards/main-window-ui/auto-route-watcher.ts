import { useLeagueClientStore } from '@renderer-shared/shards/league-client/store'
import { useOngoingGameStore } from '@renderer-shared/shards/ongoing-game/store'
import { computed, watch } from 'vue'
import { useRouter } from 'vue-router'

export function watchAutoRouteWhenGameStarts() {
  const router = useRouter()
  const store = useOngoingGameStore()
  const leagueClient = useLeagueClientStore()

  const shouldRoute = computed(() => {
    if (!leagueClient.isConnected) {
      return false
    }

    return (
      Boolean(leagueClient.champSelect.session && !leagueClient.champSelect.session.isSpectating) ||
      [
        'ChampSelect',
        'GameStart',
        'InProgress',
        'Reconnect',
        'WaitingForStats',
        'PreEndOfGame'
      ].includes(leagueClient.gameflow.phase || '')
    )
  })

  watch(
    () => shouldRoute.value,
    (value, wasInMatch) => {
      // 选人、加载和游戏属于同一场对局，手动返回战绩后不再抢回页面。
      if (value && !wasInMatch && store.settings.autoRouteWhenGameStarts) {
        void router.replace({ name: 'ongoing-game' })
      } else if (!value && wasInMatch && router.currentRoute.value.name === 'ongoing-game') {
        void router.replace({ name: 'player-tabs' })
      }
    },
    { immediate: true }
  )
}
