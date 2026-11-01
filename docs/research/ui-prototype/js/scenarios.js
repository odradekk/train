// 场景直达：地址栏 #名称 直接打开某个界面状态，供截图和逐屏查看
const SCENARIOS = {
  start: ['新局：宏观地图', () => {}],
  settlement: ['聚居地检查器（非敌对）', () => select({ kind: 'node', id: 'n1' })],
  market: ['市场与买卖', () => { select({ kind: 'node', id: 'n1' }); S.tabs.node = 'goods'; S.market = 'grain'; }],
  vehicles: ['在售现车', () => { select({ kind: 'node', id: 'n1' }); S.tabs.node = 'vehicles'; ['v10', 'v11', 'v12'].forEach(v => S.buySel.add(v)); }],
  'buy-dialog': ['购买确认', () => { SCENARIOS.vehicles[1](); PANEL_ACTIONS['buy-vehicles'](); }],
  formation: ['编组列车', () => { SCENARIOS.vehicles[1](); buyVehicles(['v10', 'v11', 'v12'], 4505, true); S.toasts = []; }],
  'plan-new': ['运行计划：新列车', () => { SCENARIOS.formation[1](); WS_ACTIONS['form-go']('1'); S.toasts = []; addStop('n2', false); WS_ACTIONS['rule-add'](); }],
  'plan-edit': ['运行计划：已有计划', () => { select({ kind: 'train', id: 't1' }); openPlan('t1'); S.workspace.sel = 1; }],
  'plan-error': ['运行计划：有问题', () => { select({ kind: 'train', id: 't3' }); openPlan('t3'); addStop('n8', false); addStop('n2', false); S.workspace.draft.stops[2].rules.push({ good: 'grain', action: 'buy', target: 40, limit: null }); S.toasts = []; }],
  train: ['列车检查器', () => select({ kind: 'train', id: 't1' })],
  'train-noplan': ['列车检查器：没有计划', () => select({ kind: 'train', id: 't3' })],
  'train-plan-tab': ['列车检查器：计划页', () => { select({ kind: 'train', id: 't1' }); S.tabs.train = 'plan'; }],
  line: ['线路检查器：断点', () => select({ kind: 'line', id: 'l3' })],
  owned: ['自有聚居地', () => select({ kind: 'node', id: 'n2' })],
  empty: ['空节点', () => select({ kind: 'node', id: 'n7' })],
  'trains-drawer': ['车队列表', () => { S.drawer = 'trains'; select({ kind: 'train', id: 't1' }); }],
  'settlements-drawer': ['聚居地列表', () => { S.drawer = 'settlements'; }],
  notif: ['通知中心', () => { S.popover = 'notif'; }],
  layers: ['图层与图例', () => { MapView.layers.platforms = true; S.popover = 'layers'; }],
  scene: ['节点场景', () => { select({ kind: 'node', id: 'n1' }); S.scene = { kind: 'node', id: 'n1' }; }],
  war: ['宣战确认', () => { select({ kind: 'node', id: 'n1' }); PANEL_ACTIONS['declare-war'](); }],
  toast: ['提示条', () => { toast('warning', '白石镇 谷物 低于补货目标', { kind: 'node', id: 'n2' }); toast('complete', '列车 2 完成第 6 圈，净现金流 +230', { kind: 'train', id: 't2' }); }],
};

function runScenario() {
  const name = decodeURIComponent(location.hash.slice(1));
  if (!SCENARIOS[name]) return;
  W = makeWorld(); S = freshUiState(); S.paused = true;
  SCENARIOS[name][1]();
  render(); MapView.fit();
}
window.addEventListener('hashchange', runScenario);

function scenarioMenu() {
  return Object.entries(SCENARIOS).filter(([k]) => !k.startsWith('_')).map(([k, [label]]) => `<button onclick="location.hash='${k}'">${label}</button>`).join('');
}

