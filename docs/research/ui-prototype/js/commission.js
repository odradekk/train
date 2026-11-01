// 委托制造车辆（非敌对聚居地）与建造建筑（自有聚居地）共用的工作区
function openCommission(node, mode, plot = null) {
  const here = trainsAt(node);
  const sources = here.map(id => ({ id, on: true }));
  if (W.nodes[node].lease) sources.push({ id: 'lease', on: true });
  const list = mode === 'building' ? BLUEPRINTS.buildings : BLUEPRINTS.vehicles;
  const first = mode === 'building' ? list.find(b => b.plot === W.nodes[node].plots.find(p => p.id === plot)?.size) : list[0];
  S.workspace = { kind: 'commission', mode, node, plot, bp: first?.id || null, sources, q: '', filter: 'all' };
}

function sourceName(node, s) { return s.id === 'lease' ? `${nodeName(node)}的租用仓库` : W.trains[s.id].name; }
function sourceGoods(node, s) { return s.id === 'lease' ? W.nodes[node].lease.goods : W.trains[s.id].cargo; }

// 按来源顺序分配材料：先用玩家选定的列车和租用仓库，不足部分在当地市场买；自有聚居地只用自己的库存
function allocate(w, bp) {
  const n = W.nodes[w.node], rows = [];
  for (const [g, need] of Object.entries(bp.bom)) {
    let left = need; const from = [];
    if (w.mode === 'building') {
      const have = Math.min(n.stock[g]?.[0] || 0, left); if (have) from.push([`${n.name}库存`, have]); left -= have;
      rows.push({ g, need, from, buy: 0, cost: 0, short: left });
      continue;
    }
    for (const s of w.sources.filter(s => s.on)) {
      const have = Math.min(sourceGoods(w.node, s)[g] || 0, left); if (have) { from.push([sourceName(w.node, s), have]); left -= have; }
    }
    const m = n.market[g], canBuy = m ? Math.min(m[0], left) : 0;
    rows.push({ g, need, from, buy: canBuy, cost: canBuy * (m?.[1] || 0), short: left - canBuy });
  }
  return rows;
}

