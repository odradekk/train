// 设计编辑器：等轴体素视口、工具、面板、保存与改装
const VOXDEFS = {
  frame: { name: '底架', color: '#4a4f55', mat: 'machinery' }, steel: { name: '钢结构', color: '#8a96a3', mat: 'machinery' },
  plate: { name: '车体板', color: '#b8743f', mat: 'machinery' }, wood: { name: '木板', color: '#9a7a52', mat: 'concrete' },
  glass: { name: '车窗玻璃', color: '#8fd3e0', mat: 'glass' }, cargo: { name: '货舱壁', color: '#6f8f5a', mat: 'concrete' },
};
const EQUIPMENT = {
  bogie: { name: '转向架', size: [2, 4, 1], color: '#33383e', note: '车辆至少需要 2 个' },
  hold: { name: '货舱', size: [4, 4, 2], color: '#6f8f5a', note: '载量 +20' },
  diesel: { name: '柴油机组', size: [3, 4, 3], color: '#c9965f', note: '牵引力 +60' },
  cab: { name: '驾驶台', size: [2, 4, 3], color: '#2b77d0', note: '机车需要一个' },
  mg: { name: '机枪', size: [1, 1, 1], color: '#c9423a', note: '武装' },
};
const ENVELOPE = { '小型': [12, 4, 5], '中型': [16, 6, 7], '大型': [20, 6, 8], '超大型': [24, 8, 9], '泰坦': [40, 12, 14] };
const PALETTE = ['#b8743f', '#8a96a3', '#4a4f55', '#2b77d0', '#c9423a', '#e0a22e', '#6f8f5a', '#e6e9ec'];
const key = (x, y, z) => `${x},${y},${z}`;
const unkey = k => k.split(',').map(Number);

function wagonModel(blank) {
  const vox = new Map(), parts = { body: { name: '车体结构' } };
  if (blank) return { vox, parts };
  const eq = (id, type, x, y, z) => { parts[id] = { name: EQUIPMENT[type].name, eq: type }; const [dx, dy, dz] = EQUIPMENT[type].size; for (let i = 0; i < dx; i++) for (let j = 0; j < dy; j++) for (let k = 0; k < dz; k++) vox.set(key(x + i, y + j, z + k), { d: 'steel', c: EQUIPMENT[type].color, p: id }); };
  eq('e1', 'bogie', 1, 1, 0); eq('e2', 'bogie', 13, 1, 0);
  for (let x = 0; x < 16; x++) for (let y = 1; y < 5; y++) vox.set(key(x, y, 1), { d: 'frame', c: VOXDEFS.frame.color, p: 'body' });
  for (let z = 2; z < 4; z++) for (let x = 0; x < 16; x++) for (let y = 1; y < 5; y++) if (x === 0 || x === 15 || y === 1 || y === 4) vox.set(key(x, y, z), { d: 'plate', c: VOXDEFS.plate.color, p: 'body' });
  return { vox, parts };
}

function openEditor({ name, lib, size = '中型', from, refit = null, blank = false }) {
  const m = wagonModel(blank);
  S.editor = { name, lib, size, from, refit, ...m, orig: refit ? new Map(m.vox) : null, tool: 'place', op: 'add', def: 'plate', color: VOXDEFS.plate.color, shape: '立方体', facing: '前',
    brush: 1, boxH: 1, plane: 'surface', clip: false, clipZ: 6, view: 0, zoom: 1, pan: [0, 0], sel: new Set(), hover: null, boxStart: null, equip: null,
    undo: [], redo: [], dirty: false, panel: 'voxels', renaming: false, msg: null };
  S.screen = 'editor'; S.workspace = null; S.selection = null; S.drawer = null;
}

// ---------- 投影 ----------
const rot = (dx, dy, k) => { for (let i = 0; i < k; i++) [dx, dy] = [-dy, dx]; return [dx, dy]; };
const unrot = (dx, dy, k) => { for (let i = 0; i < k; i++) [dx, dy] = [dy, -dx]; return [dx, dy]; };
function projector(E, w, h) {
  const env = ENVELOPE[E.size], s = 26 * E.zoom * Math.min(1, 16 / env[0] * 1.2), cx = env[0] / 2, cy = env[1] / 2;
  const P = (x, y, z) => [w / 2 + E.pan[0] + (x - y) * s * 0.866, h * 0.58 + E.pan[1] + (x + y) * s * 0.5 - z * s];
  const cell = (x, y) => rot(x + 0.5 - cx, y + 0.5 - cy, E.view);
  return { P, cell, s };
}
const shade = (c, f) => '#' + c.slice(1).match(/../g).map(x => Math.min(255, Math.round(parseInt(x, 16) * f)).toString(16).padStart(2, '0')).join('');

function cubeFaces(pr, x, y, z, faces) {
  const [rx, ry] = pr.cell(x, y), pt = (a, b, c) => pr.P(rx + a, ry + b, z + c).join(',');
  return {
    z: `${pt(-.5, -.5, 1)} ${pt(.5, -.5, 1)} ${pt(.5, .5, 1)} ${pt(-.5, .5, 1)}`,
    x: `${pt(.5, -.5, 0)} ${pt(.5, .5, 0)} ${pt(.5, .5, 1)} ${pt(.5, -.5, 1)}`,
    y: `${pt(-.5, .5, 0)} ${pt(.5, .5, 0)} ${pt(.5, .5, 1)} ${pt(-.5, .5, 1)}`,
  };
}

function inEnv(E, x, y, z) { const e = ENVELOPE[E.size]; return x >= 0 && y >= 0 && z >= 0 && x < e[0] && y < e[1] && z < e[2]; }
function visible(E, k) { return E.vox.has(k) && !(E.clip && unkey(k)[2] > E.clipZ); }