// 第 2 轮
const parkAt = (tid, node, cargo) => { const t = W.trains[tid]; Object.assign(t, { at: node, status: 'idle', running: false, cargo: cargo ?? t.cargo }); delete t.line; delete t.from; delete t.to; };
Object.assign(SCENARIOS, {
  'owned': ['自有聚居地：概况', () => select({ kind: 'node', id: 'n2' })],
  'owned-stock': ['自有聚居地：库存与装卸', () => { parkAt('t1', 'n2'); select({ kind: 'node', id: 'n2' }); S.tabs.node = 'goods'; S.market = 'grain'; S.trade.action = 'load'; }],
  'buildings': ['自有聚居地：建筑与生产', () => { select({ kind: 'node', id: 'n2' }); S.tabs.node = 'buildings'; S.building = 'b2'; }],
  'build': ['建造建筑', () => { select({ kind: 'node', id: 'n2' }); S.tabs.node = 'buildings'; openCommission('n2', 'building', 'p4'); }],
  'commission': ['委托制造', () => { select({ kind: 'node', id: 'n1' }); S.tabs.node = 'vehicles'; W.trains.t3.cargo = { machinery: 4 }; openCommission('n1', 'vehicle'); S.workspace.bp = 'bp-wagonM'; }],
  'commission-short': ['委托制造：材料不够', () => { select({ kind: 'node', id: 'n1' }); S.tabs.node = 'vehicles'; openCommission('n1', 'vehicle'); }],
  'parked': ['停放车辆：修理、出售、拆解', () => { W.vehicles.v20 = { type: 'wagonM', hp: 0.6 }; W.vehicles.v21 = { type: 'locoS', hp: 0.8 }; W.parked.n1 = ['v20', 'v21']; select({ kind: 'node', id: 'n1' }); S.tabs.node = 'vehicles'; S.parkedSel = new Set(['v20', 'v21']); }],
  'lease': ['租用仓库：已有租约', () => { select({ kind: 'node', id: 'n4' }); S.tabs.node = 'lease'; }],
  'lease-new': ['租用仓库：新租', () => { select({ kind: 'node', id: 'n1' }); S.tabs.node = 'lease'; }],
  'market-lease': ['市场：仓库与列车装卸', () => { parkAt('t3', 'n4'); select({ kind: 'node', id: 'n4' }); S.tabs.node = 'goods'; S.market = 'electronics'; S.trade.action = 'load'; }],
  'train-service': ['列车：在聚居地修理与调整编组', () => { W.vehicles.v6.hp = 0.7; select({ kind: 'train', id: 't3' }); S.tabs.train = 'vehicles'; }],
  'reform': ['调整编组', () => { W.parked.n1 = ['v11']; W.vehicles.v11.sale = false; select({ kind: 'train', id: 't3' }); openFormation('n1', W.trains.t3.vehicles, 't3'); }],
  'found': ['建立聚居地：条件不足', () => { parkAt('t3', 'n7', { concrete: 40, machinery: 10, grain: 20 }); select({ kind: 'node', id: 'n7' }); }],
  'found-ready': ['建立聚居地：可以建立', () => { parkAt('t3', 'n7', { concrete: 40, machinery: 20, grain: 20 }); select({ kind: 'node', id: 'n7' }); }],
  'battle': ['战斗', () => { startBattle(); select({ kind: 'battle', id: 'k1' }); W.battles.k1.focus = 'u1'; }],
  'train-rules': ['列车交战规则', () => { select({ kind: 'train', id: 't1' }); S.tabs.train = 'rules'; }],
  'hostile': ['敌对聚居地', () => select({ kind: 'node', id: 'n6' })],
  'surrender': ['敌对聚居地：已投降', () => { W.nodes.n6.surrendered = true; W.nodes.n6.defense.morale = 0; select({ kind: 'node', id: 'n6' }); }],
  'report': ['经营报告', () => openReport('t1')],
  'report-ledger': ['经营报告：收支明细', () => { openReport(); S.workspace.tab = 'ledger'; }],
});

