// 居中工作区（编组、运行计划）与确认对话框
function renderDialog() {
  const host = document.getElementById('dialog-host'), d = S.dialog;
  if (!d) { host.innerHTML = ''; return; }
  host.innerHTML = `<div id="dialog-layer"><div class="dialog" role="dialog" aria-label="${d.title}"><h3>${d.title}</h3><div class="body">${typeof d.body === 'function' ? d.body() : d.body}</div>
    <div class="foot"><button class="btn" data-action="dlg-cancel">${d.cancel || '取消'}</button>${d.alt ? `<button class="btn" data-action="dlg-alt">${d.alt}</button>` : ''}
    <button class="btn ${d.danger ? 'danger solid' : 'primary'}" data-action="dlg-ok" id="dlg-ok" ${(typeof d.disabled === 'function' ? d.disabled() : d.disabled) ? 'disabled' : ''}>${d.confirm}</button></div></div></div>`;
  if (document.activeElement?.closest('.dialog') == null) document.getElementById('dlg-ok').focus();
}

function renderWorkspace() {
  const host = document.getElementById('workspace-host'), w = S.workspace;
  if (!w) { host.innerHTML = ''; return; }
  const bodyScroll = [...host.querySelectorAll('.scroll')].map(e => e.scrollTop);
  host.innerHTML = `<div id="scrim" data-action="ws-scrim"></div>` + ({ formation: formationWs, plan: planWs, commission: commissionWs, report: reportWs, library: libraryWs, refit: refitWs })[w.kind](w);
  host.querySelectorAll('.scroll').forEach((e, i) => e.scrollTop = bodyScroll[i] || 0);
}

// ---------- 编组 ----------
function openFormation(node, preselect, train = null) {
  const pool = [...(train ? W.trains[train].vehicles : []), ...(W.parked[node] || [])];
  const order = train ? [...preselect] : [...preselect].sort((a, b) => VEHICLE_TYPES[W.vehicles[b].type].traction - VEHICLE_TYPES[W.vehicles[a].type].traction);
  S.workspace = { kind: 'formation', node, pool, order, train, name: train ? W.trains[train].name : `列车 ${W.nextTrain}` };
}

function tractionVerdict(s) {
  if (!s.locos) return { cls: 'error', text: '至少需要一辆机车，否则列车无法行驶。', ok: false };
  const r = s.traction / (s.mass + s.capacity * 0.6);
  if (r >= 1.2) return { cls: 'success', text: '牵引充足，满载时也能保持速度。', ok: true };
  if (r >= 0.6) return { cls: 'warning', text: '满载时偏慢。可以加一辆机车或减少货车。', ok: true };
  return { cls: 'warning', text: '牵引不足，满载时会非常慢。', ok: true };
}

