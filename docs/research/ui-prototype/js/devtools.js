// 开发工具底栏：控制台（玩家构建也有）＋五个调试页（只在开发构建出现）
const COMMANDS = {
  help: { syntax: 'help [命令]', desc: '列出全部命令，或显示一条命令的用法', release: true, args: () => Object.keys(COMMANDS).map(c => [c, COMMANDS[c].desc]) },
  clear: { syntax: 'clear', desc: '清空控制台输出', release: true },
  quit: { syntax: 'quit', desc: '退出游戏，有未保存的进度时先确认', release: true },
  loglevel: { syntax: 'loglevel [debug | info | warning | error]', desc: '查看或设置日志级别，同时影响写到磁盘的日志', release: true, args: () => [['debug', '全部'], ['info', '默认'], ['warning', '只看警告和错误'], ['error', '只看错误']] },
  reload: { syntax: 'reload', desc: '重新载入全部内容包', release: false },
  sim: { syntax: 'sim status | pause | resume | speed 1|2|4|8 | step [步数] | checkpoint', desc: '查看和控制模拟', release: false, args: () => [['status', '当前时间、步号和速度'], ['pause', '暂停'], ['resume', '继续'], ['speed', '设置倍速'], ['step', '暂停时推进若干步'], ['checkpoint', '捕获检查点摘要']] },
  train: { syntax: 'train form <车辆…> | disband <列车> | start <列车> | stop <列车> | plan <列车> once|loop station:<节点>:<秒> …', desc: '编组、解编、启停列车，替换运行计划', release: false, args: p => p.length <= 2 ? [['form', '用停放的车辆编组'], ['disband', '解编'], ['start', '发车'], ['stop', '停车'], ['plan', '替换运行计划']] : Object.entries(W.trains).map(([id, t]) => [id.slice(1), `${t.name} · ${trainWhere(id)}`]) },
  lighting: { syntax: 'lighting daylight | overcast | night | restore', desc: '切换检查用的光照', release: false, args: () => [['daylight', '晴天'], ['overcast', '阴天'], ['night', '夜晚'], ['restore', '恢复正常']] },
};
const LVL = { error: ['错误', '#ec8a84'], warn: ['警告', '#e9b45a'], info: ['信息', '#c9cfd6'], debug: ['调试', '#7f8893'], cmd: ['命令', '#56b6c2'] };
const COMMAND_LOG = [
  { seq: 1180, step: 1150, kind: 'train.start', res: 'accepted', src: '玩家界面', obj: ['train', 't1'], params: '列车 1' },
  { seq: 1181, step: 1152, kind: 'trade.buy', res: 'rejected', src: '玩家界面', obj: ['node', 'n1'], params: '青川 · 玻璃 ×10', why: '青川没有玻璃可卖' },
  { seq: 1182, step: 1160, kind: 'rail.repair', res: 'accepted', src: '玩家界面', obj: ['line', 'l3'], params: '青川—北港 · 断点 1' },
  { seq: 1183, step: 1171, kind: 'train.replace_run_plan', res: 'accepted', src: '控制台', obj: ['train', 't2'], params: '列车 2 · 循环 · 赤岭 → 北港' },
  { seq: 1184, step: 1190, kind: 'production.set_plan', res: 'faulted', src: '玩家界面', obj: ['node', 'n2'], params: '白石镇 · 果园 · 种植果蔬', why: '核心模拟在执行这条命令时出错，已保存复现包' },
];

function devState() {
  return { dockOpen: false, dockTab: 'console', dockH: 340, release: false,
    con: { lines: [['08:18:02', 'info', 'content', '内容包 core 1.4 已载入，共 412 个定义'], ['08:18:40', 'debug', 'scene', '青川 节点场景准备完成，用时 0.42 秒'], ['08:19:11', 'warn', 'simulation', '列车 3 没有运行计划，停在青川'], ['08:20:05', 'info', 'save', '自动存档完成']],
      input: '', hist: [], hi: -1, filter: 'all', sugg: 0, level: 'info' },
    step: { n: 10, cond: 'event', val: 'TrainArrived', max: '', running: null, msg: null },
    obj: { follow: true, kind: 'train', id: '1' }, cmd: { kind: 'all', res: 'all', mine: false, open: null },
    cps: [{ name: '读档后', step: 900, time: '06:40' }, { name: '修复断点前', step: 1100, time: '07:55' }], cpSel: [], cpName: '', cpResult: null, marks: [['1120', '列车 2 开始绕路']], markText: '',
    overlays: {}, };
}