// 自检：依次执行第 2 轮的操作并渲染，任何异常都会触发页面顶部的错误条。不在菜单里显示。
SCENARIOS._selftest = ['自检', () => {
  const step = f => { f(); render(); };
  step(() => { select({ kind: 'node', id: 'n1' }); openCommission('n1', 'vehicle'); S.workspace.bp = 'bp-wagonM'; WS_ACTIONS['cm-go'](); });
  step(() => { select({ kind: 'node', id: 'n2' }); openCommission('n2', 'building', 'p4'); S.workspace.bp = 'bb-farm'; WS_ACTIONS['cm-go'](); S.tabs.node = 'buildings'; });
  step(() => { PANEL_ACTIONS['open-building']('b1'); PANEL_ACTIONS['building-priority']('b2:high'); PANEL_ACTIONS['building-toggle']('b1'); });
  step(() => { select({ kind: 'node', id: 'n1' }); S.tabs.node = 'lease'; S.lease = { cap: 100, days: 7 }; PANEL_ACTIONS['lease-new'](); PANEL_ACTIONS['lease-renew'](); });
  step(() => { parkAt('t3', 'n4'); select({ kind: 'node', id: 'n4' }); S.tabs.node = 'goods'; S.market = 'electronics'; S.trade.action = 'load'; S.trade.train = 't3'; PANEL_ACTIONS['transfer-go']('electronics:lease'); });
  step(() => { parkAt('t3', 'n7', { concrete: 40, machinery: 20, grain: 20 }); select({ kind: 'node', id: 'n7' }); PANEL_ACTIONS.found(); WS_ACTIONS['dlg-ok'](); });
  step(() => { startBattle(); select({ kind: 'battle', id: 'k1' }); PANEL_ACTIONS['battle-focus']('u1'); PANEL_ACTIONS['battle-retreat']('t2'); S.paused = false; for (let i = 0; i < 14; i++) tick(); S.paused = true; });
  step(() => { parkAt('t1', 'n1'); W.vehicles.v30 = { type: 'wagonS', hp: 0.5 }; W.parked.n1 = ['v30']; select({ kind: 'train', id: 't1' }); S.tabs.train = 'vehicles'; PANEL_ACTIONS.reform(); S.workspace.order.push('v30'); WS_ACTIONS['form-go']('0'); });
  step(() => { PANEL_ACTIONS['repair-train'](); PANEL_ACTIONS.disband(); WS_ACTIONS['dlg-ok'](); });
  step(() => { S.parkedSel = new Set(W.parked.n1); PANEL_ACTIONS['repair-parked'](); S.parkedSel = new Set(W.parked.n1); PANEL_ACTIONS['sell-parked'](); WS_ACTIONS['dlg-ok'](); });
  step(() => { W.nodes.n6.surrendered = true; select({ kind: 'node', id: 'n6' }); PANEL_ACTIONS.occupy(); WS_ACTIONS['dlg-ok'](); select({ kind: 'node', id: 'n6' }); ['overview', 'goods', 'buildings', 'vehicles'].forEach(t => { S.tabs.node = t; render(); }); });
  step(() => { openReport('t1'); ['settlements', 'ledger', 'trains'].forEach(t => { WS_ACTIONS['rp-tab'](t); render(); }); });
  step(() => { S.workspace = null; select({ kind: 'train', id: 't2' }); ['overview', 'plan', 'cargo', 'vehicles', 'rules'].forEach(t => { S.tabs.train = t; render(); }); PANEL_ACTIONS['rule-set']('engage:retreat'); });
  const ok = document.createElement('div'); ok.textContent = '自检通过'; document.body.appendChild(ok);
}];

