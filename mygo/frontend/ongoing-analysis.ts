import type { useOngoingGameStore } from "@renderer-shared/shards/ongoing-game/store";
import { analyzeGames } from "@shared/data-adapter/analysis/player";
import { analyzePlayers } from "@shared/data-adapter/analysis/team";
import { toIdentities } from "@shared/data-adapter/match-history/identities";
import type { OngoingGameAnalysis } from "@shared/shards/ongoing-game";
import {
  calculateTogetherTimes,
  mergeOverlappingSets,
  removeSubsets,
} from "@shared/utils/team-up-calc";
import { markRaw, watch } from "vue";

type AnalysisStore = Pick<
  ReturnType<typeof useOngoingGameStore>,
  | "matchHistory"
  | "gameDetails"
  | "teams"
  | "analysis"
  | "settings"
  | "teamParticipantGroups"
  | "inferredPremadeTeams"
  | "mergedPremadeTeamMap"
  | "cachedGames"
  | "savedInfo"
>;

export function computeOngoingGameAnalysis(
  store: AnalysisStore,
): OngoingGameAnalysis | null {
  if (!Object.keys(store.matchHistory).length) return null;

  const players: OngoingGameAnalysis["players"] = {};
  for (const [puuid, history] of Object.entries(store.matchHistory)) {
    const pairs = history.data.map((summary) => ({
      gameId: summary.gameId,
      summary,
      details: store.gameDetails[summary.gameId],
    }));
    const analysis = analyzeGames(pairs, puuid, {
      previous: store.analysis?.players[puuid],
    });
    if (analysis) players[puuid] = analysis;
  }

  const teams: OngoingGameAnalysis["teams"] = {};
  for (const [teamId, puuids] of Object.entries(store.teams)) {
    const analysis = analyzePlayers(
      puuids.map((puuid) => players[puuid]).filter(Boolean),
    );
    if (analysis) teams[teamId] = analysis;
  }
  return { players, teams };
}

export function inferPremadeTeams(store: AnalysisStore): string[][] {
  const matches = new Map<string, { id: string; players: string[] }>();
  for (const history of Object.values(store.matchHistory)) {
    for (const summary of history.data) {
      const groups = new Map<number, string[]>();
      for (const identity of toIdentities(summary)) {
        const group = groups.get(identity.teamId) ?? [];
        group.push(identity.puuid);
        groups.set(identity.teamId, group);
      }
      for (const players of groups.values()) {
        if (players.length < 2) continue;
        // Both historical teams count; duplicate summaries from different players do not.
        const key = `${summary.source}:${summary.gameId}:${players.toSorted().join(",")}`;
        matches.set(key, { id: key, players });
      }
    }
  }
  const together = calculateTogetherTimes(
    [...matches.values()],
    Object.values(store.teams).flat(),
    store.settings.premadeTeamInferMatchCountThreshold,
  );
  return mergeOverlappingSets(
    removeSubsets(together, (group) => group.players).map(
      (group) => group.players,
    ),
  ).map((group) => group.map(String));
}

export function mergePremadeTeamMap(
  store: AnalysisStore,
): Record<string, number> {
  const teamByPlayer = new Map<string, string>();
  for (const [team, players] of Object.entries(store.teams)) {
    for (const player of players) teamByPlayer.set(player, team);
  }
  const groups = removeSubsets(
    [
      ...Object.values(store.teamParticipantGroups),
      ...store.inferredPremadeTeams,
    ].filter((group) => group.length > 1),
    (group) => group,
  );
  const result: Record<string, number> = {};
  let index = 0;
  for (const players of groups) {
    const team = teamByPlayer.get(players[0]);
    if (!team || players.some((player) => teamByPlayer.get(player) !== team))
      continue;
    ++index;
    for (const player of players) result[player] = index;
  }
  return result;
}

function pruneCachedGames(store: AnalysisStore) {
  const retained = new Set(
    Object.values(store.matchHistory).flatMap((history) =>
      history.data.map((game) => game.gameId),
    ),
  );
  for (const info of Object.values(store.savedInfo)) {
    for (const game of info.encounteredGames.data) retained.add(game.gameId);
  }
  for (const id of Object.keys(store.gameDetails)) retained.add(Number(id));
  for (const id of Object.keys(store.cachedGames)) {
    if (!retained.has(Number(id))) delete store.cachedGames[Number(id)];
  }
}

export function installOngoingGameAnalysis(store: AnalysisStore): () => void {
  // The Go host retains summaries; use Akari's existing analyzers in the active WebView.
  return watch(
    () => [
      Object.entries(store.matchHistory),
      store.teams,
      Object.values(store.gameDetails),
      store.teamParticipantGroups,
      store.settings.premadeTeamInferMatchCountThreshold,
      Object.entries(store.savedInfo),
    ],
    () => {
      try {
        const analysis = computeOngoingGameAnalysis(store);
        store.analysis = analysis ? markRaw(analysis) : null;
        store.inferredPremadeTeams = markRaw(inferPremadeTeams(store));
        store.mergedPremadeTeamMap = markRaw(mergePremadeTeamMap(store));
        pruneCachedGames(store);
      } catch (error) {
        console.warn("LeagueAkari-MyGo 无法分析对局战绩：", error);
        store.analysis = null;
      }
    },
    { immediate: true },
  );
}
