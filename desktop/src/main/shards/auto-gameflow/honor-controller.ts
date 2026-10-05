import { ChoiceMaker } from '@shared/utils/choice-maker'
import { formatError } from '@shared/utils/errors'
import { randomInt } from '@shared/utils/random'
import { comparer, computed } from 'mobx'

import type { AutoGameflowActionController } from './action-controller'
import { AUTO_GAMEFLOW_HONOR_CATEGORY, type AutoGameflowMainContext } from './context'
import type { AutoHonorStrategy } from './state'

export class AutoGameflowHonorController {
  private _activeGameId: number | null = null
  private _completedGameId: number | null = null

  constructor(
    private readonly _context: AutoGameflowMainContext,
    private readonly _actionController: AutoGameflowActionController
  ) {}

  watch() {
    const { ipc, leagueClient, logger, mobxUtils, namespace, settings } = this._context

    const honorables = computed(() => {
      if (!leagueClient.data.honor.ballot) {
        return null
      }

      const {
        eligibleAllies,
        eligibleOpponents,
        honoredPlayers,
        gameId,
        votePool: { votes }
      } = leagueClient.data.honor.ballot

      const excludedPlayers = new Set([
        leagueClient.data.summoner.me?.puuid,
        ...honoredPlayers.map((player) => player.recipientPuuid)
      ])
      return {
        allies: eligibleAllies
          .filter((player) => !player.botPlayer && !excludedPlayers.has(player.puuid))
          .map((player) => player.puuid),
        opponents: eligibleOpponents
          .filter((player) => !player.botPlayer && !excludedPlayers.has(player.puuid))
          .map((player) => player.puuid),
        votes,
        gameId
      }
    })

    mobxUtils.reaction(
      () => [honorables.get(), settings.autoHonorEnabled, settings.autoHonorStrategy] as const,
      async ([honorablePlayers, enabled, strategy]) => {
        if (honorablePlayers && honorablePlayers.gameId) {
          this._actionController.cancelPlayAgain()
        }

        if (
          honorablePlayers &&
          honorablePlayers.gameId &&
          enabled &&
          this._activeGameId !== honorablePlayers.gameId &&
          this._completedGameId !== honorablePlayers.gameId
        ) {
          this._activeGameId = honorablePlayers.gameId
          try {
            let lobbyMembers: string[] = []
            if (
              strategy === 'prefer-lobby-member' ||
              strategy === 'only-lobby-member' ||
              strategy === 'all-member-including-opponent'
            ) {
              try {
                const endOfGameStatus = (await leagueClient.api.lobby.getEogStatus()).data
                lobbyMembers = [
                  ...endOfGameStatus.eogPlayers,
                  ...endOfGameStatus.leftPlayers,
                  ...endOfGameStatus.readyPlayers
                ]
              } catch (error) {
                if (strategy === 'only-lobby-member') {
                  throw error
                }
                logger.warn(`Cannot prioritize premade teammates: ${formatError(error)}`)
              }
            }

            const candidates = this._selectCandidates(
              strategy,
              honorablePlayers.allies,
              honorablePlayers.opponents,
              honorablePlayers.votes,
              lobbyMembers
            )

            // 对选择出的 candidates 进行点赞
            for (const puuid of candidates) {
              if (!this._canContinue(honorablePlayers.gameId, strategy)) {
                return
              }
              await leagueClient.api.honor.honor(
                AUTO_GAMEFLOW_HONOR_CATEGORY[randomInt(0, AUTO_GAMEFLOW_HONOR_CATEGORY.length)],
                puuid
              )
            }

            if (!this._canContinue(honorablePlayers.gameId, strategy)) {
              return
            }
            await leagueClient.api.honor.ballot()
            this._completedGameId = honorablePlayers.gameId
            logger.info(
              `Auto-honor: voting for ${candidates.join(', ')}, game ID: ${honorablePlayers.gameId}`
            )
          } catch (error) {
            ipc.sendEvent(namespace, 'error-auto-honor', formatError(error))

            logger.warn(`Auto-honor error: ${formatError(error)}`)
          } finally {
            this._activeGameId = null
          }
        }
      },
      {
        equals: comparer.structural,
        fireImmediately: true
      }
    )
  }

  private _canContinue(gameId: number, strategy: AutoHonorStrategy) {
    return (
      this._context.settings.autoHonorEnabled &&
      this._context.settings.autoHonorStrategy === strategy &&
      this._context.leagueClient.data.honor.ballot?.gameId === gameId
    )
  }

  private _selectCandidates(
    strategy: AutoHonorStrategy,
    allies: string[],
    opponents: string[],
    votes: number,
    lobbyMembers: string[]
  ) {
    const lobbyAllies = allies.filter((puuid) => lobbyMembers.includes(puuid))
    const otherAllies = allies.filter((puuid) => !lobbyMembers.includes(puuid))
    let groups: string[][]
    switch (strategy) {
      case 'opt-out':
        return []
      case 'only-lobby-member':
        groups = [lobbyAllies]
        break
      case 'all-member':
        groups = [allies]
        break
      case 'all-member-including-opponent':
        groups = [lobbyAllies, otherAllies, opponents]
        break
      default:
        groups = [lobbyAllies, otherAllies]
    }

    const candidates: string[] = []
    for (const group of groups) {
      const remainingPlayers = [...new Set(group)].filter((puuid) => !candidates.includes(puuid))
      const count = Math.min(votes - candidates.length, remainingPlayers.length)
      if (count > 0) {
        candidates.push(
          ...new ChoiceMaker(Array(remainingPlayers.length).fill(1), remainingPlayers).choose(count)
        )
      }
    }
    return candidates
  }
}
