const $ = selector => document.querySelector(selector);
let demo = new URLSearchParams(location.search).get('demo') === '1';
let state, expandedTeammate = null, busy = false, polling = false, revision = 0;
let tabs = createTabController();

const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
function imageUrl(imagePath, fallbackId) {
  if (imagePath?.startsWith('/assets/')) return imagePath;
  if (imagePath?.startsWith('/lol-game-data/assets/')) return `/api/image?path=${encodeURIComponent(imagePath)}`;
  return `/assets/champions/${fallbackId || 103}.jpg`;
}
function image(imagePath, id) { return `<img src="${escapeHtml(imageUrl(imagePath, id))}" alt="" loading="lazy">`; }
function notice(message) { $('#notice').textContent = message; $('#notice').hidden = !message; }
function renderEffects(list, available) {
  return !available ? '<div class="empty">暂无平衡数据</div>' : list.length ? list.map(e => `<div class="effect"><span>${escapeHtml(e.name)}</span><b>${escapeHtml(e.value)}</b></div>`).join('') : '<div class="empty">无</div>';
}

function matchRow(m, own = false) {
  const date = m.timestamp ? new Date(m.timestamp).toLocaleDateString('zh-CN', { timeZone:'Asia/Shanghai', month:'numeric', day:'numeric' }) : '';
  return `<div class="match ${own ? 'own-match' : ''}">${own ? image(demo ? `/assets/champions/${m.championId}.jpg` : `/lol-game-data/assets/v1/champion-icons/${m.championId}.png`, m.championId) : ''}<span class="${m.win ? 'win':'loss'}">${m.win ? '胜利':'失败'}</span><span>${escapeHtml(m.championName)}</span><span>${m.kills} / ${m.deaths} / ${m.assists}</span><span>${Math.floor(m.duration / 60)} 分钟${own && date ? `<small>${escapeHtml(date)}</small>` : ''}</span></div>`;
}

function renderOwnHistory() {
  const self = state.self;
  $('#self-profile').innerHTML = self ? `${self.profileIconId ? image(`/lol-game-data/assets/v1/profile-icons/${self.profileIconId}.jpg`) : ''}<div><h2>${escapeHtml(self.name)}</h2><span class="secondary">${self.summonerLevel ? `等级 ${self.summonerLevel} · ` : ''}最近 10 场</span></div>` : '';
  const history = self?.history;
  $('#self-summary').textContent = history?.status === 'ready' ? `${history.wins} 胜 ${history.losses} 负 · KDA ${history.kda}` : '';
  $('#self-matches').innerHTML = history?.matches?.length ? history.matches.map(m => matchRow(m, true)).join('') : `<div class="waiting">${escapeHtml(!state.connected && !demo ? state.message : history?.message || (history?.status === 'ready' ? '暂无近期战绩' : '正在读取我的战绩…'))}</div>`;
}

