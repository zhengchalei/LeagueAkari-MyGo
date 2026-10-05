import type { Ballot } from '@shared/types/league-client/honorV2'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { AutoGameflowActionController } from './action-controller'
import type { AutoGameflowMainContext } from './context'
import { AutoGameflowHonorController } from './honor-controller'
import type { AutoHonorStrategy } from './state'

function createHarness(strategy: AutoHonorStrategy, votes = 4) {
  const player = (puuid: string, botPlayer = false) => ({ puuid, botPlayer })
  const ballot = {
    gameId: 100,
    eligibleAllies: [player('premade'), player('teammate'), player('self'), player('bot', true)],
    eligibleOpponents: [player('opponent'), player('enemy-bot', true)],
    honoredPlayers: [],
    votePool: { votes }
  } as unknown as Ballot
  const settings = { autoHonorEnabled: true, autoHonorStrategy: strategy }
  const leagueClient = {
    data: { honor: { ballot: ballot as Ballot | null }, summoner: { me: { puuid: 'self' } } },
    api: {
      lobby: {
        getEogStatus: vi.fn().mockResolvedValue({
          data: { eogPlayers: ['premade'], leftPlayers: [], readyPlayers: [] }
        })
      },
      honor: { honor: vi.fn().mockResolvedValue({}), ballot: vi.fn().mockResolvedValue({}) }
    }
  }
  let read: () => readonly unknown[]
  let handle: (values: readonly unknown[]) => Promise<void>
  const context = {
    settings,
    leagueClient,
    namespace: 'auto-gameflow-main',
    logger: { info: vi.fn(), warn: vi.fn() },
    ipc: { sendEvent: vi.fn() },
    mobxUtils: {
      reaction: (expression: typeof read, handler: typeof handle) => {
        read = expression
        handle = handler
      }
    }
  }
  const actionController = { cancelPlayAgain: vi.fn() }
  new AutoGameflowHonorController(
    context as unknown as AutoGameflowMainContext,
    actionController as unknown as AutoGameflowActionController
  ).watch()
  return {
    ...context,
    ballot,
    run: () => handle(read()),
    recipients: () => leagueClient.api.honor.honor.mock.calls.map((call) => call[1])
  }
}

describe('automatic honor targets', () => {
  afterEach(() => vi.restoreAllMocks())

  it('votes only for current teammates even when more votes and opponents are available', async () => {
    const harness = createHarness('all-member')
    await harness.run()
    expect(harness.recipients()).toEqual(['premade', 'teammate'])
    expect(harness.leagueClient.api.lobby.getEogStatus).not.toHaveBeenCalled()
    expect(harness.leagueClient.api.honor.ballot).toHaveBeenCalledOnce()
  })

  it('prioritizes premade teammates and spends remaining votes only on other teammates', async () => {
    const harness = createHarness('prefer-lobby-member')
    await harness.run()
    expect(harness.recipients()).toEqual(['premade', 'teammate'])
  })

  it('restricts voting to premade teammates and skips when there are none', async () => {
    const harness = createHarness('only-lobby-member')
    await harness.run()
    expect(harness.recipients()).toEqual(['premade'])

    const solo = createHarness('only-lobby-member')
    solo.leagueClient.api.lobby.getEogStatus.mockResolvedValue({
      data: { eogPlayers: [], leftPlayers: [], readyPlayers: [] }
    })
    await solo.run()
    expect(solo.recipients()).toEqual([])
    expect(solo.leagueClient.api.honor.ballot).toHaveBeenCalledOnce()
  })

  it('allows opponents only when that strategy is selected and teammates have been considered', async () => {
    const harness = createHarness('all-member-including-opponent')
    await harness.run()
    expect(harness.recipients()).toEqual(['premade', 'teammate', 'opponent'])
  })

  it('skips voting without depending on the lobby endpoint', async () => {
    const harness = createHarness('opt-out')
    await harness.run()
    expect(harness.recipients()).toEqual([])
    expect(harness.leagueClient.api.lobby.getEogStatus).not.toHaveBeenCalled()
    expect(harness.leagueClient.api.honor.ballot).toHaveBeenCalledOnce()
  })

  it('excludes already honored players and respects the remaining vote count', async () => {
    const harness = createHarness('all-member', 1)
    harness.ballot.honoredPlayers = [{ honorType: 'HEART', recipientPuuid: 'premade' }]
    await harness.run()
    expect(harness.recipients()).toEqual(['teammate'])
    const noVotes = createHarness('all-member', 0)
    await noVotes.run()
    expect(noVotes.recipients()).toEqual([])
  })

  it('does not vote or submit when automatic honor is disabled', async () => {
    const harness = createHarness('all-member')
    harness.settings.autoHonorEnabled = false
    await harness.run()
    expect(harness.recipients()).toEqual([])
    expect(harness.leagueClient.api.honor.ballot).not.toHaveBeenCalled()
  })

  it('continues with current teammates if premade lookup fails, but never guesses premade membership', async () => {
    const harness = createHarness('prefer-lobby-member')
    harness.leagueClient.api.lobby.getEogStatus.mockRejectedValue(new Error('unavailable'))
    await harness.run()
    expect(harness.recipients()).toEqual(['premade', 'teammate'])
    const premadeOnly = createHarness('only-lobby-member')
    premadeOnly.leagueClient.api.lobby.getEogStatus.mockRejectedValue(new Error('unavailable'))
    await premadeOnly.run()
    expect(premadeOnly.recipients()).toEqual([])
    expect(premadeOnly.ipc.sendEvent).toHaveBeenCalledOnce()
  })

  it('does not duplicate votes when ballot updates arrive during or after voting', async () => {
    const harness = createHarness('prefer-lobby-member')
    let finishLobby!: (value: any) => void
    harness.leagueClient.api.lobby.getEogStatus.mockImplementation(
      () => new Promise((resolve) => (finishLobby = resolve))
    )
    const voting = harness.run()
    await harness.run()
    finishLobby({ data: { eogPlayers: ['premade'], leftPlayers: [], readyPlayers: [] } })
    await voting
    await harness.run()
    expect(harness.recipients()).toEqual(['premade', 'teammate'])
    expect(harness.leagueClient.api.honor.ballot).toHaveBeenCalledOnce()
  })

  it.each(['disabled', 'strategy-changed', 'ballot-ended'] as const)(
    'stops pending votes when %s',
    async (change) => {
      const harness = createHarness('prefer-lobby-member')
      let finishLobby!: (value: any) => void
      harness.leagueClient.api.lobby.getEogStatus.mockImplementation(
        () => new Promise((resolve) => (finishLobby = resolve))
      )
      const voting = harness.run()
      if (change === 'disabled') harness.settings.autoHonorEnabled = false
      if (change === 'strategy-changed') harness.settings.autoHonorStrategy = 'all-member'
      if (change === 'ballot-ended') harness.leagueClient.data.honor.ballot = null
      finishLobby({ data: { eogPlayers: ['premade'], leftPlayers: [], readyPlayers: [] } })
      await voting
      expect(harness.recipients()).toEqual([])
      expect(harness.leagueClient.api.honor.ballot).not.toHaveBeenCalled()
    }
  )
})
