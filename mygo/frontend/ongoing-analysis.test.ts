import {
  getOnlySgpGame,
  readLcuGameFixture,
  wrapLcuGame,
  wrapSgpSummary,
} from "@shared/data-adapter/analysis/player/test-utils/fixtures";
import { toParticipants } from "@shared/data-adapter/match-history/participants";
import { toBasicInfo } from "@shared/data-adapter/match-history/match-basic";
import type { LcuOrSgpGameSummary } from "@shared/data-adapter/wrapper";
import { describe, expect, it } from "vitest";
import { nextTick, reactive } from "vue";

import {
  computeOngoingGameAnalysis,
  inferPremadeTeams,
  installOngoingGameAnalysis,
} from "./ongoing-analysis";
import { createDefaultOngoingGamePanelPlayerCardTagSettings } from "@shared/shards/ongoing-game/settings";

function makeStore(summary?: LcuOrSgpGameSummary) {
  const store: Parameters<typeof installOngoingGameAnalysis>[0] = {
    matchHistory: {},
    gameDetails: {},
    teams: {},
    analysis: null,
    settings: {
      enabled: true,
      matchHistoryLoadCount: 50,
      concurrency: 4,
      matchHistoryTagPreference: "current",
      gameDetailsLoadCount: 20,
      premadeTeamInferMatchCountThreshold: 5,
      orderPlayerBy: "default",
      showChampionUsage: "recent",
      showMatchHistoryItemBorder: false,
      showJunglePathing: true,
      showJunglePathingForAllPlayers: false,
      autoRouteWhenGameStarts: true,
      playerCardTags: createDefaultOngoingGamePanelPlayerCardTagSettings(),
      queryInLobbyPhase: true,
    },
    teamParticipantGroups: {},
    inferredPremadeTeams: [],
    mergedPremadeTeamMap: {},
    cachedGames: {},
    savedInfo: {},
  };
  if (summary) {
    for (const player of toParticipants(summary, toBasicInfo(summary))) {
      store.matchHistory[player.puuid] = {
        source: summary.source,
        params: {},
        data: [summary],
      };
      (store.teams[player.teamIdentifier] ||= []).push(player.puuid);
    }
  }
  return reactive(store);
}

describe("MyGo ongoing game analysis from existing client summaries", () => {
  it.each([
    [
      "Tencent SGP hextech ARAM",
      () => wrapSgpSummary(getOnlySgpGame("q_2400")),
    ],
    ["LCU ARAM", () => wrapLcuGame(readLcuGameFixture("q_450"))],
  ] as const)(
    "fills player and team statistics from %s without timeline data",
    (_name, load) => {
      const summary = load();
      const store = makeStore(summary);
      const analysis = computeOngoingGameAnalysis(store)!;
      const participants = toParticipants(summary, toBasicInfo(summary));
      expect(Object.keys(analysis.players)).toHaveLength(participants.length);
      for (const player of participants) {
        const stats = analysis.players[player.puuid];
        expect(stats.count).toBe(1);
        expect(stats.winLoss.all.winRate).toBe(
          player.winResult === "win" ? 1 : 0,
        );
        expect(stats.summary.avgKda).toBe(
          (player.kills + player.assists) / Math.max(player.deaths, 1),
        );
        expect(stats.detailsCount).toBe(0);
      }
      expect(Object.keys(analysis.teams)).toHaveLength(
        Object.keys(store.teams).length,
      );
    },
  );

  it("updates after a player history arrives and stops its watcher on disposal", async () => {
    const store = makeStore();
    const stop = installOngoingGameAnalysis(store);
    expect(store.analysis).toBeNull();
    const populated = makeStore(wrapSgpSummary(getOnlySgpGame("q_2400")));
    store.teams = populated.teams;
    store.matchHistory = populated.matchHistory;
    await nextTick();
    expect(Object.keys(store.analysis!.players)).toHaveLength(10);

    const [puuid] = Object.keys(store.matchHistory);
    const cachedSingle = Object.values(store.analysis!.players[puuid].map)[0];
    store.teams = { everyone: Object.keys(store.matchHistory) };
    await nextTick();
    expect(Object.values(store.analysis!.players[puuid].map)[0]).toBe(
      cachedSingle,
    );
    expect(store.analysis!.teams.everyone.games).toBe(10);

    stop();
    const previous = store.analysis;
    store.matchHistory = {};
    await nextTick();
    expect(store.analysis).toBe(previous);
  });
});

describe("premade groups and current-game cache", () => {
  it("counts both historical teams once despite each player carrying the same summaries", () => {
    const summary = wrapSgpSummary(getOnlySgpGame("q_2400"));
    const store = makeStore(summary);
    store.settings.premadeTeamInferMatchCountThreshold = 2;
    for (const history of Object.values(store.matchHistory)) {
      history.data.push({ ...summary, gameId: summary.gameId + 1 });
    }
    const inferred = inferPremadeTeams(store);
    expect(inferred).toHaveLength(2);
    expect(inferred.every((group) => group.length === 5)).toBe(true);
    store.settings.premadeTeamInferMatchCountThreshold = 3;
    expect(inferPremadeTeams(store)).toEqual([]);
  });

  it("maps authoritative participant groups only inside current teams and removes old cached games", async () => {
    const summary = wrapSgpSummary(getOnlySgpGame("q_2400"));
    const store = makeStore(summary);
    const [blue, red] = Object.values(store.teams);
    store.teamParticipantGroups = {
      party: blue.slice(0, 2),
      mixed: [blue[2], red[0]],
    };
    store.cachedGames[summary.gameId] = summary;
    store.cachedGames[42] = { ...summary, gameId: 42 };
    const stop = installOngoingGameAnalysis(store);
    expect(store.mergedPremadeTeamMap[blue[0]]).toBe(
      store.mergedPremadeTeamMap[blue[1]],
    );
    expect(store.mergedPremadeTeamMap[blue[2]]).toBeUndefined();
    expect(store.cachedGames[42]).toBeUndefined();
    expect(store.cachedGames[summary.gameId]).toBeTruthy();
    store.matchHistory = {};
    store.teams = {};
    await nextTick();
    expect(store.cachedGames).toEqual({});
    expect(store.mergedPremadeTeamMap).toEqual({});
    stop();
  });
});
