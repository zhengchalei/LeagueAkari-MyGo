const test = require('node:test');
const assert = require('node:assert/strict');
const { availableSkins, balanceEffects, heroSelectionRequest, normalizeMatches } = require('../app/data.cjs');
const { parseLockfile, commandValue } = require('../app/lcu.cjs');
const { Assistant } = require('../app/assistant.cjs');
const { startServer } = require('../app/server.cjs');

test('本地连接识别端口与 token，不依赖游戏安装盘符', () => {
  assert.deepEqual(parseLockfile('LeagueClient:123:50123:local-test:https'), { pid:123, port:50123, password:'local-test' });
  assert.equal(commandValue('LeagueClientUx.exe --app-port=50123 --remoting-auth-token="local-test"', 'remoting-auth-token'), 'local-test');
  assert.equal(commandValue('--region="TENCENT" --rso_platform_id="HN1"', 'region'), 'TENCENT');
  assert.equal(commandValue('--region="TENCENT" --rso_platform_id="HN1"', 'rso_platform_id'), 'HN1');
});

test('伤害减免是增益；提高受到伤害是减益', () => {
  const data = balanceEffects({ damage_dealt:105, damage_taken:95, healing:90, cooldown_reduction:10 });
  assert.deepEqual(data.buffs.map(x => [x.name, x.value]), [['造成伤害','+5%'],['受到伤害','−5%'],['技能急速','+10']]);
  assert.equal(data.debuffs[0].name, '治疗效果');
  assert.equal(balanceEffects({ damage_taken:110 }).debuffs[0].value, '+10%');
  assert.equal(balanceEffects(null).available, false);
});

test('皮肤只展示已解锁可用项，并纳入可用炫彩', () => {
  const skins = availableSkins([
    { id:103000, name:'经典', unlocked:true, disabled:false, childSkins:[] },
    { id:103027, name:'灵魂莲华', unlocked:false, childSkins:[] },
    { id:103014, name:'星之守护者', unlocked:true, disabled:true, childSkins:[
      { id:103015, name:'炫彩', unlocked:true, disabled:false },
      { id:103016, name:'未拥有炫彩', unlocked:false, disabled:false }
    ] }
  ]);
  assert.deepEqual(skins.map(s => s.id), [103000,103015]);
});

function session(championId, phase = 'FINALIZATION') {
  return { benchEnabled:true, localPlayerCellId:0, myTeam:[{cellId:0,championId}], timer:{phase}, benchChampions:[{championId:127}], actions:[[{id:12,actorCellId:0,type:'pick',completed:false}]] };
}

test('初次选人和备选席交换使用不同客户端动作；拒绝过期不可选英雄', () => {
  assert.deepEqual(heroSelectionRequest(session(0,'BAN_PICK'),127,[127],[127]), { method:'PATCH',endpoint:'/lol-champ-select/v1/session/actions/12',body:{championId:127,completed:true,type:'pick'} });
  assert.equal(heroSelectionRequest(session(103),127,[],[127]).endpoint, '/lol-champ-select/v1/session/bench/swap/127');
  assert.throws(() => heroSelectionRequest(session(103),711,[],[127]), /不可选/);
  assert.throws(() => heroSelectionRequest(session(103,'GAME_STARTING'),127,[],[127]), /阶段/);
});

test('从身份字段找到指定队友，不误读另一名玩家的数据', () => {
  const game={gameId:77,participantIdentities:[{participantId:1,player:{puuid:'other'}},{participantId:2,player:{puuid:'target'}}],participants:[{participantId:1,championId:103,stats:{kills:20,win:false}},{participantId:2,championId:711,stats:{kills:4,deaths:3,assists:12,win:true}}]};
  for (const payload of [{games:{games:[game]}},{games:[{json:game}]}]) {
    const matches=normalizeMatches(payload,'target');
    assert.equal(matches[0].kills,4);
    assert.equal(matches[0].championId,711);
    assert.equal(matches[0].win,true);
  }
  assert.equal(normalizeMatches({games:[{json:game}]},'missing').length,0);
  const flat={gameId:78,participants:[{puuid:'target',championId:127,kills:6,deaths:2,assists:11,win:true}]};
  assert.equal(normalizeMatches({games:[{json:flat}]},'target')[0].kills,6);
});

