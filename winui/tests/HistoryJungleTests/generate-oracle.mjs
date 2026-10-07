// Regenerate expected values from the upstream TypeScript implementation, not the native port.
import { createRequire } from 'node:module'
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import vm from 'node:vm'
const directory = dirname(fileURLToPath(import.meta.url))
const root = resolve(directory, '../../..')
const require = createRequire(resolve(root, 'desktop/package.json'))
const esbuild = require('esbuild')
const upstream = resolve(root, 'desktop/src/shared/data-adapter/analysis/player/index.ts')
const result = await esbuild.build({ stdin: { contents: `export { analyzeGames } from ${JSON.stringify(upstream)}`, resolveDir: root, loader: 'ts' }, bundle: true, write: false, platform: 'node', format: 'cjs', alias: { '@shared': resolve(root, 'desktop/src/shared') } })
const module = { exports: {} }
vm.runInNewContext(result.outputFiles[0].text, { module, exports: module.exports, require, Date, console })
const cases = []
const snapshot = resolve(root, 'desktop/src/shared/test-fixtures/api/snapshots/2026-05-16-tencent-hn10/lcu/match-history')
for (const queue of ['q_420', 'q_440', 'q_430']) {
  const game = JSON.parse(readFileSync(resolve(snapshot, `games/${queue}.json`), 'utf8'))
  const details = JSON.parse(readFileSync(resolve(snapshot, `timelines/${queue}.json`), 'utf8'))
  for (const player of game.participants.filter(p => p.spell1Id === 11 || p.spell2Id === 11)) {
    const puuid = game.participantIdentities.find(i => i.participantId === player.participantId).player.puuid
    const analysis = module.exports.analyzeGames([{ gameId: game.gameId, summary: { source: 'lcu', gameId: game.gameId, data: game }, details: { source: 'lcu', gameId: game.gameId, data: details } }], puuid)
    cases.push({ queue, puuid, jungle: analysis.jungle })
  }
}
writeFileSync(resolve(directory, 'upstream-oracle.json'), JSON.stringify(cases, null, 2) + '\n')
console.log(`Generated ${cases.length} upstream jungle projections`)