function formationWs(w) {
  const s = trainStats(w.order), v = w.order.length ? tractionVerdict(s) : null;
  const left = w.pool.map(id => {
    const on = w.order.includes(id);
    return `<div style="display:flex;gap:10px;align-items:center;cursor:pointer;padding:0 12px" data-action="form-toggle" data-v="${id}"><span class="check ${on ? 'on' : ''}">${on ? icon('check', 'sm') : ''}</span><div style="flex:1">${vehicleRow(id)}</div></div>`;
  }).join('') || '<div class="empty">这里没有可编组的车辆。</div>';
  const strip = w.order.map((id, i) => {
    const ty = VEHICLE_TYPES[W.vehicles[id].type];
    return `<div style="width:118px;border:1px solid var(--border);border-radius:6px;padding:8px;background:#1d2228">
      <div style="display:flex;justify-content:space-between;align-items:center"><span class="faint small">${i + 1}</span><span style="color:${ty.traction ? '#e0a22e' : '#a9b6c6'}">${icon('train')}</span></div>
      <div style="margin:6px 0 2px">${ty.name}</div><div class="small muted">${ty.traction ? `牵引 ${ty.traction}` : `载量 ${ty.capacity}`}</div>
      <div style="display:flex;gap:2px;margin-top:8px"><button class="btn ghost icon-only" style="height:26px;width:26px" data-action="form-move" data-v="${i}:-1" ${i ? '' : 'disabled'} title="前移">${icon('back', 'sm')}</button>
      <button class="btn ghost icon-only" style="height:26px;width:26px" data-action="form-move" data-v="${i}:1" ${i < w.order.length - 1 ? '' : 'disabled'} title="后移">${icon('chevron', 'sm')}</button><span style="flex:1"></span>
      <button class="btn ghost icon-only" style="height:26px;width:26px" data-action="form-toggle" data-v="${id}" title="移出">${icon('close', 'sm')}</button></div></div>`;
  }).join('');
  const blocked = !v || !v.ok;
  return `<div class="workspace" style="width:1040px;height:640px" role="dialog" aria-label="编组列车">
    <div class="ws-head"><h2>${w.train ? '调整编组' : '编组列车'} <span class="muted">· ${w.train ? W.trains[w.train].name : nodeName(w.node)}</span></h2><button class="btn ghost icon-only" data-action="ws-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div class="ws-body"><div class="ws-col" style="width:380px"><div class="col-head"><h4>停在${nodeName(w.node)}的车辆</h4><span class="muted small">勾选要编入的车辆</span></div><div class="scroll">${left}</div></div>
      <div class="ws-col" style="flex:1"><div class="col-head"><h4>编组顺序</h4><span class="muted small">顺序只影响外观，不影响牵引</span></div>
        <div style="padding:0 16px"><div style="display:flex;gap:8px;flex-wrap:wrap;min-height:150px;align-content:flex-start">${strip || '<div class="muted" style="padding:40px 0">从左侧勾选车辆。</div>'}</div></div>
        <div class="section"><div class="stats" style="grid-template-columns:repeat(5,1fr)">${[['车辆', s.count], ['总长', s.length], ['总质量', s.mass], ['牵引力', s.traction], ['载量', s.capacity]].map(([l, x]) => `<div class="stat"><div class="label">${l}</div><div class="value">${x}</div></div>`).join('')}</div>
          ${v ? `<div class="notice ${v.cls === 'error' ? 'error' : v.cls}" style="margin-top:10px">${icon(v.cls === 'success' ? 'check' : 'alert')}<div class="grow">${v.text}</div></div>` : ''}</div>
        <div class="section"><div class="field" style="max-width:320px"><label for="ws-name">列车名称</label><input id="ws-name" type="text" value="${w.name}" data-input="ws-name"></div></div></div></div>
    <div class="ws-foot"><span class="msg muted">${blocked ? (w.order.length ? '至少需要一辆机车。' : '还没有选择车辆。') : (w.train ? `移出的车辆会停在${nodeName(w.node)}。` : `编组后，${w.order.length} 辆车成为 ${w.name}，停在${nodeName(w.node)}。`)}</span>
      <button class="btn" data-action="ws-close">取消</button>${w.train ? `<button class="btn primary" data-action="form-go" data-v="0" ${blocked ? 'disabled' : ''}>保存编组</button>` : `<button class="btn" data-action="form-go" data-v="0" ${blocked ? 'disabled' : ''}>只编组</button>
      <button class="btn primary" data-action="form-go" data-v="1" ${blocked ? 'disabled' : ''}>编组并编辑计划</button>`}</div></div>`;
}

// ---------- 运行计划 ----------
function openPlan(trainId) {
  const t = W.trains[trainId];
  const draft = t.plan ? JSON.parse(JSON.stringify(t.plan)) : { loop: true, stops: t.at ? [{ node: t.at, pass: false, dwell: 30, rules: [] }] : [] };
  S.workspace = { kind: 'plan', train: trainId, draft, sel: 0, q: '' };
}