// ---------- 控制台 ----------
function conLog(lvl, msg, cat = 'console') { S.con.lines.push([clock().slice(-5) + ':00', lvl, cat, msg]); }
function suggestions() {
  const v = S.con.input, parts = v.split(/\s+/), cmds = Object.keys(COMMANDS).filter(c => S.release ? COMMANDS[c].release : true);
  if (parts.length <= 1) return cmds.filter(c => c.startsWith(parts[0] || '')).map(c => [c, COMMANDS[c].desc]);
  const c = COMMANDS[parts[0]]; if (!c?.args) return [];
  const cur = parts[parts.length - 1];
  return c.args(parts).filter(([a]) => a.startsWith(cur));
}
function runCommand(line) {
  const parts = line.trim().split(/\s+/), name = parts[0], c = COMMANDS[name];
  S.con.lines.push([clock().slice(-5) + ':00', 'cmd', 'console', `> ${line}`]); S.con.hist.unshift(line); S.con.hi = -1;
  if (!c || (S.release && !c.release)) {
    const near = Object.keys(COMMANDS).find(k => k[0] === name?.[0] && (!S.release || COMMANDS[k].release));
    return conLog('error', `没有「${name}」这条命令。${near ? `是不是想输入 ${near}？` : ''}输入 help 查看全部命令。`);
  }
  const usage = () => conLog('error', `用法：${c.syntax}`);
  if (name === 'help') { if (parts[1] && COMMANDS[parts[1]]) { conLog('info', `${parts[1]}：${COMMANDS[parts[1]].desc}`); return conLog('info', `用法：${COMMANDS[parts[1]].syntax}`); } return Object.entries(COMMANDS).filter(([, x]) => !S.release || x.release).forEach(([k, x]) => conLog('info', `${k.padEnd(9)} ${x.desc}`)); }
  if (name === 'clear') { S.con.lines = []; return; }
  if (name === 'quit') return WS_ACTIONS['menu-quit']();
  if (name === 'loglevel') { if (!parts[1]) return conLog('info', `当前日志级别：${S.con.level}`); if (!['debug', 'info', 'warning', 'error'].includes(parts[1])) return usage(); S.con.level = parts[1]; return conLog('info', `日志级别已设为 ${parts[1]}，磁盘日志同样生效`); }
  if (name === 'reload') return conLog('info', '内容包已重新载入：412 个定义，用时 1.8 秒', 'content');
  if (name === 'lighting') { if (!COMMANDS.lighting.args().some(a => a[0] === parts[1])) return usage(); return conLog('info', `光照已切换为 ${parts[1]}`); }
  if (name === 'sim') {
    const sub = parts[1];
    if (!sub || sub === 'status') return conLog('info', `${clock()} · 步 1,204 · ${S.paused ? '已暂停' : `运行中 ${S.speed}×`}`);
    if (sub === 'pause') { S.paused = true; return conLog('info', '模拟已暂停'); }
    if (sub === 'resume') { S.paused = false; return conLog('info', '模拟已继续'); }
    if (sub === 'speed') { if (![1, 2, 4, 8].includes(+parts[2])) return conLog('error', '倍速只能是 1、2、4、8。用法：sim speed 1|2|4|8'); S.speed = +parts[2]; S.paused = false; return conLog('info', `倍速已设为 ${parts[2]}×`); }
    if (sub === 'step') { if (!S.paused) return conLog('error', '模拟在运行，不能单步。先输入 sim pause 暂停。'); const n = +(parts[2] || 1); W.minute += n; return conLog('info', `已推进 ${n} 步，现在是步 ${1204 + n}`); }
    if (sub === 'checkpoint') return conLog('info', '检查点：步 1,204 · 种子 40 · 输入 sha256 9f3c…a1 · 世界 sha256 77de…04');
    return usage();
  }
  if (name === 'train') {
    const [sub, id] = [parts[1], 't' + parts[2]], t = W.trains[id];
    if (!['form', 'disband', 'start', 'stop', 'plan'].includes(sub)) return usage();
    if (sub !== 'form' && !t) return conLog('error', `找不到列车 ${parts[2] ?? '（缺少编号）'}。现有列车：${Object.keys(W.trains).map(k => k.slice(1)).join('、')}`);
    if (sub === 'start') { if (!t.plan) return conLog('error', `${t.name}没有运行计划，不能发车。用 train plan 设置计划，或在列车检查器里编辑。`); startTrain(id); return conLog('info', `${t.name}已发车`); }
    if (sub === 'stop') { t.running = false; return conLog('info', `${t.name}将在下一站停车`); }
    return conLog('info', `train ${sub} 已提交`);
  }
}

