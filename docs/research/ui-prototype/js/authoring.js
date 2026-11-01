// 创作模式：内容包定义的浏览、表单编辑、模板地块布局、写入与重载
const AU_KINDS = { voxel: '体素', vehicle: '车辆蓝图', building: '建筑蓝图', equipment: '组装体设备', creature: '生物', settlementType: '聚居地类型', lairType: '生物聚落类型', template: '聚居地模板', process: '工序', rule: '规则与生成配置' };
const AU_RO = ['process', 'rule'];
const PLOT = { Small: ['小', 4, 4], Medium: ['中', 6, 6], Large: ['大', 8, 8], Giant: ['巨型', 12, 12], SmallStation: ['小站点', 4, 10], LargeStation: ['大站点', 6, 16] };
const MATERIALS = { PaintedMetal: '涂漆金属', BareMetal: '裸金属', Concrete: '混凝土', StoneSoil: '石头和土', Glass: '玻璃', PolymerRubber: '塑料和橡胶', Organic: '有机物' };
const BEHAVIORS = { power: ['动力', [['traction', '牵引力', 'kN'], ['speed', '额定速度', 'm/s']]], storage: ['库存', [['capacity', '库存容量', '件']]], weapon: ['武器', [['damage', '伤害', ''], ['interval', '攻击间隔', '秒']]], production: ['生产', [['work', '生产工作量', '每秒']]], station: ['站台', []] };

function auDefs() {
  return [
    { kind: 'voxel', id: 'steel_plate', f: { name: '钢板', desc: '车体和建筑常用的薄钢板', icon: 'icons/steel_plate.png', sim: true, value: 12, mass: 0.4, hp: 30, raw: false, behaviors: [], orient: 'FourWay', collision: 'FullBox', present: 'Cube', material: 'PaintedMetal', texTop: 'textures/steel_top.png', texSide: 'textures/steel_side.png', texBottom: '', color: '#b8743f' } },
    { kind: 'voxel', id: 'diesel_core', f: { name: '柴油机芯', desc: '', icon: 'icons/diesel.png', sim: true, value: 140, mass: 2.1, hp: 80, raw: false, behaviors: [{ type: 'power', traction: 60, speed: 22 }], orient: 'FourWay', collision: 'FullBox', present: 'Cube', material: 'BareMetal', texTop: 'textures/diesel_top.png', texSide: 'textures/diesel_side.png', texBottom: 'textures/diesel_bottom.png', color: '#c9965f' } },
    { kind: 'voxel', id: 'glass_pane', f: { name: '车窗玻璃', desc: '', icon: 'icons/glass.png', sim: true, value: 8, mass: 0.2, hp: 6, raw: false, behaviors: [], orient: 'None', collision: 'FullBox', present: 'Cube', material: 'Glass', texTop: 'textures/glass.png', texSide: 'textures/glass.png', texBottom: 'textures/glass.png', color: '#8fd3e0' } },
    { kind: 'vehicle', id: 'wagon_m', f: { name: '中型货车', size: 'Medium' } }, { kind: 'vehicle', id: 'loco_m', f: { name: '中型机车', size: 'Medium' } },
    { kind: 'building', id: 'farm', f: { name: '农场', plot: 'Medium' } }, { kind: 'building', id: 'small_station', f: { name: '小站点', plot: 'SmallStation' } }, { kind: 'building', id: 'warehouse', f: { name: '仓库', plot: 'Small' } },
    { kind: 'equipment', id: 'bogie', f: { name: '转向架' } }, { kind: 'creature', id: 'big_beast', f: { name: '大型生物' } }, { kind: 'creature', id: 'small_beast', f: { name: '小型生物' } },
    { kind: 'settlementType', id: 'farm_town', f: { name: '农业镇', desc: '以种植和畜牧为主', major: false, weight: 3, minPop: 30, maxPop: 600 } },
    { kind: 'settlementType', id: 'mining_town', f: { name: '矿业镇', desc: '', major: false, weight: 2, minPop: 40, maxPop: 800 } },
    { kind: 'lairType', id: 'spike_nest', f: { name: '骨刺巢', desc: '', model: '', weight: 1, members: [{ creature: 'big_beast', w: 1 }, { creature: 'small_beast', w: 3 }], maxMembers: 12, raidSec: 600 } },
    { kind: 'template', id: 'farm_town_a', f: { name: '农业镇布局 A', desc: '', stype: 'farm_town', weight: 1, plots: [{ no: 1, size: 'SmallStation', x: 3, z: 10, rot: 0, pre: 'small_station' }, { no: 2, size: 'Medium', x: 11, z: 3, rot: 0, pre: '' }, { no: 3, size: 'Medium', x: 19, z: 3, rot: 0, pre: '' }, { no: 4, size: 'Small', x: 22, z: 15, rot: 0, pre: '' }, { no: 5, size: 'Large', x: 10, z: 17, rot: 0, pre: '' }],
      start: [{ plot: 2, bp: 'farm' }], stock: [{ ref: 'grain', qty: 40 }] } },
    { kind: 'process', id: 'grow_grain', f: { name: '种植谷物' } }, { kind: 'process', id: 'raise_livestock', f: { name: '饲养牲畜' } },
    { kind: 'rule', id: 'market_default', f: { name: '默认市场规则' } }, { kind: 'rule', id: 'standard_world', f: { name: '标准测试世界（生成配置）' } },
  ];
}
function auState(from) { return { src: 'core', defs: auDefs(), sel: 'voxel:steel_plate', q: '', dirty: new Set(), drafts: new Set(), plot: null, from, write: null }; }
function openAuthoring(from) { S.au = auState(from); S.screen = 'authoring'; S.overlay = null; S.popover = null; S.workspace = null; }
const auDef = (k = S.au.sel) => S.au.defs.find(d => `${d.kind}:${d.id}` === k);
const auName = (kind, id) => S.au.defs.find(d => d.kind === kind && d.id === id)?.f.name;