function stopProblems(stop) {
  const n = W.nodes[stop.node], out = [];
  if (!stop.pass && n.kind !== 'settlement') out.push(`${n.name}不是聚居地，只能作为途经点`);
  if (!stop.pass && n.relation === 'hostile') out.push(`${n.name}是敌对聚居地，不能停靠`);
  stop.rules.forEach((r, i) => {
    if ((r.action === 'buy' || r.action === 'sell') && !n.market) out.push(`规则 ${i + 1}：${n.name}是你的聚居地，没有市场，请用装载或卸载`);
    if ((r.action === 'load' || r.action === 'unload') && n.relation !== 'owned') out.push(`规则 ${i + 1}：在${n.name}装载或卸载需要先租用仓库`);
  });
  return out;
}
function planProblems(w) {
  const d = w.draft, out = [];
  if (!d.stops.filter(s => !s.pass).length) out.push({ text: '至少添加一个停靠站', at: null });
  planLegs(d).filter(g => !g.ok).forEach(g => out.push({ text: `从${nodeName(g.from)}到${nodeName(g.to)}没有建成的线路`, at: g.index }));
  d.stops.forEach((s, i) => stopProblems(s).forEach(p => out.push({ text: `第 ${i + 1} 站：${p}`, at: i })));
  return out;
}

function planWs(w) {
  const d = w.draft, t = W.trains[w.train], legs = planLegs(d), probs = planProblems(w);
  const cards = d.stops.map((s, i) => {
    const legBad = legs.some(g => g.index === i && !g.ok), bad = stopProblems(s)[0] || (legBad ? '到下一站没有建成的线路' : '');
    return `<div class="row ${w.sel === i ? 'on' : ''}" style="align-items:flex-start;border:1px solid ${bad ? '#6b3532' : 'transparent'};margin-bottom:4px" data-action="plan-sel" data-v="${i}">
      <span class="chip ${s.pass ? 'neutral' : 'warning'}" style="width:26px;justify-content:center;padding:0;margin-top:2px">${i + 1}</span>
      <div class="grow"><div class="title"><b>${nodeName(s.node)}</b> <span class="muted small">${s.pass ? '途经点' : `停靠 ${s.dwell} 秒`}</span></div>
        ${s.pass ? '' : s.rules.length ? s.rules.map(r => `<div class="sub">${ruleSentence(r)}</div>`).join('') : '<div class="sub faint">没有交易规则</div>'}
        ${bad ? `<div class="small" style="color:#ec8a84;margin-top:2px">${icon('alert', 'sm')} ${bad}</div>` : ''}</div>
      <div style="display:flex;flex-direction:column">
        <button class="btn ghost icon-only" style="height:22px;width:26px" data-action="plan-move" data-v="${i}:-1" ${i ? '' : 'disabled'} title="上移">${icon('up', 'sm')}</button>
        <button class="btn ghost icon-only" style="height:22px;width:26px" data-action="plan-move" data-v="${i}:1" ${i < d.stops.length - 1 ? '' : 'disabled'} title="下移">${icon('down', 'sm')}</button></div>
      <button class="btn ghost icon-only" style="height:26px;width:26px" data-action="plan-del" data-v="${i}" title="删除这一站">${icon('trash', 'sm')}</button></div>`;
  }).join('');
  const q = w.q.trim(), sugg = q ? Object.entries(W.nodes).filter(([, n]) => n.name.includes(q)).slice(0, 6) : [];
  const running = t.running;
  return `<div class="workspace" style="width:min(1440px, calc(100vw - 48px));height:calc(100vh - 92px)" role="dialog" aria-label="运行计划">
    <div class="ws-head"><h2>运行计划 <span class="muted">· ${t.name}</span></h2>
      <button class="btn ghost" data-action="todo" data-v="从其他列车复制计划">复制其他列车的计划…</button>
      <button class="btn ghost icon-only" data-action="ws-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div class="ws-body">
      <div class="ws-col" style="width:380px"><div class="col-head"><h4>停靠顺序</h4><label class="switch ${d.loop ? 'on' : ''}" data-action="plan-loop"><i></i>循环运行</label></div>
        <div class="scroll">${cards || '<div class="empty">还没有停靠站。<br>在下方搜索地名，或在地图上点地点。</div>'}
          ${d.loop && d.stops.length > 1 ? `<div class="small faint" style="padding:4px 12px">${icon('swap', 'sm')} 最后一站之后回到第 1 站</div>` : ''}</div>
        <div style="padding:12px;border-top:1px solid var(--divider);position:relative">
          <input id="plan-q" type="search" placeholder="添加停靠站：输入地名" value="${w.q}" data-input="plan-q" style="width:100%">
          ${sugg.length ? `<div class="popover menu" style="bottom:52px;left:12px;right:12px">${sugg.map(([id, n]) => `<button data-action="plan-add" data-v="${id}"><span class="grow">${n.name}</span><span class="muted small">${n.kind === 'settlement' ? `${RELATION[n.relation].label}聚居地` : n.kind === 'empty' ? '空节点，只能途经' : '生物聚落，只能途经'}</span></button>`).join('')}</div>` : ''}
        </div></div>
      <div class="ws-col" style="flex:1;position:relative;background:#5f6b55">
        <svg viewBox="0 0 640 520" preserveAspectRatio="xMidYMid meet" style="width:100%;height:100%">${routeMapSvg(d, 640, 520)}</svg>
        <div style="position:absolute;top:12px;left:12px;right:12px;display:flex;gap:8px;flex-wrap:wrap">
          <span class="chip info" style="background:rgba(24,28,33,.9)">点地点：添加为停靠站</span><span class="chip info" style="background:rgba(24,28,33,.9)">Shift + 点：添加为途经点</span>
          <span class="chip warning" style="background:rgba(24,28,33,.9)">黄线：计划经过的线路</span></div></div>
      <div class="ws-col" style="width:420px">${stopEditor(w)}</div>
    </div>
    <div class="ws-foot"><div class="msg">${probs.length ? `<span style="color:#ec8a84">${icon('alert', 'sm')} ${probs[0].text}${probs.length > 1 ? `，另有 ${probs.length - 1} 个问题` : ''}</span>` : `<span class="muted">${d.stops.length} 站 · ${d.loop ? '循环运行' : '单次运行，到最后一站后停车'}</span>`}</div>
      <button class="btn" data-action="ws-close">取消</button>
      ${running ? `<button class="btn primary" data-action="plan-save" data-v="0" ${probs.length ? 'disabled' : ''}>保存</button>`
        : `<button class="btn" data-action="plan-save" data-v="0" ${probs.length ? 'disabled' : ''}>只保存</button><button class="btn primary" data-action="plan-save" data-v="1" ${probs.length ? 'disabled' : ''}>${icon('play')} 保存并发车</button>`}</div></div>`;
}

