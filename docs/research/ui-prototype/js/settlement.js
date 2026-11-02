// 聚居地检查器（自有、非敌对、敌对）与空节点建城
function inspectSettlement(id) {
  const n = W.nodes[id], rel = RELATION[n.relation], here = trainsAt(id);
  let html = inspHead({ glyph: 'city', color: rel.color, name: n.name, chips: `<span class="chip ${rel.cls}">${rel.label}</span>`, meta: `${n.type} · 人口 ${fmt(n.pop)} · 站台 ${here.length}/${n.platforms}` });
  if (n.relation === 'hostile') return html + `<div class="insp-body">${hostileBody(id, n)}</div>` + (n.surrendered ? `<div class="insp-foot"><button class="btn primary" data-action="occupy">${icon('flag')} 占领${n.name}…</button></div>` : '');
  const parked = W.parked[id] || [];
  const T = n.relation === 'owned'
    ? [['overview', '概况'], ['goods', '库存'], ['buildings', '建筑', n.buildings.some(b => b.state === 'blocked')], ['vehicles', '车辆', parked.length > 0]]
    : [['overview', '概况'], ['goods', '市场'], ['vehicles', '车辆', parked.length > 0], ['lease', '仓库', n.lease && n.lease.daysLeft <= 2]];
  html += tabs('node', T) + `<div class="insp-body">${bannerHtml()}`;
  const tab = S.tabs.node;
  if (tab === 'overview') html += n.relation === 'owned' ? ownedOverview(id, n, here) : neutralOverview(id, n, here);
  else if (tab === 'goods') html += n.relation === 'owned' ? ownedStock(id, n, here) : marketTab(id, n, here);
  else if (tab === 'buildings') html += buildingsTab(id, n);
  else if (tab === 'vehicles') html += vehiclesTab(id, n, here);
  else if (tab === 'lease') html += leaseTab(id, n);
  return html + `</div>`;
}

function trainsHere(id, n, here) {
  if (!here.length) return '';
  return `<div class="section"><div class="section-head"><h4>在站列车</h4><span class="muted">${here.length}/${n.platforms}</span></div>${here.map(tid => {
    const t = W.trains[tid]; return `<div class="row" data-action="select" data-v="train:${tid}"><span class="chip ${STATUS[t.status].cls}">${STATUS[t.status].label}</span><div class="grow">${t.name}</div>${t.plan ? '' : '<span class="small" style="color:#e9b45a">没有计划</span>'}</div>`;
  }).join('')}</div>`;
}
function jobsHere(id) {
  const jobs = W.jobs.filter(j => j.node === id);
  if (!jobs.length) return '';
  return `<div class="section"><div class="section-head"><h4>进行中的委托</h4><span class="muted">${jobs.length}</span></div>${jobs.map(j => `<div style="padding:4px 0">
    <div style="display:flex;justify-content:space-between"><span>${j.kind} ${j.what}</span><span class="muted small">${Math.round(j.progress * 100)}% · ${j.eta}</span></div>
    <div class="bar" style="margin-top:4px"><i style="width:${j.progress * 100}%"></i></div></div>`).join('')}
    <div class="small faint" style="margin-top:6px">完成后车辆停在这里，并会收到通知。</div></div>`;
}

function neutralOverview(id, n, here) {
  let h = trainsHere(id, n, here) + jobsHere(id);
  if (n.lease) h += `<div class="section"><div class="section-head"><h4>你的租用仓库</h4><button class="btn ghost small" data-action="tab" data-v="node:lease">管理 ${icon('chevron', 'sm')}</button></div>
    <div class="muted">${leaseUsed(n)} / ${n.lease.capacity} · 剩 ${n.lease.daysLeft} 天到期</div></div>`;
  const short = Object.entries(n.market).filter(([, m]) => m[0] === 0), plenty = Object.entries(n.market).filter(([, m]) => m[0] > 150);
  if (short.length || plenty.length) h += `<div class="section"><div class="section-head"><h4>市场动向</h4><button class="btn ghost small" data-action="tab" data-v="node:goods">查看市场 ${icon('chevron', 'sm')}</button></div>
    ${short.map(([g, m]) => `<div style="display:flex;justify-content:space-between;padding:4px 0">${goodTag(g)}<span style="color:#e9b45a">缺货 · 收购价 ${m[2]}</span></div>`).join('')}
    ${plenty.map(([g, m]) => `<div style="display:flex;justify-content:space-between;padding:4px 0">${goodTag(g)}<span class="muted">存货多 · 售价 ${m[1]}</span></div>`).join('')}</div>`;
  h += `<div class="section"><div class="section-head"><h4>可用服务</h4></div>
    <div class="row" data-action="tab" data-v="node:vehicles"><span>${icon('train')}</span><div class="grow">现车出售 ${n.forSale.length} 辆</div>${icon('chevron', 'sm')}</div>
    ${n.services ? `<div class="row" data-action="tab" data-v="node:vehicles"><span>${icon('wrench')}</span><div class="grow">委托制造、修理、改装、拆解</div>${icon('chevron', 'sm')}</div>` : ''}
    <div class="row" data-action="tab" data-v="node:lease"><span>${icon('warehouse')}</span><div class="grow">租用仓库</div>${icon('chevron', 'sm')}</div></div>`;
  h += `<div class="section"><div class="section-head"><h4>更多操作</h4></div><button class="btn danger" data-action="declare-war">${icon('sword')} 宣战…</button></div>`;
  return h;
}

