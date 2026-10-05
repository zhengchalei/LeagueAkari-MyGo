const fs = require("node:fs/promises");
const path = require("node:path");
const { matchSummary } = require("../app/data.cjs");

async function main() {
  const target = path.join(__dirname, "../app/assets/demo.json");
  const demo = JSON.parse(await fs.readFile(target, "utf8"));
  const heroes = Object.fromEntries(demo.heroes.map((h) => [h.id, h.name]));
  const players = [
    [
      "晚风#1024",
      127,
      [true, false, true, true, false, true, false, true, true, false],
    ],
    [
      "一起推塔#7788",
      711,
      [false, true, true, false, true, false, false, true, false, true],
    ],
    [
      "今晚不熬夜#2333",
      103,
      [true, true, false, true, true, false, true, true, false, true],
    ],
    [
      "小熊软糖#5566",
      127,
      [false, true, false, true, false, false, true, false, true, false],
    ],
  ];
  demo.teammates = players.map(([name, championId, results], index) => {
    const matches = results.map((win, i) => ({
      id: (index + 1) * 100 + i,
      championId: [championId, 103, 711][i % 3],
      championName: heroes[[championId, 103, 711][i % 3]],
      win,
      kills: win ? 6 + (i % 4) : 3 + (i % 3),
      deaths: win ? 2 + (i % 3) : 6 + (i % 3),
      assists: 12 + (i % 8),
      duration: 960 + i * 40,
      timestamp: Date.now() - i * 3600000,
      mode: "ARAM",
      queueId: 450,
    }));
    return {
      id: String(index + 1),
      name,
      championId,
      championName: heroes[championId],
      history: {
        status: "ready",
        source: "演示",
        matches,
        ...matchSummary(matches),
      },
    };
  });
  demo.self = {
    name: "Timo#1024",
    summonerLevel: 168,
    championId: 103,
    history: demo.teammates[0].history,
  };
  await fs.writeFile(target, JSON.stringify(demo, null, 2));
}
main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
