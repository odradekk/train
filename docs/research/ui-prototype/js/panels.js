// 左侧列表抽屉与右侧检查器
const INPUTS = {
  query: v => { S.query = v; },
  'trade-qty': v => { S.trade.qty = Math.max(0, parseInt(v) || 0); S.trade.done = null; },
  'trade-train': v => { S.trade.train = v; S.trade.done = null; },
};

// ---------- 抽屉 ----------
function renderDrawer() {
  const el = document.getElementById('drawer');
  if (!S.drawer) { el.classList.add('hidden'); return; }
  el.classList.remove('hidden');
  const q = S.query.trim();
  const match = s => !q || s.includes(q);
  const head = (title, count, extra = '') => `<div class="panel-head"><h3>${title} <span class="count">${count}</span></h3>${extra}
    <button class="btn ghost icon-only" data-action="drawer" data-v="${S.drawer}" title="关闭（Esc）">${icon('close')}</button></div>`;
  const search = ph => `<div class="panel-tools"><input id="drawer-q" type="search" placeholder="${ph}" value="${S.query}" data-input="query"></div>`;
  const filters = opts => `<div class="filters">${opts.map(([k, l]) => `<button class="${S.filter === k ? 'on' : ''}" data-action="filter" data-v="${k}">${l}</button>`).join('')}</div>`;
  const isSel = (kind, id) => S.selection?.kind === kind && S.selection?.id === id;
  let html = '';

  if (S.drawer === 'trains') {
    const all = Object.entries(W.trains);
    const need = ([, t]) => !t.plan || t.status === 'blocked' || !t.running;
    const list = all.filter(([id, t]) => match(t.name) && (S.filter === 'all' || (S.filter === 'need' ? need([id, t]) : S.filter === 'running' ? t.running : !t.running)));
    html = head('车队', all.length) + search('搜索列车') + filters([['all', '全部'], ['need', `需要处理 ${all.filter(need).length}`], ['running', '运行中'], ['stopped', '已停车']]) +
      `<div class="scroll">${list.map(([id, t]) => {
        const st = STATUS[t.status], warn = !t.plan;
        return `<div class="row ${isSel('train', id) ? 'on' : ''}" data-action="select" data-v="train:${id}" tabindex="0">
          <span class="chip ${st.cls}" style="width:74px;justify-content:center">${icon(st.icon)}${st.label}</span>
          <div class="grow"><div class="title">${t.name}</div><div class="sub">${trainWhere(id)}</div></div>
          ${warn ? `<span title="没有运行计划" style="color:#e9b45a">${icon('alert')}</span>` : ''}
          ${t.report ? `<span class="small ${t.report.cash >= 0 ? 'pos' : 'neg'}" title="上一圈净现金流">${t.report.cash >= 0 ? '+' : ''}${money(t.report.cash)}</span>` : ''}
        </div>`;
      }).join('') || '<div class="empty">没有符合条件的列车。</div>'}
      <div class="small faint" style="padding:12px">新列车在聚居地检查器的「车辆」页编组。</div></div>`;
  } else if (S.drawer === 'lines') {
    const all = Object.entries(W.lines);
    const list = all.filter(([id, l]) => match(lineName(id)) && (S.filter === 'all' || (S.filter === 'breaks' ? l.breaks : S.filter === 'unbuilt' ? !l.tracks : l.tracks)));
    html = head('线路', all.length) + search('搜索地名') + filters([['all', '全部'], ['built', '已建成'], ['breaks', '有断点'], ['unbuilt', '未建成']]) +
      `<div class="scroll">${list.map(([id, l]) => `<div class="row ${isSel('line', id) ? 'on' : ''}" data-action="select" data-v="line:${id}" tabindex="0">
        <span style="color:${l.tracks ? '#cfd5db' : '#717a85'}">${icon('route')}</span>
        <div class="grow"><div class="title">${lineName(id)}</div><div class="sub">${l.tracks ? `铁轨 ${l.tracks}/${l.max}` : '未建成'} · 长度 ${l.length}</div></div>
        ${l.breaks ? `<span class="chip error">${icon('alert')}${l.breaks} 处断点</span>` : ''}</div>`).join('') || '<div class="empty">没有符合条件的线路。</div>'}</div>`;
  } else if (S.drawer === 'settlements') {
    const groups = [['owned', '自有聚居地'], ['neutral', '非敌对聚居地'], ['hostile', '敌对聚居地'], ['empty', '空节点'], ['lair', '生物聚落']];
    const all = Object.entries(W.nodes);
    const key = n => n.kind === 'settlement' ? n.relation : n.kind;
    html = head('聚居地与节点', all.length) + search('搜索地名') + `<div class="scroll">${groups.map(([g, label]) => {
      const items = all.filter(([, n]) => key(n) === g && match(n.name)); if (!items.length) return '';
      return `<div class="group-label">${label} ${items.length}</div>` + items.map(([id, n]) => {
        const sub = n.kind === 'settlement' ? `${n.type} · 人口 ${fmt(n.pop)} · 在站列车 ${trainsAt(id).length}` : n.kind === 'empty' ? `${n.terrain} · ${n.resources.join('、')}` : n.threat;
        const glyph = n.kind === 'settlement' ? `<span style="color:${RELATION[n.relation].color}">${icon('city')}</span>` : n.kind === 'empty' ? `<span class="faint">${icon('empty')}</span>` : `<span style="color:#ec8a84">${icon('lair')}</span>`;
        return `<div class="row ${isSel('node', id) ? 'on' : ''}" data-action="select" data-v="node:${id}" tabindex="0">${glyph}<div class="grow"><div class="title">${n.name}</div><div class="sub">${sub}</div></div></div>`;
      }).join('');
    }).join('')}</div>`;
  } else if (S.drawer === 'combat') html = combatDrawer(head);
  el.innerHTML = html;
}