// ---------- 自有聚居地 ----------
function ownedOverview(id, n, here) {
  const low = Object.entries(n.stock).filter(([, [q, tgt]]) => q < tgt), blocked = n.buildings.filter(b => b.state === 'blocked');
  let h = `<div class="section"><div class="stats"><div class="stat"><div class="label">常住人口</div><div class="value">${fmt(n.pop)}</div></div>
    <div class="stat"><div class="label">基本需求满足率</div><div class="value">${Math.round(n.needs.basic * 100)}%</div></div>
    <div class="stat"><div class="label">改善需求满足率</div><div class="value">${Math.round(n.needs.improved * 100)}%</div></div></div>
    <div style="margin-top:10px"><div style="display:flex;justify-content:space-between" class="small"><span class="muted">劳动力</span><span>${n.labor.used} / ${n.labor.total} 已分配</span></div><div class="bar" style="margin-top:4px"><i style="width:${n.labor.used / n.labor.total * 100}%"></i></div></div></div>`;
  if (low.length || blocked.length) h += `<div class="section"><div class="section-head"><h4>需要处理</h4></div>
    ${blocked.map(b => `<div class="notice warning" style="margin-bottom:6px;cursor:pointer" data-action="open-building" data-v="${b.id}">${icon('alert')}<div class="grow">${b.name}停工：${b.reason}</div>${icon('chevron', 'sm')}</div>`).join('')}
    ${low.map(([g, [q, tgt]]) => `<div class="notice warning" style="margin-bottom:6px;cursor:pointer" data-action="tab" data-v="node:goods">${icon('alert')}<div class="grow">${GOODS[g].name}低于补货目标：${q} / ${tgt}</div>${icon('chevron', 'sm')}</div>`).join('')}</div>`;
  h += trainsHere(id, n, here);
  if (n.sites.length) h += `<div class="section"><div class="section-head"><h4>工地</h4></div>${n.sites.map(s => siteRow(s)).join('')}</div>`;
  h += `<div class="section"><div class="section-head"><h4>防御</h4></div>${n.defense.towers ? `<div>防御塔 ${n.defense.towers} 座</div>` : `<div class="muted">没有防御建筑。世界生物和敌对聚居地的列车可以直接攻击这里。</div>
    <button class="btn" style="margin-top:8px" data-action="tab" data-v="node:buildings">${icon('hammer')} 在空地块上建造防御塔</button>`}</div>`;
  return h;
}
const siteRow = s => `<div style="padding:4px 0"><div style="display:flex;justify-content:space-between"><span>${icon('hammer', 'sm')} 正在建造 ${s.name} <span class="muted small">${s.plot}地块</span></span><span class="muted small">${Math.round(s.progress * 100)}% · ${s.eta}</span></div><div class="bar" style="margin-top:4px"><i style="width:${s.progress * 100}%"></i></div></div>`;

