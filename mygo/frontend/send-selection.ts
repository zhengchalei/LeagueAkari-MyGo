import { watch } from "vue";
import { useOngoingGameStore } from "@renderer-shared/shards/ongoing-game/store";

export function installSendSelection() {
  const ongoing = useOngoingGameStore();
  const set = (method: string, values: unknown[]) => {
    void window.mygo.call("Desktop.Call", "in-game-send-main", method, [
      values,
    ]);
  };
  watch(
    () => [
      ongoing.teams,
      ongoing.positionAssignments,
      ongoing.additional.spells,
    ],
    () => {
      const all = Object.values(ongoing.teams).flat();
      set("setRatingPuuids", all);
      set(
        "setJunglePuuids",
        all.filter((puuid) => {
          const spells = ongoing.additional.spells[puuid];
          return (
            ongoing.positionAssignments[puuid]?.position?.toUpperCase() ===
              "JUNGLE" ||
            spells?.spell1Id === 11 ||
            spells?.spell2Id === 11
          );
        }),
      );
    },
    { immediate: true },
  );
  watch(
    () => ongoing.mergedPremadeTeamMap,
    (map) => {
      set("setPremadeIndices", [...new Set(Object.values(map))]);
    },
    { immediate: true },
  );
}
