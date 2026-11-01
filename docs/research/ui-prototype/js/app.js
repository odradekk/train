// 界面状态、外壳（顶栏、导航、提示条、菜单）、键盘和一个极简的运行模拟
let W = makeWorld();
let S = freshUiState();

function freshUiState() {
  return {
    ...shellState(), ...devState(), au: null, auNew: null, editor: null, nd: null, sv: null, contentLoading: null,
    paused: false, speed: 1, drawer: null, selection: null,
    tabs: { node: 'overview', train: 'overview', line: 'overview' },
    workspace: null, scene: null, popover: null, dialog: null, toasts: [],
    market: null, trade: { action: 'buy', qty: 10, train: null, done: null },
    buySel: new Set(), parkedSel: new Set(), query: '', filter: 'all', notifFilter: 'all',
    banner: null, fundsDelta: null, building: null, lease: { cap: 200, days: 7 }, foundType: '农业镇',
  };
}

const fmt = n => Math.round(n).toLocaleString('zh-CN');
const money = n => (n < 0 ? '−' : '') + fmt(Math.abs(n));
const nodeName = id => W.nodes[id].name;
const lineName = id => `${nodeName(W.lines[id].a)} — ${nodeName(W.lines[id].b)}`;
const clock = () => `第 ${W.day} 天 ${String(Math.floor(W.minute / 60)).padStart(2, '0')}:${String(W.minute % 60).padStart(2, '0')}`;
const typing = () => ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName);

function trainStats(vids) {
  const vs = vids.map(v => VEHICLE_TYPES[W.vehicles[v].type]);
  return {
    count: vs.length, length: vs.reduce((a, v) => a + v.length, 0), mass: vs.reduce((a, v) => a + v.mass, 0),
    traction: vs.reduce((a, v) => a + v.traction, 0), capacity: vs.reduce((a, v) => a + v.capacity, 0), locos: vs.filter(v => v.traction > 0).length,
  };
}
function cargoTotal(t) { return Object.values(t.cargo).reduce((a, b) => a + b, 0); }
function trainWhere(id) {
  const t = W.trains[id];
  if (t.status === 'combat') return `在 ${lineName(t.line)} 战斗`;
  if (t.status === 'moving') return `前往 ${nodeName(t.to)} · ${Math.round(t.progress * 100)}%`;
  if (t.status === 'loading') return `在 ${nodeName(t.at)} 装卸`;
  if (!t.plan) return `停在 ${nodeName(t.at)} · 没有运行计划`;
  return `停在 ${nodeName(t.at)}`;
}

// ---------- 渲染 ----------
function render() {
  const a = document.activeElement, keep = a?.id ? { id: a.id, s: a.selectionStart, e: a.selectionEnd } : null;
  const game = S.screen === 'game';
  document.getElementById('game').classList.toggle('hidden', !game);
  document.getElementById('screen').innerHTML = S.screen === 'menu' ? menuScreen() : S.screen === 'newgame' ? newGameScreen() : S.screen === 'editor' ? editorScreen() : S.screen === 'authoring' ? authoringScreen() + authoringWriteOverlay() : '';
  if (S.screen === 'editor') renderEditorView();
  if (game) { renderTopbar(); renderRail(); renderDrawer(); renderInspector(); renderWorkspace(); renderStage(); }
  renderDock(); renderOverlay(); renderDialog(); renderPopovers(); renderToasts();
  if (keep) { const el = document.getElementById(keep.id); if (el) { el.focus(); try { el.setSelectionRange(keep.s, keep.e); } catch { } } }
}