// 每个定义的校验结果：[字段路径, 说明]
function auProblems(d) {
  const f = d.f, out = [];
  if (!/^[a-z][a-z0-9_]*$/.test(d.id)) out.push(['id', '本地 ID 只能用小写字母、数字和下划线，并以字母开头']);
  if (!f.name) out.push(['name', '名称不能为空']);
  if (d.kind === 'voxel') {
    if (f.mass != null && f.mass < 0) out.push(['mass', '质量不能是负数']);
    if (f.present === 'Cube') ['texTop', 'texSide', 'texBottom'].forEach(k => { if (!f[k]) out.push([k, '方块表现需要顶面、侧面、底面三张贴图']); });
  }
  if (d.kind === 'template') {
    f.plots.forEach((p, i) => f.plots.forEach((q, j) => { if (j > i && overlap(p, q)) out.push([`plot${q.no}`, `地块 ${p.no} 和地块 ${q.no} 重叠`]); }));
    f.start.forEach(s => { if (!f.plots.some(p => p.no === s.plot)) out.push(['start', `起始建筑用的地块 ${s.plot} 不存在`]); });
  }
  if (d.kind === 'lairType' && f.members.some(m => !(m.w > 0))) out.push(['members', '成员权重必须大于 0']);
  return out;
}
const plotRect = p => { const [, w, h] = PLOT[p.size], r = p.rot % 180; return [p.x, p.z, r ? h : w, r ? w : h]; };
function overlap(a, b) { const [ax, az, aw, ah] = plotRect(a), [bx, bz, bw, bh] = plotRect(b); return ax < bx + bw && bx < ax + aw && az < bz + bh && bz < az + ah; }

// ---------- 字段控件 ----------
function fld(label, path, ctl, hint = '') {
  const err = auProblems(auDef()).filter(([p]) => p === path).map(([, m]) => m)[0];
  return `<div class="field au-field ${err ? 'bad' : ''}" id="auf-${path}"><label>${label}</label>${ctl}${err ? `<div class="field-error">${err}</div>` : hint ? `<div class="small faint">${hint}</div>` : ''}</div>`;
}
const fText = (label, k, hint) => fld(label, k, `<input id="au-${k}" type="text" value="${auDef().f[k] ?? ''}" data-input="au-f" data-k="${k}">`, hint);
const fNum = (label, k, unit = '', nullable) => fld(label, k, `<div style="display:flex;gap:6px;align-items:center"><input id="au-${k}" type="number" value="${auDef().f[k] ?? ''}" placeholder="${nullable ? '使用默认值' : ''}" data-input="au-f" data-k="${k}" data-num="1" style="flex:1">${unit ? `<span class="muted small">${unit}</span>` : ''}</div>`);
const fBool = (label, k) => `<label class="switch ${auDef().f[k] ? 'on' : ''}" data-action="au-bool" data-v="${k}" style="margin:6px 0"><i></i>${label}</label>`;
const fEnum = (label, k, opts) => fld(label, k, `<select id="au-${k}" data-change="au-f" data-k="${k}">${Object.entries(opts).map(([v, l]) => `<option value="${v}" ${auDef().f[k] === v ? 'selected' : ''}>${l}</option>`).join('')}</select>`);
const fRef = (label, k, kind, nullable = true) => fld(label, k, `<select id="au-${k}" data-change="au-f" data-k="${k}">${nullable ? '<option value="">（不指定）</option>' : ''}${S.au.defs.filter(d => d.kind === kind).map(d => `<option value="${d.id}" ${auDef().f[k] === d.id ? 'selected' : ''}>${d.f.name} · core:${d.id}</option>`).join('')}</select>`, `引用一个${AU_KINDS[kind]}`);
const fAsset = (label, k) => fld(label, k, `<div style="display:flex;gap:6px"><input id="au-${k}" type="text" value="${auDef().f[k] ?? ''}" placeholder="包内相对路径" data-input="au-f" data-k="${k}" style="flex:1"><button class="btn" data-action="todo" data-v="选择资产文件">选择文件…</button></div>`);
const sec = (title, body) => `<div class="section"><div class="section-head"><h4>${title}</h4></div><div class="au-grid">${body}</div></div>`;