// 悬停目标：添加时是表面外侧的格，其余操作是表面所在的格
function targetCells(E) {
  const h = E.hover; if (!h) return { cells: [], ok: false };
  const [x, y, z] = h.c, [nx, ny, nz] = h.n;
  const adding = (E.tool === 'equip') || (E.op === 'add' && E.tool !== 'select');
  const base = adding ? [x + nx, y + ny, z + nz] : [x, y, z];
  if (h.floor && !adding) return { cells: [], ok: false };
  let cells = [base];
  if (E.tool === 'equip') { const [dx, dy, dz] = EQUIPMENT[E.equip].size; cells = []; for (let i = 0; i < dx; i++) for (let j = 0; j < dy; j++) for (let k = 0; k < dz; k++) cells.push([base[0] + i, base[1] + j, base[2] + k]); }
  else if (E.tool === 'box' && E.boxStart) { const a = E.boxStart; cells = []; for (let i = Math.min(a[0], base[0]); i <= Math.max(a[0], base[0]); i++) for (let j = Math.min(a[1], base[1]); j <= Math.max(a[1], base[1]); j++) for (let k = Math.min(a[2], base[2]); k <= Math.max(a[2], base[2]) + (adding ? E.boxH - 1 : 0); k++) cells.push([i, j, k]); }
  else if (E.tool === 'brush' && E.brush > 1) { cells = []; const r = E.brush - 1; for (let i = -r; i <= r; i++) for (let j = -r; j <= r; j++) cells.push([base[0] + i, base[1] + j, base[2]]); }
  if (adding) {
    if (cells.some(c => !inEnv(E, ...c))) return { cells, ok: false, why: `超出${E.size}的外形范围` };
    if (cells.some(c => E.vox.has(key(...c)))) return { cells, ok: false, why: '这里已经有体素' };
  } else cells = cells.filter(c => E.vox.has(key(...c)));
  if (!adding && cells.some(c => E.vox.get(key(...c))?.p !== 'body') && E.op !== 'paint' && E.tool !== 'select') return { cells, ok: false, why: '设备要用选择工具选中后删除' };
  return { cells, ok: true };
}

function renderEditorView() {
  const E = S.editor, el = document.querySelector('.ed-view'); if (!el) return;
  const r = el.getBoundingClientRect(), pr = projector(E, r.width, r.height), env = ENVELOPE[E.size];
  let out = '';
  // 工作平面网格（z = 0 平面）与外形范围
  for (let x = 0; x < env[0]; x++) for (let y = 0; y < env[1]; y++) {
    if (E.vox.has(key(x, y, 0))) continue;
    const f = cubeFaces(pr, x, y, -1);
    out += `<polygon points="${f.z}" fill="rgba(255,255,255,.025)" stroke="rgba(255,255,255,.09)" data-c="${x},${y},-1" data-n="0,0,1" data-floor="1"/>`;
  }
  const corner = (x, y, z) => { const [rx, ry] = rot(x - env[0] / 2, y - env[1] / 2, E.view); return pr.P(rx, ry, z).join(','); };
  const C = [[0, 0], [env[0], 0], [env[0], env[1]], [0, env[1]]];
  out += `<polygon points="${C.map(([x, y]) => corner(x, y, env[2])).join(' ')}" fill="none" stroke="rgba(224,162,46,.35)" stroke-dasharray="5 5"/>`;
  C.forEach(([x, y]) => { out += `<line x1="${corner(x, y, 0).split(',')[0]}" y1="${corner(x, y, 0).split(',')[1]}" x2="${corner(x, y, env[2]).split(',')[0]}" y2="${corner(x, y, env[2]).split(',')[1]}" stroke="rgba(224,162,46,.25)" stroke-dasharray="5 5"/>`; });
  // 体素按深度从后往前画
  const T = targetCells(E), tset = new Set(T.cells.map(c => key(...c)));
  const items = [...E.vox.keys()].filter(k => visible(E, k)).map(k => { const [x, y, z] = unkey(k), [rx, ry] = pr.cell(x, y); return { k, x, y, z, d: rx + ry + z }; });
  const ghosts = (T.cells.length && (E.tool === 'equip' || (E.op === 'add' && E.tool !== 'select'))) ? T.cells.map(([x, y, z]) => { const [rx, ry] = pr.cell(x, y); return { ghost: true, x, y, z, d: rx + ry + z }; }) : [];
  for (const it of [...items, ...ghosts].sort((a, b) => a.d - b.d)) {
    const f = cubeFaces(pr, it.x, it.y, it.z);
    if (it.ghost) { const c = T.ok ? '#2f9e5c' : '#c9423a'; out += ['z', 'x', 'y'].map(n => `<polygon points="${f[n]}" fill="${c}" fill-opacity=".45" stroke="${c}" pointer-events="none"/>`).join(''); continue; }
    const v = E.vox.get(it.k), sel = E.sel.has(v.p === 'body' ? it.k : v.p), hit = tset.has(it.k);
    const tint = hit && E.op === 'remove' ? '#c9423a' : hit && E.op === 'paint' ? E.color : null;
    const [ax, ay] = unrot(1, 0, E.view), [bx, by] = unrot(0, 1, E.view);
    const N = { z: [0, 0, 1], x: [ax, ay, 0], y: [bx, by, 0] }, F = { z: 1.12, x: .82, y: .64 };
    for (const n of ['z', 'x', 'y']) {
      const [dx, dy, dz] = N[n]; if (visible(E, key(it.x + dx, it.y + dy, it.z + dz))) continue;
      out += `<polygon points="${f[n]}" fill="${shade(tint || v.c, F[n])}" stroke="${sel ? '#e0a22e' : 'rgba(0,0,0,.25)'}" stroke-width="${sel ? 2 : 1}" data-c="${it.x},${it.y},${it.z}" data-n="${dx},${dy},${dz}"/>`;
    }
  }
  const sc = selCells(E).map(unkey);
  if (sc.length) {
    const lo = [0, 1, 2].map(i => Math.min(...sc.map(c => c[i]))), hi = [0, 1, 2].map(i => Math.max(...sc.map(c => c[i])) + 1);
    const Pw = (x, y, z) => { const [rx, ry] = rot(x - env[0] / 2, y - env[1] / 2, E.view); return pr.P(rx, ry, z); };
    const cs = [[lo[0], lo[1]], [hi[0], lo[1]], [hi[0], hi[1]], [lo[0], hi[1]]];
    const seg = (a, b) => `<line x1="${a[0]}" y1="${a[1]}" x2="${b[0]}" y2="${b[1]}" stroke="#e0a22e" stroke-width="2" stroke-dasharray="6 4" pointer-events="none"/>`;
    cs.forEach((c, i) => { const d = cs[(i + 1) % 4]; out += seg(Pw(...c, lo[2]), Pw(...d, lo[2])) + seg(Pw(...c, hi[2]), Pw(...d, hi[2])) + seg(Pw(...c, lo[2]), Pw(...c, hi[2])); });
  }
  el.querySelector('svg').innerHTML = out;
  const hud = document.querySelector('.ed-hud');
  if (hud) hud.innerHTML = (E.hover ? `<span class="pill">x ${E.hover.c[0]} · y ${E.hover.c[1]} · z ${Math.max(0, E.hover.c[2])}</span>` : '') + (E.hover && !T.ok && T.why ? `<span class="pill" style="color:#ec8a84">${T.why}</span>` : '') + (E.msg ? `<span class="pill" style="color:#e9b45a">${E.msg}</span>` : '') + (E.boxStart ? '<span class="pill">再点一下确定长方体的另一角，Esc 取消</span>' : '');
}