test('国服 SGP 故障时改用 LCU，皮肤写入必须通过拥有状态校验', async () => {
  const calls=[];
  const assistant=new Assistant({json:async(auth,url,method,body)=>{
    calls.push({url,method,body});
    if(url==='/entitlements/v1/token')return {accessToken:'test-token'};
    if(url.includes('/matches?'))return {games:{games:[]}};
    if(url.endsWith('/skin-carousel-skins'))return [{id:103000,unlocked:true,disabled:false}];
    if(url.endsWith('/my-selection')&&method!=='PATCH')return {championId:103};
    return null;
  },fetch:async()=>{throw new Error('SGP offline');}});
  assistant.auth={};assistant.platformId='HN1';assistant.snapshot.currentChampionId=103;
  assistant.tick=async()=>{};
  const history=await assistant.getHistory('target');
  assert.equal(history.source,'LCU');
  await assert.rejects(assistant.selectSkin(103027),/未解锁/);
  assert.equal(calls.some(c=>c.method==='PATCH'),false);
  await assistant.selectSkin(103000);
  assert.deepEqual(calls.find(c=>c.method==='PATCH').body,{selectedSkinId:103000});
});

test('本地服务隔离演示操作、拒绝外部写入并提供真实本地图片', async t => {
  const writes=[];
  const assistant={assetsDir:require('node:path').join(__dirname,'../app/assets'),auth:null,
    getState:demo=>({demo}),selectHero:async(id,demo)=>{writes.push({id,demo});return {id,demo};},close:()=>{}};
  const local=await startServer({assistant});
  t.after(()=>local.close());
  const snapshot=await (await fetch(local.origin+'/api/state?demo=1')).json();
  assert.equal(snapshot.demo,true);
  const denied=await fetch(local.origin+'/api/hero',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:127})});
  assert.equal(denied.status,403);assert.equal(writes.length,0);
  const allowed=await fetch(local.origin+'/api/hero',{method:'POST',headers:{'Content-Type':'application/json','X-Assistant-Token':snapshot.actionToken},body:JSON.stringify({id:127,demo:true})});
  assert.equal(allowed.status,200);assert.deepEqual(writes,[{id:127,demo:true}]);
  const image=await fetch(local.origin+'/assets/champions/103.jpg');
  assert.equal(image.headers.get('content-type'),'image/jpeg');
  assert.ok((await image.arrayBuffer()).byteLength>1000);
  const escape=await fetch(local.origin+'/assets/%2e%2e%2flcu.cjs');
  assert.notEqual(escape.status,200);
});