function auForm(d) {
  const f = d.f, head = `<div class="section" style="display:flex;align-items:center;gap:10px"><div style="flex:1"><div class="small muted">${AU_KINDS[d.kind]}</div><h3 style="margin:2px 0 0;font-size:18px">${f.name || '（未命名）'} <span class="muted small" style="font-weight:400">core:${d.id}</span></h3></div>
    ${['vehicle', 'building', 'equipment', 'creature'].includes(d.kind) ? `<button class="btn" data-action="au-open-editor">${icon('edit')} 在设计编辑器中编辑</button>` : ''}<button class="btn danger" data-action="au-del">删除定义…</button></div>`;
  if (AU_RO.includes(d.kind)) return head + `<div class="section"><div class="notice">${icon('info')}<div class="grow">这类定义还没有表单，只能直接编辑包里的源文件。改完后点「重新载入」读取。</div></div></div>`;
  if (d.kind === 'voxel') return head
    + sec('基本', fText('名称', 'name') + fText('说明', 'desc') + fAsset('图标', 'icon'))
    + sec('模拟', fBool('参与模拟（有质量、耐久和价值）', 'sim') + (f.sim ? fNum('基本货币价值', 'value', '', true) + fNum('质量', 'mass', '吨', true) + fNum('最大耐久', 'hp', '', true) + fBool('原材料', 'raw') : ''))
    + `<div class="section"><div class="section-head"><h4>行为</h4><button class="btn ghost small" data-action="au-beh-add">${icon('plus', 'sm')} 添加行为</button></div>${f.behaviors.map((b, i) => `<div class="au-row"><select data-change="au-beh-type" data-i="${i}" aria-label="行为种类">${Object.entries(BEHAVIORS).map(([k, [l]]) => `<option value="${k}" ${b.type === k ? 'selected' : ''}>${l}</option>`).join('')}</select>
      ${BEHAVIORS[b.type][1].map(([k, l, u]) => `<label class="small muted">${l}</label><input type="number" value="${b[k] ?? ''}" data-input="au-beh" data-i="${i}" data-k="${k}" style="width:90px"><span class="small muted">${u}</span>`).join('')}<span style="flex:1"></span><button class="btn ghost icon-only" data-action="au-beh-del" data-v="${i}" title="删除这个行为">${icon('trash', 'sm')}</button></div>`).join('') || '<div class="muted small">没有行为。普通结构体素不需要行为。</div>'}</div>`
    + sec('朝向与碰撞', fEnum('允许的朝向', 'orient', { None: '不能旋转', FourWay: '水平四个方向', All24: '全部 24 个方向' }) + fEnum('碰撞', 'collision', { None: '没有碰撞', FullBox: '整格', LocalBox: '自定义范围' }))
    + sec('表现', fEnum('表现方式', 'present', { None: '不显示', Cube: '方块（贴图）', Mesh: '模型' }) + fEnum('材质类别', 'material', MATERIALS) + (f.present === 'Cube' ? fAsset('顶面贴图', 'texTop') + fAsset('侧面贴图', 'texSide') + fAsset('底面贴图', 'texBottom') : ''));
  if (d.kind === 'settlementType') return head + sec('基本', fText('名称', 'name') + fText('说明', 'desc') + fBool('主要聚居地', 'major')) + sec('生成', fNum('生成权重', 'weight', '', true) + fNum('最少人口', 'minPop', '人', true) + fNum('最多人口', 'maxPop', '人', true));
  if (d.kind === 'lairType') return head + sec('基本', fText('名称', 'name') + fText('说明', 'desc') + fRef('场景模型', 'model', 'building'))
    + `<div class="section"><div class="section-head"><h4>成员</h4><button class="btn ghost small" data-action="au-mem-add">${icon('plus', 'sm')} 添加成员</button></div>${f.members.map((m, i) => `<div class="au-row"><select data-change="au-mem" data-i="${i}" data-k="creature" aria-label="生物">${S.au.defs.filter(x => x.kind === 'creature').map(x => `<option value="${x.id}" ${m.creature === x.id ? 'selected' : ''}>${x.f.name}</option>`).join('')}</select><label class="small muted">权重</label><input type="number" value="${m.w}" data-input="au-mem" data-i="${i}" data-k="w" style="width:80px"><span style="flex:1"></span><button class="btn ghost icon-only" data-action="au-mem-del" data-v="${i}">${icon('trash', 'sm')}</button></div>`).join('')}
      ${auProblems(d).filter(([p]) => p === 'members').map(([, m]) => `<div class="field-error">${m}</div>`).join('')}</div>` + sec('行为', fNum('最多成员', 'maxMembers', '只', true) + fNum('袭击间隔', 'raidSec', '秒', true));
  if (d.kind === 'template') return head + sec('基本', fText('名称', 'name') + fText('说明', 'desc') + fRef('聚居地类型', 'stype', 'settlementType', false) + fNum('生成权重', 'weight', '', true))
    + `<div class="section"><div class="section-head"><h4>地块</h4><span class="small muted">在右侧布局图里拖动地块、按 R 旋转</span></div>${f.plots.map((p, i) => { const err = auProblems(d).find(([k]) => k === `plot${p.no}`);
      return `<div class="au-row ${S.au.plot === p.no ? 'on' : ''}" id="plot-row-${p.no}" data-action="au-plot" data-v="${p.no}"><b style="width:56px">地块 ${p.no}</b><select data-change="au-plot-size" data-i="${i}" aria-label="地块尺寸">${Object.entries(PLOT).map(([k, [l]]) => `<option value="${k}" ${p.size === k ? 'selected' : ''}>${l}</option>`).join('')}</select>
        <span class="small muted">位置 ${p.x}, ${p.z} · 朝向 ${['北', '东', '南', '西'][p.rot / 90]}</span><select data-change="au-plot-pre" data-i="${i}" aria-label="预建建筑" style="width:150px"><option value="">（不预建建筑）</option>${S.au.defs.filter(x => x.kind === 'building').map(x => `<option value="${x.id}" ${p.pre === x.id ? 'selected' : ''}>${x.f.name}</option>`).join('')}</select>
        <span style="flex:1"></span><button class="btn ghost icon-only" data-action="au-plot-del" data-v="${i}" title="删除地块">${icon('trash', 'sm')}</button></div>${err ? `<div class="field-error" style="margin:-2px 0 6px 12px">${err[1]}</div>` : ''}`; }).join('')}
      <button class="btn ghost small" style="margin-top:6px" data-action="au-plot-add">${icon('plus', 'sm')} 添加地块</button></div>`
    + `<div class="section"><div class="section-head"><h4>起始建筑与初始库存</h4></div>${f.start.map(s => `<div class="au-row"><span>地块 ${s.plot}</span><span class="muted">建</span><b>${auName('building', s.bp)}</b></div>`).join('')}${f.stock.map(s => `<div class="au-row"><span>初始库存</span>${goodTag(s.ref)}<span>×${s.qty}</span></div>`).join('')}
      ${auProblems(d).filter(([p]) => p === 'start').map(([, m]) => `<div class="field-error">${m}</div>`).join('')}</div>`;
  return head + sec('基本', fText('名称', 'name')) + `<div class="section"><div class="muted">这个蓝图的体素网格和规格在设计编辑器里编辑。</div></div>`;
}