// ---------- 统计与问题 ----------
function designStats(E) {
  const vs = [...E.vox.values()], parts = Object.values(E.parts).filter(p => p.eq && vs.some(v => v.p === Object.keys(E.parts).find(k => E.parts[k] === p)));
  const eqCount = t => Object.entries(E.parts).filter(([id, p]) => p.eq === t && vs.some(v => v.p === id)).length;
  const body = vs.filter(v => v.p === 'body').length, walls = vs.filter(v => v.d === 'plate' || v.d === 'cargo').length;
  const capacity = Math.round(walls / 1.8) + eqCount('hold') * 20, traction = eqCount('diesel') * 60;
  const out = [...E.vox.keys()].filter(k => !inEnv(E, ...unkey(k))).length;
  const problems = [];
  if (eqCount('bogie') < 2) problems.push(['车辆至少需要 2 个转向架', 'bogie']);
  if (out) problems.push([`${out} 格超出${E.size}的外形范围`, 'out']);
  return { count: vs.length, mass: Math.round(vs.length * 0.09), hp: body * 3, capacity, traction, value: vs.length * 7 + eqCount('diesel') * 600, problems, parts };
}
function bomOf(vox) { const b = {}; for (const v of vox.values()) { const m = VOXDEFS[v.d]?.mat || 'machinery'; b[m] = (b[m] || 0) + 1; } return b; }
function refitDiff(E) {
  const added = [...E.vox.keys()].filter(k => !E.orig.has(k)).length, removed = [...E.orig.keys()].filter(k => !E.vox.has(k)).length;
  const a = bomOf(E.vox), b = bomOf(E.orig), bom = {};
  for (const g of Object.keys(a)) { const d = Math.ceil((a[g] - (b[g] || 0)) / 4); if (d > 0) bom[g] = d; }
  return { added, removed, bom };
}