// ---------- 检查器 ----------
function renderInspector() {
  const el = document.getElementById('inspector');
  const sel = S.selection;
  if (!sel) { el.classList.add('hidden'); return; }
  el.classList.remove('hidden');
  const scroll = el.querySelector('.insp-body')?.scrollTop || 0;
  el.innerHTML = sel.kind === 'train' ? inspectTrain(sel.id) : sel.kind === 'line' ? inspectLine(sel.id) : sel.kind === 'battle' ? inspectBattle(sel.id) : inspectNode(sel.id);
  const body = el.querySelector('.insp-body'); if (body) body.scrollTop = scroll;
}

function inspHead({ glyph, color, name, chips = '', meta, scene = true }) {
  return `<div class="insp-head"><div class="glyph" style="color:${color}">${icon(glyph, 'lg')}</div>
    <div class="who"><div class="name">${name}${chips}</div><div class="meta">${meta}</div></div>
    <div class="acts"><button class="btn ghost icon-only" data-action="locate" title="在地图上定位（F）">${icon('locate')}</button>
    ${scene ? `<button class="btn ghost icon-only" data-action="scene" title="进入场景（V）">${icon('scene')}</button>` : ''}
    <button class="btn ghost icon-only" data-action="deselect" title="关闭（Esc）">${icon('close')}</button></div></div>`;
}
function tabs(kind, list) {
  const cur = list.some(t => t[0] === S.tabs[kind]) ? S.tabs[kind] : list[0][0];
  S.tabs[kind] = cur;
  return `<div class="tabs">${list.map(([k, l, dot]) => `<button class="tab ${cur === k ? 'on' : ''}" data-action="tab" data-v="${kind}:${k}">${l}${dot ? '<span class="dot"></span>' : ''}</button>`).join('')}</div>`;
}
const bannerHtml = () => S.banner ? `<div class="section"><div class="notice ${S.banner.lvl}">${icon(S.banner.lvl === 'success' ? 'check' : 'info')}<div class="grow">${S.banner.text}</div></div></div>` : '';