function stopEditor(w) {
  const s = w.draft.stops[w.sel];
  if (!s) return `<div class="empty">选择左侧的一站来设置停留时间和交易规则。</div>`;
  const n = W.nodes[s.node], probs = stopProblems(s), owned = n.relation === 'owned';
  const allowed = owned ? ['load', 'unload'] : n.market ? ['buy', 'sell'] : [];
  const goodsHere = n.market ? Object.keys(n.market) : n.stock ? Object.keys(n.stock) : Object.keys(GOODS);
  let h = `<div class="col-head"><h4>第 ${w.sel + 1} 站 · ${n.name}</h4></div><div class="scroll" style="padding:0 16px 16px">`;
  h += `<div class="field"><label>类型</label><div class="seg"><button class="${s.pass ? '' : 'on'}" data-action="stop-pass" data-v="0">停靠站</button><button class="${s.pass ? 'on' : ''}" data-action="stop-pass" data-v="1">途经点</button></div>
    <div class="small faint">${s.pass ? '途经点只经过、不停车，用来指定路线。' : '列车在这里停车并执行交易规则。'}</div></div>`;
  if (!s.pass) {
    h += `<div class="field" style="margin-top:14px"><label for="stop-dwell">停留时间（秒）</label><input id="stop-dwell" type="number" min="0" value="${s.dwell}" data-input="stop-dwell" style="width:120px">
      <div class="small faint">规则提前完成时仍停满这段时间。</div></div>`;
    h += `<div class="section-head" style="margin-top:18px"><h4>交易规则</h4><span class="muted small">${owned ? '自有聚居地：装载、卸载' : n.market ? '市场：买入、卖出' : ''}</span></div>`;
    h += s.rules.map((r, i) => `<div style="border:1px solid var(--border);border-radius:6px;padding:10px;margin-bottom:8px;background:#1d2228">
      <div style="display:flex;gap:8px;align-items:center"><select id="rg-${i}" data-change="rule-good" data-i="${i}" aria-label="货物" style="flex:1">${goodsHere.map(g => `<option value="${g}" ${g === r.good ? 'selected' : ''}>${GOODS[g].name}</option>`).join('')}</select>
        <div class="seg">${['buy', 'sell', 'load', 'unload'].filter(a => allowed.includes(a)).map(a => `<button class="${r.action === a ? 'on' : ''}" data-action="rule-act" data-v="${i}:${a}">${ACTIONS[a].label}</button>`).join('')}</div>
        <button class="btn ghost icon-only" data-action="rule-del" data-v="${i}" title="删除规则">${icon('trash', 'sm')}</button></div>
      <div style="display:flex;gap:8px;margin-top:8px"><div class="field" style="flex:1"><label for="rt-${i}">目标持有量</label><input id="rt-${i}" type="number" min="0" value="${r.target}" data-input="rule-target" data-i="${i}"></div>
        ${r.action === 'buy' || r.action === 'sell' ? `<div class="field" style="flex:1"><label for="rl-${i}">${r.action === 'buy' ? '最高单价' : '最低单价'}</label><input id="rl-${i}" type="number" min="0" placeholder="不限" value="${r.limit ?? ''}" data-input="rule-limit" data-i="${i}"></div>` : ''}</div>
      <div class="small muted" style="margin-top:6px">${ruleSentence(r)}。</div></div>`).join('');
    h += allowed.length ? `<button class="btn block" data-action="rule-add">${icon('plus')} 添加交易规则</button>` : '';
  }
  if (probs.length) h += `<div style="margin-top:12px">${probs.map(p => `<div class="notice error" style="margin-bottom:6px">${icon('alert')}<div class="grow">${p}</div></div>`).join('')}</div>`;
  return h + `</div>`;
}