// ---------- 画面 ----------
function editorScreen() {
  const E = S.editor, st = designStats(E), lib = { local: '本局蓝图库', user: '用户内容库' }[E.lib] || '本局蓝图库';
  const tools = [['select', '选择', 'V', 'pin'], ['place', '单格', 'B', 'plus'], ['brush', '笔刷', 'N', 'edit'], ['box', '长方体', 'M', 'blueprint']];
  const ops = [['add', '添加', 1], ['remove', '移除', 2], ['paint', '涂装', 3], ['replace', '替换', 4]];
  let opts = '';
  if (E.tool === 'select') {
    const n = E.sel.size, eqSel = [...E.sel].filter(s => E.parts[s]);
    opts = n ? `<div class="grp"><b>已选 ${eqSel.length ? eqSel.map(s => E.parts[s].name).join('、') : `${n} 格`}</b></div>
      <div class="grp"><span class="lbl">移动</span>${[['←', -1, 0, 0], ['→', 1, 0, 0], ['↑', 0, -1, 0], ['↓', 0, 1, 0], ['升', 0, 0, 1], ['降', 0, 0, -1]].map(([l, x, y, z]) => `<button class="btn" style="height:30px;padding:0 9px" data-action="ed-move" data-v="${x},${y},${z}" title="方向键移动，PgUp/PgDn 升降">${l}</button>`).join('')}</div>
      <button class="btn" style="height:30px" data-action="ed-rotate" title="R">旋转 90°</button><button class="btn" style="height:30px" data-action="ed-dup" title="Ctrl D">复制</button>
      <button class="btn" style="height:30px" data-action="ed-paint-sel">涂成当前颜色</button>${eqSel.length === 1 ? `<button class="btn" style="height:30px" data-action="todo" data-v="设备另存为蓝图">另存为蓝图…</button>` : ''}
      <button class="btn danger" style="height:30px" data-action="ed-del" title="Delete">删除</button>`
      : `<span class="muted">点体素或设备选中，按住 Shift 多选。选中后可以移动、旋转、复制和删除。</span>`;
  } else if (E.tool === 'equip') opts = `<b>放置：${EQUIPMENT[E.equip].name}</b><span class="muted">${EQUIPMENT[E.equip].size.join(' × ')} 格 · 点表面放下 · Esc 结束</span>`;
  else opts = `<div class="seg">${ops.map(([k, l, n]) => `<button class="${E.op === k ? 'on' : ''}" data-action="ed-op" data-v="${k}" title="${n}">${l}</button>`).join('')}</div>
    ${E.tool === 'brush' ? `<div class="grp"><span class="lbl">笔刷大小</span><div class="seg">${[1, 2, 3].map(b => `<button class="${E.brush === b ? 'on' : ''}" data-action="ed-brush" data-v="${b}">${b * 2 - 1}</button>`).join('')}</div></div>` : ''}
    ${E.tool === 'box' && E.op === 'add' ? `<div class="grp"><label class="lbl" for="ed-boxh">高度</label><input id="ed-boxh" type="number" min="1" max="${ENVELOPE[E.size][2]}" value="${E.boxH}" data-input="ed-boxh" style="width:64px;height:30px"></div>` : ''}
    <div class="grp"><label class="switch ${E.clip ? 'on' : ''}" data-action="ed-clip"><i></i>剖切</label>${E.clip ? `<span class="lbl">显示到第</span><input id="ed-clipz" type="range" min="0" max="${ENVELOPE[E.size][2] - 1}" value="${E.clipZ}" data-input="ed-clipz" style="width:100px"><span>${E.clipZ} 层</span>` : ''}</div>
    <span class="small faint">按住 Shift 临时移除 · Alt 点击吸取</span>`;
  const panel = { voxels: voxPanel, equipment: equipPanel, structure: structPanel, stats: statsPanel }[E.panel](E, st);
  const refit = E.refit, diff = refit ? refitDiff(E) : null;
  return `<div class="ed">
    <div class="ed-top"><button class="btn ghost" data-action="ed-exit" title="退出编辑器（Esc）">${icon('back')} 退出</button>
      <div class="title">${E.renaming ? `<input id="ed-name" type="text" value="${E.name}" data-input="ed-name" data-enter="ed-rename-done">` : `<span data-action="ed-rename" style="cursor:text" title="点击改名">${refit ? `改装：${W.trains[refit.train]?.name || '列车'}的${VEHICLE_TYPES[W.vehicles[refit.vehicle]?.type]?.name || '车辆'}` : E.name}</span>`}
        <span class="chip neutral">${refit ? '改装草稿' : lib} · 车辆 · ${E.size}</span></div>
      <button class="btn ghost icon-only" data-action="ed-undo" ${E.undo.length ? '' : 'disabled'} title="撤销（Ctrl Z）${E.undo.length ? `，还能撤销 ${E.undo.length} 步` : ''}">${icon('undo')}</button>
      <button class="btn ghost icon-only" data-action="ed-redo" ${E.redo.length ? '' : 'disabled'} title="重做（Ctrl Y）">${icon('redo')}</button>
      <div style="flex:1"></div><span class="chip warning">${icon('pause')}世界已暂停</span>${E.dirty ? '<span class="small" style="color:#e9b45a">● 有未保存的修改</span>' : '<span class="small muted">已保存</span>'}
      <button class="btn" data-action="ed-draft" title="Ctrl S">保存草稿</button>
      ${refit ? `<button class="btn primary" data-action="ed-refit-done" ${diff.added + diff.removed ? '' : 'disabled title="还没有改动"'}>完成改装方案…</button>` : `<button class="btn primary" data-action="ed-save">保存蓝图…</button>`}</div>
    <div class="ed-tools">${tools.map(([k, l, kb, ic]) => `<button class="${E.tool === k ? 'on' : ''}" data-action="ed-tool" data-v="${k}" title="${l}（${kb}）">${icon(ic, 'lg')}${l}<span class="kbd">${kb}</span></button>`).join('')}</div>
    <div class="ed-opts">${opts}</div>
    <div class="ed-view"><svg></svg></div>
    <div class="ed-hud"></div>
    <div class="ed-view-tools"><button class="btn" data-action="ed-view" data-v="-1" title="Q">${icon('back', 'sm')} 左转</button><button class="btn" data-action="ed-view" data-v="1" title="E">右转 ${icon('chevron', 'sm')}</button><button class="btn" data-action="ed-view" data-v="0" title="Home">重置视角</button></div>
    <div class="ed-side"><div class="tabs">${[['voxels', '体素'], ['equipment', '设备'], ['structure', '结构'], ['stats', '数值', st.problems.length]].map(([k, l, d]) => `<button class="tab ${E.panel === k ? 'on' : ''}" data-action="ed-panel" data-v="${k}">${l}${d ? '<span class="dot"></span>' : ''}</button>`).join('')}</div>
      <div class="scroll" style="padding:12px 16px">${panel}</div>
      <div class="ed-summary" data-action="ed-panel" data-v="stats">${refit ? `<div class="small muted">和原车相比</div><div>+${diff.added} 格、−${diff.removed} 格 · 载量 ${designStats({ ...E, vox: E.orig }).capacity} → ${st.capacity}</div>`
        : `<div style="display:flex;gap:12px"><span>质量 ${st.mass}</span><span>载量 ${st.capacity}</span><span>价值 ${fmt(st.value)}</span></div>`}
        ${st.problems.length ? `<div class="small" style="color:#ec8a84;margin-top:4px">${icon('alert', 'sm')} ${st.problems.length} 个问题，保存蓝图前需要解决</div>` : '<div class="small pos" style="margin-top:4px">没有问题</div>'}</div></div></div>`;
}