// ----- 列车 -----
function inspectTrain(id) {
  const t = W.trains[id], st = STATUS[t.status], s = trainStats(t.vehicles);
  let html = inspHead({ glyph: 'train', color: STATUS_COLOR[t.status], name: t.name, chips: `<span class="chip ${st.cls}">${icon(st.icon)}${st.label}</span>`, meta: `${s.count} 辆车 · 载量 ${s.capacity} · ${trainWhere(id)}` });
  html += tabs('train', [['overview', '概况', !t.plan], ['plan', '计划'], ['cargo', '货物'], ['vehicles', '车辆'], ['rules', '交战']]);
  html += `<div class="insp-body">${bannerHtml()}`;
  const tab = S.tabs.train;
  if (tab === 'overview') {
    if (!t.plan) html += `<div class="section"><div class="notice warning">${icon('alert')}<div class="grow">这列车没有运行计划，写好计划后才能发车。</div></div></div>`;
    const fight = Object.entries(W.battles).find(([, b]) => b.ours.includes(id));
    if (fight) html += `<div class="section"><div class="notice error">${icon('sword')}<div class="grow">${t.name}正在${battleName(fight[1])}战斗。<div style="margin-top:6px"><button class="btn" data-action="select" data-v="battle:${fight[0]}">查看战斗</button></div></div></div></div>`;
    if (t.status === 'moving') {
      html += `<div class="section"><div class="section-head"><h4>前往 ${nodeName(t.to)}</h4><span class="muted">${Math.round(t.progress * 100)}%</span></div><div class="bar"><i style="width:${t.progress * 100}%"></i></div>
        <div class="small muted" style="margin-top:6px">经由 ${lineName(t.line)}${t.target && t.target !== t.to ? ` · 目的地 ${nodeName(t.target)}` : ''}</div></div>`;
    } else if (t.at) {
      html += `<div class="section"><div class="section-head"><h4>${t.status === 'loading' ? '正在装卸' : '停在'} ${nodeName(t.at)}</h4></div>
        <button class="btn" data-action="select" data-v="node:${t.at}">${icon('city')} 查看 ${nodeName(t.at)}</button></div>`;
    }
    if (t.plan) {
      const next = t.plan.stops.map((x, i) => ({ ...x, i })).filter(x => !x.pass);
      html += `<div class="section"><div class="section-head"><h4>运行计划</h4><span class="muted small">${t.plan.loop ? '循环' : '单次'} · ${t.plan.stops.length} 站</span></div>
        <div class="muted">${next.map(x => `<span style="color:${x.i === t.stopIndex ? '#e0a22e' : ''}">${nodeName(x.node)}</span>`).join(' → ')}${t.plan.loop ? ' → …' : ''}</div></div>`;
    }
    const r = t.report;
    html += `<div class="section"><div class="section-head"><h4>上一次运行报告</h4>${r ? `<span class="muted small">第 ${r.lap} 圈</span>` : ''}</div>${r ? `
      <div class="stats" style="grid-template-columns:1fr 1fr"><div class="stat"><div class="label">净现金流</div><div class="value ${r.cash >= 0 ? 'pos' : 'neg'}">${r.cash >= 0 ? '+' : ''}${money(r.cash)}</div></div>
      <div class="stat"><div class="label">载运率</div><div class="value">${Math.round(r.load * 100)}%</div></div></div>
      ${r.detail?.issues.map(x => `<div class="small" style="color:#e9b45a;margin-top:8px">${icon('alert', 'sm')} ${x}</div>`).join('') || ''}
      <button class="btn ghost small" style="margin-top:6px" data-action="open-report" data-v="${id}">查看明细 ${icon('chevron', 'sm')}</button>` : '<div class="muted">还没有完成过一圈。</div>'}</div>`;
  } else if (tab === 'plan') {
    if (!t.plan) html += `<div class="empty">还没有运行计划。<br><button class="btn primary" data-action="edit-plan">${icon('edit')} 编辑计划</button></div>`;
    else html += `<div class="section"><div class="section-head"><h4>${t.plan.loop ? '循环运行' : '单次运行'}</h4><button class="btn" data-action="edit-plan">${icon('edit')} 编辑计划</button></div>
      ${t.plan.stops.map((x, i) => `<div style="display:flex;gap:10px;padding:8px 0;border-top:1px solid #22282f">
        <span class="chip ${x.pass ? 'neutral' : 'warning'}" style="width:26px;justify-content:center;padding:0">${i + 1}</span>
        <div class="grow"><div><b>${nodeName(x.node)}</b> <span class="muted small">${x.pass ? '途经点' : `停靠 ${x.dwell} 秒`}</span></div>
        ${x.rules.map(r => `<div class="small muted">${ruleSentence(r)}</div>`).join('') || (x.pass ? '' : '<div class="small faint">没有交易规则</div>')}</div></div>`).join('')}</div>`;
  } else if (tab === 'cargo') {
    const tot = cargoTotal(t);
    html += `<div class="section"><div class="section-head"><h4>车上货物</h4><span class="muted">${tot} / ${s.capacity}</span></div><div class="bar ${tot / s.capacity > .9 ? 'warn' : ''}"><i style="width:${s.capacity ? tot / s.capacity * 100 : 0}%"></i></div></div>
      <div class="section">${Object.keys(t.cargo).length ? `<table class="grid"><tr><th>货物</th><th class="num">数量</th></tr>${Object.entries(t.cargo).map(([g, q]) => `<tr><td>${goodTag(g)}</td><td class="num">${q}</td></tr>`).join('')}</table>` : '<div class="muted">车上没有货物。</div>'}</div>`;
  } else if (tab === 'vehicles') {
    const n = t.at ? W.nodes[t.at] : null, svc = n && n.kind === 'settlement' && n.relation !== 'hostile' && !t.running;
    const repair = t.vehicles.reduce((a, v) => a + repairCost(v), 0);
    html += `<div class="section">${t.vehicles.map((v, i) => vehicleRow(v, `${i + 1}`, svc && n.services ? `<button class="btn ghost small" style="margin-left:8px" data-action="refit" data-v="${v}">改装…</button>` : '')).join('')}</div>
      <div class="section"><div class="kv"><dt>总长</dt><dd>${s.length}</dd><dt>总质量</dt><dd>${s.mass}</dd><dt>牵引力</dt><dd>${s.traction}</dd><dt>载量</dt><dd>${s.capacity}</dd></div></div>`;
    html += svc ? `<div class="section"><div class="section-head"><h4>在${n.name}</h4></div><div style="display:flex;gap:8px;flex-wrap:wrap">
        ${n.services ? `<button class="btn" data-action="repair-train" ${repair ? '' : 'disabled'}>${icon('wrench')} ${repair ? `全部修理 · ${fmt(repair)}` : '没有损伤，不需要修理'}</button>` : ''}
        <button class="btn" data-action="reform">${icon('train')} 调整编组…</button></div></div>`
      : `<div class="section"><div class="small faint">${t.running ? '列车停在聚居地后，可以在这里修理、改装和调整编组。' : '这里没有可用的服务。'}</div></div>`;
    html += `<div class="section"><button class="btn danger" data-action="disband" ${t.running ? 'disabled' : ''}>解编列车…</button>${t.running ? '<div class="small faint" style="margin-top:6px">先停车才能解编。</div>' : ''}</div>`;
  } else if (tab === 'rules') {
    html += trainRulesTab(id);
  }
  html += `</div><div class="insp-foot">`;
  if (!t.plan) html += `<button class="btn primary" data-action="edit-plan">${icon('edit')} 编辑计划</button><button class="btn" disabled title="没有运行计划">发车</button>`;
  else if (t.running) html += `<button class="btn" data-action="edit-plan">${icon('edit')} 编辑计划</button><button class="btn" data-action="stop-train">${icon('stop')} 停车</button>`;
  else html += `<button class="btn" data-action="edit-plan">${icon('edit')} 编辑计划</button><button class="btn primary" data-action="start-train">${icon('play')} 发车</button>`;
  return html + `</div>`;
}