function ownedStock(id, n, here) {
  const used = Object.values(n.stock).reduce((a, [q]) => a + q, 0);
  const rows = Object.entries(n.stock).map(([g, [q, tgt, d]]) => {
    const open = S.market === g;
    return `<tr class="click ${open ? 'on' : ''}" data-action="market-row" data-v="${g}"><td>${goodTag(g)}</td><td class="num">${q}</td><td class="num muted">${tgt}</td>
      <td class="num ${d > 0 ? 'pos' : d < 0 ? 'neg' : 'muted'}">${d > 0 ? '+' : ''}${d || '—'}</td><td style="width:20px">${q < tgt ? `<span style="color:#e9b45a" title="低于补货目标">${icon('alert', 'sm')}</span>` : ''}</td></tr>
      ${open ? `<tr><td colspan="5" style="padding:0;height:auto;white-space:normal">${transferBox(id, n.name, g, q, here, 'stock')}</td></tr>` : ''}`;
  }).join('');
  return `<div class="section"><div style="display:flex;justify-content:space-between" class="small"><span class="muted">仓储</span><span>${used} / ${n.capacity}</span></div><div class="bar ${used / n.capacity > .9 ? 'warn' : ''}" style="margin-top:4px"><i style="width:${used / n.capacity * 100}%"></i></div></div>
    <div class="section"><table class="grid"><tr><th>货物</th><th class="num">库存</th><th class="num">补货目标</th><th class="num">每天</th><th></th></tr>${rows}</table>
    <div class="small faint" style="margin-top:8px">补货目标是聚居地为居民和生产准备的数量，由聚居地自动计算。点一行货物在库存和列车之间装卸。</div></div>`;
}

// 库存与在站列车之间装卸，不涉及价格；自有库存和租用仓库共用
function transferBox(id, place, g, stockQty, here, src, seg = null) {
  if (!here.length) return `<div style="padding:12px"><div class="notice">${icon('info')}<div class="grow">没有你的列车停在${nodeName(id)}。列车到站后才能装卸。</div></div></div>`;
  const tid = here.includes(S.trade.train) ? S.trade.train : here[0]; S.trade.train = tid;
  const t = W.trains[tid], cap = trainStats(t.vehicles).capacity - cargoTotal(t), have = t.cargo[g] || 0;
  const act = S.trade.action === 'unload' ? 'unload' : 'load';
  const max = act === 'load' ? Math.min(stockQty, cap) : have, qty = Math.min(S.trade.qty, Math.max(max, 0));
  const reason = act === 'load' ? (!stockQty ? `${place}没有${GOODS[g].name}` : cap <= 0 ? `${t.name}已装满` : '') : (!have ? `${t.name}上没有${GOODS[g].name}` : '');
  const done = S.trade.done ? `<div class="small pos" style="margin-top:6px">${icon('check', 'sm')} ${S.trade.done}</div>` : '';
  return `<div style="padding:12px;background:#1d2228;border-bottom:1px solid var(--divider)">
    <div style="display:flex;gap:8px;align-items:center;margin-bottom:10px;flex-wrap:wrap">${seg || `<div class="seg"><button class="${act === 'load' ? 'on' : ''}" data-action="trade-act" data-v="load">装上列车</button><button class="${act === 'unload' ? 'on' : ''}" data-action="trade-act" data-v="unload">卸到库存</button></div>`}
      <select id="trade-train" data-change="trade-train" aria-label="列车" style="flex:1;min-width:200px">${here.map(x => `<option value="${x}" ${x === tid ? 'selected' : ''}>${W.trains[x].name}（车上 ${W.trains[x].cargo[g] || 0}，余量 ${trainStats(W.trains[x].vehicles).capacity - cargoTotal(W.trains[x])}）</option>`).join('')}</select></div>
    <div style="display:flex;gap:8px;align-items:center"><label class="muted" for="trade-qty">数量</label><input id="trade-qty" type="number" min="0" value="${qty}" data-input="trade-qty" style="width:90px"><button class="btn" data-action="trade-max">最大 ${Math.max(max, 0)}</button></div>
    <button class="btn primary block" style="margin-top:10px" data-action="transfer-go" data-v="${g}:${src}" ${reason || !qty ? 'disabled' : ''}>${act === 'load' ? '装上' : '卸下'} ${qty} ${GOODS[g].name}</button>
    ${reason ? `<div class="field-error" style="margin-top:6px">${reason}</div>` : ''}${done}</div>`;
}