function startTrain(id) {
  const t = W.trains[id];
  t.running = true;
  if (t.at && t.plan.stops[0].node === t.at) { t.stopIndex = 0; t.status = 'loading'; t.dwellLeft = 6; applyRules(id, t, t.plan.stops[0]); }
  else if (t.at) { t.stopIndex = -1; departTo(t, t.plan.stops[0].node); }
}

Object.assign(INPUTS, {
  'ws-name': v => { S.workspace.name = v; },
  'plan-q': v => { S.workspace.q = v; },
  'stop-dwell': v => { S.workspace.draft.stops[S.workspace.sel].dwell = Math.max(0, parseInt(v) || 0); },
  'rule-good': (v, el) => { S.workspace.draft.stops[S.workspace.sel].rules[+el.dataset.i].good = v; },
  'rule-target': (v, el) => { S.workspace.draft.stops[S.workspace.sel].rules[+el.dataset.i].target = Math.max(0, parseInt(v) || 0); },
  'rule-limit': (v, el) => { S.workspace.draft.stops[S.workspace.sel].rules[+el.dataset.i].limit = v === '' ? null : Math.max(0, parseInt(v) || 0); },
});

function addStop(id, pass) {
  const w = S.workspace, n = W.nodes[id];
  const forcedPass = n.kind !== 'settlement' || n.relation === 'hostile';
  w.draft.stops.push({ node: id, pass: pass || forcedPass, dwell: 30, rules: [] });
  w.sel = w.draft.stops.length - 1; w.q = '';
  if (forcedPass && !pass) toast('info', `${n.name}不能停靠，已作为途经点添加`, null, false);
}