function ruleSentence(r) {
  const g = GOODS[r.good].name;
  const base = { buy: `买入${g}，直到车上有 ${r.target}`, sell: `卖出${g}，直到车上剩 ${r.target}`, load: `从库存装载${g}，直到车上有 ${r.target}`, unload: `卸载${g}到库存，直到车上剩 ${r.target}` }[r.action];
  const lim = r.limit == null ? '' : r.action === 'buy' ? `；单价高于 ${r.limit} 不买` : r.action === 'sell' ? `；单价低于 ${r.limit} 不卖` : '';
  return base + lim;
}
const goodTag = g => `<span class="good"><i style="background:${GOODS[g].color}"></i>${GOODS[g].name}</span>`;
function vehicleRow(v, lead = '', extra = '') {
  const x = W.vehicles[v], ty = VEHICLE_TYPES[x.type];
  return `<div style="display:flex;gap:10px;align-items:center;padding:6px 0">${lead ? `<span class="faint" style="width:16px">${lead}</span>` : ''}
    <span style="color:${ty.traction ? '#e0a22e' : '#a9b6c6'}">${icon('train')}</span>
    <div class="grow" style="flex:1"><div>${ty.name}</div><div class="small muted">${ty.traction ? `牵引 ${ty.traction}` : `载量 ${ty.capacity}`} · 长 ${ty.length}</div></div>
    <div style="width:90px"><div class="bar ${x.hp < .5 ? 'bad' : x.hp < .8 ? 'warn' : ''}"><i style="width:${x.hp * 100}%"></i></div><div class="small muted" style="text-align:right">耐久 ${Math.round(x.hp * 100)}%</div></div>${extra}</div>`;
}