function buildingsTab(id, n) {
  const open = S.building;
  let h = `<div class="section"><div class="section-head"><h4>建筑</h4><span class="muted small">点建筑设置生产</span></div>`;
  h += n.buildings.map(b => {
    if (b.station) return `<div class="row" style="cursor:default"><span>${icon('train')}</span><div class="grow"><div class="title">${b.name}</div><div class="sub">${b.plot}地块 · 站台 ${n.platforms}</div></div></div>`;
    const p = PROCESSES[b.process], st = !b.enabled ? ['neutral', '停用'] : b.state === 'blocked' ? ['warning', '停工'] : ['success', '生产中'];
    let row = `<div class="row ${open === b.id ? 'on' : ''}" data-action="open-building" data-v="${b.id}"><span>${icon('hammer')}</span><div class="grow"><div class="title">${b.name} <span class="muted small">${b.plot}地块</span></div><div class="sub">${p.name} · 每天约 ${p.per} ${GOODS[p.out].name}</div></div><span class="chip ${st[0]}">${st[1]}</span>${icon(open === b.id ? 'chevdown' : 'chevron', 'sm')}</div>`;
    if (open === b.id) row += `<div style="padding:10px 12px 14px;background:#1d2228;border-radius:4px;margin-bottom:6px">
      ${b.state === 'blocked' && b.enabled ? `<div class="notice warning" style="margin-bottom:10px">${icon('alert')}<div class="grow">${b.reason}<div class="small muted" style="margin-top:2px">可以调高它的优先级，或停用其他建筑让出劳动力。</div></div></div>` : ''}
      <div class="field"><label for="proc-${b.id}">生产流程</label><select id="proc-${b.id}" data-change="building-process" data-v="${b.id}">${b.processes.map(k => `<option value="${k}" ${k === b.process ? 'selected' : ''}>${PROCESSES[k].name}：每天约 ${PROCESSES[k].per} ${GOODS[PROCESSES[k].out].name}${PROCESSES[k].input ? `，消耗 ${Object.entries(PROCESSES[k].input).map(([g, q]) => `${GOODS[g].name} ${q}`).join('、')}` : ''}</option>`).join('')}</select></div>
      <div style="display:flex;gap:16px;align-items:flex-end;margin-top:12px"><label class="switch ${b.enabled ? 'on' : ''}" data-action="building-toggle" data-v="${b.id}"><i></i>${b.enabled ? '启用' : '已停用'}</label>
        <div class="field"><label>优先级</label><div class="seg">${[['low', '低'], ['mid', '中'], ['high', '高']].map(([k, l]) => `<button class="${b.priority === k ? 'on' : ''}" data-action="building-priority" data-v="${b.id}:${k}">${l}</button>`).join('')}</div></div></div>
      <div class="small faint" style="margin-top:6px">劳动力或原料不够时，优先级高的建筑先得到。改动立即生效。</div>
      <div class="kv small" style="margin-top:10px"><dt>需要劳动力</dt><dd>${p.labor}</dd><dt>产出</dt><dd>每天约 ${p.per} ${GOODS[p.out].name}</dd></div>
      <button class="btn danger" style="margin-top:12px;height:28px" data-action="demolish" data-v="${b.id}">拆除${b.name}…</button></div>`;
    return row;
  }).join('') + `</div>`;
  if (n.sites.length) h += `<div class="section"><div class="section-head"><h4>工地</h4></div>${n.sites.map(s => siteRow(s)).join('')}</div>`;
  h += `<div class="section"><div class="section-head"><h4>空地块</h4><span class="muted small">${n.plots.length}</span></div>${n.plots.map(p => `<div class="row" style="cursor:default"><span class="faint">${icon('plus')}</span><div class="grow"><div class="title">${p.size}地块</div></div><button class="btn" data-action="open-build" data-v="${p.id}">${icon('hammer')} 建造…</button></div>`).join('') || '<div class="muted">没有空地块。</div>'}</div>`;
  return h;
}