function auPreview(d) {
  if (d.kind === 'voxel') return `<div class="section"><div class="section-head"><h4>预览</h4><span class="small muted">改动后自动更新</span></div><div style="display:flex;justify-content:center;background:#232a31;border-radius:6px;padding:20px">${d.f.present === 'None' ? '<div class="muted">不显示</div>' : `<svg width="200" height="170">${isoBoxes([[-1, -1, 0, 2, 2, 2, d.f.color]], 200, 210, 34)}</svg>`}</div></div>`;
  if (['vehicle', 'building', 'equipment', 'creature'].includes(d.kind)) return `<div class="section"><div style="display:flex;justify-content:center;background:#232a31;border-radius:6px">${thumb({ kind: d.kind === 'building' ? 'building' : 'vehicle', type: d.id.startsWith('loco') ? 'locoM' : 'wagonM' }, 360, 200, 11)}</div></div>`;
  if (d.kind !== 'template') return '';
  const c = 12, n = 32;
  const rects = d.f.plots.map(p => { const [x, z, w, h] = plotRect(p), bad = d.f.plots.some(q => q !== p && overlap(p, q)), on = S.au.plot === p.no;
    return `<g data-plot="${p.no}" style="cursor:move"><rect x="${x * c}" y="${z * c}" width="${w * c}" height="${h * c}" rx="3" fill="${p.size.includes('Station') ? '#2b77d0' : '#b8743f'}" fill-opacity="${on ? .55 : .35}" stroke="${bad ? '#c9423a' : on ? '#e0a22e' : '#e6e9ec'}" stroke-width="${bad || on ? 2.5 : 1}"/>
      <text x="${(x + w / 2) * c}" y="${(z + h / 2) * c + 4}" text-anchor="middle" font-size="12" fill="#fff" pointer-events="none">${p.no} · ${PLOT[p.size][0]}</text>
      <path d="M${(x + w / 2) * c} ${z * c + 4} l-5 7 h10z" fill="#fff" opacity=".8" transform="rotate(${p.rot} ${(x + w / 2) * c} ${(z + h / 2) * c})" pointer-events="none"/></g>`; }).join('');
  return `<div class="section"><div class="section-head"><h4>地块布局</h4><span class="small muted">俯视 · 每格 1 米</span></div>
    <svg id="au-layout" width="${n * c}" height="${n * c}" style="background:#5f6b55;border-radius:6px;display:block;margin:0 auto">${[...Array(n + 1)].map((_, i) => `<line x1="${i * c}" y1="0" x2="${i * c}" y2="${n * c}" stroke="rgba(255,255,255,${i % 4 ? .05 : .12})"/><line x1="0" y1="${i * c}" x2="${n * c}" y2="${i * c}" stroke="rgba(255,255,255,${i % 4 ? .05 : .12})"/>`).join('')}${rects}</svg>
    <div style="display:flex;gap:8px;margin-top:8px;justify-content:center"><button class="btn" data-action="au-plot-rot" ${S.au.plot ? '' : 'disabled'}>旋转所选地块（R）</button><button class="btn" data-action="todo" data-v="在 3D 场景模型上查看">在 3D 场景里查看</button></div>
    <div class="small faint" style="margin-top:6px;text-align:center">箭头指向地块正面。红框表示和其他地块重叠。</div></div>`;
}