// ----- 线路 -----
function inspectLine(id) {
  const l = W.lines[id], here = Object.entries(W.trains).filter(([, t]) => t.line === id);
  const chips = l.breaks ? `<span class="chip error">${icon('alert')}${l.breaks} 处断点</span>` : l.tracks ? '' : '<span class="chip neutral">未建成</span>';
  let html = inspHead({ glyph: 'route', color: '#cfd5db', name: lineName(id), chips, meta: `长度 ${l.length} · ${l.tracks ? `铁轨 ${l.tracks}/${l.max}` : `最多可建 ${l.max} 条铁轨`}` });
  html += `<div class="insp-body">${bannerHtml()}`;
  if (l.breaks) html += `<div class="section"><div class="section-head"><h4>断点</h4></div>
    <div class="notice error">${icon('alert')}<div class="grow">铁轨 1 在距${nodeName(l.a)} ${Math.round(l.length * .55)} 处断开，列车只能走其余铁轨。<div class="small muted" style="margin-top:4px">修复需要 钢轨 ×6、道床碎石 ×10，从${nodeName(l.a)}市场补购，约 ${fmt(420)}</div></div></div>
    <div style="margin-top:8px"><button class="btn primary" data-action="repair">${icon('wrench')} 修复断点 · ${fmt(420)}</button></div></div>`;
  html += `<div class="section"><div class="section-head"><h4>${l.tracks ? '加一条铁轨' : '建设这条线路'}</h4><span class="muted small">${l.tracks}/${l.max}</span></div>
    ${l.tracks < l.max ? `<div class="kv"><dt>材料</dt><dd>钢轨 ×${l.length}、道床碎石 ×${l.length * 2}</dd><dt>材料来源</dt><dd>${nodeName(l.a)} 市场</dd><dt>工期</dt><dd>约 ${Math.ceil(l.length / 12)} 天</dd><dt>预计费用</dt><dd>${fmt(l.length * 46)}</dd></div>
      <div style="margin-top:10px"><button class="btn ${l.breaks ? '' : 'primary'}" data-action="expand" ${W.funds < l.length * 46 ? 'disabled' : ''}>${icon('hammer')} ${l.tracks ? '加轨' : '开工'} · ${fmt(l.length * 46)}</button>
      ${W.funds < l.length * 46 ? `<div class="field-error" style="margin-top:6px">资金不足，还差 ${fmt(l.length * 46 - W.funds)}</div>` : ''}</div>`
      : '<div class="muted">已达到这条线路的铁轨上限。</div>'}</div>`;
  if (l.tracks) html += `<div class="section"><div class="section-head"><h4>自动修复</h4><label class="switch" data-action="todo" data-v="自动修复"><i></i>关闭</label></div><div class="small muted">打开后，新断点会自动用选定来源的材料修复。</div></div>`;
  html += `<div class="section"><div class="section-head"><h4>线路上的列车</h4><span class="muted">${here.length}</span></div>${here.map(([tid, t]) => `<div class="row" data-action="select" data-v="train:${tid}"><span class="chip ${STATUS[t.status].cls}">${STATUS[t.status].label}</span><div class="grow">${t.name}</div><span class="muted small">${trainWhere(tid)}</span></div>`).join('') || '<div class="muted">没有列车。</div>'}</div>`;
  html += `<div class="section"><div class="kv"><dt>两端</dt><dd><a href="#" data-action="select" data-v="node:${l.a}" style="color:inherit">${nodeName(l.a)}</a> · <a href="#" data-action="select" data-v="node:${l.b}" style="color:inherit">${nodeName(l.b)}</a></dd><dt>地貌</dt><dd>平原，起伏小</dd></div></div>`;
  return html + `</div>`;
}