function voxPanel(E) {
  return `<div class="section-head"><h4>体素</h4></div><div class="vox-grid">${Object.entries(VOXDEFS).map(([k, d]) => `<div class="vox ${E.def === k ? 'on' : ''}" data-action="ed-def" data-v="${k}"><i style="background:${d.color}"></i>${d.name}</div>`).join('')}</div>
    <div style="display:flex;gap:10px;margin-top:14px"><div class="field" style="flex:1"><label for="ed-shape">形状</label><select id="ed-shape" data-change="ed-shape">${['立方体', '斜面', '半格', '圆角'].map(o => `<option ${E.shape === o ? 'selected' : ''}>${o}</option>`).join('')}</select></div>
      <div class="field"><label>朝向</label><div class="seg">${['前', '右', '后', '左'].map(o => `<button class="${E.facing === o ? 'on' : ''}" data-action="ed-facing" data-v="${o}">${o}</button>`).join('')}</div></div></div>
    <div class="field" style="margin-top:14px"><label>颜色</label><div class="swatches">${PALETTE.map(c => `<span class="swatch ${E.color === c ? 'on' : ''}" style="background:${c}" data-action="ed-color" data-v="${c}" title="${c}"></span>`).join('')}</div></div>
    <div class="small muted" style="margin-top:14px">放置：${VOXDEFS[E.def].name} · ${E.shape} · 朝${E.facing}</div>`;
}
function equipPanel(E) {
  const placed = Object.entries(E.parts).filter(([id, p]) => p.eq && [...E.vox.values()].some(v => v.p === id));
  return `<div class="section-head"><h4>放置大型设备</h4></div>${Object.entries(EQUIPMENT).map(([k, q]) => `<div class="row ${E.tool === 'equip' && E.equip === k ? 'on' : ''}" data-action="ed-equip" data-v="${k}"><i style="width:18px;height:18px;border-radius:3px;background:${q.color}"></i><div class="grow"><div class="title">${q.name}</div><div class="sub">${q.size.join('×')} 格 · ${q.note}</div></div></div>`).join('')}
    <div class="section-head" style="margin-top:14px"><h4>已放置</h4><span class="muted small">${placed.length}</span></div>${placed.map(([id, p]) => `<div class="row ${E.sel.has(id) ? 'on' : ''}" data-action="ed-pick" data-v="${id}"><div class="grow">${p.name}</div><button class="btn ghost small" data-action="todo" data-v="进入设备内部编辑">进入编辑</button></div>`).join('')}`;
}
function structPanel(E) {
  const body = [...E.vox.entries()].filter(([, v]) => v.p === 'body'), byDef = {};
  body.forEach(([, v]) => byDef[v.d] = (byDef[v.d] || 0) + 1);
  const eqs = Object.entries(E.parts).filter(([id, p]) => p.eq && [...E.vox.values()].some(v => v.p === id));
  return `<div class="row" data-action="ed-select-body"><span>${icon('blueprint')}</span><div class="grow"><div class="title">车体结构</div><div class="sub">${body.length} 格：${Object.entries(byDef).map(([d, n]) => `${VOXDEFS[d].name} ${n}`).join('、')}</div></div></div>
    ${eqs.map(([id, p]) => `<div class="row ${E.sel.has(id) ? 'on' : ''}" data-action="ed-pick" data-v="${id}" style="padding-left:28px"><span>${icon('hammer', 'sm')}</span><div class="grow">${p.name}</div></div>`).join('')}
    <div class="small faint" style="margin-top:10px">点一项选中它。车体按体素种类汇总，不逐格列出。</div>`;
}
function statsPanel(E, st) {
  return `${st.problems.length ? `<div class="section-head"><h4>问题</h4></div>${st.problems.map(([t, k]) => `<div class="notice error" style="margin-bottom:6px">${icon('alert')}<div class="grow">${t}${k === 'bogie' ? `<div style="margin-top:6px"><button class="btn" style="height:28px" data-action="ed-equip" data-v="bogie">放置转向架</button></div>` : ''}</div></div>`).join('')}` : `<div class="notice success">${icon('check')}<div class="grow">没有问题，可以保存为蓝图。</div></div>`}
    <div class="section-head" style="margin-top:14px"><h4>设计属性</h4></div><div class="kv"><dt>体素</dt><dd>${st.count} 格</dd><dt>质量</dt><dd>${st.mass}</dd><dt>结构耐久</dt><dd>${st.hp}</dd><dt>载量</dt><dd>${st.capacity}</dd><dt>牵引力</dt><dd>${st.traction || '—'}</dd><dt>基本货币价值</dt><dd>${fmt(st.value)}</dd></div>
    <div class="section-head" style="margin-top:14px"><h4>制造材料</h4></div>${Object.entries(bomOf(E.vox)).map(([g, n]) => `<div style="display:flex;justify-content:space-between;padding:2px 0">${goodTag(g)}<span>×${Math.ceil(n / 4)}</span></div>`).join('')}`;
}

// ---------- 编辑操作 ----------
function snapshot(E) { E.undo.push({ vox: new Map([...E.vox].map(([k, v]) => [k, { ...v }])), parts: { ...E.parts } }); E.redo = []; E.dirty = true; }
function applyTarget(E, op) {
  const T = targetCells(E); if (!T.ok || !T.cells.length) { if (T.why) E.msg = T.why; return false; }
  for (const c of T.cells) {
    const k = key(...c);
    if (E.tool === 'equip') continue;
    if (op === 'add') E.vox.set(k, { d: E.def, c: E.color, p: 'body' });
    else if (op === 'remove') E.vox.delete(k);
    else if (op === 'paint') E.vox.get(k).c = E.color;
    else if (op === 'replace') Object.assign(E.vox.get(k), { d: E.def, c: E.color });
  }
  if (E.tool === 'equip') { const id = 'e' + Date.now(); E.parts[id] = { name: EQUIPMENT[E.equip].name, eq: E.equip }; T.cells.forEach(c => E.vox.set(key(...c), { d: 'steel', c: EQUIPMENT[E.equip].color, p: id })); }
  return true;
}
function selCells(E) { return [...E.vox.keys()].filter(k => E.sel.has(k) || E.sel.has(E.vox.get(k).p)); }
function moveSel(E, dx, dy, dz) {
  const ks = selCells(E), moved = ks.map(k => { const [x, y, z] = unkey(k); return [k, key(x + dx, y + dy, z + dz), [x + dx, y + dy, z + dz]]; });
  const blocked = moved.find(([, nk, c]) => !inEnv(E, ...c) || (E.vox.has(nk) && !ks.includes(nk)));
  if (blocked) { E.msg = !inEnv(E, ...blocked[2]) ? `不能移动：会超出${E.size}的外形范围` : `不能移动：会和${E.parts[E.vox.get(blocked[1]).p]?.name || '其他体素'}重叠`; return; }
  snapshot(E); const vals = moved.map(([k]) => E.vox.get(k)); moved.forEach(([k]) => E.vox.delete(k)); moved.forEach(([, nk], i) => E.vox.set(nk, vals[i]));
  E.sel = new Set([...E.sel].map(s => E.parts[s] ? s : moved.find(([k]) => k === s)?.[1] || s));
}