function authoringScreen() {
  const A = S.au, d = auDef(), q = A.q.trim();
  const probs = A.defs.flatMap(x => auProblems(x).map(p => [x, p]));
  const tree = Object.entries(AU_KINDS).map(([k, l]) => { const items = A.defs.filter(x => x.kind === k && (!q || x.f.name.includes(q) || x.id.includes(q))); if (!items.length) return '';
    return `<div class="group-label">${l}${AU_RO.includes(k) ? '（只读）' : ''} ${items.length}</div>` + items.map(x => { const key = `${x.kind}:${x.id}`, bad = auProblems(x).length;
      return `<div class="row ${A.sel === key ? 'on' : ''}" data-action="au-sel" data-v="${key}"><div class="grow"><div class="title">${x.f.name}</div><div class="sub" style="font-family:Consolas,monospace">${x.id}</div></div>${bad ? `<span style="color:#ec8a84" title="${bad} 个问题">${icon('alert', 'sm')}</span>` : A.dirty.has(key) ? '<span style="color:#e9b45a" title="有未保存的修改">●</span>' : A.drafts.has(key) ? '<span class="chip neutral" title="已存草稿，还没写入包">草稿</span>' : ''}</div>`; }).join(''); }).join('');
  const pending = A.drafts.size, dirty = A.dirty.size;
  return `<div class="ed">
    <div class="ed-top"><button class="btn ghost" data-action="au-exit">${icon('back')} 退出</button><div class="title">创作模式</div>
      <select id="au-src" data-change="au-src" aria-label="源包" style="width:240px;height:32px"><option value="core">core（开发源码）</option><option value="steam">mod:steam-pack（本地 Mod）</option><option value="new">新建本地 Mod…</option></select>
      <button class="btn ghost" data-action="todo" data-v="重新载入源包" title="源文件被外部修改后重新读取">重新载入</button>
      <div style="flex:1"></div><span class="chip warning">${icon('pause')}世界已暂停</span>
      ${probs.length ? `<button class="btn ghost" style="color:#ec8a84" data-action="au-sel" data-v="${probs[0][0].kind}:${probs[0][0].id}">${icon('alert', 'sm')} ${probs.length} 个问题</button>` : ''}
      <span class="small ${dirty ? '' : 'muted'}" style="${dirty ? 'color:#e9b45a' : ''}">${dirty ? `${dirty} 个定义有未保存的修改` : pending ? `${pending} 个草稿待写入` : '没有待写入的修改'}</span>
      <button class="btn" data-action="au-save" ${dirty ? '' : 'disabled'} title="Ctrl S">保存草稿</button><button class="btn primary" data-action="au-write" ${pending && !dirty ? '' : 'disabled'} title="${dirty ? '先保存草稿' : pending ? '' : '没有待写入的草稿'}">写入包并重载…</button></div>
    <div class="ed-side" style="left:0;right:auto;width:320px;border-left:0;border-right:1px solid var(--border)"><div class="panel-tools" style="padding-top:12px"><input id="au-q" type="search" placeholder="搜索定义" value="${A.q}" data-input="au-q"><button class="btn primary icon-only" data-action="au-new" title="新建定义">${icon('plus')}</button></div><div class="scroll">${tree}</div></div>
    <div style="position:absolute;top:48px;left:320px;right:${d.kind === 'template' ? 440 : d.kind === 'voxel' || ['vehicle', 'building', 'equipment', 'creature'].includes(d.kind) ? 420 : 0}px;bottom:0;overflow-y:auto;background:var(--bg-panel)" class="au-form">${auForm(d)}</div>
    ${auPreview(d) ? `<div class="ed-side" style="width:${d.kind === 'template' ? 440 : 420}px"><div class="scroll" style="padding:0">${auPreview(d)}</div></div>` : ''}</div>`;
}