// ----- 节点 -----
function inspectNode(id) {
  const n = W.nodes[id];
  if (n.kind === 'empty') return inspectEmpty(id);
  if (n.kind === 'lair') {
    return inspHead({ glyph: 'lair', color: '#ec8a84', name: n.name, chips: '<span class="chip error">生物聚落</span>', meta: n.threat, scene: false }) +
      `<div class="insp-body"><div class="section"><div class="notice error">${icon('alert')}<div class="grow">这里的世界生物会成群袭击附近的线路和聚居地。武装列车停在这里并消灭全部生物后，节点会变为空节点。</div></div></div></div>`;
  }
  return inspectSettlement(id);
}

function marketTab(id, n, here) {
  const lease = n.lease?.goods;
  const rows = Object.entries(n.market).map(([g, [stock, buy, sell]]) => {
    const open = S.market === g;
    return `<tr class="click ${open ? 'on' : ''}" data-action="market-row" data-v="${g}"><td>${goodTag(g)}</td><td class="num">${stock || '<span style="color:#e9b45a">缺货</span>'}</td><td class="num">${buy ?? '—'}</td><td class="num">${sell ?? '—'}</td>${lease ? `<td class="num muted">${lease[g] || '—'}</td>` : ''}<td style="width:20px">${icon(open ? 'chevdown' : 'chevron', 'sm')}</td></tr>
      ${open ? `<tr><td colspan="${lease ? 6 : 5}" style="padding:0;height:auto;white-space:normal">${tradeBox(id, n, g, here)}</td></tr>` : ''}`;
  }).join('');
  return `<div class="section"><table class="grid"><tr><th>货物</th><th class="num">可买</th><th class="num">买价</th><th class="num">卖价</th>${lease ? '<th class="num">你的仓库</th>' : ''}<th></th></tr>${rows}</table>
    <div class="small faint" style="margin-top:8px">点一行货物${lease ? '买卖，或在你的租用仓库和列车之间装卸' : '买卖'}。买价、卖价是单件报价，数量越大价格越不利。</div></div>`;
}

function tradeSeg(n) {
  const acts = [['buy', '买入'], ['sell', '卖出'], ...(n.lease ? [['load', '从仓库装车'], ['unload', '卸到仓库']] : [])];
  const cur = acts.some(a => a[0] === S.trade.action) ? S.trade.action : 'buy';
  return `<div class="seg">${acts.map(([k, l]) => `<button class="${cur === k ? 'on' : ''}" data-action="trade-act" data-v="${k}">${l}</button>`).join('')}</div>`;
}