// ---------- 车辆（自有、非敌对共用） ----------
function vehiclesTab(id, n, here) {
  const parked = W.parked[id] || [], sel = [...S.parkedSel].filter(v => parked.includes(v));
  let h = jobsHere(id);
  h += `<div class="section"><div class="section-head"><h4>停在这里的车辆</h4><span class="muted small">未编入列车</span></div>`;
  if (!parked.length) h += `<div class="muted">没有未编组的车辆。买下现车或委托制造后，车辆会停在这里。</div>`;
  else {
    h += parked.map(v => `<div style="display:flex;gap:10px;align-items:center;cursor:pointer" data-action="parked-toggle" data-v="${v}"><span class="check ${S.parkedSel.has(v) ? 'on' : ''}">${S.parkedSel.has(v) ? icon('check', 'sm') : ''}</span><div style="flex:1">${vehicleRow(v)}</div></div>`).join('');
    const repair = sel.reduce((a, v) => a + repairCost(v), 0), sale = sel.reduce((a, v) => a + Math.round(vehiclePrice(v) * 0.8), 0);
    h += `<button class="btn primary block" style="margin-top:8px" data-action="open-formation" ${sel.length ? '' : 'disabled'}>${icon('train')} 用所选 ${sel.length} 辆编组列车</button>`;
    if (n.relation !== 'owned') h += `<div style="display:flex;gap:8px;margin-top:8px">
      <button class="btn" style="flex:1" data-action="repair-parked" ${sel.length && repair ? '' : 'disabled'}>${icon('wrench')} 修理${repair ? ` · ${fmt(repair)}` : ''}</button>
      <button class="btn" style="flex:1" data-action="sell-parked" ${sel.length ? '' : 'disabled'}>出售${sel.length ? ` · ${fmt(sale)}` : ''}</button>
      <button class="btn danger" data-action="scrap-parked" ${sel.length ? '' : 'disabled'}>拆解…</button></div>
      ${sel.length && !repair ? '<div class="small faint" style="margin-top:6px">所选车辆没有损伤，不需要修理。</div>' : ''}`;
  }
  h += `</div>`;
  if (n.relation !== 'owned') {
    const sale = n.forSale.filter(v => W.vehicles[v].sale), total = [...S.buySel].reduce((a, v) => a + vehiclePrice(v), 0);
    h += `<div class="section"><div class="section-head"><h4>在售现车</h4><span class="muted small">${sale.length} 辆</span></div>`;
    h += sale.length ? sale.map(v => `<div style="display:flex;gap:10px;align-items:center;cursor:pointer" data-action="buy-toggle" data-v="${v}"><span class="check ${S.buySel.has(v) ? 'on' : ''}">${S.buySel.has(v) ? icon('check', 'sm') : ''}</span><div style="flex:1">${vehicleRow(v, '', `<b style="width:56px;text-align:right">${fmt(vehiclePrice(v))}</b>`)}</div></div>`).join('') : '<div class="muted">现在没有在售的车辆。</div>';
    if (sale.length) h += `<div style="display:flex;justify-content:space-between;margin-top:8px"><span class="muted">已选 ${S.buySel.size} 辆</span><span>合计 <b>${fmt(total)}</b></span></div>
      <button class="btn primary block" style="margin-top:8px" data-action="buy-vehicles" ${S.buySel.size && total <= W.funds ? '' : 'disabled'}>购买所选车辆 · ${fmt(total)}</button>
      ${total > W.funds ? `<div class="field-error" style="margin-top:6px">资金不足，还差 ${fmt(total - W.funds)}</div>` : ''}`;
    h += `</div>`;
    if (n.services) h += `<div class="section"><div class="section-head"><h4>委托制造</h4></div><div class="muted" style="margin-bottom:8px">按蓝图在${n.name}造一辆新车，完成后停在这里。</div><button class="btn block" data-action="open-commission">${icon('wrench')} 选择蓝图委托制造…</button></div>`;
  }
  return h;
}
const repairCost = v => Math.round((1 - W.vehicles[v].hp) * VEHICLE_TYPES[W.vehicles[v].type].price * 0.4);

// ---------- 租用仓库 ----------
const leaseUsed = n => Object.values(n.lease.goods).reduce((a, b) => a + b, 0);
function leaseTab(id, n) {
  const L = S.lease, rent = Math.round(L.cap * L.days * 0.3);
  if (n.lease) {
    const used = leaseUsed(n), renew = Math.round(n.lease.capacity * L.days * 0.3);
    return `<div class="section"><div class="section-head"><h4>你的租用仓库</h4><span class="chip ${n.lease.daysLeft <= 2 ? 'warning' : 'neutral'}">剩 ${n.lease.daysLeft} 天</span></div>
      <div style="display:flex;justify-content:space-between" class="small"><span class="muted">已用</span><span>${used} / ${n.lease.capacity}</span></div><div class="bar" style="margin-top:4px"><i style="width:${used / n.lease.capacity * 100}%"></i></div>
      <div class="small muted" style="margin-top:6px">到期时间 ${n.lease.expires}。到期后只能取出，不能再存入。</div></div>
      <div class="section"><div class="section-head"><h4>仓库里的货物</h4></div><table class="grid">${Object.entries(n.lease.goods).map(([g, q]) => `<tr><td>${goodTag(g)}</td><td class="num">${q}</td></tr>`).join('')}</table>
      <div class="small faint" style="margin-top:6px">在「市场」页点货物行，可以在仓库和列车之间装卸。</div></div>
      <div class="section"><div class="section-head"><h4>续租</h4></div><div class="seg">${[7, 14, 28].map(d => `<button class="${L.days === d ? 'on' : ''}" data-action="lease-days" data-v="${d}">${d} 天</button>`).join('')}</div>
      <button class="btn primary block" style="margin-top:10px" data-action="lease-renew">续租 ${L.days} 天 · ${fmt(renew)}</button></div>`;
  }
  const over = L.cap > n.leaseFree;
  return `<div class="section"><div class="muted">你在${n.name}没有租用仓库。租用后，你的货物可以存在这里，等列车来装。</div></div>
    <div class="section"><div class="section-head"><h4>租用新仓库</h4><span class="muted small">可租 ${fmt(n.leaseFree)}</span></div>
      <div class="field"><label for="lease-cap">容量</label><input id="lease-cap" type="number" min="1" value="${L.cap}" data-input="lease-cap" class="${over ? 'invalid' : ''}">${over ? `<div class="field-error">最多可租 ${fmt(n.leaseFree)}</div>` : ''}</div>
      <div class="field" style="margin-top:10px"><label>期限</label><div class="seg">${[7, 14, 28].map(d => `<button class="${L.days === d ? 'on' : ''}" data-action="lease-days" data-v="${d}">${d} 天</button>`).join('')}</div></div>
      <div class="kv" style="margin-top:12px"><dt>租金（预付）</dt><dd>${fmt(rent)}</dd><dt>到期时间</dt><dd>第 ${W.day + L.days} 天 ${clock().slice(-5)}</dd></div>
      <button class="btn primary block" style="margin-top:10px" data-action="lease-new" ${over || !L.cap || rent > W.funds ? 'disabled' : ''}>租用 ${L.cap} 容量 ${L.days} 天 · ${fmt(rent)}</button>
      ${rent > W.funds ? `<div class="field-error" style="margin-top:6px">资金不足，还差 ${fmt(rent - W.funds)}</div>` : ''}</div>`;
}

