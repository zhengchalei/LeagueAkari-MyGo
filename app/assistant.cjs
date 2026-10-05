const fs = require('node:fs/promises');
const path = require('node:path');
const { discoverClient, requestLcu, lcuJson } = require('./lcu.cjs');
const { balanceEffects, availableSkins, currentChampion, heroSelectionRequest, normalizeMatches, matchSummary } = require('./data.cjs');

const SGP_HOSTS = {
  HN1: 'hn1-k8s-sgp', HN10: 'hn10-k8s-sgp', BGP2: 'bgp2-k8s-sgp',
  TJ100: 'tj100-sgp', TJ101: 'tj101-sgp', NJ100: 'nj100-sgp', GZ100: 'gz100-sgp', CQ100: 'cq100-sgp',
  PBE: 'pbe-sgp', PREPBE: 'prepbe-sgp'
};
const SERVER_NAMES = { HN1: '艾欧尼亚', HN10: '黑色玫瑰', BGP2: '峡谷之巅', TJ100: '联盟四区', TJ101: '联盟五区', NJ100: '联盟一区', GZ100: '联盟二区', CQ100: '联盟三区' };

class Assistant {
  constructor(options = {}) {
    this.assetsDir = options.assetsDir || path.join(__dirname, 'assets');
    this.cacheDir = options.cacheDir || path.join(__dirname, '..', '.cache');
    this.discover = options.discover || discoverClient;
    this.json = options.json || lcuJson;
    this.binary = options.binary || requestLcu;
    this.fetch = options.fetch || globalThis.fetch;
    this.auth = null;
    this.champions = new Map();
    this.balance = new Map();
    this.balanceTime = null;
    this.history = new Map();
    this.snapshot = { connected: false, stage: 'waiting', heroes: [], skins: [], teammates: [], currentChampionId: 0, message: '启动国服 LOL 客户端后自动连接' };
    this.demo = null;
    this.updating = null;
    this.discoveryTime = 0;
    this.skinChampion = 0;
    this.timer = null;
    this.actionBusy = false;
  }

  async init() {
    this.demo = JSON.parse(await fs.readFile(path.join(this.assetsDir, 'demo.json'), 'utf8'));
    try { this.setBalance(JSON.parse(await fs.readFile(path.join(this.assetsDir, 'balance.json'), 'utf8'))); } catch {}
    void this.refreshBalance();
    await this.tick();
    this.timer = setInterval(() => void this.tick(), 1500);
    this.timer.unref();
  }

  setBalance(data) {
    this.balance = new Map((data.data || []).map(b => [b.champion_id, b]));
    this.balanceTime = data.fetchedAt || null;
  }

  async refreshBalance() {
    try {
      const response = await this.fetch('https://lol-api-champion.op.gg/api/contents/aram-balance', { signal: AbortSignal.timeout(8000) });
      if (!response.ok) return;
      const data = await response.json();
      if (!Array.isArray(data.data)) return;
      this.setBalance({ ...data, fetchedAt: new Date().toISOString() });
    } catch {}
  }

  champion(id) {
    return this.champions.get(id) || this.demo.heroes.find(h => h.id === id) || { id, name: `英雄 ${id}` };
  }

  async localImage(kind, id, fallback) {
    for (const extension of ['jpg', 'png', 'webp']) {
      try {
        await fs.access(path.join(this.assetsDir, kind, `${id}.${extension}`));
        return `/assets/${kind}/${id}.${extension}`;
      } catch {}
    }
    return fallback;
  }

  getState(demo = false) {
    const base = demo ? this.demo : this.snapshot;
    const effects = balanceEffects(this.balance.get(base.currentChampionId));
    const self = base.self ? { ...base.self, history: demo ? base.self.history :
      this.history.get(`${this.platformId}:${base.self.puuid}`)?.result || { status: 'loading', matches: [] } } : null;
    return { ...base, self, inMatch: base.inMatch ?? base.stage === 'select', demo, balance: effects, balanceTime: this.balanceTime,
      heroes: base.heroes.map(h => ({ ...h, name: h.name || this.champion(h.id).name })) };
  }

  async tick() {
    if (this.updating) return this.updating;
    this.updating = this.update().finally(() => { this.updating = null; });
    return this.updating;
  }