function tradeBox(id, n, g, here) {
  const [stock, buy, sell] = n.market[g];
  if (!here.length) return `<div style="padding:12px"><div class="notice">${icon('info')}<div class="grow">没有你的列车停在${n.name}。列车到站后才能在这里买卖。</div></div></div>`;
  if (n.lease && (S.trade.action === 'load' || S.trade.action === 'unload')) return transferBox(id, '你的租用仓库', g, n.lease.goods[g] || 0, here, 'lease', tradeSeg(n));
  const tid = here.includes(S.trade.train) ? S.trade.train : here[0]; S.trade.train = tid;
  const t = W.trains[tid], cap = trainStats(t.vehicles).capacity - cargoTotal(t), have = t.cargo[g] || 0;
  const act = S.trade.action === 'sell' ? 'sell' : 'buy';
  const unit = act === 'buy' ? buy : sell, max = act === 'buy' ? Math.min(stock, cap, unit ? Math.floor(W.funds / unit) : 0) : have;
  const qty = Math.min(S.trade.qty, Math.max(max, 0));
  const slip = act === 'buy' ? 1 + qty / 400 : 1 - qty / 400, total = Math.round(qty * unit * slip);
  let reason = '';
  if (act === 'buy' && !buy) reason = `${n.name}没有${GOODS[g].name}可卖`;
  else if (act === 'buy' && cap <= 0) reason = `${t.name}已装满`;
  else if (act === 'buy' && W.funds < unit) reason = '资金不足';
  else if (act === 'sell' && !have) reason = `${t.name}上没有${GOODS[g].name}`;
  const done = S.trade.done ? `<div class="small pos" style="margin-top:6px">${icon('check', 'sm')} ${S.trade.done}</div>` : '';
  return `<div style="padding:12px;background:#1d2228;border-bottom:1px solid var(--divider)">
    <div style="display:flex;gap:8px;align-items:center;margin-bottom:10px;flex-wrap:wrap">${tradeSeg(n)}
      <select id="trade-train" data-change="trade-train" aria-label="列车" style="flex:1;min-width:200px">${here.map(x => `<option value="${x}" ${x === tid ? 'selected' : ''}>${W.trains[x].name}（车上 ${W.trains[x].cargo[g] || 0}，余量 ${trainStats(W.trains[x].vehicles).capacity - cargoTotal(W.trains[x])}）</option>`).join('')}</select></div>
    <div style="display:flex;gap:8px;align-items:center"><label class="muted" for="trade-qty">数量</label><input id="trade-qty" type="number" min="0" max="${max}" value="${qty}" data-input="trade-qty" style="width:90px">
      <button class="btn" data-action="trade-max">最大 ${Math.max(max, 0)}</button><span class="grow" style="flex:1"></span><span class="muted">单价约 ${unit ?? '—'}</span></div>
    <div style="display:flex;justify-content:space-between;margin-top:10px"><span class="muted">${act === 'buy' ? '花费' : '收入'}</span><b>${fmt(total)}</b></div>
    <div style="display:flex;justify-content:space-between"><span class="muted">交易后资金</span><span>${fmt(W.funds + (act === 'buy' ? -total : total))}</span></div>
    <button class="btn primary block" style="margin-top:10px" data-action="trade-go" data-v="${g}" ${reason || !qty ? 'disabled' : ''}>${act === 'buy' ? '买入' : '卖出'} ${qty} ${GOODS[g].name} · ${fmt(total)}</button>
    ${reason ? `<div class="field-error" style="margin-top:6px">${reason}</div>` : ''}${done}</div>`;
}

const vehiclePrice = v => Math.round(VEHICLE_TYPES[W.vehicles[v].type].price * (0.5 + W.vehicles[v].hp / 2));