// ---------- 敌对聚居地 ----------
function hostileBody(id, n) {
  const fight = Object.entries(W.battles).find(([, b]) => b.node === id);
  let h = n.surrendered
    ? `<div class="section"><div class="notice success">${icon('flag')}<div class="grow">${n.name}已经投降。占领后它会成为你的聚居地。</div></div></div>`
    : `<div class="section"><div class="notice error">${icon('sword')}<div class="grow">敌对聚居地不和你交易，也不提供服务。它的防御建筑会攻击靠近的列车。</div></div></div>`;
  h += `<div class="section"><div class="section-head"><h4>防御</h4></div><div class="kv"><dt>防御塔</dt><dd>${n.defense.towers} 座</dd><dt>士气</dt><dd>${Math.round(n.defense.morale * 100)}%</dd></div>
    <div class="bar ${n.defense.morale < .3 ? 'bad' : 'warn'}" style="margin-top:6px"><i style="width:${n.defense.morale * 100}%"></i></div></div>`;
  if (fight) h += `<div class="section"><button class="btn block" data-action="select" data-v="battle:${fight[0]}">${icon('sword')} 查看这里的战斗</button></div>`;
  else if (!n.surrendered) h += `<div class="section"><div class="section-head"><h4>怎样占领</h4></div><div class="muted">把武装列车开到${n.name}。士气降到 0 时它会投降，然后就能占领。</div></div>`;
  return h;
}