function markDirty() { S.au.dirty.add(S.au.sel); }
Object.assign(INPUTS, {
  'au-q': v => { S.au.q = v; },
  'au-f': (v, el) => { auDef().f[el.dataset.k] = el.dataset.num ? (v === '' ? null : +v) : v; markDirty(); },
  'au-beh': (v, el) => { auDef().f.behaviors[+el.dataset.i][el.dataset.k] = +v; markDirty(); },
  'au-beh-type': (v, el) => { auDef().f.behaviors[+el.dataset.i] = { type: v }; markDirty(); },
  'au-mem': (v, el) => { auDef().f.members[+el.dataset.i][el.dataset.k] = el.dataset.k === 'w' ? +v : v; markDirty(); },
  'au-plot-size': (v, el) => { auDef().f.plots[+el.dataset.i].size = v; markDirty(); },
  'au-plot-pre': (v, el) => { auDef().f.plots[+el.dataset.i].pre = v; markDirty(); },
  'au-src': v => { if (v === 'new') { document.getElementById('au-src').value = 'core'; todo('新建本地 Mod'); } else toast('info', v === 'core' ? '已打开 core（开发源码）' : '已打开 mod:steam-pack', null, false); },
  'au-nd-id': v => { S.auNew.id = v; }, 'au-nd-kind': v => { S.auNew.kind = v; },
});
Object.assign(WS_ACTIONS, {
  'au-sel': v => { S.au.sel = v; S.au.plot = null; },
  'au-bool': v => { const f = auDef().f; f[v] = !f[v]; markDirty(); },
  'au-beh-add': () => { auDef().f.behaviors.push({ type: 'storage', capacity: 10 }); markDirty(); }, 'au-beh-del': v => { auDef().f.behaviors.splice(+v, 1); markDirty(); },
  'au-mem-add': () => { auDef().f.members.push({ creature: 'small_beast', w: 1 }); markDirty(); }, 'au-mem-del': v => { auDef().f.members.splice(+v, 1); markDirty(); },
  'au-plot': v => { S.au.plot = +v; },
  'au-plot-add': () => { const p = auDef().f.plots; const no = Math.max(0, ...p.map(x => x.no)) + 1; p.push({ no, size: 'Small', x: 1, z: 1, rot: 0, pre: '' }); S.au.plot = no; markDirty(); },
  'au-plot-del': v => { auDef().f.plots.splice(+v, 1); S.au.plot = null; markDirty(); },
  'au-plot-rot': () => { const p = auDef().f.plots.find(x => x.no === S.au.plot); if (p) { p.rot = (p.rot + 90) % 360; markDirty(); } },
  'au-open-editor': () => { const d = auDef(); openEditor({ name: d.f.name, lib: 'local', from: 'authoring' }); S.editor.au = S.au; },
  'au-del': () => { const d = auDef(); S.dialog = { title: `删除定义「${d.f.name}」？`, body: `删除会在写入包时生效。其他定义如果引用了 core:${d.id}，写入时会报错并指出位置。`, confirm: '删除', danger: true, onConfirm: () => { S.au.defs.splice(S.au.defs.indexOf(d), 1); S.au.drafts.add('deleted'); S.au.sel = `${S.au.defs[0].kind}:${S.au.defs[0].id}`; } }; },
  'au-save': () => { S.au.dirty.forEach(k => S.au.drafts.add(k)); S.au.dirty.clear(); toast('complete', '草稿已保存，还没写入包', null, false); },
  'au-new': () => { S.auNew = { kind: 'voxel', id: '' }; S.dialog = { title: '新建定义', body: () => { const ok = /^[a-z][a-z0-9_]*$/.test(S.auNew.id), dup = S.au.defs.some(x => x.id === S.auNew.id);
    return `<div style="display:flex;flex-direction:column;gap:12px"><div class="field"><label for="au-nd-kind">种类</label><select id="au-nd-kind" data-change="au-nd-kind">${Object.entries(AU_KINDS).filter(([k]) => !AU_RO.includes(k)).map(([k, l]) => `<option value="${k}" ${S.auNew.kind === k ? 'selected' : ''}>${l}</option>`).join('')}</select></div>
      <div class="field"><label for="au-nd-id">本地 ID</label><input id="au-nd-id" type="text" value="${S.auNew.id}" data-input="au-nd-id" placeholder="例如 rusty_plate" class="${S.auNew.id && (!ok || dup) ? 'invalid' : ''}">
      ${!S.auNew.id ? '<div class="small faint">只能用小写字母、数字和下划线，并以字母开头。完整 ID 是 core:本地 ID。</div>' : !ok ? '<div class="field-error">只能用小写字母、数字和下划线，并以字母开头</div>' : dup ? '<div class="field-error">这个 ID 已经被用了</div>' : `<div class="small pos">完整 ID：core:${S.auNew.id}</div>`}</div>
      ${['vehicle', 'building', 'equipment', 'creature'].includes(S.auNew.kind) ? '<div class="small muted">蓝图创建后会打开设计编辑器。</div>' : ''}</div>`; },
    disabled: () => !/^[a-z][a-z0-9_]*$/.test(S.auNew.id) || S.au.defs.some(x => x.id === S.auNew.id), confirm: '创建',
    onConfirm: () => { const n = S.auNew, f = n.kind === 'voxel' ? { ...auDefs()[0].f, name: '新体素', behaviors: [] } : n.kind === 'template' ? { name: '新模板', desc: '', stype: 'farm_town', weight: 1, plots: [], start: [], stock: [] } : { name: '新定义', desc: '', members: [], weight: 1 };
      S.au.defs.push({ kind: n.kind, id: n.id, f }); S.au.sel = `${n.kind}:${n.id}`; S.au.dirty.add(S.au.sel); } }; },
  'au-write': () => {
    const list = [...S.au.drafts].filter(k => k !== 'deleted').map(k => auDef(k)?.f.name).filter(Boolean);
    S.dialog = { title: '写入包并重载？', body: `<div>要写入 core（开发源码）的修改：</div><ul style="margin:6px 0 10px;padding-left:20px">${list.map(n => `<li>${n}</li>`).join('')}</ul><div class="notice warning">${icon('alert')}<div class="grow">写入期间不要用其他工具修改 builtin/core。写入前会再检查一次源文件；如果它被外部改过，就一个文件也不写。</div></div>`,
      confirm: '写入并重载', onConfirm: () => { S.au.write = { step: 0 }; const go = () => { if (!S.au?.write) return; S.au.write.step++; if (S.au.write.step >= 5) { S.au.write = { done: true, ok: !S.au.failNext, files: list } ; if (!S.au.failNext) S.au.drafts.clear(); } render(); if (!S.au.write.done) setTimeout(go, 350); }; setTimeout(go, 350); } };
  },
  'au-write-close': () => { S.au.write = null; },
  'au-locate': v => { S.au.write = null; S.au.sel = v; },
  'au-exit': () => { const A = S.au, leave = () => { S.screen = A.from === 'menu' ? 'menu' : 'game'; S.au = null; };
    if (A.dirty.size) S.dialog = { title: '退出创作模式？', body: `${A.dirty.size} 个定义有未保存的修改。`, confirm: '保存草稿并退出', alt: '不保存，直接退出', onConfirm: () => leave() }; else leave(); },
  'open-authoring': () => openAuthoring(S.screen === 'menu' ? 'menu' : 'game'),
});