const PANEL_ACTIONS = {
  select: v => { const [kind, id] = v.split(':'); select({ kind, id }); return false; },
  deselect: () => { select(null); return false; },
  filter: v => { S.filter = v; },
  tab: v => { const [k, t] = v.split(':'); S.tabs[k] = t; S.market = null; S.banner = null; },
  locate: () => { MapView.centerOn(S.selection); return false; },
  scene: () => { enterScene(S.selection); return false; },
  'market-row': v => { S.market = S.market === v ? null : v; S.trade.done = null; S.trade.qty = 10; },
  'trade-act': v => { S.trade.action = v; S.trade.done = null; },
  'trade-max': () => { S.trade.qty = 99999; S.trade.done = null; },
  'trade-go': g => {
    const n = W.nodes[S.selection.id], t = W.trains[S.trade.train], [, buy, sell] = n.market[g], act = S.trade.action;
    const max = act === 'buy' ? Math.min(n.market[g][0], trainStats(t.vehicles).capacity - cargoTotal(t), Math.floor(W.funds / buy)) : (t.cargo[g] || 0);
    const qty = Math.min(S.trade.qty, max), unit = act === 'buy' ? buy : sell, total = Math.round(qty * unit * (act === 'buy' ? 1 + qty / 400 : 1 - qty / 400));
    if (act === 'buy') { spend(total); n.market[g][0] -= qty; t.cargo[g] = (t.cargo[g] || 0) + qty; }
    else { spend(-total); n.market[g][0] += qty; t.cargo[g] -= qty; if (!t.cargo[g]) delete t.cargo[g]; }
    S.trade.done = `已${act === 'buy' ? '买入' : '卖出'} ${qty} ${GOODS[g].name}，${act === 'buy' ? '花费' : '收入'} ${fmt(total)}`;
  },
  'buy-toggle': v => { S.buySel.has(v) ? S.buySel.delete(v) : S.buySel.add(v); },
  'parked-toggle': v => { S.parkedSel.has(v) ? S.parkedSel.delete(v) : S.parkedSel.add(v); },
  'buy-vehicles': () => {
    const ids = [...S.buySel], total = ids.reduce((a, v) => a + vehiclePrice(v), 0), names = ids.map(v => VEHICLE_TYPES[W.vehicles[v].type].name);
    S.dialog = { title: `购买 ${ids.length} 辆现车？`, body: `<div>${names.join('、')}</div><div class="kv" style="margin-top:12px"><dt>合计</dt><dd><b>${fmt(total)}</b></dd><dt>购买后资金</dt><dd>${fmt(W.funds - total)}</dd></div>
      <div class="small faint" style="margin-top:10px">买下的车辆停在${nodeName(S.selection.id)}，可以马上编组。</div>`,
      confirm: '购买并编组', alt: '只购买', onConfirm: then => buyVehicles(ids, total, then) };
  },
  'open-formation': () => openFormation(S.selection.id, [...S.parkedSel]),
  'edit-plan': () => openPlan(S.selection.id),
  'start-train': () => { const id = S.selection.id, t = W.trains[id]; startTrain(id); toast('info', `${t.name} 已发车`, { kind: 'train', id }, false); },
  'stop-train': () => { const id = S.selection.id, t = W.trains[id]; t.running = false; toast('info', `${t.name} 将在下一站停车`, { kind: 'train', id }, false); },
  disband: () => { const t = W.trains[S.selection.id]; S.dialog = { title: `解编${t.name}？`, body: `${t.vehicles.length} 辆车会停在${nodeName(t.at)}，车上货物留在车里。列车的运行计划会被删除。`, confirm: '解编', danger: true, onConfirm: () => { const id = S.selection.id, node = t.at; (W.parked[node] ||= []).push(...t.vehicles); delete W.trains[id]; select({ kind: 'node', id: node }); S.tabs.node = 'vehicles'; toast('complete', `${t.name} 已解编，${t.vehicles.length} 辆车停在${nodeName(node)}`, null, false); } }; },
  'repair-train': () => { const t = W.trains[S.selection.id], cost = t.vehicles.reduce((a, v) => a + repairCost(v), 0); spend(cost); t.vehicles.forEach(v => W.vehicles[v].hp = 1); S.banner = { lvl: 'success', text: `已修好${t.name}的全部车辆，花费 ${fmt(cost)}。材料在${nodeName(t.at)}市场购买。` }; },
  reform: () => { const t = W.trains[S.selection.id]; openFormation(t.at, t.vehicles, S.selection.id); },
  'open-report': v => openReport(v),
  'declare-war': () => { const n = W.nodes[S.selection.id]; S.dialog = { title: `向${n.name}宣战？`, body: `${n.name}会永久变为敌对，不能恢复：<ul style="margin:8px 0 0;padding-left:20px"><li>停止和你交易，不再提供委托和仓库</li><li>你在这里租用的仓库立即失效</li><li>它的防御建筑会攻击靠近的列车</li></ul>`, confirm: '宣战', danger: true, onConfirm: () => todo('宣战') }; },
  repair: () => { spend(420); W.lines[S.selection.id].breaks = 0; S.banner = { lvl: 'success', text: '已开始修复断点，预计 1 天完成。' }; },
  expand: () => { const l = W.lines[S.selection.id]; spend(l.length * 46); S.banner = { lvl: 'success', text: `已开工，预计 ${Math.ceil(l.length / 12)} 天后新铁轨可用。` }; },
};

function buyVehicles(ids, total, thenForm) {
  const node = S.selection.id;
  spend(total);
  W.parked[node] = W.parked[node] || [];
  for (const v of ids) { W.vehicles[v].sale = false; W.parked[node].push(v); }
  W.nodes[node].forSale = W.nodes[node].forSale.filter(v => !ids.includes(v));
  S.buySel.clear();
  toast('complete', `已购买 ${ids.length} 辆车，停在${nodeName(node)}`, null);
  if (thenForm) openFormation(node, [...W.parked[node]]);
  else { S.parkedSel = new Set(ids); }
}
