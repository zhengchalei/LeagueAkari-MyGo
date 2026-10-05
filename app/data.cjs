const BALANCE_FIELDS = [
  ['damage_dealt', '造成伤害', 100, false], ['damage_taken', '受到伤害', 100, true],
  ['attack_speed', '攻击速度', 100, false], ['cooldown_reduction', '技能急速', 0, false],
  ['healing', '治疗效果', 100, false], ['tenacity', '韧性', 0, false],
  ['shield_amount', '护盾效果', 100, false], ['energy_regen', '能量回复', 100, false],
  ['area_of_effect_damage', '范围伤害', 100, false]
];

function balanceEffects(balance) {
  if (!balance) return { available: false, buffs: [], debuffs: [] };
  const result = { available: true, buffs: [], debuffs: [] };
  for (const [key, name, baseline, reversed] of BALANCE_FIELDS) {
    const value = balance[key];
    if (!Number.isFinite(value) || value === baseline) continue;
    const change = value - baseline;
    const effect = { name, value: `${change > 0 ? '+' : '−'}${Math.abs(change)}${baseline === 100 ? '%' : ''}` };
    result[(change > 0) !== reversed ? 'buffs' : 'debuffs'].push(effect);
  }
  return result;
}

function availableSkins(carousel, details) {
  const names = new Map();
  for (const skin of details?.skins || []) {
    names.set(skin.id, skin.name);
    for (const chroma of skin.chromas || []) names.set(chroma.id, chroma.name);
  }
  return (carousel || []).flatMap(skin => {
    if (!skin.unlocked) return [];
    return [skin, ...(skin.childSkins || [])]
      .filter(s => s.unlocked && !s.disabled)
      .map(s => ({ id: s.id, name: names.get(s.id) || s.name,
        imagePath: s.chromaPreviewPath || s.splashPath || s.tilePath || '' }));
  });
}

function currentChampion(session) {
  return session?.myTeam?.find(p => p.cellId === session.localPlayerCellId)?.championId || 0;
}

function heroSelectionRequest(session, championId, subsetIds, pickableIds) {
  if (!session?.benchEnabled) throw new Error('当前不在可切换英雄的选人阶段');
  if (currentChampion(session) === championId) return null;
  const phase = session.timer?.phase;
  if (phase !== 'BAN_PICK' && phase !== 'FINALIZATION') throw new Error('当前阶段无法切换英雄');
  const subset = subsetIds || [];
  const pool = [...(session.benchChampions || []).map(c => c.championId), ...subset];
  if (!pool.includes(championId) || !pickableIds.includes(championId)) throw new Error('该英雄当前不可选');
  if (phase === 'BAN_PICK' && !subset.includes(championId)) throw new Error('该英雄暂时无法切换');
  if (phase === 'BAN_PICK' && !currentChampion(session)) {
    const action = (session.actions || []).flat().find(a => a.actorCellId === session.localPlayerCellId && a.type === 'pick' && !a.completed);
    if (!action) throw new Error('未找到可执行的选人动作');
    return { method: 'PATCH', endpoint: `/lol-champ-select/v1/session/actions/${action.id}`,
      body: { championId, completed: true, type: 'pick' } };
  }
  return { method: 'POST', endpoint: `/lol-champ-select/v1/session/bench/swap/${championId}` };
}

function normalizeMatches(payload, puuid, summonerId) {
  const games = Array.isArray(payload?.games) ? payload.games : payload?.games?.games || [];
  return games.map(entry => {
    let game = entry.json || entry;
    if (typeof game === 'string') { try { game = JSON.parse(game); } catch { return null; } }
    const identity = game.participantIdentities?.find(p => p.player?.puuid === puuid ||
      (summonerId && p.player?.summonerId === summonerId));
    const participant = game.participants?.find(p => identity ? p.participantId === identity.participantId : p.puuid === puuid);
    if (!participant) return null;
    const stats = participant.stats || participant;
    return {
      id: game.gameId || game.gameCreation, championId: participant.championId,
      win: stats.win === true || stats.win === 'Win', kills: stats.kills || 0,
      deaths: stats.deaths || 0, assists: stats.assists || 0,
      duration: game.gameDuration || 0, timestamp: game.gameCreation || Date.parse(game.gameCreationDate) || 0,
      mode: game.gameMode || '', queueId: game.queueId
    };
  }).filter(Boolean).slice(0, 10);
}

function matchSummary(matches) {
  const wins = matches.filter(m => m.win).length;
  const totals = matches.reduce((acc, m) => ({ kills: acc.kills + m.kills, deaths: acc.deaths + m.deaths, assists: acc.assists + m.assists }), { kills: 0, deaths: 0, assists: 0 });
  return { count: matches.length, wins, losses: matches.length - wins,
    kda: matches.length ? ((totals.kills + totals.assists) / Math.max(1, totals.deaths)).toFixed(1) : null };
}

module.exports = { balanceEffects, availableSkins, currentChampion, heroSelectionRequest, normalizeMatches, matchSummary };