// ---------- 底栏 ----------
const DOCK_TABS = [['console', '控制台'], ['step', '推进与重放'], ['object', '对象'], ['commands', '命令记录'], ['checkpoints', '检查点'], ['scene', '场景与性能']];
function renderDock() {
  const el = document.getElementById('devdock');
  document.documentElement.style.setProperty('--dock-h', S.dockOpen && S.screen === 'game' ? S.dockH + 'px' : '0px');
  if (!S.dockOpen || S.screen !== 'game') { el.innerHTML = ''; el.classList.add('hidden'); return; }
  el.classList.remove('hidden'); el.style.height = S.dockH + 'px';
  const tabs = S.release ? [DOCK_TABS[0]] : DOCK_TABS;
  const scroll = el.querySelector('.dock-body')?.scrollTop;
  const body = { console: dockConsole, step: dockStep, object: dockObject, commands: dockCommands, checkpoints: dockCheckpoints, scene: dockScene }[S.dockTab]();
  el.innerHTML = `<div class="dock-grip" data-grip="1" title="拖动调整高度"></div><div class="dock-head"><div class="tabs" style="border:0;padding:0">${tabs.map(([k, l]) => `<button class="tab ${S.dockTab === k ? 'on' : ''}" data-action="dock-tab" data-v="${k}">${l}</button>`).join('')}</div>
    <span style="flex:1"></span><span class="small muted">${S.release ? '' : `活动会话 · 步 1,204 · ${S.paused ? '已暂停' : `运行中 ${S.speed}×`}`}</span>
    ${S.dockTab === 'console' ? `<button class="btn ghost small" data-action="con-copy">复制全部</button><button class="btn ghost small" data-action="con-clear">清空</button>` : ''}
    <button class="btn ghost icon-only" data-action="dock-close" title="关闭（\`）">${icon('close', 'sm')}</button></div><div class="dock-body">${body}</div>`;
  const b = el.querySelector('.dock-body');
  if (S.dockTab === 'console') { const out = el.querySelector('.con-out'); if (out) out.scrollTop = out.scrollHeight; } else if (b && scroll) b.scrollTop = scroll;
}