const EDITOR_ACTIONS = {
  'ed-tool': v => { const E = S.editor; E.tool = v; E.boxStart = null; E.equip = null; if (v !== 'select') E.sel.clear(); },
  'ed-op': v => { S.editor.op = v; },
  'ed-brush': v => { S.editor.brush = +v; },
  'ed-clip': () => { S.editor.clip = !S.editor.clip; },
  'ed-def': v => { const E = S.editor; E.def = v; E.color = VOXDEFS[v].color; if (E.tool === 'select' || E.tool === 'equip') E.tool = 'place'; },
  'ed-color': v => { S.editor.color = v; },
  'ed-facing': v => { S.editor.facing = v; },
  'ed-panel': v => { S.editor.panel = v; },
  'ed-equip': v => { const E = S.editor; E.tool = 'equip'; E.equip = v; E.panel = 'equipment'; E.sel.clear(); },
  'ed-pick': v => { const E = S.editor; E.tool = 'select'; E.sel = new Set([v]); },
  'ed-select-body': () => { const E = S.editor; E.tool = 'select'; E.sel = new Set([...E.vox.keys()].filter(k => E.vox.get(k).p === 'body')); },
  'ed-view': v => { const E = S.editor; if (v === '0') { E.view = 0; E.zoom = 1; E.pan = [0, 0]; } else E.view = (E.view + +v + 4) % 4; },
  'ed-move': v => { const [x, y, z] = v.split(',').map(Number); const [wx, wy] = unrot(x, y, S.editor.view); moveSel(S.editor, wx, wy, z); },
  'ed-rotate': () => { const E = S.editor, ks = selCells(E); if (!ks.length) return; const cs = ks.map(unkey), cx = Math.round(cs.reduce((a, c) => a + c[0], 0) / cs.length), cy = Math.round(cs.reduce((a, c) => a + c[1], 0) / cs.length);
    const nk = cs.map(([x, y, z]) => [cx - (y - cy), cy + (x - cx), z]); if (nk.some(c => !inEnv(E, ...c) || (E.vox.has(key(...c)) && !ks.includes(key(...c))))) { E.msg = '不能旋转：会超出外形范围或和其他体素重叠'; return; }
    snapshot(E); const vals = ks.map(k => E.vox.get(k)); ks.forEach(k => E.vox.delete(k)); nk.forEach((c, i) => E.vox.set(key(...c), vals[i]));
    const eqs = [...E.sel].filter(s => E.parts[s]); E.sel = new Set(eqs.length ? eqs : nk.map(c => key(...c))); },
  'ed-dup': () => { const E = S.editor, ks = selCells(E); if (!ks.length) return; const span = Math.max(...ks.map(k => unkey(k)[0])) - Math.min(...ks.map(k => unkey(k)[0])) + 1;
    const nk = ks.map(k => { const [x, y, z] = unkey(k); return [x + span, y, z]; }); if (nk.some(c => !inEnv(E, ...c) || E.vox.has(key(...c)))) { E.msg = '不能复制到右侧：没有空间。先移动或缩小选择。'; return; }
    snapshot(E); const map = {}; ks.forEach((k, i) => { const v = E.vox.get(k); let p = v.p; if (E.parts[p]?.eq) { map[p] ||= 'e' + Date.now() + i; E.parts[map[p]] = { ...E.parts[p] }; p = map[p]; } E.vox.set(key(...nk[i]), { ...v, p }); }); E.sel = new Set(Object.values(map)); },
  'ed-paint-sel': () => { const E = S.editor; snapshot(E); selCells(E).forEach(k => E.vox.get(k).c = E.color); },
  'ed-del': () => { const E = S.editor, ks = selCells(E); if (!ks.length) return; snapshot(E); ks.forEach(k => E.vox.delete(k)); E.sel.clear(); },
  'ed-undo': () => { const E = S.editor, s = E.undo.pop(); if (!s) return; E.redo.push({ vox: E.vox, parts: E.parts }); E.vox = s.vox; E.parts = s.parts; E.sel.clear(); E.dirty = true; },
  'ed-redo': () => { const E = S.editor, s = E.redo.pop(); if (!s) return; E.undo.push({ vox: E.vox, parts: E.parts }); E.vox = s.vox; E.parts = s.parts; E.dirty = true; },
  'ed-rename': () => { S.editor.renaming = true; },
  'ed-rename-done': () => { S.editor.renaming = false; },
  'ed-draft': () => { S.editor.dirty = false; toast('complete', '草稿已保存', null, false); },
  'ed-save': () => {
    const E = S.editor, st = designStats(E); S.sv = { name: E.name, lib: E.lib === 'user' ? '用户内容库' : '本局蓝图库' };
    S.dialog = { title: '保存为蓝图', body: () => `<div style="display:flex;flex-direction:column;gap:14px"><div class="field"><label for="sv-name">名称</label><input id="sv-name" type="text" value="${S.sv.name}" data-input="sv-name"></div>
      <div class="field"><label>保存到</label><div class="seg">${['本局蓝图库', '用户内容库'].map(o => `<button class="${S.sv.lib === o ? 'on' : ''}" data-action="sv-lib" data-v="${o}">${o}</button>`).join('')}</div><div class="small muted">${S.sv.lib === '本局蓝图库' ? LIBS.local[1] : LIBS.user[1]}。</div></div>
      ${LIBRARY.some(i => i.name === S.sv.name && i.lib === (S.sv.lib === '本局蓝图库' ? 'local' : 'user')) ? `<div class="notice warning">${icon('alert')}<div class="grow">${S.sv.lib}里已有同名蓝图，保存会覆盖它。已按旧蓝图建成的车辆不受影响。</div></div>` : ''}
      ${st.problems.length ? `<div class="notice error">${icon('alert')}<div class="grow">先解决这些问题才能保存为蓝图：${st.problems.map(p => p[0]).join('；')}。可以先保存草稿。</div></div>` : ''}</div>`,
      confirm: '保存', disabled: () => designStats(S.editor).problems.length > 0, onConfirm: () => { E.name = S.sv.name; E.lib = S.sv.lib === '本局蓝图库' ? 'local' : 'user'; E.dirty = false; if (!LIBRARY.some(i => i.name === E.name)) LIBRARY.push({ id: 'l' + Date.now(), name: E.name, lib: E.lib, kind: 'vehicle', type: 'wagonM', used: 0 }); toast('complete', `已保存到${S.sv.lib}`, null, false); } };
  },
  'sv-lib': v => { S.sv.lib = v; },
  'ed-exit': () => {
    const E = S.editor, leave = () => { const from = E.from, refit = E.refit; S.screen = from === 'authoring' ? 'authoring' : 'game'; if (from === 'authoring') S.au = E.au; S.editor = null; if (from === 'library') openLibrary(); else if (refit) select({ kind: 'train', id: refit.train }); };
    if (E.dirty) S.dialog = { title: '退出编辑器？', body: '有未保存的修改。', confirm: '保存草稿并退出', alt: '不保存，直接退出', onConfirm: () => leave() };
    else leave();
  },
  'ed-refit-done': () => { const E = S.editor, d = refitDiff(E), r = E.refit; S.screen = 'game'; S.editor = null; select({ kind: 'train', id: r.train }); S.workspace = { kind: 'refit', ...r, diff: d, sources: trainsAt(r.node).map(id => ({ id, on: true })), saved: E }; },
};
Object.assign(WS_ACTIONS, EDITOR_ACTIONS);
Object.assign(INPUTS, {
  'ed-boxh': v => { S.editor.boxH = Math.max(1, parseInt(v) || 1); }, 'ed-clipz': v => { S.editor.clipZ = +v; },
  'ed-shape': v => { S.editor.shape = v; }, 'ed-name': v => { S.editor.name = v; }, 'sv-name': v => { S.sv.name = v; },
});

