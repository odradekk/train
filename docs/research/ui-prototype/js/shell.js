// 主菜单、新游戏、存档、设置、问题反馈
const SAVES = [
  { id: 's1', world: '标准测试世界 · 种子 40', name: '白石镇的对局', time: '第 3 天 08:20', saved: '今天 21:40', funds: 12400, trains: 3, owned: 1 },
  { id: 's2', world: '标准测试世界 · 种子 40', name: '自动存档', auto: true, time: '第 3 天 06:00', saved: '今天 21:12', funds: 11800, trains: 3, owned: 1 },
  { id: 's3', world: '标准测试世界 · 种子 40', name: '建城前', time: '第 2 天 18:00', saved: '今天 20:30', funds: 9600, trains: 2, owned: 0 },
  { id: 's4', world: '标准测试世界 · 种子 7', name: '试玩一', time: '第 9 天 13:00', saved: '10 月 28 日', funds: 30210, trains: 6, owned: 2, missing: ['mod:steam-pack'] },
];
const SETTINGS = {
  window: '无边框全屏', quality: '高', scale: 100, fps: '60', blurPause: false, autosave: '60 分钟', shake: true, alwaysBars: false,
  notif: {}, keys: [['暂停 / 继续', '空格'], ['1× 速度', '1'], ['2× 速度', '2'], ['4× 速度', '3'], ['8× 速度', '4'], ['打开车队', 'T'], ['打开线路', 'L'], ['打开聚居地', 'J'], ['打开报告', 'R'], ['打开设计', 'K'], ['打开战斗', 'B'], ['定位选中对象', 'F'], ['进入场景', 'V'], ['平移地图', 'W A S D'], ['保存', 'Ctrl S'], ['关闭 / 返回', 'Esc'], ['开关控制台', '`']],
};
const NOTIF_EVENTS = [
  ['严重', ['遇敌', '列车被摧毁', '聚居地遭到攻击', '建筑被摧毁']], ['警告', ['线路出现断点', '列车受阻', '运行计划无法完成', '库存低于补货目标', '租约即将到期', '建筑停工']],
  ['完成', ['委托完成', '建筑完工', '列车完成一圈', '建城完成']], ['信息', ['自动存档完成', '战斗结束']],
];
NOTIF_EVENTS.forEach(([, evs]) => evs.forEach(e => SETTINGS.notif[e] = e === '遇敌' ? 'slow' : 'none'));

function shellState() {
  return { screen: 'game', overlay: null, ng: { config: 'std', seed: 40, name: '新的对局', missing: false, creating: null }, saveSel: 's1', saveQ: '', saveName: '白石镇的对局 · 第 3 天', setTab: 'display', capture: null, keyNote: null, issue: { desc: '', done: null }, savedAgo: 12 };
}

// ---------- 主菜单 ----------
function menuScreen() {
  const loading = S.contentLoading, last = SAVES[0];
  return `<div class="menu-screen"><svg class="bg" viewBox="0 0 640 520">${routeMapSvg({ loop: false, stops: [] }, 640, 520)}</svg>
    <div class="menu-col"><h1>Train</h1><div class="tagline">体素列车经营</div>
      <button class="menu-btn primary" id="menu-first" data-action="menu-continue" ${loading ? 'disabled' : ''}>继续游戏<span class="sub">${last.name} · ${last.time} · ${last.saved}保存</span></button>
      <button class="menu-btn" data-action="menu-new" ${loading ? 'disabled' : ''}>新游戏</button>
      <button class="menu-btn" data-action="open-saves">读取存档</button>
      <button class="menu-btn" data-action="open-settings">设置</button>
      <button class="menu-btn" data-action="menu-quit">退出游戏</button>
      ${loading ? `<div style="margin-top:10px"><div class="small muted">正在加载内容包… ${loading}%　加载完成后可以开始游戏</div><div class="bar" style="margin-top:6px"><i style="width:${loading}%"></i></div></div>` : ''}
      <div style="margin-top:18px;border-top:1px solid var(--divider);padding-top:12px"><div class="small faint" style="margin-bottom:6px">开发构建</div><div style="display:flex;gap:8px"><button class="btn" data-action="menu-example">从示例存档开始…</button><button class="btn" data-action="open-authoring">创作模式…</button></div></div></div>
    <div class="menu-foot">版本 0.6.0 · 内容包 core 1.4</div></div>`;
}