  async update() {
    try {
      if (!this.auth) {
        if (Date.now() - this.discoveryTime < 5000) return;
        this.discoveryTime = Date.now();
        this.auth = await this.discover();
        if (!this.auth) return;
        this.champions.clear();
        this.history.clear();
        this.skinChampion = 0;
      }
      const [me, region, login, gameflow, gamePhase] = await Promise.all([
        this.json(this.auth, '/lol-summoner/v1/current-summoner'),
        this.json(this.auth, '/riotclient/region-locale').catch(() => ({})),
        this.json(this.auth, '/lol-login/v1/session').catch(() => ({})),
        this.json(this.auth, '/lol-gameflow/v1/session').catch(() => null),
        this.json(this.auth, '/lol-gameflow/v1/gameflow-phase').catch(() => null)
      ]);
      this.platformId = String(this.auth.platformId || login.platformId || '').toUpperCase();
      this.region = String(this.auth.region || region.region || '').toUpperCase();
      const server = SERVER_NAMES[this.platformId] || this.platformId || (this.region === 'TENCENT' ? '国服' : this.region);
      if (!this.champions.size) {
        const list = await this.json(this.auth, '/lol-game-data/assets/v1/champion-summary.json');
        this.champions = new Map(list.filter(c => c.id > 0).map(c => [c.id, c]));
      }
      const playerName = me.gameName || me.displayName;
      const self = { name: me.tagLine ? `${playerName}#${me.tagLine}` : playerName, puuid: me.puuid,
        summonerLevel: me.summonerLevel, profileIconId: me.profileIconId };
      if (me.puuid) void this.getHistory(me.puuid, me.summonerId);
      const session = await this.json(this.auth, '/lol-champ-select/v1/session').catch(error => { if (error.status === 404) return null; throw error; });
      const phase = session ? 'ChampSelect' : gamePhase || gameflow?.phase || 'None';
      const inMatch = !!session || ['GameStart', 'InProgress', 'Reconnect', 'WaitingForStats', 'PreEndOfGame'].includes(phase);
      const context = { connected: true, server, playerName, self, phase, inMatch };
      if (!session) {
        this.session = null;
        this.skinChampion = 0;
        this.snapshot = { ...context, stage: inMatch ? 'in-game' : 'lobby',
          heroes: [], skins: [], teammates: inMatch ? this.snapshot.teammates : [], currentChampionId: 0,
          message: inMatch ? '对局进行中' : '进入选人后，英雄、皮肤和队友战绩会出现在这里' };
        return;
      }
      this.session = session;
      const mode = gameflow?.gameData?.queue?.gameMode;
      if (!session.benchEnabled || (mode && !['ARAM', 'KIWI'].includes(mode))) {
        this.skinChampion = 0;
        this.snapshot = { ...context, stage: 'unsupported',
          heroes: [], skins: [], teammates: [], currentChampionId: 0, message: '进入大乱斗类选人后使用英雄切换' };
        return;
      }
      const [selection, subset, pickable] = await Promise.all([
        this.json(this.auth, '/lol-champ-select/v1/session/my-selection').catch(() => ({})),
        this.json(this.auth, '/lol-lobby-team-builder/champ-select/v1/subset-champion-list').catch(() => []),
        this.json(this.auth, '/lol-champ-select/v1/pickable-champion-ids')
      ]);
      const championId = currentChampion(session);
      this.subset = Array.isArray(subset) ? subset : [];
      this.pickable = Array.isArray(pickable) ? pickable : [];
      const ids = [...new Set([championId, ...(session.benchChampions || []).map(c => c.championId),
        ...(session.timer?.phase === 'BAN_PICK' ? this.subset : [])].filter(id => id > 0))];
      let skins = this.skinChampion === championId ? this.snapshot.skins : [];
      let skinMessage = '';
      if (championId && this.skinChampion !== championId) {
        try {
          const [carousel, details] = await Promise.all([
            this.json(this.auth, '/lol-champ-select/v1/skin-carousel-skins'),
            this.json(this.auth, `/lol-game-data/assets/v1/champions/${championId}.json`)
          ]);
          skins = await Promise.all(availableSkins(carousel, details).map(async skin => ({ ...skin,
            imagePath: await this.localImage('skins', skin.id, skin.imagePath) })));
          this.skinChampion = championId;
        } catch { skinMessage = '暂时无法读取皮肤，正在重试'; }
      }
      const teammates = await Promise.all(session.myTeam.filter(p => p.cellId !== session.localPlayerCellId).map(async p => {
        let puuid = p.puuid;
        let player = null;
        if ((!puuid || /^0+$/.test(puuid.replaceAll('-', ''))) && p.summonerId) {
          player = await this.json(this.auth, `/lol-summoner/v1/summoners/${p.summonerId}`).catch(() => null);
          puuid = player?.puuid;
        }
        const name = p.gameName || player?.gameName || player?.displayName || p.playerAlias || `队友 ${p.cellId + 1}`;
        const tag = p.tagLine || player?.tagLine;
        const cachedHistory = this.history.get(`${this.platformId}:${puuid}`)?.result;
        if (puuid) void this.getHistory(puuid, p.summonerId);
        return { id: String(p.cellId), name: tag ? `${name}#${tag}` : name, puuid: puuid || '',
          summonerId: p.summonerId, championId: p.championId,
          championName: p.championId ? this.champion(p.championId).name : '正在选择',
          history: cachedHistory || (!puuid || /^0+$/.test(puuid.replaceAll('-', '')) ?
            { status: 'hidden', message: '客户端暂未提供玩家身份', matches: [] } :
            { status: 'loading', matches: [] }) };
      }));
      this.snapshot = { ...context, stage: 'select',
        currentChampionId: championId, selectedSkinId: selection.selectedSkinId || session.myTeam.find(p => p.cellId === session.localPlayerCellId)?.selectedSkinId,
        heroes: await Promise.all(ids.map(async id => {
          const c = this.champion(id);
          let selectable = id === championId;
          if (!selectable) { try { selectable = !!heroSelectionRequest(session, id, this.subset, this.pickable); } catch {} }
          return { id, name: c.name, selectable, imagePath: await this.localImage('champions', id, `/lol-game-data/assets/v1/champion-icons/${id}.png`) };
        })), skins, skinMessage, teammates, message: '' };
    } catch (error) {
      this.auth = null;
      this.session = null;
      this.skinChampion = 0;
      this.snapshot = { connected: false, stage: 'waiting', heroes: [], skins: [], teammates: [], currentChampionId: 0,
        message: error.status === 401 ? '客户端凭据已变化，正在重新连接' : '等待客户端连接；若客户端以管理员运行，请以相同权限启动助手' };
    }
  }

