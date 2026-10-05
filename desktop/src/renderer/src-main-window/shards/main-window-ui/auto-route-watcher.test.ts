import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, reactive, ref } from 'vue'

import { watchAutoRouteWhenGameStarts } from './auto-route-watcher'

const mocks = vi.hoisted(() => ({
  leagueClient: null as any,
  ongoingGame: null as any,
  router: null as any
}))

vi.mock('@renderer-shared/shards/league-client/store', () => ({
  useLeagueClientStore: () => mocks.leagueClient
}))
vi.mock('@renderer-shared/shards/ongoing-game/store', () => ({
  useOngoingGameStore: () => mocks.ongoingGame
}))
vi.mock('vue-router', () => ({
  useRouter: () => mocks.router
}))

describe('match navigation', () => {
  let scope: ReturnType<typeof effectScope>

  beforeEach(() => {
    mocks.leagueClient = reactive({
      isConnected: true,
      champSelect: { session: null },
      gameflow: { phase: 'Lobby' }
    })
    mocks.ongoingGame = reactive({ settings: { autoRouteWhenGameStarts: true } })
    const currentRoute = ref({ name: 'player-tabs' })
    mocks.router = {
      currentRoute,
      replace: vi.fn(async (target: { name: string }) => {
        currentRoute.value = target
      })
    }
    scope = effectScope()
  })

  afterEach(() => scope.stop())

  it('opens the match once and preserves a manual return during loading and gameplay', async () => {
    scope.run(watchAutoRouteWhenGameStarts)
    expect(mocks.router.replace).not.toHaveBeenCalled()

    mocks.leagueClient.champSelect.session = { isSpectating: false }
    mocks.leagueClient.gameflow.phase = 'ChampSelect'
    await nextTick()
    expect(mocks.router.replace).toHaveBeenCalledTimes(1)
    expect(mocks.router.currentRoute.value.name).toBe('ongoing-game')

    mocks.router.currentRoute.value = { name: 'player-tabs' }
    mocks.leagueClient.champSelect.session = null
    mocks.leagueClient.gameflow.phase = 'GameStart'
    await nextTick()
    mocks.leagueClient.gameflow.phase = 'InProgress'
    await nextTick()
    expect(mocks.router.replace).toHaveBeenCalledTimes(1)
    expect(mocks.router.currentRoute.value.name).toBe('player-tabs')

    mocks.leagueClient.gameflow.phase = 'EndOfGame'
    await nextTick()
    mocks.leagueClient.gameflow.phase = 'ChampSelect'
    await nextTick()
    expect(mocks.router.replace).toHaveBeenCalledTimes(2)
  })

  it('handles opening during a game and returns to history after the match', async () => {
    mocks.leagueClient.gameflow.phase = 'InProgress'
    scope.run(watchAutoRouteWhenGameStarts)
    await nextTick()
    expect(mocks.router.currentRoute.value.name).toBe('ongoing-game')
    mocks.leagueClient.gameflow.phase = 'EndOfGame'
    await nextTick()
    expect(mocks.router.currentRoute.value.name).toBe('player-tabs')
  })

  it('respects the automatic navigation setting and ignores disconnected clients', async () => {
    mocks.ongoingGame.settings.autoRouteWhenGameStarts = false
    scope.run(watchAutoRouteWhenGameStarts)
    mocks.leagueClient.gameflow.phase = 'ChampSelect'
    await nextTick()
    expect(mocks.router.replace).not.toHaveBeenCalled()

    mocks.leagueClient.isConnected = false
    await nextTick()
    mocks.ongoingGame.settings.autoRouteWhenGameStarts = true
    mocks.leagueClient.gameflow.phase = 'InProgress'
    await nextTick()
    expect(mocks.router.replace).not.toHaveBeenCalled()
  })
})