function newGameScreen() {
  const g = S.ng;
  if (g.creating != null) return `<div class="menu-screen"><div class="page-panel" style="padding:32px"><h2 style="margin:0 0 16px">正在生成世界…</h2><div class="bar"><i style="width:${g.creating}%"></i></div>
    <div class="small muted" style="margin-top:8px">${g.creating < 50 ? '生成节点和线路' : '生成聚居地和初始市场'}</div><div style="margin-top:20px;text-align:right"><button class="btn" data-action="ng-cancel">取消</button></div></div></div>`;
  return `<div class="menu-screen"><div class="page-panel"><div class="ws-head"><h2>新游戏</h2></div><div style="padding:20px 24px;display:flex;flex-direction:column;gap:16px">
    <div class="field"><label for="ng-config">世界生成配置</label><select id="ng-config" data-change="ng-config"><option value="std" ${g.config === 'std' ? 'selected' : ''}>标准测试世界</option><option value="big" ${g.config === 'big' ? 'selected' : ''}>大陆（示例）</option></select>
      <div class="small muted">${g.config === 'std' ? '14 个节点，3 座核心城市，2 处生物聚落。适合第一次玩。' : '40 个节点，生物聚落更多，聚居地之间距离更远。'}</div></div>
    <div class="field"><label for="ng-seed">种子</label><div style="display:flex;gap:8px"><input id="ng-seed" type="number" value="${g.seed}" data-input="ng-seed" style="width:200px"><button class="btn" data-action="ng-random">随机</button></div>
      <div class="small muted">同一配置和种子总是生成同一个世界。</div></div>
    <div class="field"><label for="ng-name">存档名称</label><input id="ng-name" type="text" value="${g.name}" data-input="ng-name"></div>
    ${g.missing ? `<div class="notice warning">${icon('alert')}<div class="grow">有 1 个内容包缺失：mod:steam-pack。缺失的内容会显示为占位，相关的建筑和车辆不能使用。</div></div>` : ''}
    </div><div class="ws-foot"><span class="msg small muted">新对局开始时游戏是暂停的，按空格开始。</span><button class="btn" data-action="ng-back">返回</button><button class="btn primary" data-action="ng-start">${g.missing ? '仍然开始' : '开始游戏'}</button></div></div></div>`;
}