// 委托改装：编辑器里完成的改装方案，在这里看材料和费用后委托
function refitQuote(w) {
  const bp = { bom: w.diff.bom, labor: 200 + (w.diff.added + w.diff.removed) * 4, days: 1 };
  const rows = allocate({ ...w, mode: 'vehicle' }, bp), mat = rows.reduce((a, r) => a + r.cost, 0);
  return { bp, rows, mat, total: mat + bp.labor, short: rows.filter(r => r.short > 0) };
}
function refitWs(w) {
  const n = W.nodes[w.node], t = W.trains[w.train], ty = VEHICLE_TYPES[W.vehicles[w.vehicle].type];
  const { bp, rows, mat, total, short } = refitQuote(w);
  return `<div class="workspace" style="width:900px" role="dialog"><div class="ws-head"><h2>委托改装 <span class="muted">· ${t.name}的${ty.name} · ${n.name}</span></h2><button class="btn ghost icon-only" data-action="ws-close">${icon('close')}</button></div>
    <div class="section"><div class="stats" style="grid-template-columns:repeat(3,1fr)"><div class="stat"><div class="label">新增体素</div><div class="value">+${w.diff.added}</div></div><div class="stat"><div class="label">拆掉体素</div><div class="value">−${w.diff.removed}</div></div><div class="stat"><div class="label">工期</div><div class="value">约 1 天</div></div></div>
      <button class="btn" style="margin-top:10px" data-action="refit-back">${icon('edit')} 回到编辑器修改</button></div>
    <div class="section"><div class="section-head"><h4>材料</h4><span class="muted small">先用你的货物，不够的在${n.name}市场购买</span></div>
      ${rows.length ? `<table class="grid"><tr><th>材料</th><th class="num">需要</th><th>来自</th><th class="num">当地购买</th><th class="num">费用</th></tr>${rows.map(r => `<tr><td>${goodTag(r.g)}</td><td class="num">${r.need}</td><td class="small">${r.from.map(([s, q]) => `${s} ×${q}`).join('，') || '—'}${r.short ? `<span class="neg"> 缺 ${r.short}</span>` : ''}</td><td class="num">${r.buy || '—'}</td><td class="num">${r.cost ? fmt(r.cost) : '—'}</td></tr>`).join('')}</table>` : '<div class="muted">只拆不加，不需要新材料。</div>'}</div>
    <div class="section"><div class="kv"><dt>材料费</dt><dd>${fmt(mat)}</dd><dt>工钱</dt><dd>${fmt(bp.labor)}</dd><dt><b>合计</b></dt><dd><b>${fmt(total)}</b></dd></div></div>
    <div class="ws-foot"><span class="msg small muted">改装期间${t.name}不能运行。委托后不能取消。</span><button class="btn" data-action="ws-close">取消</button><button class="btn primary" data-action="refit-go" ${short.length || total > W.funds ? 'disabled' : ''}>${icon('wrench')} 委托改装 · ${fmt(total)}</button></div></div>`;
}
Object.assign(WS_ACTIONS, {
  'refit-back': () => { const E = S.workspace.saved; S.workspace = null; S.editor = E; S.screen = 'editor'; },
  'refit-go': () => { const w = S.workspace, t = W.trains[w.train], { total } = refitQuote(w); spend(total); W.jobs.push({ id: 'j' + Date.now(), node: w.node, kind: '改装', what: `${t.name}的${VEHICLE_TYPES[W.vehicles[w.vehicle].type].name}`, progress: 0, eta: '约 1 天' }); S.workspace = null; toast('complete', `已委托${nodeName(w.node)}改装，约 1 天`, { kind: 'train', id: w.train }, false); },
  refit: v => { const t = S.selection.id; openEditor({ name: '改装', lib: 'local', from: 'train', refit: { train: t, vehicle: v, node: W.trains[t].at } }); },
});