function renderTopbar() {
  const unread = W.notifications.filter(n => n.unread).length;
  const speeds = [1, 2, 4, 8].map((s, i) => `<button class="${!S.paused && S.speed === s ? 'on' : ''}" data-action="speed" data-v="${s}" title="${s}× 速度（${i + 1}）">${s}×</button>`).join('');
  const crumb = S.scene ? `<div class="crumb"><button class="btn ghost" data-action="exit-scene" title="返回宏观地图（Esc）">${icon('back')} 宏观地图</button><span>›</span><b>${sceneTitle()}</b></div>` : '';
  const pauseReason = S.paused && S.pauseReason ? `<span class="chip warning">${icon('pause')}${S.pauseReason}</span>` : '';
  document.getElementById('topbar').innerHTML = `
    <div class="group">
      <button class="btn icon-only ${S.paused ? 'primary' : ''}" data-action="toggle-pause" title="${S.paused ? '继续' : '暂停'}（空格）">${icon(S.paused ? 'play' : 'pause')}</button>
      <div class="seg">${speeds}</div>
      <span class="clock">${clock()}</span>${pauseReason}
    </div>
    <div class="spacer"></div>${crumb}<div class="spacer"></div>
    <div class="group">
      <span class="funds" title="玩家资金">${icon('coin')} ${fmt(W.funds)}${S.fundsDelta ? `<span class="delta ${S.fundsDelta > 0 ? 'pos' : 'neg'}">${S.fundsDelta > 0 ? '+' : ''}${money(S.fundsDelta)}</span>` : ''}</span>
      <button class="btn ghost icon-only rel" data-action="pop" data-v="notif" title="通知">${icon('bell')}${unread ? `<span class="badge">${unread}</span>` : ''}</button>
      <button class="btn ghost" data-action="pop" data-v="menu" title="菜单（Esc）">${icon('menu')} 菜单</button>
    </div>`;
}

const RAIL = [
  ['trains', 'train', '车队', 'T'], ['lines', 'route', '线路', 'L'], ['settlements', 'city', '聚居地', 'J'],
  ['reports', 'report', '报告', 'R'], ['designs', 'blueprint', '设计', 'K'], ['combat', 'sword', '战斗', 'B'],
];
function renderRail() {
  const warnTrains = Object.values(W.trains).filter(t => !t.plan || t.status === 'blocked').length;
  const breaks = Object.values(W.lines).reduce((a, l) => a + l.breaks, 0);
  const badge = { trains: warnTrains, lines: breaks };
  document.getElementById('rail').innerHTML = RAIL.map(([k, ic, label, key]) =>
    `<button class="rail-item ${S.drawer === k || (k === 'reports' && S.workspace?.kind === 'report') || (k === 'designs' && S.workspace?.kind === 'library') ? 'on' : ''}" data-action="drawer" data-v="${k}" title="${label}（${key}）">${icon(ic, 'lg')}<span>${label}</span>${badge[k] ? `<span class="badge warn">${badge[k]}</span>` : ''}</button>`).join('');
}

function renderStage() {
  document.getElementById('map').classList.toggle('hidden', !!S.scene);
  const sc = document.getElementById('scene');
  sc.classList.toggle('hidden', !S.scene);
  if (S.scene) sc.innerHTML = `<div class="scene-label">${sceneTitle()}<small>3D 场景占位：右键拖动环绕，中键或 WASD 平移，滚轮缩放</small></div>
    ${S.scene.kind === 'node' ? `<div id="minimap"><span class="title">节点小地图</span></div>` : ''}`;
  document.getElementById('map-tools').classList.toggle('hidden', !!S.scene);
  document.getElementById('map-tools').style.left = `calc(var(--rail-w) + ${S.drawer ? 340 : 0}px + 16px)`;
  MapView.render();
}
function sceneTitle() {
  const s = S.scene; if (!s) return '';
  if (s.kind === 'node') return nodeName(s.id);
  if (s.kind === 'line') return lineName(s.id);
  if (s.kind === 'battle') return `战斗 · ${battleName(W.battles[s.id])}`;
  return `${W.trains[s.id].name}（跟随中）`;
}