function render() {
  if (!state) return;
  $('#connection').textContent = demo ? '演示数据 · 未操作客户端' : state.connected ? `${state.server || '国服'} · ${state.playerName || '已连接'}` : '未连接客户端';
  $('#demo-toggle').textContent = demo ? '连接客户端' : '查看演示';
  const historyVisible = tabs.activeTab === 'history';
  $('#history-tab').setAttribute('aria-pressed', historyVisible);
  $('#match-tab').setAttribute('aria-pressed', !historyVisible);
  $('#history-panel').hidden = !historyVisible;
  $('#match-panel').hidden = historyVisible;
  renderOwnHistory();
  $('#selection').hidden = state.stage !== 'select';
  $('#waiting').hidden = state.stage === 'select';
  $('#waiting-text').textContent = state.message || '';
  $('#match-team').hidden = !state.inMatch;
  const hero = state.heroes.find(h => h.id === state.currentChampionId);
  $('#hero-status').textContent = hero ? `当前：${hero.name}` : '点击头像直接选择';
  $('#heroes').innerHTML = state.heroes.map(h => `<button type="button" class="hero" data-hero="${h.id}" aria-pressed="${h.id === state.currentChampionId}" ${busy || h.selectable === false ? 'disabled' : ''}>${image(h.imagePath, h.id)}<span>${escapeHtml(h.name)}</span></button>`).join('');
  $('#buffs').innerHTML = renderEffects(state.balance.buffs, state.balance.available);
  $('#debuffs').innerHTML = renderEffects(state.balance.debuffs, state.balance.available);
  const time = state.balanceTime ? new Date(state.balanceTime).toLocaleDateString('zh-CN', { timeZone:'Asia/Shanghai' }) : '';
  $('#balance-source').textContent = `OP.GG 平衡参考${time ? ' · 更新于 ' + time : ''}`;
  $('#skin-count').textContent = hero ? `${hero.name} · ${state.skins.length} 款` : '';
  $('#skin-list').innerHTML = state.skins.length ? state.skins.map(s => `<button type="button" class="skin" data-skin="${s.id}" aria-pressed="${s.id === state.selectedSkinId}" ${busy ? 'disabled' : ''}>${image(s.imagePath, state.currentChampionId)}<span>${escapeHtml(s.name)}</span></button>`).join('') : `<span class="empty">${escapeHtml(state.skinMessage || (hero ? '暂无可用皮肤' : '选择英雄后显示已有皮肤'))}</span>`;
  const skin = state.skins.find(s => s.id === state.selectedSkinId);
  $('#skin-choice').textContent = skin ? `已选择：${skin.name}` : '';
  $('#team-list').innerHTML = state.teammates.map(t => {
    const summary = t.history?.status === 'ready' ? t.history.count ? `${t.history.wins} 胜 ${t.history.losses} 负 · KDA ${t.history.kda}` : '暂无近期战绩' : t.history?.message || '正在查询战绩';
    return `<button type="button" class="teammate" data-teammate="${escapeHtml(t.id)}" aria-expanded="${expandedTeammate === t.id}" aria-controls="team-detail">${image(demo ? `/assets/champions/${t.championId}.jpg` : `/lol-game-data/assets/v1/champion-icons/${t.championId}.png`, t.championId)}<span><b>${escapeHtml(t.name)}</b><small>${escapeHtml(t.championName)} · ${escapeHtml(summary)}</small></span><span class="expand" aria-hidden="true">${expandedTeammate === t.id ? '−' : '+'}</span></button>`;
  }).join('') || '<div class="empty">等待队友加入</div>';
  const teammate = state.teammates.find(t => t.id === expandedTeammate);
  $('#team-detail').hidden = !teammate;
  if (teammate) $('#team-detail').innerHTML = `<div class="detail-title">${escapeHtml(teammate.name)}<span>近期对局${demo ? ' · 演示' : ''}</span></div>${teammate.history?.matches?.length ? teammate.history.matches.map(m => matchRow(m)).join('') : `<div class="empty">${escapeHtml(teammate.history?.message || (teammate.history?.status === 'ready' ? '暂无近期战绩' : '正在读取战绩…'))}</div>`}`;
}

async function poll() {
  if (polling || busy) return;
  polling = true;
  const requestRevision = revision;
  try {
    const response = await fetch(`/api/state${demo ? '?demo=1' : ''}`);
    if (!response.ok) throw new Error('无法连接助手服务');
    const next = await response.json();
    if (requestRevision === revision && !busy && JSON.stringify(state) !== JSON.stringify(next)) { state = next; tabs.sync(next.inMatch); render(); }
  } catch (error) { notice(error.message); }
  finally { polling = false; }
}

async function select(kind, id) {
  if (busy || !state) return;
  busy = true; revision++; notice(''); render();
  try {
    const response = await fetch(`/api/${kind}`, { method:'POST', headers:{ 'Content-Type':'application/json', 'X-Assistant-Token':state.actionToken }, body:JSON.stringify({ id, demo }) });
    const next = await response.json();
    if (!response.ok) throw new Error(next.error || '操作失败，请重试');
    state = next;
  } catch (error) { notice(error.message); }
  finally { busy = false; render(); }
}

document.addEventListener('click', event => {
  const button = event.target.closest('button');
  if (!button) return;
  if (button.dataset.tab) { tabs.choose(button.dataset.tab); render(); }
  else if (button.dataset.hero) void select('hero', Number(button.dataset.hero));
  else if (button.dataset.skin) void select('skin', Number(button.dataset.skin));
  else if (button.dataset.teammate !== undefined) { expandedTeammate = expandedTeammate === button.dataset.teammate ? null : button.dataset.teammate; render(); }
  else if (button.id === 'demo-toggle' && !busy) {
    demo = !demo; revision++; expandedTeammate = null; tabs = createTabController(); notice('');
    history.replaceState(null, '', demo ? '/?demo=1' : '/');
    $('#connection').textContent = '正在切换…';
    void poll();
  }
});
document.addEventListener('error', event => { if (event.target instanceof HTMLImageElement) { event.target.classList.add('image-fallback'); event.target.removeAttribute('src'); } }, true);
void poll();
setInterval(() => void poll(), 1500);
