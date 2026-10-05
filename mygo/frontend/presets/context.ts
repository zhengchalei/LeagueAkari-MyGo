import type { useOngoingGameStore } from '@renderer-shared/shards/ongoing-game/store'
import type { useInGameSendStore } from '@renderer-shared/shards/in-game-send/store'
import type { SummonerInfo } from '@shared/types/league-client/summoner'

// This is the data-only part of the original main context required by preset builders.
export interface InGameSendMainContext {
  settings: ReturnType<typeof useInGameSendStore>['settings']
  state: ReturnType<typeof useInGameSendStore>['state']
  ongoingGame: { state: ReturnType<typeof useOngoingGameStore> }
  leagueClient: { data: {
    summoner: { me: SummonerInfo | null }
    gameData: { championName(id: number): string }
  } }
}