// ---------- 存档 ----------
function savesOverlay() {
  const inGame = S.screen === 'game', q = S.saveQ.trim();
  const list = SAVES.filter(s => !q || s.name.includes(q));
  const worlds = [...new Set(list.map(s => s.world))];
  const sel = SAVES.find(s => s.id === S.saveSel) || SAVES[0];
  const rows = worlds.map(wn => `<div class="group-label">${wn}</div>` + list.filter(s => s.world === wn).map(s => `<div class="row ${s.id === sel.id ? 'on' : ''}" data-action="save-sel" data-v="${s.id}" data-dbl="save-load">
    <span class="faint">${icon(s.auto ? 'clock' : 'save')}</span><div class="grow"><div class="title">${s.name}${s.auto ? ' <span class="chip neutral">自动</span>' : ''}${s.missing ? ` <span style="color:#e9b45a">${icon('alert', 'sm')}</span>` : ''}</div><div class="sub">${s.time} · ${s.saved}</div></div></div>`).join('')).join('')
    + `<div class="group-label">示例存档（开发构建）</div><div class="row" data-action="menu-example"><span class="faint">${icon('folder')}</span><div class="grow"><div class="title">起步贸易</div><div class="sub">用于验收的固定开局</div></div></div>`;
  return `<div class="ov-panel" style="width:1180px;height:760px"><div class="ws-head"><h2>${inGame ? '存档与读档' : '读取存档'}</h2>
      <button class="btn ghost" data-action="todo" data-v="导入存档文件">${icon('folder')} 导入存档文件…</button><button class="btn ghost icon-only" data-action="ov-close" title="关闭（Esc）">${icon('close')}</button></div>
    ${inGame ? `<div style="display:flex;gap:8px;align-items:center;padding:12px 24px;border-bottom:1px solid var(--divider)"><span class="muted">保存当前对局</span><input id="save-name" type="text" value="${S.saveName}" data-input="save-name" style="flex:1;max-width:420px"><button class="btn primary" data-action="save-new">${icon('save')} 保存</button><span class="small muted">${S.savedAgo ? `上次保存是 ${S.savedAgo} 分钟前` : '刚刚保存过'}</span></div>` : ''}
    <div class="ws-body"><div class="ws-col" style="width:520px"><div class="panel-tools" style="padding-top:12px"><input id="save-q" type="search" placeholder="搜索存档" value="${S.saveQ}" data-input="save-q"></div><div class="scroll">${rows}</div><div class="small faint" style="padding:8px 16px">双击存档直接读取。</div></div>
      <div class="ws-col" style="flex:1"><div class="scroll" style="padding:16px 24px">
        <div style="height:200px;border-radius:6px;overflow:hidden;background:#5f6b55"><svg viewBox="0 0 640 520" preserveAspectRatio="xMidYMid slice" style="width:100%;height:100%">${routeMapSvg({ loop: false, stops: [] }, 640, 520)}</svg></div>
        <h3 style="margin:14px 0 4px;font-size:18px">${sel.name}</h3><div class="muted">${sel.world}</div>
        <div class="kv" style="margin-top:12px"><dt>游戏时间</dt><dd>${sel.time}</dd><dt>保存时间</dt><dd>${sel.saved}</dd><dt>资金</dt><dd>${fmt(sel.funds)}</dd><dt>列车</dt><dd>${sel.trains} 列</dd><dt>自有聚居地</dt><dd>${sel.owned} 座</dd><dt>内容包</dt><dd>core 1.4${sel.missing ? '、mod:steam-pack' : ''}</dd></div>
        ${sel.missing ? `<div class="notice warning" style="margin-top:12px">${icon('alert')}<div class="grow">这个存档用到的内容包 mod:steam-pack 没有安装。读取后，用到它的建筑和车辆会显示为占位，不能使用。</div></div>` : ''}
      </div><div class="insp-foot"><button class="btn primary" data-action="save-load">读取</button><button class="btn" data-action="todo" data-v="重命名存档">重命名</button><button class="btn" data-action="todo" data-v="导出存档文件">导出文件…</button><button class="btn danger" data-action="save-del">删除…</button></div></div></div></div>`;
}