// ---------- 空节点：建立聚居地 ----------
const FOUNDING = { types: { '农业镇': { pop: 50 }, '矿业镇': { pop: 40 }, '林业镇': { pop: 45 } }, bom: { concrete: 40, machinery: 20, grain: 20 }, days: 2 };
function foundingCheck(id) {
  const here = Object.entries(W.trains).filter(([, t]) => t.at === id && !t.line);
  const [tid, t] = here[0] || [];
  const goods = Object.entries(FOUNDING.bom).map(([g, need]) => ({ g, need, have: t ? Math.min(t.cargo[g] || 0, need) : 0 }));
  return { tid, t, goods, ok: !!t && goods.every(x => x.have >= x.need) };
}
function inspectEmpty(id) {
  const n = W.nodes[id], c = foundingCheck(id);
  let h = inspHead({ glyph: 'empty', color: '#cfd5db', name: n.name, chips: `<span class="chip neutral">${n.founding ? '建城中' : '空节点'}</span>`, meta: `${n.terrain} · 资源：${n.resources.join('、')}` });
  h += `<div class="insp-body">${bannerHtml()}`;
  if (n.founding) return h + `<div class="section"><div class="section-head"><h4>正在建立${n.founding.type}</h4><span class="muted small">${Math.round(n.founding.progress * 100)}% · 约 ${FOUNDING.days} 天</span></div><div class="bar"><i style="width:${n.founding.progress * 100}%"></i></div><div class="small muted" style="margin-top:6px">建成后会收到通知。期间列车可以离开。</div></div></div>`;
  const type = S.foundType;
  h += `<div class="section"><div class="section-head"><h4>建立聚居地</h4></div>
    <div class="field"><label for="found-type">聚居地类型</label><select id="found-type" data-change="found-type">${Object.keys(FOUNDING.types).map(k => `<option ${k === type ? 'selected' : ''}>${k}</option>`).join('')}</select></div>
    <div class="kv" style="margin-top:10px"><dt>初始人口</dt><dd>${FOUNDING.types[type].pop}</dd><dt>建成后</dt><dd>小站点 1 座、空地块 4 块</dd><dt>工期</dt><dd>约 ${FOUNDING.days} 天</dd></div></div>`;
  h += `<div class="section"><div class="section-head"><h4>条件</h4></div>
    <div style="display:flex;gap:8px;padding:4px 0">${c.t ? `<span class="pos">${icon('check', 'sm')}</span>${c.t.name}停在这里` : `<span class="neg">${icon('close', 'sm')}</span>需要你的一列车停在${n.name}`}</div>
    <div class="small muted" style="margin:8px 0 4px">列车上要装着起始材料：</div>
    ${c.goods.map(x => `<div style="display:flex;gap:8px;align-items:center;padding:3px 0"><span class="${x.have >= x.need ? 'pos' : 'neg'}">${icon(x.have >= x.need ? 'check' : 'close', 'sm')}</span>${goodTag(x.g)}<span style="flex:1"></span><span class="${x.have >= x.need ? '' : 'neg'}">${x.have} / ${x.need}</span></div>`).join('')}
    ${!c.ok ? `<div class="notice" style="margin-top:10px">${icon('info')}<div class="grow">青川市场有混凝土和机械部件，白石镇有谷物。<div style="margin-top:6px"><button class="btn" data-action="select" data-v="node:n1">查看青川</button></div></div></div>` : ''}</div>`;
  const lack = !c.t ? `需要一列车停在${n.name}` : c.goods.filter(x => x.have < x.need).map(x => `${GOODS[x.g].name}还差 ${x.need - x.have}`).join('，');
  return h + `</div><div class="insp-foot" style="flex-direction:column">${c.ok ? '' : `<div class="field-error">${lack}</div>`}<button class="btn primary" data-action="found" ${c.ok ? '' : 'disabled'}>${icon('flag')} 建立${type}</button></div>`;
}

Object.assign(INPUTS, {
  'lease-cap': v => { S.lease.cap = Math.max(0, parseInt(v) || 0); },
  'found-type': v => { S.foundType = v; },
  'building-process': (v, el) => { W.nodes[S.selection.id].buildings.find(b => b.id === el.dataset.v).process = v; },
});

