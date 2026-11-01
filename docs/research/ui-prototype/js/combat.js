// 战斗：地图标记、战斗检查器、战斗列表、列车交战规则
function startBattle() {
  const t = W.trains.t2;
  Object.assign(t, { line: 'l4', from: 'n3', to: 'n4', progress: 0.45, status: 'combat', target: 'n4' }); delete t.at;
  W.vehicles.v4.hp = 0.55; W.vehicles.v5.hp = 0.38;
  W.battles.k1 = {
    line: 'l4', at: 0.45, since: '第 3 天 08:12', ours: ['t2'], focus: null, retreating: {},
    enemy: { name: '骨刺巢的生物群', units: [
      { id: 'u1', name: '大型生物', hp: 0.72 }, { id: 'u2', name: '大型生物', hp: 1 },
      { id: 'u3', name: '小型生物', hp: 0.3 }, { id: 'u4', name: '小型生物', hp: 0.9 }, { id: 'u5', name: '小型生物', hp: 0 } ] },
    log: [['08:19', '列车 2 的机枪击中小型生物，小型生物被消灭'], ['08:17', '大型生物撞击列车 2 的小型货车，耐久降到 38%'], ['08:14', '列车 2 的机枪击中大型生物'], ['08:12', '骨刺巢的生物群在赤岭—北港袭击列车 2']],
  };
  toast('critical', '列车 2 在赤岭—北港遭到生物群袭击', { kind: 'battle', id: 'k1' });
}
const battleName = b => b.line ? lineName(b.line) : nodeName(b.node);
function battlePoint(id) {
  const b = W.battles[id]; if (b.node) return W.nodes[b.node];
  const l = W.lines[b.line], a = W.nodes[l.a], c = W.nodes[l.b];
  return { x: a.x + (c.x - a.x) * b.at, y: a.y + (c.y - a.y) * b.at };
}
function battleMarkers(isSel, isHv) {
  let out = '';
  for (const id of Object.keys(W.battles)) {
    const p = battlePoint(id), on = isSel('battle', id) || isHv('battle', id);
    out += `<g transform="translate(${p.x},${p.y - 26})"><circle r="16" fill="none" stroke="#c9423a" stroke-width="2"><animate attributeName="r" values="14;24;14" dur="1.6s" repeatCount="indefinite"/><animate attributeName="opacity" values="1;0;1" dur="1.6s" repeatCount="indefinite"/></circle>
      <circle r="14" fill="#c9423a" stroke="${on ? '#e0a22e' : '#fff'}" stroke-width="${on ? 3 : 2}"/><g transform="translate(-9,-9) scale(.75)" stroke="#fff" fill="none" stroke-width="2.2" stroke-linecap="round">${ICONS.sword}</g></g>
      <circle class="map-hit" data-kind="battle" data-id="${id}" cx="${p.x}" cy="${p.y - 26}" r="18" fill="transparent"/>`;
  }
  return out;
}

function inspectBattle(id) {
  const b = W.battles[id], alive = b.enemy.units.filter(u => u.hp > 0);
  let h = inspHead({ glyph: 'sword', color: '#ec8a84', name: `战斗 · ${battleName(b)}`, chips: '<span class="chip error">进行中</span>', meta: `开始于 ${b.since} · 我方 ${b.ours.length} 列车 · 敌方 ${alive.length} 只生物` });
  h += `<div class="insp-body">`;
  h += `<div class="section"><div class="section-head"><h4>我方</h4></div>${b.ours.map(tid => {
    const t = W.trains[tid], r = b.retreating[tid];
    return `<div style="margin-bottom:8px"><div style="display:flex;align-items:center;gap:8px"><b style="flex:1;cursor:pointer" data-action="select" data-v="train:${tid}">${t.name}</b>
      ${r != null ? `<span class="chip warning">${icon('back')}撤退中 · ${Math.ceil(r)} 秒后脱离</span>` : `<button class="btn" style="height:28px" data-action="battle-retreat" data-v="${tid}">${icon('back', 'sm')} 撤退</button>`}</div>
      ${t.vehicles.map(v => vehicleRow(v)).join('')}</div>`;
  }).join('')}<div class="small faint">撤退的列车停火一段时间后脱离战斗，开回出发的聚居地并停车。</div></div>`;
  h += `<div class="section"><div class="section-head"><h4>${b.enemy.name}</h4><span class="muted small">点一个目标集中攻击</span></div>${b.enemy.units.map(u => {
    const dead = u.hp <= 0, foc = b.focus === u.id;
    return `<div class="row ${foc ? 'on' : ''}" ${dead ? 'style="opacity:.4;cursor:default"' : `data-action="battle-focus" data-v="${u.id}"`}>
      <span style="color:#ec8a84">${icon(foc ? 'locate' : 'lair')}</span><div class="grow"><div class="title">${u.name}${foc ? ' <span class="chip warning">集中攻击</span>' : ''}</div></div>
      ${dead ? '<span class="muted small">已消灭</span>' : `<div style="width:110px"><div class="bar ${u.hp < .5 ? 'bad' : 'warn'}"><i style="width:${u.hp * 100}%"></i></div></div>`}</div>`;
  }).join('')}${b.focus ? `<button class="btn ghost small" style="margin-top:6px" data-action="battle-focus" data-v="${b.focus}">取消集中攻击</button>` : ''}</div>`;
  h += `<div class="section"><div class="section-head"><h4>战报</h4></div>${b.log.map(([tm, text]) => `<div style="display:flex;gap:10px;padding:3px 0"><span class="faint small" style="width:40px;flex:none">${tm}</span><span class="small">${text}</span></div>`).join('')}</div>`;
  return h + `</div><div class="insp-foot"><button class="btn" data-action="scene">${icon('scene')} 进入战斗场景</button></div>`;
}