  async getHistory(puuid, summonerId, force = false) {
    if (!puuid || /^0+$/.test(puuid.replaceAll('-', ''))) return { status: 'hidden', message: '客户端暂未提供玩家身份', matches: [] };
    const key = `${this.platformId}:${puuid}`;
    const cached = this.history.get(key);
    if (cached?.pending) return cached.pending;
    if (cached && !force && Date.now() - cached.time < 60000) return cached.result;
    const pending = this.loadHistory(puuid, summonerId, key);
    this.history.set(key, { time: Date.now(), result: cached?.result, pending });
    return pending;
  }

  async loadHistory(puuid, summonerId, key) {
    let payload;
    let source = 'LCU';
    try {
      const host = SGP_HOSTS[this.platformId];
      if (host) {
        try {
          const token = await this.json(this.auth, '/entitlements/v1/token');
          if (!token.accessToken) throw new Error('登录凭据尚未就绪');
          const response = await this.fetch(`https://${host}.lol.qq.com:21019/match-history-query/v1/products/lol/player/${encodeURIComponent(puuid)}/SUMMARY?startIndex=0&count=10`, {
            headers: { Authorization: `Bearer ${token.accessToken}` }, signal: AbortSignal.timeout(8000)
          });
          if (!response.ok) throw new Error('国服战绩接口暂不可用');
          payload = await response.json();
          source = 'SGP';
        } catch {}
      }
      if (!payload) payload = await this.json(this.auth, `/lol-match-history/v1/products/lol/${encodeURIComponent(puuid)}/matches?begIndex=0&endIndex=9`);
      const matches = normalizeMatches(payload, puuid, summonerId).map(m => ({ ...m, championName: this.champion(m.championId).name }));
      const result = { status: 'ready', source, matches, ...matchSummary(matches) };
      this.history.set(key, { time: Date.now(), result });
      return result;
    } catch {
      const result = { status: 'error', message: '战绩暂时无法读取', matches: [] };
      this.history.set(key, { time: Date.now(), result });
      return result;
    }
  }

  async selectHero(id, demo = false) {
    if (demo) {
      if (!this.demo.heroes.some(h => h.id === id)) throw new Error('英雄不存在');
      this.demo.currentChampionId = id;
      this.demo.skins = this.demo.skinLists[id];
      this.demo.selectedSkinId = this.demo.skins[0]?.id;
      return this.getState(true);
    }
    return this.performAction(async () => {
      const session = await this.json(this.auth, '/lol-champ-select/v1/session');
      const [subset, pickable] = await Promise.all([
        this.json(this.auth, '/lol-lobby-team-builder/champ-select/v1/subset-champion-list').catch(() => []),
        this.json(this.auth, '/lol-champ-select/v1/pickable-champion-ids')
      ]);
      const request = heroSelectionRequest(session, id, Array.isArray(subset) ? subset : [], pickable);
      if (request) await this.json(this.auth, request.endpoint, request.method, request.body);
      this.skinChampion = 0;
    });
  }

  async selectSkin(id, demo = false) {
    if (demo) {
      if (!this.demo.skins.some(s => s.id === id)) throw new Error('皮肤不可用');
      this.demo.selectedSkinId = id;
      return this.getState(true);
    }
    return this.performAction(async () => {
      const [carousel, selection] = await Promise.all([
        this.json(this.auth, '/lol-champ-select/v1/skin-carousel-skins'),
        this.json(this.auth, '/lol-champ-select/v1/session/my-selection')
      ]);
      if (selection.championId !== this.snapshot.currentChampionId) throw new Error('英雄已变化，请重试');
      if (!availableSkins(carousel).some(s => s.id === id)) throw new Error('该皮肤未解锁或当前不可用');
      await this.json(this.auth, '/lol-champ-select/v1/session/my-selection', 'PATCH', { selectedSkinId: id });
    });
  }

  async performAction(action) {
    if (!this.auth) throw new Error('客户端尚未连接');
    if (this.actionBusy) throw new Error('上一项操作仍在处理中');
    this.actionBusy = true;
    try { await action(); if (this.updating) await this.updating; await this.tick(); return this.getState(); }
    finally { this.actionBusy = false; }
  }

  close() { clearInterval(this.timer); }
}

module.exports = { Assistant, SGP_HOSTS };