Object.assign(PANEL_ACTIONS, {
  'open-building': v => { S.tabs.node = 'buildings'; S.building = S.building === v ? null : v; },
  'building-toggle': v => { const b = W.nodes[S.selection.id].buildings.find(b => b.id === v); b.enabled = !b.enabled; },
  'building-priority': v => { const [bid, p] = v.split(':'); const b = W.nodes[S.selection.id].buildings.find(b => b.id === bid); b.priority = p; if (p === 'high' && b.state === 'blocked') { b.state = 'running'; b.reason = null; } },
  demolish: v => { const b = W.nodes[S.selection.id].buildings.find(b => b.id === v); S.dialog = { title: `拆除${b.name}？`, body: `拆除需要约 1 天，回收部分材料放进${nodeName(S.selection.id)}的库存。拆除后地块空出，${b.name}的生产停止。拆除开始后可以暂停，不能撤销已拆掉的部分。`, confirm: '拆除', danger: true, onConfirm: () => todo('拆除建筑') }; },
  'transfer-go': v => {
    const [g, src] = v.split(':'), n = W.nodes[S.selection.id], t = W.trains[S.trade.train], act = S.trade.action === 'unload' ? 'unload' : 'load';
    const store = src === 'lease' ? n.lease.goods : null, have = src === 'lease' ? (store[g] || 0) : n.stock[g][0];
    const qty = Math.min(S.trade.qty, act === 'load' ? Math.min(have, trainStats(t.vehicles).capacity - cargoTotal(t)) : (t.cargo[g] || 0));
    const delta = act === 'load' ? -qty : qty;
    if (src === 'lease') store[g] = have + delta; else n.stock[g][0] = have + delta;
    t.cargo[g] = (t.cargo[g] || 0) - delta; if (!t.cargo[g]) delete t.cargo[g];
    S.trade.done = `已${act === 'load' ? '装上' : '卸下'} ${qty} ${GOODS[g].name}`;
  },
  'lease-days': v => { S.lease.days = +v; },
  'lease-new': () => { const n = W.nodes[S.selection.id], rent = Math.round(S.lease.cap * S.lease.days * 0.3); spend(rent); n.lease = { capacity: S.lease.cap, goods: {}, expires: `第 ${W.day + S.lease.days} 天 ${clock().slice(-5)}`, daysLeft: S.lease.days }; n.leaseFree -= S.lease.cap; toast('complete', `已在${n.name}租用 ${S.lease.cap} 容量仓库 ${S.lease.days} 天`, null, false); },
  'lease-renew': () => { const n = W.nodes[S.selection.id], rent = Math.round(n.lease.capacity * S.lease.days * 0.3); spend(rent); n.lease.daysLeft += S.lease.days; n.lease.expires = `第 ${W.day + n.lease.daysLeft} 天 08:00`; S.banner = { lvl: 'success', text: `已续租 ${S.lease.days} 天，新的到期时间 ${n.lease.expires}。` }; },
  'repair-parked': () => { const id = S.selection.id, sel = [...S.parkedSel], cost = sel.reduce((a, v) => a + repairCost(v), 0); spend(cost); W.jobs.push({ id: 'j' + Date.now(), node: id, kind: '修理', what: `${sel.length} 辆车`, progress: 0, eta: '约 1 天' }); S.parkedSel.clear(); S.banner = { lvl: 'success', text: `已委托修理 ${sel.length} 辆车，花费 ${fmt(cost)}。材料在${nodeName(id)}市场购买。` }; },
  'sell-parked': () => { const id = S.selection.id, sel = [...S.parkedSel], sum = sel.reduce((a, v) => a + Math.round(vehiclePrice(v) * 0.8), 0); S.dialog = { title: `出售 ${sel.length} 辆车？`, body: `${sel.map(v => VEHICLE_TYPES[W.vehicles[v].type].name).join('、')}<div class="kv" style="margin-top:12px"><dt>收入</dt><dd><b>${fmt(sum)}</b></dd></div><div class="small faint" style="margin-top:8px">出售后车辆归${nodeName(id)}所有，会出现在它的在售现车里。</div>`, confirm: `出售 · ${fmt(sum)}`, onConfirm: () => { spend(-sum); W.parked[id] = W.parked[id].filter(v => !sel.includes(v)); sel.forEach(v => { W.vehicles[v].sale = true; W.nodes[id].forSale.push(v); }); S.parkedSel.clear(); toast('complete', `已出售 ${sel.length} 辆车，收入 ${fmt(sum)}`, null, false); } }; },
  'scrap-parked': () => { const sel = [...S.parkedSel]; S.dialog = { title: `拆解 ${sel.length} 辆车？`, body: `车辆会被拆成材料，放进你的列车或租用仓库；放不下的部分按市价卖给${nodeName(S.selection.id)}。拆解不能撤销。`, confirm: '拆解', danger: true, onConfirm: () => todo('拆解') }; },
  occupy: () => { const n = W.nodes[S.selection.id]; S.dialog = { title: `占领${n.name}？`, body: `${n.name}会成为你的聚居地：建筑、库存和常住人口都归你管理。占领后它的防御建筑会保护你。`, confirm: '占领', onConfirm: () => { n.relation = 'owned'; Object.assign(n, { capacity: 500, stock: { concrete: [80, 40, 0] }, needs: { basic: .5, improved: .2 }, labor: { used: 60, total: 140 }, buildings: [{ id: 'bx', name: '小站点', plot: '小站点', station: true }], plots: [{ id: 'px', size: '中' }], sites: [], defense: { towers: 2 } }); toast('complete', `已占领${n.name}`, { kind: 'node', id: S.selection.id }); } }; },
  found: () => {
    const id = S.selection.id, c = foundingCheck(id), type = S.foundType;
    S.dialog = { title: `在${nodeName(id)}建立${type}？`, body: `${c.t.name}上的 ${Object.entries(FOUNDING.bom).map(([g, q]) => `${GOODS[g].name} ×${q}`).join('、')} 会用于建城。建城约 ${FOUNDING.days} 天，期间列车可以离开。`, confirm: `建立${type}`,
      onConfirm: () => { Object.entries(FOUNDING.bom).forEach(([g, q]) => { c.t.cargo[g] -= q; if (!c.t.cargo[g]) delete c.t.cargo[g]; }); W.nodes[id].founding = { type, progress: 0.02 }; toast('complete', `开始在${nodeName(id)}建立${type}`, { kind: 'node', id }); } };
  },
});