// 第 3 轮
const edScene = (o = {}) => { openEditor({ name: '加长货车 改', lib: 'local', from: 'library', ...o }); return S.editor; };
Object.assign(SCENARIOS, {
  'menu': ['主菜单', () => { S.screen = 'menu'; }],
  'menu-loading': ['主菜单：内容加载中', () => { S.screen = 'menu'; S.contentLoading = 64; }],
  'newgame': ['新游戏', () => { S.screen = 'newgame'; }],
  'newgame-missing': ['新游戏：内容包缺失', () => { S.screen = 'newgame'; S.ng.missing = true; }],
  'newgame-creating': ['新游戏：正在生成', () => { S.screen = 'newgame'; S.ng.creating = 50; }],
  'saves-menu': ['读取存档（主菜单）', () => { S.screen = 'menu'; S.overlay = 'saves'; }],
  'saves': ['存档与读档（游戏中）', () => { S.overlay = 'saves'; S.saveSel = 's4'; }],
  'settings': ['设置：显示', () => { S.overlay = 'settings'; }],
  'settings-notif': ['设置：通知', () => { S.overlay = 'settings'; S.setTab = 'notif'; }],
  'settings-keys': ['设置：按键', () => { S.overlay = 'settings'; S.setTab = 'keys'; S.capture = 6; }],
  'issue': ['报告问题', () => { S.overlay = 'issue'; }],
  'issue-done': ['报告问题：已导出', () => { S.overlay = 'issue'; WS_ACTIONS['issue-export'](); }],
  'library': ['蓝图库', () => openLibrary()],
  'library-pack': ['蓝图库：内容包', () => { openLibrary(); S.workspace.lib = 'pack'; S.workspace.sel = 'bp-locoM'; }],
  'new-design': ['新建设计', () => { openLibrary(); WS_ACTIONS['nd-open'](); }],
  'editor': ['设计编辑器', () => { const E = edScene(); E.hover = { c: [0, 2, 3], n: [0, 0, 1] }; }],
  'editor-box': ['编辑器：长方体', () => { const E = edScene(); E.tool = 'box'; E.boxStart = [2, 2, 2]; E.hover = { c: [9, 3, 1], n: [0, 0, 1] }; }],
  'editor-select': ['编辑器：选中设备', () => { const E = edScene(); E.tool = 'select'; E.sel = new Set(['e1']); E.panel = 'structure'; }],
  'editor-problems': ['编辑器：有问题', () => { const E = edScene(); [...E.vox.keys()].filter(k => E.vox.get(k).p === 'e2').forEach(k => E.vox.delete(k)); E.dirty = true; E.panel = 'stats'; }],
  'editor-save': ['编辑器：保存蓝图', () => { const E = edScene(); E.dirty = true; WS_ACTIONS['ed-save'](); }],
  'editor-exit': ['编辑器：退出时有未保存修改', () => { const E = edScene(); E.dirty = true; WS_ACTIONS['ed-exit'](); }],
  'train-refit': ['列车：改装入口', () => { select({ kind: 'train', id: 't3' }); S.tabs.train = 'vehicles'; }],
  'refit': ['编辑器：改装', () => { select({ kind: 'train', id: 't3' }); WS_ACTIONS.refit('v7'); const E = S.editor; for (let x = 0; x < 16; x++) for (const y of [1, 4]) E.vox.set(key(x, y, 4), { d: 'plate', c: '#b8743f', p: 'body' }); E.dirty = true; }],
  'refit-commission': ['委托改装', () => { SCENARIOS.refit[1](); WS_ACTIONS['ed-refit-done'](); }],
});
SCENARIOS._selftest3 = ['自检 3', () => {
  let n = 0; const step = f => { n++; try { f(); render(); } catch (e) { const d = document.createElement('div'); d.textContent = `STEPERR ${n}: ${e.message} | ${(e.stack || '').split('\n').slice(1, 3).join(' ')}`; document.body.appendChild(d); throw e; } };
  step(() => { S.screen = 'menu'; WS_ACTIONS['menu-new'](); WS_ACTIONS['ng-random'](); WS_ACTIONS['ng-back'](); WS_ACTIONS['open-saves'](); WS_ACTIONS['save-sel']('s3'); WS_ACTIONS['save-load'](); });
  step(() => { WS_ACTIONS['open-saves'](); WS_ACTIONS['save-new'](); WS_ACTIONS['save-del'](); WS_ACTIONS['dlg-ok'](); WS_ACTIONS['ov-close'](); });
  step(() => { WS_ACTIONS['open-settings'](); ['display', 'game', 'notif', 'keys', 'about'].forEach(t => { WS_ACTIONS['set-tab'](t); render(); }); WS_ACTIONS.set('window:窗口'); WS_ACTIONS['dlg-cancel'](); WS_ACTIONS['set-notif']('遇敌:pause'); WS_ACTIONS['set-toggle']('shake'); WS_ACTIONS['ov-close'](); });
  step(() => { WS_ACTIONS['open-issue'](); WS_ACTIONS['issue-export'](); WS_ACTIONS['ov-close'](); });
  step(() => { openLibrary(); ['local', 'user', 'pack', 'draft'].forEach(l => { WS_ACTIONS['lib-lib'](l); render(); }); WS_ACTIONS['lib-lib']('pack'); WS_ACTIONS['lib-copy-edit']('bp-wagonL'); });
  step(() => { const E = S.editor; E.hover = { c: [0, 2, 3], n: [0, 0, 1] }; snapshot(E); applyTarget(E, 'add'); WS_ACTIONS['ed-tool']('select'); E.sel = new Set(['e1']); WS_ACTIONS['ed-move']('1,0,0'); WS_ACTIONS['ed-rotate'](); WS_ACTIONS['ed-dup'](); WS_ACTIONS['ed-del'](); WS_ACTIONS['ed-undo'](); WS_ACTIONS['ed-redo'](); ['voxels', 'equipment', 'structure', 'stats'].forEach(p => { WS_ACTIONS['ed-panel'](p); render(); }); });
  step(() => { WS_ACTIONS['ed-equip']('bogie'); WS_ACTIONS['ed-save'](); render(); WS_ACTIONS['dlg-cancel'](); WS_ACTIONS['ed-exit'](); WS_ACTIONS['dlg-alt'](); });
  step(() => { S.workspace = null; WS_ACTIONS['nd-open'](); WS_ACTIONS['nd-set']('kind:建筑'); render(); WS_ACTIONS['nd-tag']('车间'); WS_ACTIONS['dlg-ok'](); WS_ACTIONS['ed-exit'](); });
  step(() => { S.workspace = null; select({ kind: 'train', id: 't3' }); WS_ACTIONS.refit('v7'); const E = S.editor; E.vox.set(key(5, 1, 4), { d: 'plate', c: '#b8743f', p: 'body' }); E.dirty = true; WS_ACTIONS['ed-refit-done'](); render(); WS_ACTIONS['refit-back'](); WS_ACTIONS['ed-refit-done'](); WS_ACTIONS['refit-go'](); });
  step(() => { S.savedAgo = 5; WS_ACTIONS['to-menu'](); WS_ACTIONS['dlg-alt'](); });
  const ok = document.createElement('div'); ok.textContent = '自检通过'; document.body.appendChild(ok);
}];