function combatDrawer(head) {
  const live = Object.entries(W.battles);
  return head('战斗', live.length) + `<div class="scroll">
    <div class="group-label">进行中</div>${live.map(([id, b]) => `<div class="row ${S.selection?.kind === 'battle' && S.selection.id === id ? 'on' : ''}" data-action="select" data-v="battle:${id}">
      <span style="color:#ec8a84">${icon('sword')}</span><div class="grow"><div class="title">${battleName(b)}</div><div class="sub">${b.ours.map(t => W.trains[t].name).join('、')} 对 ${b.enemy.name}</div></div></div>`).join('') || '<div class="muted" style="padding:4px 12px">没有进行中的战斗。</div>'}
    <div class="group-label">最近结束</div>
    <div class="row" style="cursor:default"><span class="pos">${icon('check')}</span><div class="grow"><div class="title">南坡附近 · 胜利</div><div class="sub">第 2 天 18:30 · 列车 1 耐久 −12%</div></div></div>
    <div class="small faint" style="padding:12px">每列车遇敌时怎么做，在列车检查器的「交战」页设置。</div></div>`;
}

const RULE_TEXT = { priority: { nearest: '最近的目标', weakest: '耐久最低的目标', threat: '威胁最大的目标' }, scope: { all: '攻击所有敌对目标', defend: '只还击' } };
function trainRulesTab(id) {
  const t = W.trains[id], r = t.rules ||= { engage: 'fight', retreatAt: 40, priority: 'nearest', scope: 'all' };
  const sentence = r.engage === 'retreat' ? '遇到敌人时立即撤退，不开火。' : `遇到敌人时交战，${RULE_TEXT.scope[r.scope]}，优先打${RULE_TEXT.priority[r.priority]}；结构耐久低于 ${r.retreatAt}% 时撤退。`;
  return `<div class="section"><div class="notice">${icon('info')}<div class="grow">${sentence}</div></div></div>
    <div class="section"><div class="field"><label>遇到敌人时</label><div class="seg">${[['fight', '交战'], ['retreat', '撤退']].map(([k, l]) => `<button class="${r.engage === k ? 'on' : ''}" data-action="rule-set" data-v="engage:${k}">${l}</button>`).join('')}</div></div></div>
    ${r.engage === 'fight' ? `<div class="section"><div class="field"><label for="rule-retreat">结构耐久低于多少时撤退</label><div style="display:flex;gap:10px;align-items:center"><input id="rule-retreat" type="range" min="0" max="90" step="5" value="${r.retreatAt}" data-input="rule-retreat" style="flex:1"><b style="width:44px;text-align:right">${r.retreatAt}%</b></div><div class="small faint">设为 0% 表示打到底，不撤退。</div></div></div>
    <div class="section"><div class="field"><label>攻击范围</label><div class="seg">${Object.entries(RULE_TEXT.scope).map(([k, l]) => `<button class="${r.scope === k ? 'on' : ''}" data-action="rule-set" data-v="scope:${k}">${l}</button>`).join('')}</div></div>
      <div class="field" style="margin-top:12px"><label for="rule-prio">优先攻击</label><select id="rule-prio" data-change="rule-prio">${Object.entries(RULE_TEXT.priority).map(([k, l]) => `<option value="${k}" ${r.priority === k ? 'selected' : ''}>${l}</option>`).join('')}</select></div></div>` : ''}
    <div class="section"><div class="small faint">改动立即生效，正在进行的战斗也按新规则执行。</div></div>`;
}

function combatTick() {
  for (const [id, b] of Object.entries(W.battles)) {
    for (const tid of Object.keys(b.retreating)) {
      b.retreating[tid] -= 0.5 * S.speed;
      if (b.retreating[tid] <= 0) {
        delete b.retreating[tid]; b.ours = b.ours.filter(x => x !== tid);
        const t = W.trains[tid]; Object.assign(t, { status: 'moving', running: false, target: t.from }); [t.from, t.to] = [t.to, t.from]; t.progress = 1 - t.progress;
        b.log.unshift([clock().slice(-5), `${t.name}脱离战斗，返回${nodeName(t.to)}`]);
        toast('warning', `${t.name}已撤退，正在返回${nodeName(t.to)}`, { kind: 'train', id: tid });
      }
    }
    if (!b.ours.length) { delete W.battles[id]; if (S.selection?.kind === 'battle' && S.selection.id === id) S.selection = null; }
  }
}

Object.assign(INPUTS, {
  'rule-retreat': v => { W.trains[S.selection.id].rules.retreatAt = +v; },
  'rule-prio': v => { W.trains[S.selection.id].rules.priority = v; },
});
Object.assign(PANEL_ACTIONS, {
  'battle-focus': v => { const b = W.battles[S.selection.id]; b.focus = b.focus === v ? null : v; if (b.focus) b.log.unshift([clock().slice(-5), `你命令集中攻击${b.enemy.units.find(u => u.id === v).name}`]); },
  'battle-retreat': v => { const b = W.battles[S.selection.id]; b.retreating[v] = 5; b.log.unshift([clock().slice(-5), `你命令${W.trains[v].name}撤退`]); },
  'rule-set': v => { const [k, x] = v.split(':'); W.trains[S.selection.id].rules[k] = x; },
});