// ---------- 视口输入 ----------
let edDrag = null;
document.addEventListener('pointermove', e => {
  const E = S.editor; if (S.screen !== 'editor' || !E) return;
  if (edDrag?.pan) { E.pan = [edDrag.px + e.clientX - edDrag.x, edDrag.py + e.clientY - edDrag.y]; return renderEditorView(); }
  if (edDrag?.orbit) { const d = Math.round((e.clientX - edDrag.x) / 140); if (d !== edDrag.d) { E.view = (edDrag.v + d + 400) % 4; edDrag.d = d; renderEditorView(); } return; }
  const f = e.target.closest?.('.ed-view [data-c]');
  const h = f ? { c: f.dataset.c.split(',').map(Number), n: f.dataset.n.split(',').map(Number), floor: !!f.dataset.floor } : null;
  if (JSON.stringify(h) !== JSON.stringify(E.hover)) { E.hover = h; E.msg = null; if (edDrag?.brush && h) applyTarget(E, E.op); renderEditorView(); }
});
document.addEventListener('pointerdown', e => {
  const E = S.editor; if (S.screen !== 'editor' || !E || !e.target.closest('.ed-view')) return;
  if (e.button === 1) { edDrag = { pan: true, x: e.clientX, y: e.clientY, px: E.pan[0], py: E.pan[1] }; return; }
  if (e.button === 2) { edDrag = { orbit: true, x: e.clientX, v: E.view, d: 0 }; return; }
  if (!E.hover) { if (E.tool === 'select' && !e.shiftKey) { E.sel.clear(); render(); } return; }
  const v = E.vox.get(key(...E.hover.c));
  if (e.altKey && v) { E.def = v.d; E.color = v.c; E.msg = `已吸取：${VOXDEFS[v.d].name}`; return render(); }
  if (E.tool === 'select') { if (!v) return; const id = v.p === 'body' ? key(...E.hover.c) : v.p; if (e.shiftKey) E.sel.has(id) ? E.sel.delete(id) : E.sel.add(id); else E.sel = new Set([id]); return render(); }
  const saved = E.op; if (e.shiftKey) E.op = 'remove';
  if (E.tool === 'box' && !E.boxStart) { const [x, y, z] = E.hover.c, [nx, ny, nz] = E.hover.n; E.boxStart = E.op === 'add' ? [x + nx, y + ny, z + nz] : [x, y, z]; E.op = saved; return render(); }
  const T = targetCells(E);
  if (T.ok && T.cells.length) { snapshot(E); applyTarget(E, E.op); } else E.msg = T.why || null;
  E.op = saved;
  if (E.tool === 'box') E.boxStart = null;
  if (E.tool === 'brush') edDrag = { brush: true };
  render();
});
document.addEventListener('pointerup', () => { edDrag = null; });
document.addEventListener('contextmenu', e => { if (S.screen === 'editor') e.preventDefault(); });
document.addEventListener('wheel', e => { if (S.screen !== 'editor' || !e.target.closest('.ed-view')) return; S.editor.zoom = Math.max(0.4, Math.min(3, S.editor.zoom * (e.deltaY < 0 ? 1.15 : 1 / 1.15))); renderEditorView(); }, { passive: true });

// 编辑器快捷键；Esc 依次取消长方体、设备放置、选择，最后退出
function editorKey(e) {
  const E = S.editor, k = e.key.toLowerCase(), ctrl = e.ctrlKey || e.metaKey;
  if (k === 'escape') { if (E.renaming) E.renaming = false; else if (E.boxStart) E.boxStart = null; else if (E.tool === 'equip') { E.tool = 'place'; E.equip = null; } else if (E.sel.size) E.sel.clear(); else return EDITOR_ACTIONS['ed-exit'](); return; }
  if (ctrl && k === 'z') return EDITOR_ACTIONS['ed-undo']();
  if (ctrl && k === 'y') return EDITOR_ACTIONS['ed-redo']();
  if (ctrl && k === 's') return EDITOR_ACTIONS['ed-draft']();
  if (ctrl && k === 'd') return EDITOR_ACTIONS['ed-dup']();
  if (ctrl) return false;
  const tool = { v: 'select', b: 'place', n: 'brush', m: 'box' }[k]; if (tool) return EDITOR_ACTIONS['ed-tool'](tool);
  const op = { 1: 'add', 2: 'remove', 3: 'paint', 4: 'replace' }[k]; if (op) return EDITOR_ACTIONS['ed-op'](op);
  if (k === 'q') return EDITOR_ACTIONS['ed-view']('-1');
  if (k === 'e') return EDITOR_ACTIONS['ed-view']('1');
  if (k === 'home') return EDITOR_ACTIONS['ed-view']('0');
  if (k === 'delete' || k === 'backspace') return EDITOR_ACTIONS['ed-del']();
  if (k === 'r' && E.sel.size) return EDITOR_ACTIONS['ed-rotate']();
  const mv = { arrowleft: '-1,0,0', arrowright: '1,0,0', arrowup: '0,-1,0', arrowdown: '0,1,0', pageup: '0,0,1', pagedown: '0,0,-1' }[k];
  if (mv && E.sel.size) { e.preventDefault(); return EDITOR_ACTIONS['ed-move'](mv); }
  return false;
}