function dockConsole() {
  const f = S.con.filter, lines = S.con.lines.filter(([, l]) => f === 'all' || l === f || (f === 'warn' && l === 'error') || l === 'cmd');
  const sugg = S.con.input ? suggestions() : [], cmd = COMMANDS[S.con.input.split(/\s+/)[0]];
  return `<div style="display:flex;flex-direction:column;height:100%"><div class="filters" style="padding:6px 12px 4px">${[['all', '全部'], ['warn', '警告和错误'], ['info', '信息'], ['debug', '调试']].map(([k, l]) => `<button class="${f === k ? 'on' : ''}" data-action="con-filter" data-v="${k}">${l}</button>`).join('')}</div>
    <div class="con-out">${lines.map(([t, l, cat, m]) => `<div class="con-line"><span class="faint">${t}</span><span class="con-lvl" style="color:${LVL[l][1]}">${LVL[l][0]}</span><span class="faint">${cat}</span><span style="color:${l === 'cmd' ? LVL.cmd[1] : l === 'error' ? LVL.error[1] : l === 'warn' ? LVL.warn[1] : ''}">${m.replace(/</g, '&lt;')}</span></div>`).join('') || '<div class="faint" style="padding:8px">没有输出。</div>'}</div>
    <div style="position:relative;padding:6px 12px 10px;border-top:1px solid var(--divider)">
      ${sugg.length ? `<div class="popover menu con-sugg">${sugg.slice(0, 7).map(([a, d], i) => `<button class="${i === S.con.sugg ? 'on' : ''}" data-action="con-pick" data-v="${a}"><b style="width:120px">${a}</b><span class="grow muted small">${d}</span></button>`).join('')}<div class="small faint" style="padding:4px 12px">Tab 补全 · ↑↓ 选择</div></div>` : ''}
      ${cmd ? `<div class="small muted" style="margin-bottom:4px">用法：${cmd.syntax.replace(/</g, '&lt;')}</div>` : ''}
      <div style="display:flex;gap:8px;align-items:center"><span style="color:${LVL.cmd[1]}">&gt;</span><input id="con-input" type="text" value="${S.con.input.replace(/"/g, '&quot;')}" data-input="con-input" placeholder="输入命令，help 查看全部命令；↑↓ 翻历史" style="flex:1;font-family:Consolas,monospace" autocomplete="off"></div></div></div>`;
}

function dockStep() {
  const s = S.step, condLabel = { step: '步号', rejected: '命令种类', fault: '对象', event: '事件类型' }[s.cond];
  return `<div class="dock-grid"><div class="dock-card"><h4>步进</h4><div style="display:flex;gap:8px;flex-wrap:wrap;align-items:flex-end">
      <button class="btn" data-action="toggle-pause">${icon(S.paused ? 'play' : 'pause')} ${S.paused ? '继续' : '暂停'}</button><button class="btn" data-action="step-one" ${S.paused ? '' : 'disabled title="先暂停"'}>单步</button>
      <div class="field"><label for="step-n">步数</label><input id="step-n" type="number" min="1" value="${s.n}" data-input="step-n" style="width:90px"></div><button class="btn" data-action="step-n" ${S.paused ? '' : 'disabled'}>推进 ${s.n} 步</button></div>
      ${S.paused ? '' : '<div class="small faint" style="margin-top:6px">单步和按步推进需要先暂停。</div>'}</div>
    <div class="dock-card"><h4>运行到条件</h4><div style="display:flex;gap:8px;flex-wrap:wrap;align-items:flex-end">
      <div class="field"><label for="step-cond">条件</label><select id="step-cond" data-change="step-cond">${[['step', '到达步号'], ['rejected', '有命令被拒绝'], ['fault', '对象出现故障'], ['event', '发生领域事件']].map(([k, l]) => `<option value="${k}" ${s.cond === k ? 'selected' : ''}>${l}</option>`).join('')}</select></div>
      <div class="field"><label for="step-val">${condLabel}</label>${s.cond === 'event' ? `<select id="step-val" data-change="step-val">${['TrainArrived', 'TrainDeparted', 'RailBroken', 'BattleStarted', 'JobCompleted'].map(v => `<option ${s.val === v ? 'selected' : ''}>${v}</option>`).join('')}</select>` : s.cond === 'fault' ? `<input id="step-val" type="text" value="${S.selection ? '当前选中对象' : ''}" placeholder="先在地图上选中对象" disabled>` : `<input id="step-val" type="text" value="${s.val}" data-input="step-val" placeholder="${s.cond === 'step' ? '例如 1500' : '例如 trade.buy，留空表示任意'}">`}</div>
      <div class="field"><label for="step-max">最多推进（可选）</label><input id="step-max" type="number" value="${s.max}" data-input="step-max" placeholder="不限" style="width:110px"></div>
      ${s.running ? `<button class="btn" data-action="step-cancel">取消</button>` : `<button class="btn primary" data-action="step-run">开始</button>`}</div>
      ${s.running ? `<div class="small" style="margin-top:6px">已推进 ${s.running} 步，条件还没满足…</div>` : s.msg ? `<div class="small pos" style="margin-top:6px">${s.msg}</div>` : ''}</div>
    <div class="dock-card"><h4>复现包与重放</h4><div class="small muted" style="margin-bottom:8px">打开复现包会在隔离的只读会话里重放，不影响当前对局。</div>
      <button class="btn" data-action="todo" data-v="打开复现包">${icon('folder')} 打开复现包…</button></div></div>`;
}