function commissionWs(w) {
  const n = W.nodes[w.node], isB = w.mode === 'building';
  const plotSize = isB ? n.plots.find(p => p.id === w.plot)?.size : null;
  const all = isB ? BLUEPRINTS.buildings : BLUEPRINTS.vehicles;
  const q = w.q.trim();
  const list = all.filter(b => (!q || (b.name || bpName(b)).includes(q)) && (w.filter === 'all' || (!isB && VEHICLE_TYPES[b.type].role === w.filter)));
  const groups = isB ? [['', list]] : [['本局蓝图库', list.filter(b => b.lib === '本局蓝图库')], ['内容包', list.filter(b => b.lib === '内容包')]];
  const left = groups.map(([label, items]) => (label ? `<div class="group-label">${label}</div>` : '') + items.map(b => {
    const ty = b.type ? VEHICLE_TYPES[b.type] : null, fits = !isB || b.plot === plotSize;
    const sub = isB ? `${b.plot}地块 · ${b.note}` : `${ty.size} · ${ty.traction ? `牵引 ${ty.traction}` : `载量 ${b.capacity || ty.capacity}`}`;
    return `<div class="row ${w.bp === b.id ? 'on' : ''}" ${fits ? `data-action="cm-bp" data-v="${b.id}"` : 'style="opacity:.45;cursor:not-allowed"'} title="${fits ? '' : `需要${b.plot}地块`}">
      <span style="color:${isB ? '#cfd5db' : ty.traction ? '#e0a22e' : '#a9b6c6'}">${icon(isB ? 'hammer' : 'train')}</span><div class="grow"><div class="title">${bpName(b)}</div><div class="sub">${fits ? sub : `需要${b.plot}地块`}</div></div></div>`;
  }).join('')).join('');

  const bp = all.find(b => b.id === w.bp);
  let right = `<div class="empty">从左侧选择${isB ? '要建的建筑' : '蓝图'}。</div>`, foot = '', ok = false, total = 0;
  if (bp) {
    const rows = allocate(w, bp), matCost = rows.reduce((a, r) => a + r.cost, 0), labor = isB ? 0 : bp.labor;
    total = matCost + labor; const short = rows.filter(r => r.short > 0);
    ok = !short.length && total <= W.funds;
    const ty = bp.type ? VEHICLE_TYPES[bp.type] : null;
    right = `<div class="section"><div style="display:flex;align-items:center;gap:10px"><h3 style="margin:0;font-size:18px">${bpName(bp)}</h3>${bp.lib ? `<span class="chip neutral">${bp.lib}</span>` : ''}</div>
      ${isB ? `<div class="muted" style="margin-top:4px">${bp.note} · 占${bp.plot}地块</div>` : `<div class="stats" style="grid-template-columns:repeat(4,1fr);margin-top:10px">${[[ty.traction ? '牵引力' : '载量', ty.traction || bp.capacity || ty.capacity], ['质量', ty.mass], ['长度', ty.length], ['尺寸', ty.size]].map(([l, x]) => `<div class="stat"><div class="label">${l}</div><div class="value">${x}</div></div>`).join('')}</div>`}</div>
      <div class="section"><div class="section-head"><h4>材料</h4><span class="muted small">${isB ? `只用${n.name}的库存` : `先用你的货物，不够的在${n.name}市场购买`}</span></div>
      <table class="grid"><tr><th>材料</th><th class="num">需要</th><th>来自</th>${isB ? '' : '<th class="num">当地购买</th><th class="num">费用</th>'}</tr>
      ${rows.map(r => `<tr><td>${goodTag(r.g)}</td><td class="num">${r.need}</td><td class="small">${r.from.map(([s, q]) => `${s} ×${q}`).join('，') || '<span class="faint">—</span>'}${r.short ? `<span class="neg"> 缺 ${r.short}</span>` : ''}</td>${isB ? '' : `<td class="num">${r.buy || '—'}</td><td class="num">${r.cost ? fmt(r.cost) : '—'}</td>`}</tr>`).join('')}</table>
      ${short.length ? `<div class="notice error" style="margin-top:10px">${icon('alert')}<div class="grow">${short.map(r => `${GOODS[r.g].name}还缺 ${r.short}`).join('，')}。${isB ? `运一些到${n.name}，或换一个建筑。` : `${n.name}市场的货不够，请用列车带来。`}</div></div>` : ''}</div>`;
    if (!isB && w.sources.length) right += `<div class="section"><div class="section-head"><h4>你的货物来源</h4><span class="muted small">按顺序使用</span></div>
      ${w.sources.map((s, i) => `<div style="display:flex;align-items:center;gap:8px;padding:3px 0"><span class="check ${s.on ? 'on' : ''}" data-action="cm-src" data-v="${i}" style="cursor:pointer">${s.on ? icon('check', 'sm') : ''}</span><span style="flex:1">${sourceName(w.node, s)}</span>
        <button class="btn ghost icon-only" style="height:24px;width:24px" data-action="cm-src-move" data-v="${i}:-1" ${i ? '' : 'disabled'} title="提前">${icon('up', 'sm')}</button><button class="btn ghost icon-only" style="height:24px;width:24px" data-action="cm-src-move" data-v="${i}:1" ${i < w.sources.length - 1 ? '' : 'disabled'} title="推后">${icon('down', 'sm')}</button></div>`).join('')}</div>`;
    else if (!isB) right += `<div class="section"><div class="small faint">没有你的列车或租用仓库在${n.name}，材料全部在当地购买。</div></div>`;
    right += `<div class="section"><div class="kv">${isB ? '' : `<dt>材料费</dt><dd>${fmt(matCost)}</dd><dt>工钱</dt><dd>${fmt(labor)}</dd>`}<dt><b>合计</b></dt><dd><b>${fmt(total)}</b></dd><dt>工期</dt><dd>约 ${bp.days} 天</dd><dt>完成后</dt><dd>${isB ? `${bpName(bp)}出现在${plotSize}地块` : `车辆停在${n.name}`}</dd></div>
      ${total > W.funds ? `<div class="field-error" style="margin-top:6px">资金不足，还差 ${fmt(total - W.funds)}</div>` : ''}</div>`;
    foot = `<span class="msg small muted">${isB ? '开工后材料立即从库存扣除。' : '委托后不能取消，材料和费用立即扣除。'}</span>`;
  }
  return `<div class="workspace" style="width:1080px;height:min(760px, calc(100vh - 92px))" role="dialog">
    <div class="ws-head"><h2>${isB ? `建造 <span class="muted">· ${n.name} · ${plotSize}地块</span>` : `委托制造 <span class="muted">· ${n.name}</span>`}</h2><button class="btn ghost icon-only" data-action="ws-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div class="ws-body"><div class="ws-col" style="width:380px"><div class="panel-tools" style="padding-top:12px"><input id="cm-q" type="search" placeholder="${isB ? '搜索建筑' : '搜索蓝图'}" value="${w.q}" data-input="cm-q"></div>
      ${isB ? '' : `<div class="filters">${[['all', '全部'], ['机车', '机车'], ['货车', '货车']].map(([k, l]) => `<button class="${w.filter === k ? 'on' : ''}" data-action="cm-filter" data-v="${k}">${l}</button>`).join('')}</div>`}
      <div class="scroll">${left}</div></div>
      <div class="ws-col" style="flex:1"><div class="scroll" style="padding:0">${right}</div></div></div>
    <div class="ws-foot">${foot || '<span class="msg"></span>'}<button class="btn" data-action="ws-close">取消</button>
      <button class="btn primary" data-action="cm-go" ${ok ? '' : 'disabled'}>${isB ? `${icon('hammer')} 开工` : `${icon('wrench')} 委托制造`}${bp ? ` · ${fmt(total)}` : ''}</button></div></div>`;
}