// ---------- 设置 ----------
function settingsOverlay() {
  const T = S.setTab, st = SETTINGS;
  const seg = (k, opts) => `<div class="seg">${opts.map(o => `<button class="${st[k] === o ? 'on' : ''}" data-action="set" data-v="${k}:${o}">${o}</button>`).join('')}</div>`;
  const sw = k => `<label class="switch ${st[k] ? 'on' : ''}" data-action="set-toggle" data-v="${k}"><i></i>${st[k] ? '开' : '关'}</label>`;
  const row = (label, sub, ctl) => `<div class="set-row"><div class="lbl">${label}${sub ? `<div class="small">${sub}</div>` : ''}</div>${ctl}</div>`;
  let body = '';
  if (T === 'display') body = row('窗口模式', '', seg('window', ['窗口', '无边框全屏', '全屏'])) + row('画质', '“中”减少远处细节，帧率更稳。', seg('quality', ['中', '高']))
    + row('界面缩放', '文字和按钮的大小。', `<div style="display:flex;gap:10px;align-items:center;width:320px"><input id="set-scale" type="range" min="75" max="200" step="25" value="${st.scale}" data-input="set-scale" style="flex:1"><b style="width:48px;text-align:right">${st.scale}%</b></div>`)
    + row('帧率上限', '', `<select id="set-fps" data-change="set-fps" style="width:160px">${['30', '60', '120', '不限'].map(o => `<option ${st.fps === o ? 'selected' : ''}>${o}</option>`).join('')}</select>`);
  else if (T === 'game') body = row('窗口失去焦点时暂停', '切到其他程序时自动暂停游戏。', sw('blurPause')) + row('自动存档', '', `<select id="set-auto" data-change="set-auto" style="width:160px">${['关闭', '15 分钟', '30 分钟', '60 分钟'].map(o => `<option ${st.autosave === o ? 'selected' : ''}>${o}</option>`).join('')}</select>`)
    + row('镜头抖动', '爆炸和撞击时晃动镜头。', sw('shake')) + row('一直显示耐久条和状态图标', '关闭时只在悬停或选中时显示。', sw('alwaysBars'));
  else if (T === 'notif') body = `<div class="small muted" style="margin-bottom:8px">选择每类通知出现时游戏怎么做。通知本身总会显示。</div><table class="grid"><tr><th>通知</th><th style="text-align:center">只显示</th><th style="text-align:center">降到 1×</th><th style="text-align:center">暂停</th></tr>
    ${NOTIF_EVENTS.map(([lvl, evs]) => `<tr><td colspan="4" class="small faint" style="height:28px">${lvl}</td></tr>` + evs.map(e => `<tr><td>${e}</td>${['none', 'slow', 'pause'].map(v => `<td style="text-align:center"><span class="radio ${st.notif[e] === v ? 'on' : ''}" data-action="set-notif" data-v="${e}:${v}" role="radio" aria-checked="${st.notif[e] === v}"></span></td>`).join('')}</tr>`).join('')).join('')}</table>
    <button class="btn" style="margin-top:12px" data-action="set-notif-reset">恢复默认</button>`;
  else if (T === 'keys') body = `${S.keyNote ? `<div class="notice warning" style="margin-bottom:10px">${icon('alert')}<div class="grow">${S.keyNote}</div></div>` : ''}<table class="grid"><tr><th>操作</th><th>按键</th></tr>${st.keys.map(([a, k], i) => `<tr><td>${a}</td><td><button class="keycap ${S.capture === i ? 'capturing' : ''}" data-action="key-capture" data-v="${i}">${S.capture === i ? '按下新按键…' : k}</button></td></tr>`).join('')}</table>
    <div class="small muted" style="margin-top:10px">鼠标：地图上左键或中键拖动平移，滚轮缩放；场景里右键拖动环绕，中键拖动平移。</div><button class="btn" style="margin-top:12px" data-action="key-reset">恢复默认</button>`;
  else body = row('版本', '', '0.6.0') + row('内容包', '', 'core 1.4') + row('报告问题', '导出一个故障复现包，发给开发者。', `<button class="btn" data-action="open-issue">${icon('flag')} 报告问题…</button>`);
  const nav = [['display', '显示'], ['game', '游戏'], ['notif', '通知'], ['keys', '按键'], ['about', '关于']];
  return `<div class="ov-panel" style="width:1000px;height:720px"><div class="ws-head"><h2>设置</h2><span class="small muted">改动立即生效</span><button class="btn ghost icon-only" data-action="ov-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div class="ws-body"><nav class="side-nav">${nav.map(([k, l]) => `<button class="${T === k ? 'on' : ''}" data-action="set-tab" data-v="${k}">${l}</button>`).join('')}</nav><div class="scroll" style="flex:1;padding:8px 24px 24px">${body}</div></div></div>`;
}