function inspectedObject() {
  const o = S.obj, sel = o.follow ? S.selection : { kind: o.kind, id: o.kind === 'train' ? 't' + o.id : o.id };
  if (!sel) return null;
  if (sel.kind === 'train') { const t = W.trains[sel.id]; return t && { title: t.name, data: { id: +sel.id.slice(1), name: t.name, status: t.status, running: t.running, location: t.line ? { line: t.line, from: t.from, to: t.to, progress: +t.progress.toFixed(3) } : { node: t.at }, vehicles: t.vehicles, cargo: t.cargo, plan: t.plan ? { loop: t.plan.loop, stops: t.plan.stops.length } : null } }; }
  if (sel.kind === 'node') { const n = W.nodes[sel.id]; return n && { title: n.name, data: { id: sel.id, name: n.name, kind: n.kind, relation: n.relation ?? null, population: n.pop ?? null, platforms: n.platforms ?? null } }; }
  if (sel.kind === 'line') { const l = W.lines[sel.id]; return l && { title: lineName(sel.id), data: { id: sel.id, ...l } }; }
  return { title: '战斗', data: W.battles[sel.id] };
}
const jsonHtml = (v, ind = 0) => v === null ? '<span style="color:#7f8893">null</span>' : typeof v === 'number' ? `<span style="color:#e9b45a">${v}</span>` : typeof v === 'boolean' ? `<span style="color:#c27fd1">${v}</span>` : typeof v === 'string' ? `<span style="color:#5cc78a">"${v}"</span>`
  : Array.isArray(v) ? `[${v.map(x => jsonHtml(x, ind)).join(', ')}]` : `{\n${Object.entries(v).map(([k, x]) => `${'  '.repeat(ind + 1)}<span style="color:#7fb4e6">${k}</span>: ${jsonHtml(x, ind + 1)}`).join(',\n')}\n${'  '.repeat(ind)}}`;
