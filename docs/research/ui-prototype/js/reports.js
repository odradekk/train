// 经营报告工作区
const LEDGER = [
  ['第 3 天 07:40', '列车 1 在青川卖出谷物 40', 436], ['第 3 天 07:38', '列车 1 在青川买入机械部件 12', -712],
  ['第 3 天 06:10', '列车 2 在北港卖出铁矿石 20', 238], ['第 3 天 05:20', '列车 2 在赤岭买入铁矿石 20', -188],
  ['第 3 天 05:05', '列车 2 在赤岭修理', -160], ['第 2 天 12:00', '北港租用仓库 200 容量 7 天', -420],
  ['第 2 天 15:10', '列车 1 在青川卖出谷物 40', 452], ['第 2 天 14:30', '列车 2 在北港卖出铁矿石 40', 486],
  ['第 2 天 09:40', '列车 1 在白石镇装载谷物 40（自有库存，不计收支）', 0], ['第 1 天 20:00', '列车 2 在北港卖出铁矿石 40', 470],
  ['第 1 天 09:30', '青川委托制造中型货车', -2860],
];
const DAYS = ['第 1 天', '第 2 天', '第 3 天'];
const DAILY = DAYS.map(d => [d === `第 ${3} 天` ? '第 3 天（至今）' : d, LEDGER.filter(x => x[0].startsWith(d)).reduce((a, x) => a + x[2], 0)]);

function openReport(train = null) { S.workspace = { kind: 'report', tab: 'trains', open: train }; }