const WS_ACTIONS = {
  'dlg-cancel': () => { const d = S.dialog; S.dialog = null; d.onCancel?.(); },
  'dlg-ok': () => { const d = S.dialog; S.dialog = null; d.onConfirm(true); },
  'dlg-alt': () => { const d = S.dialog; S.dialog = null; d.onConfirm(false); },
  'ws-close': () => { S.workspace = null; },
  'ws-scrim': () => { /* 点遮罩不关闭，避免误触丢失编辑 */ return false; },
  'form-toggle': id => { const o = S.workspace.order; const i = o.indexOf(id); i >= 0 ? o.splice(i, 1) : o.push(id); },
  'form-move': v => { const [i, d] = v.split(':').map(Number), o = S.workspace.order; [o[i], o[i + d]] = [o[i + d], o[i]]; },
  'form-go': v => {
    const w = S.workspace;
    if (w.train) {
      const t = W.trains[w.train], out = t.vehicles.filter(x => !w.order.includes(x));
      W.parked[w.node] = [...(W.parked[w.node] || []).filter(x => !w.order.includes(x)), ...out];
      t.vehicles = [...w.order]; t.name = w.name.trim() || t.name; S.workspace = null;
      toast('complete', `${t.name} 的编组已更新`, { kind: 'train', id: w.train }, false); return;
    }
    const id = 't' + W.nextTrain++;
    W.trains[id] = { name: w.name.trim() || `列车 ${id.slice(1)}`, vehicles: [...w.order], running: false, status: 'idle', at: w.node, cargo: {}, plan: null, report: null };
    W.parked[w.node] = W.parked[w.node].filter(x => !w.order.includes(x));
    S.workspace = null; S.parkedSel.clear();
    S.selection = { kind: 'train', id }; S.tabs.train = 'overview';
    toast('complete', `${W.trains[id].name} 编组完成，停在${nodeName(w.node)}`, { kind: 'train', id });
    if (v === '1') openPlan(id);
  },
  'plan-sel': v => { S.workspace.sel = +v; },
  'plan-loop': () => { S.workspace.draft.loop = !S.workspace.draft.loop; },
  'plan-move': v => { const [i, d] = v.split(':').map(Number), s = S.workspace.draft.stops; [s[i], s[i + d]] = [s[i + d], s[i]]; S.workspace.sel = i + d; },
  'plan-del': v => { const w = S.workspace; w.draft.stops.splice(+v, 1); w.sel = Math.min(w.sel, w.draft.stops.length - 1); },
  'plan-add': v => addStop(v, false),
  'plan-add-node': (v, el, e) => { if (W.nodes[v].kind === 'lair' && !e.shiftKey) { toast('info', '生物聚落只能作为途经点：按住 Shift 点击', null, false); return; } addStop(v, e.shiftKey); },
  'stop-pass': v => { const s = S.workspace.draft.stops[S.workspace.sel]; s.pass = v === '1'; },
  'rule-add': () => {
    const s = S.workspace.draft.stops[S.workspace.sel], n = W.nodes[s.node];
    const goods = n.market ? Object.keys(n.market) : Object.keys(n.stock || GOODS);
    const good = goods.find(g => !s.rules.some(r => r.good === g)) || goods[0];
    s.rules.push({ good, action: n.relation === 'owned' ? 'load' : 'buy', target: 20, limit: null });
  },
  'rule-act': v => { const [i, a] = v.split(':'); const r = S.workspace.draft.stops[S.workspace.sel].rules[+i]; r.action = a; if (a === 'sell' || a === 'unload') r.target = 0; },
  'rule-del': v => { S.workspace.draft.stops[S.workspace.sel].rules.splice(+v, 1); },
  'plan-save': v => {
    const w = S.workspace, t = W.trains[w.train], wasEmpty = !t.plan;
    t.plan = w.draft; if (t.stopIndex == null || t.stopIndex >= t.plan.stops.length) t.stopIndex = 0;
    S.workspace = null;
    if (v === '1') { startTrain(w.train); toast('complete', `${t.name} 已发车，前往${nodeName(t.target || t.at)}`, { kind: 'train', id: w.train }); }
    else toast('complete', `${t.name} 的运行计划已保存${wasEmpty && !t.running ? '，可以发车了' : ''}`, { kind: 'train', id: w.train }, false);
  },
};