function dockObject() {
  const o = S.obj, x = inspectedObject();
  return `<div style="display:flex;height:100%"><div style="width:300px;padding:12px;border-right:1px solid var(--divider)">
      <label class="switch ${o.follow ? 'on' : ''}" data-action="obj-follow"><i></i>跟随选中对象</label>
      <div class="small faint" style="margin:6px 0 12px">打开时显示地图或检查器里选中的对象。</div>
      ${o.follow ? '' : `<div class="field"><label for="obj-kind">类型</label><select id="obj-kind" data-change="obj-kind">${[['train', '列车'], ['node', '节点'], ['line', '线路'], ['vehicle', '车辆'], ['building', '建筑'], ['job', '任务'], ['battle', '战斗']].map(([k, l]) => `<option value="${k}" ${o.kind === k ? 'selected' : ''}>${l}</option>`).join('')}</select></div>
        <div class="field" style="margin-top:8px"><label for="obj-id">编号</label><input id="obj-id" type="text" value="${o.id}" data-input="obj-id"></div>`}
      ${x ? `<div style="display:flex;gap:6px;margin-top:12px;flex-wrap:wrap"><button class="btn" data-action="locate">${icon('locate', 'sm')} 定位</button><button class="btn" data-action="todo" data-v="复制 JSON">复制 JSON</button></div>` : ''}</div>
    <div style="flex:1;overflow:auto;padding:12px 16px">${x ? `<div style="margin-bottom:6px"><b>${x.title}</b> <span class="chip success">存在</span></div><pre class="json">${jsonHtml(x.data)}</pre>` : `<div class="muted">${o.follow ? '在地图上选中一个对象，这里会显示它的内部数据。' : '找不到这个对象。'}</div>`}</div></div>`;
}
function dockCommands() {
  const c = S.cmd, kinds = [...new Set(COMMAND_LOG.map(r => r.kind))];
  const rows = COMMAND_LOG.filter(r => (c.kind === 'all' || r.kind === c.kind) && (c.res === 'all' || r.res === c.res) && (!c.mine || (S.selection && r.obj[1] === S.selection.id)));
  const RES = { accepted: ['success', '接受'], rejected: ['warning', '拒绝'], faulted: ['error', '故障'] };
  return `<div style="padding:8px 12px"><div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-bottom:6px">
      <select id="cmd-kind" data-change="cmd-kind" aria-label="命令种类" style="width:240px"><option value="all">全部命令种类</option>${kinds.map(k => `<option ${c.kind === k ? 'selected' : ''}>${k}</option>`).join('')}</select>
      <div class="filters" style="padding:0">${[['all', '全部结果'], ['accepted', '接受'], ['rejected', '拒绝'], ['faulted', '故障']].map(([k, l]) => `<button class="${c.res === k ? 'on' : ''}" data-action="cmd-res" data-v="${k}">${l}</button>`).join('')}</div>
      <label class="switch ${c.mine ? 'on' : ''}" data-action="cmd-mine"><i></i>只看选中对象</label><span style="flex:1"></span><span class="small muted">记录范围：步 900–1,204，共 312 条（读档后从步 900 重新开始）</span></div>
    <table class="grid"><tr><th class="num">序号</th><th class="num">步</th><th>命令</th><th>结果</th><th>来源</th><th>对象</th><th>参数</th></tr>${rows.map(r => `<tr class="click ${c.open === r.seq ? 'on' : ''}" data-action="cmd-open" data-v="${r.seq}"><td class="num">${r.seq}</td><td class="num">${r.step}</td><td style="font-family:Consolas,monospace">${r.kind}</td><td><span class="chip ${RES[r.res][0]}">${RES[r.res][1]}</span></td><td class="muted">${r.src}</td>
      <td><a href="#" data-action="select" data-v="${r.obj[0]}:${r.obj[1]}" style="color:inherit">${r.obj[0] === 'train' ? W.trains[r.obj[1]]?.name : r.obj[0] === 'line' ? lineName(r.obj[1]) : nodeName(r.obj[1])}</a></td><td class="muted">${r.params}</td></tr>
      ${c.open === r.seq && r.why ? `<tr><td colspan="7" style="height:auto;padding:8px 12px;white-space:normal"><div class="notice ${r.res === 'faulted' ? 'error' : 'warning'}">${icon('alert')}<div class="grow">${r.why}</div></div></td></tr>` : ''}`).join('')}</table></div>`;
}
function dockCheckpoints() {
  const r = S.cpResult;
  return `<div class="dock-grid"><div class="dock-card" style="flex:1.4"><h4>检查点</h4><div style="display:flex;gap:8px;margin-bottom:8px"><input id="cp-name" type="text" placeholder="说明，例如：修复断点后" value="${S.cpName}" data-input="cp-name" style="flex:1"><button class="btn" data-action="cp-capture">捕获检查点</button></div>
      <table class="grid"><tr><th style="width:30px"></th><th>说明</th><th class="num">步</th><th>时间</th></tr>${S.cps.map((c, i) => `<tr class="click" data-action="cp-sel" data-v="${i}"><td><span class="check ${S.cpSel.includes(i) ? 'on' : ''}">${S.cpSel.includes(i) ? icon('check', 'sm') : ''}</span></td><td>${c.name}${S.cpSel[0] === i ? ' <span class="chip neutral">基准</span>' : S.cpSel[1] === i ? ' <span class="chip neutral">对照</span>' : ''}</td><td class="num">${c.step}</td><td class="muted">${c.time}</td></tr>`).join('')}</table>
      <div style="display:flex;gap:8px;align-items:center;margin-top:8px"><button class="btn" data-action="cp-compare" ${S.cpSel.length === 2 ? '' : 'disabled'}>比较所选的两个</button><span class="small muted">${S.cpSel.length === 2 ? '' : '勾选两个检查点：先勾的是基准，后勾的是对照。'}</span></div>
      ${r ? `<div class="notice ${r.ok ? 'success' : 'warning'}" style="margin-top:8px">${icon(r.ok ? 'check' : 'alert')}<div class="grow">${r.text}</div></div>` : ''}<div class="small faint" style="margin-top:6px">检查点只存在内存里，退出游戏后清空。</div></div>
    <div class="dock-card"><h4>标记</h4><div style="display:flex;gap:8px;margin-bottom:8px"><input id="mark-text" type="text" placeholder="给当前时间点写一句说明" value="${S.markText}" data-input="mark-text" style="flex:1"><button class="btn" data-action="mark-add">添加</button></div>
      ${S.marks.map(([s, t]) => `<div style="display:flex;gap:10px;padding:3px 0"><span class="faint" style="width:60px">步 ${s}</span><span>${t}</span></div>`).join('')}</div></div>`;
}
function dockScene() {
  const ov = [['Chunks', '区块边界'], ['RailwaySegments', '铁路分段'], ['Origin', '浮动原点'], ['PickRay', '拾取射线'], ['Centerline', '线路中心线']];
  return `<div class="dock-grid"><div class="dock-card"><h4>场景调试线</h4>${ov.map(([k, l]) => `<div style="padding:4px 0"><label class="switch ${S.overlays[k] ? 'on' : ''}" data-action="ov-toggle" data-v="${k}"><i></i>${l}</label></div>`).join('')}
      <button class="btn" style="margin-top:8px" data-action="todo" data-v="重新生成当前场景" ${S.scene ? '' : 'disabled title="先进入一个场景"'}>重新生成当前场景</button><div class="small faint" style="margin-top:4px">只重建画面，不改模拟状态。</div></div>
    <div class="dock-card" style="flex:2"><h4>性能</h4><div class="stats" style="grid-template-columns:repeat(4,1fr)">
      <div class="stat"><div class="label">帧耗时</div><div class="value">16.4 ms</div><div class="small muted">约 61 帧/秒</div></div><div class="stat"><div class="label">内存</div><div class="value">1.82 GiB</div></div>
      <div class="stat"><div class="label">速度</div><div class="value">${S.paused ? '已暂停' : `${S.speed}× → ${S.speed > 2 ? (S.speed * 0.8).toFixed(1) : S.speed}×`}</div><div class="small ${S.speed > 2 && !S.paused ? '' : 'muted'}" style="${S.speed > 2 && !S.paused ? 'color:#e9b45a' : ''}">${S.paused ? '' : S.speed > 2 ? '模拟跟不上设定倍速' : '跟得上'}</div></div>
      <div class="stat"><div class="label">场景对象</div><div class="value">12,430</div></div></div></div></div>`;
}