function reportWs(w) {
  const income = LEDGER.filter(x => x[2] > 0).reduce((a, x) => a + x[2], 0), expense = LEDGER.filter(x => x[2] < 0).reduce((a, x) => a + x[2], 0);
  const net = DAILY.reduce((a, d) => a + d[1], 0), maxAbs = Math.max(...DAILY.map(d => Math.abs(d[1])));
  const bars = DAILY.map(([label, v], i) => {
    const h = Math.abs(v) / maxAbs * 70, x = 40 + i * 110;
    return `<rect x="${x}" y="${v >= 0 ? 90 - h : 90}" width="64" height="${h}" rx="3" fill="${v >= 0 ? '#2f9e5c' : '#c9423a'}"/>
      <text x="${x + 32}" y="${v >= 0 ? 84 - h : 84}" text-anchor="middle" font-size="12" fill="#e6e9ec">${v >= 0 ? '+' : ''}${money(v)}</text>
      <text x="${x + 32}" y="186" text-anchor="middle" font-size="12" fill="#9aa3ad">${label}</text>`;
  }).join('');
  let body = '';
  if (w.tab === 'trains') {
    body = `<table class="grid"><tr><th>列车</th><th>状态</th><th class="num">已完成圈数</th><th class="num">最近一圈净现金流</th><th class="num">载运率</th><th>未完成的规则</th><th></th></tr>${Object.entries(W.trains).map(([id, t]) => {
      const r = t.report, open = w.open === id, d = r?.detail;
      let row = `<tr class="click ${open ? 'on' : ''}" data-action="rp-open" data-v="${id}"><td><b>${t.name}</b></td><td><span class="chip ${STATUS[t.status].cls}">${STATUS[t.status].label}</span></td>
        <td class="num">${r?.lap ?? '—'}</td><td class="num ${r ? (r.cash >= 0 ? 'pos' : 'neg') : ''}">${r ? `${r.cash >= 0 ? '+' : ''}${money(r.cash)}` : '—'}</td><td class="num">${r ? Math.round(r.load * 100) + '%' : '—'}</td>
        <td>${!t.plan ? '<span style="color:#e9b45a">没有运行计划</span>' : d?.issues.length ? `<span style="color:#e9b45a">${icon('alert', 'sm')} ${d.issues.length} 条</span>` : '<span class="muted">无</span>'}</td><td style="width:20px">${icon(open ? 'chevdown' : 'chevron', 'sm')}</td></tr>`;
      if (open) row += `<tr><td colspan="7" style="height:auto;white-space:normal;padding:12px 16px;background:#1d2228">${d ? `
        <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:24px">
          <div><div class="muted small" style="margin-bottom:4px">销售</div>${d.sales.map(([g, q, v]) => `<div style="display:flex;justify-content:space-between">${goodTag(g)} ×${q}<span class="pos">+${fmt(v)}</span></div>`).join('') || '<div class="faint">无</div>'}</div>
          <div><div class="muted small" style="margin-bottom:4px">采购与服务</div>${d.purchases.map(([g, q, v]) => `<div style="display:flex;justify-content:space-between">${goodTag(g)} ×${q}<span class="neg">${money(v)}</span></div>`).join('')}${d.service ? `<div style="display:flex;justify-content:space-between">修理<span class="neg">${money(-d.service)}</span></div>` : ''}</div>
          <div><div class="muted small" style="margin-bottom:4px">未完成的规则</div>${d.issues.map(x => `<div style="color:#e9b45a">${icon('alert', 'sm')} ${x}</div>`).join('') || '<div class="faint">全部完成</div>'}</div></div>
        <div style="display:flex;gap:8px;margin-top:12px"><button class="btn" data-action="rp-train" data-v="${id}">${icon('train')} 查看${t.name}</button><button class="btn" data-action="rp-plan" data-v="${id}">${icon('edit')} 编辑计划</button></div>`
        : `<div class="muted">${t.plan ? '还没有完成过一圈。' : '这列车没有运行计划。'}</div>${t.plan ? '' : `<button class="btn primary" style="margin-top:8px" data-action="rp-plan" data-v="${id}">${icon('edit')} 编辑计划</button>`}`}</td></tr>`;
      return row;
    }).join('')}</table><div class="small faint" style="margin-top:8px">净现金流是实际收入减实际支出，不扣除货物成本和折旧。</div>`;
  } else if (w.tab === 'settlements') {
    const own = Object.entries(W.nodes).filter(([, n]) => n.relation === 'owned');
    body = `<div class="section-head"><h4>自有聚居地</h4></div><table class="grid"><tr><th>聚居地</th><th class="num">常住人口</th><th class="num">基本需求满足率</th><th class="num">改善需求满足率</th><th>今天的产出</th><th>需要处理</th></tr>${own.map(([id, n]) => `<tr class="click" data-action="rp-node" data-v="${id}"><td><b>${n.name}</b></td><td class="num">${fmt(n.pop)}</td><td class="num">${Math.round((n.needs?.basic ?? 0) * 100)}%</td><td class="num">${Math.round((n.needs?.improved ?? 0) * 100)}%</td><td>谷物 +12，果蔬 +4</td><td>${n.buildings?.some(b => b.state === 'blocked') ? '<span style="color:#e9b45a">1 座建筑停工</span>' : '<span class="muted">无</span>'}</td></tr>`).join('')}</table>
      <div class="section-head" style="margin-top:20px"><h4>在其他聚居地的交易</h4></div><table class="grid"><tr><th>聚居地</th><th class="num">卖出</th><th class="num">买入</th><th class="num">服务与租金</th></tr>
      <tr><td>青川</td><td class="num pos">+436</td><td class="num neg">−712</td><td class="num neg">−2,860</td></tr><tr><td>北港</td><td class="num pos">+238</td><td class="num">—</td><td class="num neg">−420</td></tr><tr><td>赤岭</td><td class="num">—</td><td class="num neg">−188</td><td class="num neg">−160</td></tr></table>`;
  } else {
    body = `<table class="grid"><tr><th>时间</th><th>项目</th><th class="num">金额</th></tr>${LEDGER.filter(x => x[2]).map(([tm, what, v]) => `<tr><td class="muted">${tm}</td><td>${what}</td><td class="num ${v >= 0 ? 'pos' : 'neg'}">${v >= 0 ? '+' : ''}${money(v)}</td></tr>`).join('')}</table>`;
  }
  return `<div class="workspace" style="width:1160px;height:min(820px, calc(100vh - 92px))" role="dialog" aria-label="经营报告">
    <div class="ws-head"><h2>经营报告</h2><button class="btn ghost icon-only" data-action="ws-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div style="display:flex;gap:24px;padding:16px 24px;border-bottom:1px solid var(--divider);align-items:center">
      <div class="stats" style="grid-template-columns:repeat(2,180px);gap:12px"><div class="stat"><div class="label">资金</div><div class="value">${fmt(W.funds)}</div></div>
        <div class="stat"><div class="label">本局净现金流</div><div class="value ${net >= 0 ? 'pos' : 'neg'}">${net >= 0 ? '+' : ''}${money(net)}</div><div class="small muted">收入 ${fmt(income)} · 支出 ${fmt(-expense)}</div></div></div>
      <svg width="380" height="196" style="flex:none"><line x1="30" x2="370" y1="90" y2="90" stroke="#3b434d"/>${bars}</svg>
      <div class="small muted" style="flex:1">第 1 天的支出主要是委托制造中型货车和租用仓库。</div></div>
    <div class="tabs" style="padding:0 24px">${[['trains', '列车'], ['settlements', '聚居地'], ['ledger', '收支明细']].map(([k, l]) => `<button class="tab ${w.tab === k ? 'on' : ''}" data-action="rp-tab" data-v="${k}">${l}</button>`).join('')}</div>
    <div class="scroll" style="padding:12px 24px 24px">${body}</div></div>`;
}

Object.assign(WS_ACTIONS, {
  'rp-tab': v => { S.workspace.tab = v; },
  'rp-open': v => { S.workspace.open = S.workspace.open === v ? null : v; },
  'rp-train': v => { S.workspace = null; select({ kind: 'train', id: v }); return false; },
  'rp-node': v => { S.workspace = null; select({ kind: 'node', id: v }); return false; },
  'rp-plan': v => { S.selection = { kind: 'train', id: v }; openPlan(v); },
});