// ---------- 问题反馈 ----------
function issueOverlay() {
  const d = S.issue;
  if (d.done) return `<div class="ov-panel" style="width:640px"><div class="ws-head"><h2>报告问题</h2><button class="btn ghost icon-only" data-action="ov-close">${icon('close')}</button></div>
    <div style="padding:20px 24px"><div class="notice success">${icon('check')}<div class="grow">已导出故障复现包。<div class="small muted" style="margin-top:4px;user-select:text">${d.done}</div></div></div>
    <div class="muted" style="margin-top:12px">把这个文件发给开发者，并说明问题是怎样出现的。</div></div>
    <div class="ws-foot"><span class="msg"></span><button class="btn" data-action="todo" data-v="打开所在文件夹">${icon('folder')} 打开所在文件夹</button><button class="btn primary" data-action="ov-close">完成</button></div></div>`;
  return `<div class="ov-panel" style="width:640px"><div class="ws-head"><h2>报告问题</h2><button class="btn ghost icon-only" data-action="ov-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div style="padding:16px 24px;display:flex;flex-direction:column;gap:14px">
      <div class="muted">导出一个故障复现包：里面有当前存档、最近的操作记录和日志，开发者可以用它重现你遇到的问题。</div>
      <div style="display:flex;gap:8px;align-items:center"><span class="pos">●</span>正在记录 · 已记录 42 分钟（从第 3 天 06:00 起）</div>
      <div class="notice warning">${icon('info')}<div class="grow">你在 42 分钟前读过一次档，记录从读档后重新开始，之前的操作不在包里。</div></div>
      <div class="field"><label for="issue-desc">发生了什么（可选）</label><textarea id="issue-desc" data-input="issue-desc" placeholder="例如：列车 2 在青川一直等待，不出发">${d.desc}</textarea></div>
      <div class="small muted">包里会有：当前存档、最近 42 分钟的操作记录、游戏日志、你写的描述。不包含其他个人文件。</div></div>
    <div class="ws-foot"><span class="msg"></span><button class="btn" data-action="ov-close">取消</button><button class="btn primary" data-action="issue-export">导出复现包…</button></div></div>`;
}

function renderOverlay() {
  const el = document.getElementById('overlay');
  el.innerHTML = !S.overlay ? '' : ({ saves: savesOverlay, settings: settingsOverlay, issue: issueOverlay })[S.overlay]();
}

function enterGame(msg) { W = makeWorld(); Object.assign(S, freshUiState(), { screen: 'game' }); if (msg) toast('complete', msg, null, false); MapView.fit(); }