Object.assign(INPUTS, {
  'con-input': v => { S.con.input = v; S.con.sugg = 0; }, 'step-n': v => { S.step.n = Math.max(1, parseInt(v) || 1); }, 'step-val': v => { S.step.val = v; }, 'step-max': v => { S.step.max = v; }, 'step-cond': v => { S.step.cond = v; S.step.val = v === 'event' ? 'TrainArrived' : ''; },
  'obj-kind': v => { S.obj.kind = v; }, 'obj-id': v => { S.obj.id = v; }, 'cmd-kind': v => { S.cmd.kind = v; }, 'cp-name': v => { S.cpName = v; }, 'mark-text': v => { S.markText = v; },
});
Object.assign(PANEL_ACTIONS, {
  'dock-tab': v => { S.dockTab = v; }, 'dock-close': () => { S.dockOpen = false; },
  'con-filter': v => { S.con.filter = v; }, 'con-clear': () => { S.con.lines = []; }, 'con-copy': () => toast('complete', `已复制 ${S.con.lines.length} 行到剪贴板`, null, false),
  'con-pick': v => { const p = S.con.input.split(/\s+/); p[p.length - 1] = v; S.con.input = p.join(' ') + ' '; setTimeout(() => document.getElementById('con-input')?.focus()); },
  'step-one': () => { W.minute += 1; S.step.msg = '已推进 1 步，现在是步 1,205'; }, 'step-n': () => { W.minute += S.step.n; S.step.msg = `已推进 ${S.step.n} 步，现在是步 ${1204 + S.step.n}`; },
  'step-run': () => { S.step.running = 1; const go = () => { if (!S.step.running) return; S.step.running += 37; if (S.step.running > 150) { S.step.running = null; S.step.msg = `条件已满足：步 1,358 发生 ${S.step.val}（列车 2 到达北港）。已暂停。`; S.paused = true; } render(); if (S.step.running) setTimeout(go, 300); }; setTimeout(go, 300); },
  'step-cancel': () => { S.step.running = null; S.step.msg = '已取消。'; },
  'obj-follow': () => { S.obj.follow = !S.obj.follow; },
  'cmd-res': v => { S.cmd.res = v; }, 'cmd-mine': () => { S.cmd.mine = !S.cmd.mine; }, 'cmd-open': v => { S.cmd.open = S.cmd.open === +v ? null : +v; },
  'cp-capture': () => { S.cps.push({ name: S.cpName || `检查点 ${S.cps.length + 1}`, step: 1204, time: clock().slice(-5) }); S.cpName = ''; },
  'cp-sel': v => { const i = +v, s = S.cpSel; s.includes(i) ? s.splice(s.indexOf(i), 1) : s.push(i); if (s.length > 2) s.shift(); S.cpResult = null; },
  'cp-compare': () => { const [a, b] = S.cpSel.map(i => S.cps[i]); S.cpResult = a.step === b.step ? { ok: true, text: '两个检查点完全一致。' } : { ok: false, text: `两个检查点不在同一步（步 ${a.step} 和步 ${b.step}），只能比较同一步捕获的检查点。` }; },
  'mark-add': () => { if (S.markText) S.marks.push(['1204', S.markText]); S.markText = ''; },
  'ov-toggle': v => { S.overlays[v] = !S.overlays[v]; },
});