// 写入进度与结果，盖在创作模式上面
function authoringWriteOverlay() {
  const w = S.au?.write; if (!w) return '';
  const steps = ['校验草稿', '载入并校验候选包', '复核源文件没有被外部修改', '逐个写入文件', '重新载入内容'];
  if (!w.done) return `<div id="dialog-layer"><div class="dialog"><h3>正在写入…</h3><div class="body">${steps.map((s, i) => `<div style="display:flex;gap:8px;padding:3px 0;${i > w.step ? 'opacity:.4' : ''}"><span class="${i < w.step ? 'pos' : ''}">${i < w.step ? icon('check', 'sm') : i === w.step ? '…' : '·'}</span>${s}</div>`).join('')}</div></div></div>`;
  if (w.ok) return `<div id="dialog-layer"><div class="dialog"><h3>写入完成</h3><div class="body"><div class="notice success">${icon('check')}<div class="grow">已写入 ${w.files.length} 个文件，内容重载成功。</div></div>
    <div style="margin-top:8px">${w.files.map(f => `<div class="small"><span class="pos">${icon('check', 'sm')}</span> ${f}</div>`).join('')}</div></div><div class="foot"><button class="btn primary" data-action="au-write-close">完成</button></div></div></div>`;
  return `<div id="dialog-layer"><div class="dialog" style="width:560px"><h3>没有写入</h3><div class="body"><div class="notice error">${icon('alert')}<div class="grow">候选包校验没有通过，一个文件都没有写，游戏里的内容保持原样。草稿还在。</div></div>
    <div style="margin-top:10px"><div class="row" style="cursor:default"><div class="grow"><div class="title">聚居地模板「农业镇布局 A」· 起始建筑</div><div class="sub">引用的建筑蓝图 core:farm 已被删除</div></div><button class="btn" data-action="au-locate" data-v="template:farm_town_a">定位</button></div></div></div>
    <div class="foot"><button class="btn" data-action="au-write-close">关闭</button></div></div></div>`;
}

