import i18next from "i18next";
import { useLeagueClientStore } from "@renderer-shared/shards/league-client/store";
import { useOngoingGameStore } from "@renderer-shared/shards/ongoing-game/store";
import { useInGameSendStore } from "@renderer-shared/shards/in-game-send/store";
import {
  buildRatingPresetLinesFromMainContext,
  buildJunglePresetLinesFromMainContext,
  buildPremadePresetLinesFromMainContext,
} from "./presets";
import type { InGameSendMainContext } from "./presets/context";
import type { InGameSendPresetTarget } from "@shared/shards/in-game-send";
import zhMain from "@shared/i18n/zh-CN/main.yaml";
import enMain from "@shared/i18n/en/main.yaml";

// The original pure builders run beside the analysis data; Go owns delivery and shortcuts.
export function generatePresetLines(
  kind: string,
  target: InGameSendPresetTarget,
): string[] {
  const ns = i18next.options.defaultNS || "translation";
  const namespace = Array.isArray(ns) ? ns[0] : ns;
  i18next.addResourceBundle("zh-CN", namespace, zhMain, true, true);
  i18next.addResourceBundle("en", namespace, enMain, true, true);
  const league = useLeagueClientStore();
  const ongoing = useOngoingGameStore();
  const send = useInGameSendStore();
  const context = {
    settings: send.settings,
    state: send.state,
    ongoingGame: { state: ongoing },
    leagueClient: {
      data: {
        summoner: { me: league.summoner.me },
        gameData: {
          ...league.gameData,
          championName: (id: number) =>
            league.gameData.champions[id]?.name || String(id),
        },
      },
    },
  } as unknown as InGameSendMainContext;
  switch (kind.toLowerCase()) {
    case "rating":
      return buildRatingPresetLinesFromMainContext(context, target);
    case "jungle":
      return buildJunglePresetLinesFromMainContext(context, target);
    case "premade":
      return buildPremadePresetLinesFromMainContext(context, target);
    default:
      throw new Error("未知发送预设");
  }
}