// 控制台快捷键：` 开关底栏；输入框里 Tab 补全、↑↓ 选建议或翻历史、Enter 执行
document.addEventListener('keydown', e => {
  if (e.key === '`' && S.screen === 'game' && !(typing() && document.activeElement.id !== 'con-input')) {
    e.preventDefault(); e.stopImmediatePropagation(); S.dockOpen = !S.dockOpen; if (S.dockOpen) S.dockTab = S.dockTab || 'console'; render(); setTimeout(() => document.getElementById('con-input')?.focus()); return;
  }
  if (document.activeElement?.id !== 'con-input') return;
  const sugg = S.con.input ? suggestions() : [];
  if (e.key === 'Tab') { e.preventDefault(); if (sugg[S.con.sugg]) PANEL_ACTIONS['con-pick'](sugg[S.con.sugg][0]); }
  else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
    e.preventDefault(); const d = e.key === 'ArrowUp' ? -1 : 1;
    if (sugg.length > 1 && S.con.hi < 0) S.con.sugg = (S.con.sugg + d + sugg.length) % Math.min(sugg.length, 7);
    else { S.con.hi = Math.max(-1, Math.min(S.con.hist.length - 1, S.con.hi - d)); S.con.input = S.con.hist[S.con.hi] ?? ''; }
  } else if (e.key === 'Enter') { e.preventDefault(); if (S.con.input.trim()) runCommand(S.con.input); S.con.input = ''; }
  else if (e.key === 'Escape') { e.stopImmediatePropagation(); S.dockOpen = false; document.activeElement.blur(); }
  else return;
  e.stopImmediatePropagation(); render(); document.getElementById('con-input')?.focus();
}, true);

// 拖动底栏上沿调整高度
document.addEventListener('pointerdown', e => {
  if (!e.target.dataset?.grip) return;
  const y0 = e.clientY, h0 = S.dockH;
  const mv = ev => { S.dockH = Math.max(180, Math.min(window.innerHeight - 200, h0 + y0 - ev.clientY)); renderDock(); };
  const up = () => { document.removeEventListener('pointermove', mv); document.removeEventListener('pointerup', up); };
  document.addEventListener('pointermove', mv); document.addEventListener('pointerup', up);
});