function renderPopovers() {
  const host = document.getElementById('popovers');
  if (!S.popover) { host.innerHTML = ''; return; }
  if (S.popover === 'menu') {
    host.innerHTML = `<div class="popover menu" style="top:48px;right:12px">
      <button data-action="save">${icon('save')}<span class="grow">保存</span><span class="kbd">Ctrl S</span></button>
      <button data-action="open-saves">${icon('folder')}<span class="grow">存档与读档…</span></button>
      <button data-action="open-settings">${icon('gear')}<span class="grow">设置…</span></button>
      <button data-action="open-issue">${icon('flag')}<span class="grow">报告问题…</span></button>
      <hr><button data-action="to-menu">${icon('exit')}<span class="grow">返回主菜单…</span></button>
      <button data-action="menu-quit">${icon('close')}<span class="grow">退出游戏…</span></button>
      ${S.release ? '' : `<hr><div class="small faint" style="padding:4px 12px">开发构建</div><button data-action="dock-open">${icon('report')}<span class="grow">开发工具</span><span class="kbd">\`</span></button><button data-action="open-authoring">${icon('blueprint')}<span class="grow">创作模式…</span></button>`}</div>`;
  } else if (S.popover === 'notif') {
    const f = S.notifFilter, list = W.notifications.filter(n => f === 'all' || n.lvl === f);
    const LV = { info: ['info', 'info'], complete: ['complete', 'check'], warning: ['warning', 'alert'], critical: ['critical', 'alert'] };
    host.innerHTML = `<div class="popover" id="notif-center" style="top:48px;right:96px">
      <div class="panel-head"><h3>通知</h3><button class="btn ghost small" data-action="notif-read-all">全部标为已读</button></div>
      <div class="filters">${[['all', '全部'], ['critical', '严重'], ['warning', '警告'], ['complete', '完成'], ['info', '信息']].map(([k, l]) => `<button class="${f === k ? 'on' : ''}" data-action="notif-filter" data-v="${k}">${l}</button>`).join('')}</div>
      <div class="scroll">${list.length ? list.map(n => `<div class="notif ${n.unread ? 'unread' : ''}" data-action="notif-open" data-v="${n.id}">
        <span class="lvl ${LV[n.lvl][0]}">${icon(LV[n.lvl][1], 'sm')}</span>
        <div class="grow"><div class="text">${n.text}</div><div class="when">${n.when}${n.target ? ' · 点击查看' : ''}</div></div></div>`).join('') : '<div class="empty">没有这一类通知。</div>'}</div>
      <div class="insp-foot"><button class="btn ghost" data-action="todo" data-v="通知设置">${icon('gear')} 通知设置…</button></div></div>`;
  } else if (S.popover === 'layers') {
    const L = MapView.layers;
    const row = (k, label, legend) => `<label class="switch ${L[k] ? 'on' : ''}" data-action="layer" data-v="${k}"><i></i>${label}</label><div class="legend" style="margin:4px 0 10px 42px">${legend}</div>`;
    host.innerHTML = `<div class="popover" id="layers" style="left:calc(var(--rail-w) + ${S.drawer ? 340 : 0}px + 16px)">
      ${row('resources', '节点资源', '<span>空节点下方列出可开采资源</span>')}
      ${row('platforms', '站台占用', '<span>聚居地下方显示 在站列车/站台数</span>')}
      ${row('creatures', '世界生物', '<span><i style="background:#c9423a55;border:1px dashed #c9423a"></i>生物聚落活动范围</span>')}
      <div class="legend" style="border-top:1px solid var(--divider);padding-top:10px">
        <span><i style="background:#2b77d0;border-radius:3px"></i>自有聚居地</span><span><i style="background:#d7d2c4;border-radius:3px"></i>非敌对聚居地</span>
        <span><i style="background:#c9423a;border-radius:3px"></i>敌对聚居地</span><span><i style="border:2px dashed #e8e4d6"></i>空节点</span>
        <span><i style="background:#2b2f33;border-radius:0;height:4px"></i>线路（线条数 = 铁轨数，虚线为未建成）</span></div></div>`;
  } else if (S.popover === 'proto') {
    host.innerHTML = '';
  }
  const pm = document.querySelector('#proto .menu'); if (pm) pm.classList.toggle('hidden', S.popover !== 'proto');
}

function renderToasts() {
  const LV = { info: 'info', complete: 'check', warning: 'alert', critical: 'alert' };
  document.getElementById('toasts').innerHTML = S.toasts.map(t => `<div class="toast ${t.lvl}" data-action="toast-open" data-v="${t.id}">
    ${icon(LV[t.lvl])}<div class="grow">${t.text}${t.target ? '<div class="small faint">点击查看</div>' : ''}</div>
    <button class="btn ghost icon-only" data-action="toast-close" data-v="${t.id}" title="关闭">${icon('close', 'sm')}</button></div>`).join('');
}

// ---------- 动作 ----------
let toastSeq = 100;
function toast(lvl, text, target = null, log = true) {
  const id = ++toastSeq;
  S.toasts.push({ id, lvl, text, target });
  if (S.toasts.length > 3) S.toasts.shift();
  if (log) W.notifications.unshift({ id, lvl, text, when: clock(), target, unread: false });
  if (lvl !== 'critical') setTimeout(() => { S.toasts = S.toasts.filter(t => t.id !== id); renderToasts(); }, 6000);
  renderToasts();
}
function spend(amount) {
  W.funds -= amount; S.fundsDelta = -amount;
  clearTimeout(spend.t); spend.t = setTimeout(() => { S.fundsDelta = null; renderTopbar(); }, 2500);
}
function select(sel) {
  S.selection = sel; S.market = null; S.trade.done = null; S.buySel.clear(); S.parkedSel.clear(); S.banner = null;
  S.popover = null; render();
}
function enterScene(sel) {
  if (sel.kind === 'node' && W.nodes[sel.id].kind === 'lair') { toast('info', '生物聚落没有可进入的场景', null, false); return; }
  S.scene = sel; S.popover = null; hideTip(); render();
}
function exitScene() { S.scene = null; render(); MapView.render(); }
function todo(what) { toast('info', `原型后续轮次补全：${what}`, null, false); }

function closeTop() {
  if (S.dialog) { const d = S.dialog; S.dialog = null; d.onCancel?.(); return render(); }
  if (S.popover) { S.popover = null; return render(); }
  if (S.overlay) { S.overlay = null; S.capture = null; return render(); }
  if (S.screen === 'newgame') { S.screen = 'menu'; return render(); }
  if (S.screen !== 'game') return;
  if (S.workspace) { S.workspace = null; return render(); }
  if (S.selection) { return select(null); }
  if (S.drawer) { S.drawer = null; return render(); }
  if (S.scene) return exitScene();
  S.popover = 'menu'; render();
}

const ACTIONS_MAP = {
  'toggle-pause': () => { S.paused = !S.paused; S.pauseReason = null; },
  speed: v => { S.speed = +v; S.paused = false; S.pauseReason = null; },
  drawer: v => { if (v === 'designs') { S.workspace?.kind === 'library' ? S.workspace = null : openLibrary(); return; } if (v === 'reports') { S.workspace?.kind === 'report' ? S.workspace = null : openReport(); return; } S.drawer = S.drawer === v ? null : v; S.query = ''; S.filter = 'all'; },
  pop: v => { S.popover = S.popover === v ? null : v; },
  'exit-scene': () => exitScene(),
  save: () => { S.popover = null; S.savedAgo = 0; toast('complete', `已保存「${SAVES[0].name}」`, null, false); },
  todo: v => { S.popover = null; todo(v); },
  layer: v => { MapView.layers[v] = !MapView.layers[v]; },
  'map-zoom': v => MapView.zoom(v === 'in' ? 1.25 : 0.8),
  'map-fit': () => MapView.fit(),
  'notif-filter': v => { S.notifFilter = v; },
  'notif-read-all': () => W.notifications.forEach(n => n.unread = false),
  'notif-open': v => { const n = W.notifications.find(n => n.id === +v); n.unread = false; if (n.target) { S.popover = null; select(n.target); } },
  'toast-open': v => { const t = S.toasts.find(t => t.id === +v); if (t?.target) select(t.target); },
  'toast-close': v => { S.toasts = S.toasts.filter(t => t.id !== +v); },
  'proto-reset': () => { W = makeWorld(); S = freshUiState(); MapView.fit(); },
  'proto-toast': () => toast('warning', '白石镇 谷物 低于补货目标', { kind: 'node', id: 'n2' }),
  'proto-poor': () => { W.funds = 800; },
  'proto-release': () => { S.release = !S.release; S.dockTab = 'console'; toast('info', S.release ? '现在按玩家构建显示：开发工具只剩控制台' : '现在按开发构建显示', null, false); },
  'dock-open': () => { S.dockOpen = true; S.popover = null; },
  'proto-battle': () => { if (!W.battles.k1) startBattle(); select({ kind: 'battle', id: 'k1' }); return false; },
  'proto-surrender': () => { Object.assign(W.nodes.n6, { surrendered: true }); W.nodes.n6.defense.morale = 0; select({ kind: 'node', id: 'n6' }); return false; },
  'proto-rich': () => { W.funds = 12400; },
};

document.addEventListener('click', e => {
  const el = e.target.closest('[data-action]'); if (!el) {
    if (S.popover && !e.target.closest('.popover') && !e.target.closest('#proto')) { S.popover = null; render(); }
    return;
  }
  if (el.closest('#map svg') && !el.dataset.action.startsWith('plan')) return;
  const fn = ACTIONS_MAP[el.dataset.action] || PANEL_ACTIONS[el.dataset.action] || WS_ACTIONS[el.dataset.action];
  if (!fn) return;
  e.stopPropagation(); e.preventDefault();
  if (S.popover && !el.closest('.popover') && el.dataset.action !== 'pop' && el.dataset.action !== 'proto') S.popover = null;
  const r = fn(el.dataset.v, el, e);
  if (r !== false) render();
});
document.addEventListener('input', e => {
  const el = e.target.closest('[data-input]'); if (!el) return;
  const fn = INPUTS[el.dataset.input]; if (fn) { fn(el.value, el); render(); }
});
document.addEventListener('change', e => {
  const el = e.target.closest('[data-change]'); if (!el) return;
  const fn = INPUTS[el.dataset.change]; if (fn) { fn(el.value, el); render(); }
});

document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && document.activeElement?.dataset.enter) { WS_ACTIONS[document.activeElement.dataset.enter](); render(); return; }
  if (S.screen === 'editor' && !S.dialog && !S.overlay && !S.popover && (!typing() || e.key === 'Escape')) { if (editorKey(e) !== false) { e.preventDefault(); render(); } return; }
  if (e.key === 'Escape') { e.preventDefault(); if (typing()) document.activeElement.blur(); closeTop(); return; }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); ACTIONS_MAP.save(); render(); return; }
  if (typing() || S.dialog || S.screen !== 'game' || S.overlay) return;
  const k = e.key.toLowerCase();
  if (k === ' ') { e.preventDefault(); ACTIONS_MAP['toggle-pause'](); render(); return; }
  if (['1', '2', '3', '4'].includes(k)) { ACTIONS_MAP.speed([1, 2, 4, 8][+k - 1]); render(); return; }
  if (S.workspace) return;
  const r = RAIL.find(r => r[3].toLowerCase() === k); if (r) { ACTIONS_MAP.drawer(r[0]); render(); return; }
  if (k === 'f' && S.selection) { MapView.centerOn(S.selection); return; }
  if (k === 'v' && S.selection) { enterScene(S.selection); return; }
  if (['w', 'a', 's', 'd'].includes(k) && !S.scene) {
    const d = 60; MapView.view.x += k === 'a' ? d : k === 'd' ? -d : 0; MapView.view.y += k === 'w' ? d : k === 's' ? -d : 0; MapView.render();
  }
});

// ---------- 极简运行模拟：只为让地图和检查器“动起来” ----------
function nextStop(t) { return t.plan.stops[(t.stopIndex + 1) % t.plan.stops.length]; }
function departTo(t, targetNode) {
  const path = findPath(t.at, targetNode);
  if (!path || !path.length) { t.status = 'blocked'; return; }
  const l = W.lines[path[0]], nxt = l.a === t.at ? l.b : l.a;
  Object.assign(t, { line: path[0], from: t.at, to: nxt, progress: 0, status: 'moving', target: targetNode }); delete t.at;
}
function applyRules(id, t, stop) {
  const n = W.nodes[stop.node];
  for (const r of stop.rules) {
    const have = t.cargo[r.good] || 0, cap = trainStats(t.vehicles).capacity - cargoTotal(t);
    const price = n.market?.[r.good];
    if ((r.action === 'buy' || r.action === 'load') && have < r.target) {
      const q = Math.min(r.target - have, cap); if (q <= 0) continue;
      t.cargo[r.good] = have + q; if (r.action === 'buy' && price?.[1]) { W.funds -= q * price[1]; t.lapCash = (t.lapCash || 0) - q * price[1]; }
    } else if ((r.action === 'sell' || r.action === 'unload') && have > r.target) {
      const q = have - r.target; t.cargo[r.good] = r.target; if (!t.cargo[r.good]) delete t.cargo[r.good];
      if (r.action === 'sell' && price?.[2]) { W.funds += q * price[2]; t.lapCash = (t.lapCash || 0) + q * price[2]; }
    }
  }
}
function tick() {
  if (S.paused || S.screen !== 'game' || S.overlay) return;
  combatTick(); workTick();
  W.minute += 2 * S.speed; if (W.minute >= 1440) { W.minute -= 1440; W.day++; }
  for (const [id, t] of Object.entries(W.trains)) {
    if (!t.running || !t.plan) continue;
    if (t.status === 'moving') {
      t.progress += 0.03 * S.speed * 42 / W.lines[t.line].length;
      if (t.progress >= 1) {
        t.at = t.to; delete t.line; delete t.from; delete t.to;
        if (t.at === t.target || !t.target) {
          t.stopIndex = (t.stopIndex + 1) % t.plan.stops.length;
          const stop = t.plan.stops[t.stopIndex];
          if (stop.pass) { departTo(t, nextStop(t).node); continue; }
          t.status = 'loading'; t.dwellLeft = 6; applyRules(id, t, stop);
          if (t.stopIndex === 0 && t.plan.loop) {
            const lap = (t.report?.lap || 0) + 1, cash = t.lapCash || 0;
            t.report = { lap, cash, load: Math.min(1, cargoTotal(t) / Math.max(1, trainStats(t.vehicles).capacity) + .3), issue: null }; t.lapCash = 0;
            toast('complete', `${t.name} 完成第 ${lap} 圈，净现金流 ${cash >= 0 ? '+' : ''}${money(cash)}`, { kind: 'train', id });
          }
        } else departTo(t, t.target);
      }
    } else if (t.status === 'loading') {
      t.dwellLeft -= S.speed;
      if (t.dwellLeft <= 0) {
        if (!t.plan.loop && t.stopIndex === t.plan.stops.length - 1) { t.running = false; t.status = 'idle'; toast('complete', `${t.name} 完成运行计划，停在 ${nodeName(t.at)}`, { kind: 'train', id }); continue; }
        departTo(t, nextStop(t).node);
      }
    } else if (t.status === 'idle' && t.running) departTo(t, nextStop(t).node);
  }
  renderTopbar();
  if (!S.scene) MapView.render();
  if (S.selection && !document.querySelector('#inspector :focus')) renderInspector();
  if (S.drawer === 'trains' && !typing()) renderDrawer();
}

window.addEventListener('load', () => {
  MapView.bind(); if (!location.hash) S.screen = 'menu'; render(); MapView.fit();
  setInterval(tick, 500);
  window.addEventListener('resize', () => MapView.render());
});