// 模板布局图：拖动地块，按格对齐
let auDrag = null;
document.addEventListener('pointerdown', e => {
  const g = e.target.closest?.('#au-layout [data-plot]'); if (!g || S.screen !== 'authoring') return;
  const p = auDef().f.plots.find(x => x.no === +g.dataset.plot); S.au.plot = p.no;
  auDrag = { p, x0: e.clientX, y0: e.clientY, px: p.x, pz: p.z }; render();
  setTimeout(() => document.getElementById(`plot-row-${p.no}`)?.scrollIntoView({ block: 'nearest' }));
});
document.addEventListener('pointermove', e => {
  if (!auDrag) return; const { p } = auDrag, [, , w, h] = plotRect(p);
  const nx = Math.max(0, Math.min(32 - w, auDrag.px + Math.round((e.clientX - auDrag.x0) / 12))), nz = Math.max(0, Math.min(32 - h, auDrag.pz + Math.round((e.clientY - auDrag.y0) / 12)));
  if (nx !== p.x || nz !== p.z) { p.x = nx; p.z = nz; markDirty(); render(); }
});
document.addEventListener('pointerup', () => { auDrag = null; });
document.addEventListener('keydown', e => {
  if (S.screen !== 'authoring' || typing() || S.dialog) return;
  if (e.key === 'r' || e.key === 'R') { WS_ACTIONS['au-plot-rot'](); render(); }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); WS_ACTIONS['au-save'](); render(); }
  if (e.key === 'Escape') { e.stopImmediatePropagation(); S.au.plot ? S.au.plot = null : WS_ACTIONS['au-exit'](); render(); }
}, true);