// 第 4 轮
const dock = (tab, f) => { S.dockOpen = true; S.dockTab = tab; f?.(); };
Object.assign(SCENARIOS, {
  'console': ['控制台', () => dock('console', () => { ['help train', 'train start 3', 'sim step', 'trian stop 1'].forEach(runCommand); S.con.input = 'train s'; })],
  'console-release': ['控制台（玩家构建）', () => { S.release = true; dock('console', () => { ['help', 'sim status'].forEach(runCommand); }); }],
  'dev-step': ['开发工具：推进与重放', () => { S.paused = true; dock('step', () => { S.step.msg = '已推进 10 步，现在是步 1,214'; }); }],
  'dev-step-running': ['开发工具：运行到条件', () => dock('step', () => { S.step.running = 74; })],
  'dev-object': ['开发工具：对象', () => { select({ kind: 'train', id: 't1' }); dock('object'); }],
  'dev-commands': ['开发工具：命令记录', () => dock('commands', () => { S.cmd.open = 1184; })],
  'dev-checkpoints': ['开发工具：检查点', () => dock('checkpoints', () => { S.cpSel = [0, 1]; PANEL_ACTIONS['cp-compare'](); })],
  'dev-scene': ['开发工具：场景与性能', () => { S.speed = 4; dock('scene', () => { S.overlays.Chunks = true; }); }],
  'authoring': ['创作模式：体素', () => openAuthoring('menu')],
  'authoring-template': ['创作模式：聚居地模板', () => { openAuthoring('menu'); S.au.sel = 'template:farm_town_a'; S.au.plot = 4; Object.assign(auDef().f.plots[3], { x: 21, z: 6 }); S.au.dirty.add(S.au.sel); }],
  'authoring-lair': ['创作模式：生物聚落类型', () => { openAuthoring('menu'); S.au.sel = 'lairType:spike_nest'; }],
  'authoring-new': ['创作模式：新建定义', () => { openAuthoring('menu'); WS_ACTIONS['au-new'](); S.auNew.id = 'Rusty-Plate'; }],
  'authoring-write': ['创作模式：写入确认', () => { openAuthoring('menu'); S.au.drafts = new Set(['voxel:steel_plate', 'template:farm_town_a']); WS_ACTIONS['au-write'](); }],
  'authoring-fail': ['创作模式：写入失败', () => { openAuthoring('menu'); S.au.drafts = new Set(['building:farm']); S.au.write = { done: true, ok: false, files: [] }; }],
});
SCENARIOS._selftest4 = ['自检 4', () => {
  let n = 0; const step = f => { n++; try { f(); render(); } catch (e) { const d = document.createElement('div'); d.textContent = `STEPERR ${n}: ${e.message} | ${(e.stack || '').split('\n').slice(1, 3).join(' ')}`; document.body.appendChild(d); throw e; } };
  step(() => { S.dockOpen = true; ['help', 'help sim', 'clear', 'loglevel', 'loglevel warning', 'loglevel bad', 'reload', 'lighting night', 'lighting x', 'sim', 'sim pause', 'sim step 5', 'sim speed 3', 'sim speed 2', 'sim resume', 'sim checkpoint', 'train', 'train start 9', 'train start 1', 'train stop 1', 'train plan 1', 'zzz'].forEach(runCommand); S.con.input = 'sim '; render(); S.con.input = 'train start '; render(); });
  step(() => { DOCK_TABS.forEach(([k]) => { S.dockTab = k; render(); }); PANEL_ACTIONS['step-one'](); S.paused = true; PANEL_ACTIONS['step-n'](); PANEL_ACTIONS['obj-follow'](); S.dockTab = 'object'; render(); PANEL_ACTIONS['cmd-res']('rejected'); PANEL_ACTIONS['cmd-mine'](); PANEL_ACTIONS['cp-capture'](); PANEL_ACTIONS['cp-sel']('0'); PANEL_ACTIONS['cp-sel']('2'); PANEL_ACTIONS['cp-compare'](); PANEL_ACTIONS['mark-add'](); PANEL_ACTIONS['ov-toggle']('Origin'); });
  step(() => { S.release = true; S.dockTab = 'console'; runCommand('sim status'); runCommand('help'); S.release = false; PANEL_ACTIONS['dock-close'](); });
  step(() => { openAuthoring('game'); S.au.defs.forEach(d => { S.au.sel = `${d.kind}:${d.id}`; render(); }); });
  step(() => { S.au.sel = 'voxel:steel_plate'; WS_ACTIONS['au-bool']('sim'); WS_ACTIONS['au-bool']('sim'); WS_ACTIONS['au-beh-add'](); WS_ACTIONS['au-beh-del']('0'); S.au.sel = 'lairType:spike_nest'; WS_ACTIONS['au-mem-add'](); WS_ACTIONS['au-mem-del']('2'); });
  step(() => { S.au.sel = 'template:farm_town_a'; WS_ACTIONS['au-plot']('2'); WS_ACTIONS['au-plot-rot'](); WS_ACTIONS['au-plot-add'](); WS_ACTIONS['au-plot-del']('5'); WS_ACTIONS['au-save'](); });
  step(() => { WS_ACTIONS['au-new'](); S.auNew = { kind: 'template', id: 'farm_town_b' }; WS_ACTIONS['dlg-ok'](); WS_ACTIONS['au-save'](); WS_ACTIONS['au-write'](); WS_ACTIONS['dlg-ok'](); S.au.write = { done: true, ok: true, files: ['a'] }; render(); WS_ACTIONS['au-write-close'](); });
  step(() => { WS_ACTIONS['au-del'](); WS_ACTIONS['dlg-ok'](); S.au.sel = 'vehicle:wagon_m'; WS_ACTIONS['au-open-editor'](); WS_ACTIONS['ed-exit'](); WS_ACTIONS['au-exit'](); });
  const ok = document.createElement('div'); ok.textContent = '自检通过'; document.body.appendChild(ok);
}];