test('完整国服适配链路：自动连接、读取选人和皮肤、SGP 战绩、选择英雄与皮肤', async t => {
  let championId=103,selectedSkinId=103000;
  const writes=[];
  const assistant=new Assistant({discover:async()=>({pid:1,port:5555,password:'test',region:'TENCENT',platformId:'HN1'}),
    json:async(auth,endpoint,method,body)=>{
      if(method){writes.push({endpoint,method,body});
        if(endpoint.includes('/bench/swap/')){championId=Number(endpoint.split('/').pop());selectedSkinId=championId*1000;}
        if(body?.selectedSkinId)selectedSkinId=body.selectedSkinId;
        return null;
      }
      if(endpoint==='/lol-summoner/v1/current-summoner')return {gameName:'测试用户'};
      if(endpoint==='/riotclient/region-locale')return {region:'TENCENT'};
      if(endpoint==='/lol-login/v1/session')return {platformId:'HN1'};
      if(endpoint==='/lol-gameflow/v1/session')return {gameData:{queue:{gameMode:'ARAM'}}};
      if(endpoint==='/lol-game-data/assets/v1/champion-summary.json')return [{id:103,name:'阿狸'},{id:127,name:'丽桑卓'}];
      if(endpoint==='/lol-champ-select/v1/session')return {...session(championId),myTeam:[{cellId:0,championId},{cellId:1,championId:127,gameName:'测试队友',puuid:'mock-teammate',summonerId:123}]};
      if(endpoint==='/lol-champ-select/v1/session/my-selection')return {championId,selectedSkinId};
      if(endpoint.endsWith('/subset-champion-list'))return [];
      if(endpoint.endsWith('/pickable-champion-ids'))return [103,127];
      if(endpoint.endsWith('/skin-carousel-skins'))return [{id:championId*1000,name:'经典',unlocked:true,disabled:false},{id:championId*1000+1,name:'测试皮肤',unlocked:true,disabled:false},{id:championId*1000+2,name:'未拥有',unlocked:false}];
      if(endpoint.includes('/champions/'))return {skins:[]};
      if(endpoint==='/entitlements/v1/token')return {accessToken:'fake-token'};
      throw new Error('unexpected endpoint '+endpoint);
    },fetch:async url=>({ok:true,json:async()=>url.includes('aram-balance')?{data:[]}:{games:[{json:{gameId:2,participants:[{puuid:'mock-teammate',championId:127,kills:6,deaths:2,assists:11,win:true}]}}]}})});
  t.after(()=>assistant.close());
  await assistant.init();
  await assistant.getHistory('mock-teammate',123);
  await assistant.tick();
  let state=assistant.getState();
  assert.equal(state.server,'艾欧尼亚');assert.equal(state.currentChampionId,103);
  assert.equal(state.skins.length,2);assert.equal(state.heroes[0].imagePath,'/assets/champions/103.jpg');
  assert.equal(state.teammates[0].history.source,'SGP');assert.equal(state.teammates[0].history.wins,1);
  state=await assistant.selectHero(127);
  assert.equal(state.currentChampionId,127);assert.equal(state.skins[0].id,127000);
  state=await assistant.selectSkin(127001);
  assert.equal(state.selectedSkinId,127001);
  assert.equal(writes[0].endpoint,'/lol-champ-select/v1/session/bench/swap/127');
  assert.equal(writes[1].body.selectedSkinId,127001);
});

test('大厅即可读取自己的真实身份和战绩；无选人会话时依然识别对局阶段', async t => {
  let phase = 'Lobby';
  const assistant = new Assistant({ discover: async () => ({ region:'TENCENT', platformId:'HN1' }),
    json: async (_, endpoint) => {
      if (endpoint === '/lol-summoner/v1/current-summoner') return { puuid:'my-puuid', summonerId:42, gameName:'自己的账号', tagLine:'1234', summonerLevel:100 };
      if (endpoint === '/lol-gameflow/v1/gameflow-phase') return phase;
      if (endpoint === '/lol-game-data/assets/v1/champion-summary.json') return [{ id:16, name:'索拉卡' }];
      if (endpoint === '/lol-champ-select/v1/session') throw Object.assign(new Error('no selection'), { status:404 });
      if (endpoint === '/entitlements/v1/token') return { accessToken:'test-token' };
      return {};
    }, fetch: async url => ({ ok:true, json:async () => url.includes('aram-balance') ? { data:[] } :
      { games:[{ json:{ gameId:1, participants:[{ puuid:'my-puuid', championId:16, kills:3, deaths:9, assists:53, win:true }] } }] } }) });
  t.after(() => assistant.close());
  await assistant.init();
  await assistant.getHistory('my-puuid', 42);
  let state = assistant.getState();
  assert.equal(state.self.name, '自己的账号#1234');
  assert.equal(state.self.history.matches[0].championName, '索拉卡');
  assert.equal(state.self.history.source, 'SGP');
  assert.equal(state.inMatch, false);
  phase = 'InProgress';
  await assistant.tick();
  state = assistant.getState();
  assert.equal(state.inMatch, true);
  assert.equal(state.stage, 'in-game');
  assert.equal(state.self.history.matches.length, 1);
});