Object.assign(INPUTS, { 'cm-q': v => { S.workspace.q = v; } });
Object.assign(WS_ACTIONS, {
  'cm-bp': v => { S.workspace.bp = v; },
  'cm-filter': v => { S.workspace.filter = v; },
  'cm-src': v => { const s = S.workspace.sources[+v]; s.on = !s.on; },
  'cm-src-move': v => { const [i, d] = v.split(':').map(Number), s = S.workspace.sources; [s[i], s[i + d]] = [s[i + d], s[i]]; },
  'cm-go': () => {
    const w = S.workspace, n = W.nodes[w.node], isB = w.mode === 'building', bp = (isB ? BLUEPRINTS.buildings : BLUEPRINTS.vehicles).find(b => b.id === w.bp);
    const rows = allocate(w, bp), total = rows.reduce((a, r) => a + r.cost, 0) + (isB ? 0 : bp.labor);
    for (const r of rows) {
      if (isB) n.stock[r.g][0] -= r.need;
      else { let left = r.need - r.buy; for (const s of w.sources.filter(s => s.on)) { const store = sourceGoods(w.node, s), take = Math.min(store[r.g] || 0, left); if (take) { store[r.g] -= take; if (!store[r.g]) delete store[r.g]; left -= take; } } if (r.buy) n.market[r.g][0] -= r.buy; }
    }
    spend(total);
    if (isB) { n.plots = n.plots.filter(p => p.id !== w.plot); n.sites.push({ id: 's' + Date.now(), name: bpName(bp), plot: bp.plot, progress: 0, eta: `约 ${bp.days} 天` }); }
    else W.jobs.push({ id: 'j' + Date.now(), node: w.node, kind: '制造', what: bpName(bp), progress: 0, eta: `约 ${bp.days} 天` });
    S.workspace = null; S.tabs.node = isB ? 'buildings' : 'vehicles';
    toast('complete', isB ? `${n.name}开工建造${bpName(bp)}，约 ${bp.days} 天` : `已委托${n.name}制造${bpName(bp)}，约 ${bp.days} 天后完成`, { kind: 'node', id: w.node }, false);
  },
});
Object.assign(PANEL_ACTIONS, {
  'open-commission': () => openCommission(S.selection.id, 'vehicle'),
  'open-build': v => openCommission(S.selection.id, 'building', v),
});

function workTick() {
  for (const j of [...W.jobs]) {
    j.progress = Math.min(1, j.progress + 0.004 * S.speed);
    if (j.progress < 1) continue;
    W.jobs = W.jobs.filter(x => x !== j);
    if (j.kind === '制造') { const vid = 'v' + W.nextVehicle++, bp = BLUEPRINTS.vehicles.find(b => bpName(b) === j.what); W.vehicles[vid] = { type: bp?.type || 'wagonM', hp: 1 }; (W.parked[j.node] ||= []).push(vid); }
    toast('complete', `${nodeName(j.node)}的${j.kind}${j.what}已完成`, { kind: 'node', id: j.node });
  }
  for (const n of Object.values(W.nodes)) {
    for (const st of n.sites || []) st.progress = Math.min(0.99, st.progress + 0.002 * S.speed);
    if (n.founding) n.founding.progress = Math.min(0.99, n.founding.progress + 0.003 * S.speed);
  }
}