Object.assign(INPUTS, {
  'ng-seed': v => { S.ng.seed = v; }, 'ng-name': v => { S.ng.name = v; }, 'ng-config': v => { S.ng.config = v; },
  'save-q': v => { S.saveQ = v; }, 'save-name': v => { S.saveName = v; },
  'set-scale': v => { SETTINGS.scale = +v; }, 'set-fps': v => { SETTINGS.fps = v; }, 'set-auto': v => { SETTINGS.autosave = v; },
  'issue-desc': v => { S.issue.desc = v; },
});
Object.assign(WS_ACTIONS, {
  'menu-continue': () => enterGame(`已读取「${SAVES[0].name}」`),
  'menu-new': () => { S.screen = 'newgame'; },
  'menu-quit': () => todo('退出游戏'),
  'menu-example': () => { S.overlay = null; enterGame('已从示例存档「起步贸易」开始'); },
  'ng-back': () => { S.screen = 'menu'; },
  'ng-random': () => { S.ng.seed = Math.floor(Math.random() * 99999); },
  'ng-start': () => {
    S.ng.creating = 0;
    const step = () => { if (S.ng.creating == null) return; S.ng.creating += 25; if (S.ng.creating >= 100) { enterGame(); S.paused = true; S.pauseReason = '新对局'; toast('info', '新对局已开始。游戏暂停中，按空格开始。', null, false); render(); return; } render(); setTimeout(step, 400); };
    setTimeout(step, 400);
  },
  'ng-cancel': () => { S.ng.creating = null; },
  'open-saves': () => { S.overlay = 'saves'; S.popover = null; },
  'open-settings': () => { S.overlay = 'settings'; S.popover = null; },
  'open-issue': () => { S.overlay = 'issue'; S.issue = { desc: '', done: null }; S.popover = null; },
  'ov-close': () => { S.overlay = null; S.capture = null; },
  'save-sel': v => { S.saveSel = v; },
  'save-new': () => { SAVES.unshift({ ...SAVES[0], id: 's' + Date.now(), name: S.saveName, saved: '刚刚', auto: false }); S.saveSel = SAVES[0].id; S.savedAgo = 0; toast('complete', `已保存「${S.saveName}」`, null, false); },
  'save-load': () => {
    const s = SAVES.find(x => x.id === S.saveSel), go = () => { S.overlay = null; enterGame(`已读取「${s.name}」`); };
    if (S.screen === 'game' && S.savedAgo) S.dialog = { title: `读取「${s.name}」？`, body: `当前对局有 ${S.savedAgo} 分钟的进度没有保存。`, confirm: '保存后读取', alt: '不保存，直接读取', onConfirm: () => go() };
    else go();
  },
  'save-del': () => { const s = SAVES.find(x => x.id === S.saveSel); S.dialog = { title: `删除「${s.name}」？`, body: '存档文件会被删除，不能恢复。', confirm: '删除', danger: true, onConfirm: () => { SAVES.splice(SAVES.indexOf(s), 1); S.saveSel = SAVES[0]?.id; } }; },
  'set-tab': v => { S.setTab = v; S.capture = null; S.keyNote = null; },
  set: v => {
    const [k, x] = v.split(':'), old = SETTINGS[k]; SETTINGS[k] = x;
    if (k === 'window' && x !== old) S.dialog = { title: '保留新的窗口模式？', body: `已切换到「${x}」。如果画面显示不正常，不用操作，10 秒后会自动恢复为「${old}」。`, confirm: '保留', cancel: `恢复为「${old}」`, onCancel: () => { SETTINGS.window = old; }, onConfirm: () => {} };
  },
  'set-toggle': v => { SETTINGS[v] = !SETTINGS[v]; },
  'set-notif': v => { const [e, x] = v.split(':'); SETTINGS.notif[e] = x; },
  'set-notif-reset': () => Object.keys(SETTINGS.notif).forEach(e => SETTINGS.notif[e] = e === '遇敌' ? 'slow' : 'none'),
  'key-capture': v => { S.capture = +v; S.keyNote = null; },
  'key-reset': () => { todo('恢复默认按键'); },
  'issue-export': () => { S.issue.done = 'D:\\Users\\玩家\\Documents\\Train\\问题报告\\train-问题-2026-10-31-2140.zip'; },
  'to-menu': () => { S.popover = null; const go = () => { S.screen = 'menu'; S.selection = null; S.drawer = null; S.workspace = null; }; if (S.savedAgo) S.dialog = { title: '返回主菜单？', body: `当前对局有 ${S.savedAgo} 分钟的进度没有保存。`, confirm: '保存后返回', alt: '不保存，直接返回', onConfirm: () => go() }; else go(); },
});

// 改键：下一次按键成为新的绑定；和已有绑定冲突时交换两者
document.addEventListener('keydown', e => {
  if (S.capture == null) return;
  e.preventDefault(); e.stopImmediatePropagation();
  if (e.key === 'Escape') { S.capture = null; return render(); }
  const name = (e.ctrlKey ? 'Ctrl ' : '') + (e.key === ' ' ? '空格' : e.key.length === 1 ? e.key.toUpperCase() : e.key);
  const keys = SETTINGS.keys, i = S.capture, j = keys.findIndex(([, k], n) => n !== i && k === name);
  S.keyNote = j >= 0 ? `「${name}」原来用于「${keys[j][0]}」，两者已交换。` : null;
  if (j >= 0) keys[j][1] = keys[i][1];
  keys[i][1] = name; S.capture = null; render();
}, true);
document.addEventListener('dblclick', e => {
  const el = e.target.closest('[data-dbl]'); if (!el) return;
  const fn = ACTIONS_MAP[el.dataset.dbl] || PANEL_ACTIONS[el.dataset.dbl] || WS_ACTIONS[el.dataset.dbl]; if (fn) { fn(el.dataset.v); render(); }
});
