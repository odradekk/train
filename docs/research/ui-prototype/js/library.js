// 蓝图库工作区与新建设计对话框
const LIBS = { local: ['本局蓝图库', '只在这一局可用，随存档保存'], user: ['用户内容库', '所有对局都能用，保存在这台电脑上'], pack: ['内容包', '游戏和模组自带，只读'], draft: ['草稿', '还没保存成蓝图的设计和改装'] };
const LIBRARY = [
  { id: 'l1', name: '加长货车 改', lib: 'local', kind: 'vehicle', type: 'wagonM', capacity: 55, used: 2 },
  { id: 'l2', name: '快速机车', lib: 'local', kind: 'vehicle', type: 'locoM', traction: 150, used: 0 },
  { id: 'u1', name: '罐车', lib: 'user', kind: 'vehicle', type: 'wagonL' },
  ...BLUEPRINTS.vehicles.filter(b => b.lib === '内容包').map(b => ({ id: b.id, name: bpName(b), lib: 'pack', kind: 'vehicle', type: b.type, bom: b.bom })),
  ...BLUEPRINTS.buildings.map(b => ({ id: b.id, name: b.name, lib: 'pack', kind: 'building', plot: b.plot, note: b.note, bom: b.bom })),
  { id: 'd1', name: '改装：列车 1 的第 2 辆车', lib: 'draft', kind: 'vehicle', type: 'wagonM', draft: '第 3 天 07:50 保存 · 中型货车' },
];

// 用几个长方体画一个等轴缩略图
function isoBoxes(boxes, w, h, s) {
  const P = (x, y, z) => [w / 2 + (x - y) * s * 0.866, h * 0.62 + (x + y) * s * 0.5 - z * s];
  const pts = a => a.map(p => P(...p).join(',')).join(' ');
  const shade = (c, f) => '#' + c.slice(1).match(/../g).map(x => Math.min(255, Math.round(parseInt(x, 16) * f)).toString(16).padStart(2, '0')).join('');
  return boxes.sort((a, b) => (a[0] + a[1] + a[2]) - (b[0] + b[1] + b[2])).map(([x, y, z, dx, dy, dz, c]) =>
    `<polygon points="${pts([[x, y, z + dz], [x + dx, y, z + dz], [x + dx, y + dy, z + dz], [x, y + dy, z + dz]])}" fill="${shade(c, 1.15)}"/>
     <polygon points="${pts([[x + dx, y, z], [x + dx, y + dy, z], [x + dx, y + dy, z + dz], [x + dx, y, z + dz]])}" fill="${shade(c, .8)}"/>
     <polygon points="${pts([[x, y + dy, z], [x + dx, y + dy, z], [x + dx, y + dy, z + dz], [x, y + dy, z + dz]])}" fill="${shade(c, .62)}"/>`).join('');
}
function thumb(item, w = 180, h = 100, s = 7) {
  const off = [-6, -2];
  const B = item.kind === 'building' ? [[-4, -4, 0, 8, 8, 4, '#9a9488'], [-1, -1, 4, 3, 3, 3, '#b8743f']]
    : item.type?.startsWith('loco') ? [[-7, -2, 0, 14, 4, 1, '#3a3f45'], [-6, -2, 1, 9, 4, 3, '#c9965f'], [3, -2, 1, 3, 4, 4, '#2b77d0']]
    : [[-8, -2, 0, 16, 4, 1, '#3a3f45'], [-8, -2, 1, 16, 4, 3, item.lib === 'user' ? '#6f7a86' : '#b8743f']];
  return `<svg width="${w}" height="${h}">${isoBoxes(B.map(b => [b[0] + off[0] + 6, b[1] + off[1] + 2, ...b.slice(2)]), w, h, s)}</svg>`;
}
function itemStats(it) {
  if (it.kind === 'building') return [['地块', it.plot], ['用途', it.note]];
  const ty = VEHICLE_TYPES[it.type];
  return [['类型', ty.role], ['尺寸档位', ty.size], [ty.traction ? '牵引力' : '载量', it.traction || it.capacity || ty.traction || ty.capacity], ['质量', ty.mass], ['长度', ty.length], ['基本货币价值', fmt(ty.price)]];
}

function openLibrary() { S.workspace = { kind: 'library', lib: 'local', sel: 'l1', q: '', filter: 'all' }; }

function libraryWs(w) {
  const q = w.q.trim(), items = LIBRARY.filter(i => i.lib === w.lib && (!q || i.name.includes(q)) && (w.filter === 'all' || i.kind === w.filter));
  const it = LIBRARY.find(i => i.id === w.sel && i.lib === w.lib) || items[0];
  const cards = items.map(i => `<div class="card ${it && i.id === it.id ? 'on' : ''}" data-action="lib-sel" data-v="${i.id}" data-dbl="lib-edit"><div class="thumb">${thumb(i)}</div>
    <div class="meta"><div>${i.name}</div><div class="small muted">${i.kind === 'building' ? `建筑 · ${i.plot}地块` : `${VEHICLE_TYPES[i.type].role} · ${VEHICLE_TYPES[i.type].size}`}</div></div></div>`).join('');
  let detail = '<div class="empty">这里还没有蓝图。</div>';
  if (it) {
    const acts = { local: [['lib-edit', '编辑', 1], ['lib-copy-user', '复制到用户内容库'], ['todo:重命名', '重命名'], ['lib-del', '删除…', 0, 1]],
      user: [['lib-edit', '编辑', 1], ['lib-copy-local', '复制到本局蓝图库'], ['lib-del', '删除…', 0, 1]],
      pack: [['lib-copy-edit', '复制到本局蓝图库并编辑', 1], ['lib-copy-user', '复制到用户内容库']],
      draft: [['lib-edit', '继续编辑', 1], ['lib-del', '丢弃…', 0, 1]] }[it.lib];
    detail = `<div class="scroll" style="padding:16px"><div style="background:#232a31;border-radius:6px;display:flex;justify-content:center">${thumb(it, 320, 170, 11)}</div>
      <h3 style="margin:12px 0 2px;font-size:18px">${it.name}</h3><div class="muted small">${LIBS[it.lib][0]}${it.draft ? ` · ${it.draft}` : ''}</div>
      ${it.lib === 'pack' ? `<div class="notice" style="margin-top:10px">${icon('info')}<div class="grow">内容包里的蓝图不能直接修改。复制一份后再编辑。</div></div>` : ''}
      <div class="kv" style="margin-top:12px">${itemStats(it).map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</div>
      ${it.bom ? `<div class="section-head" style="margin-top:14px"><h4>制造材料</h4></div>${Object.entries(it.bom).map(([g, q]) => `<div style="display:flex;justify-content:space-between;padding:2px 0">${goodTag(g)}<span>×${q}</span></div>`).join('')}` : ''}
      ${it.used ? `<div class="small muted" style="margin-top:12px">这一局有 ${it.used} 辆车按这个蓝图建造。修改蓝图不会改变已建成的车辆。</div>` : ''}</div>
      <div class="insp-foot" style="flex-wrap:wrap">${acts.map(([a, l, p, d]) => { const [act, v] = a.split(':'); return `<button class="btn ${p ? 'primary' : d ? 'danger' : ''}" style="flex:${p ? '1 1 100%' : '1'}" data-action="${act}" data-v="${v || it.id}">${l}</button>`; }).join('')}</div>`;
  }
  return `<div class="workspace" style="width:min(1320px, calc(100vw - 48px));height:min(800px, calc(100vh - 92px))" role="dialog" aria-label="蓝图库">
    <div class="ws-head"><h2>蓝图库</h2><button class="btn primary" data-action="nd-open">${icon('plus')} 新建设计</button><button class="btn ghost icon-only" data-action="ws-close" title="关闭（Esc）">${icon('close')}</button></div>
    <div class="ws-body"><nav class="side-nav" style="width:230px">${Object.entries(LIBS).map(([k, [l, tip]]) => `<button class="${w.lib === k ? 'on' : ''}" data-action="lib-lib" data-v="${k}" title="${tip}">${l}<span class="count">${LIBRARY.filter(i => i.lib === k).length}</span></button>`).join('')}
        <div class="small faint" style="padding:12px">${LIBS[w.lib][1]}。</div><div style="flex:1"></div><div class="small faint" style="padding:12px">要造车或改装，到非敌对聚居地的「车辆」页委托。</div></nav>
      <div class="ws-col" style="flex:1"><div style="display:flex;gap:8px;padding:12px 16px"><input id="lib-q" type="search" placeholder="搜索蓝图" value="${w.q}" data-input="lib-q" style="flex:1">
          <div class="seg">${[['all', '全部'], ['vehicle', '车辆'], ['building', '建筑']].map(([k, l]) => `<button class="${w.filter === k ? 'on' : ''}" data-action="lib-filter" data-v="${k}">${l}</button>`).join('')}</div></div>
        <div class="scroll" style="padding:0 16px 16px">${cards ? `<div class="cards">${cards}</div>` : `<div class="empty">${q ? '没有符合条件的蓝图。' : '这里还没有蓝图。'}</div>`}</div></div>
      <div class="ws-col" style="width:380px">${detail}</div></div></div>`;
}

// 新建设计：名称、类型、尺寸档位或地块、保存位置
function newDesignBody() {
  const d = S.nd;
  const seg = (k, opts) => `<div class="seg">${opts.map(o => `<button class="${d[k] === o ? 'on' : ''}" data-action="nd-set" data-v="${k}:${o}">${o}</button>`).join('')}</div>`;
  const env = ENVELOPE[d.size];
  return `<div style="display:flex;flex-direction:column;gap:14px">
    <div class="field"><label for="nd-name">名称</label><input id="nd-name" type="text" value="${d.name}" data-input="nd-name"></div>
    <div class="field"><label>类型</label>${seg('kind', ['车辆', '建筑'])}</div>
    ${d.kind === '车辆' ? `<div class="field"><label>尺寸档位</label>${seg('size', Object.keys(ENVELOPE))}<div class="small muted">外形不能超过 长 ${env[0]} × 宽 ${env[1]} × 高 ${env[2]} 格。尺寸档位也决定轨距。</div></div>`
      : `<div class="field"><label>地块</label>${seg('plot', ['小', '中', '大', '巨型'])}</div>
         <div class="field"><label>设施</label><div style="display:flex;gap:14px;flex-wrap:wrap">${['车间', '仓储', '发电', '防御'].map(t => `<label style="display:flex;gap:6px;align-items:center;cursor:pointer" data-action="nd-tag" data-v="${t}"><span class="check ${d.tags.includes(t) ? 'on' : ''}">${d.tags.includes(t) ? icon('check', 'sm') : ''}</span>${t}</label>`).join('')}</div></div>`}
    <div class="field"><label>保存到</label>${seg('lib', ['本局蓝图库', '用户内容库'])}<div class="small muted">${d.lib === '本局蓝图库' ? LIBS.local[1] : LIBS.user[1]}。</div></div></div>`;
}

Object.assign(INPUTS, { 'lib-q': v => { S.workspace.q = v; }, 'nd-name': v => { S.nd.name = v; } });
Object.assign(WS_ACTIONS, {
  'lib-lib': v => { S.workspace.lib = v; S.workspace.sel = null; },
  'lib-filter': v => { S.workspace.filter = v; },
  'lib-sel': v => { S.workspace.sel = v; },
  'lib-edit': v => { const it = LIBRARY.find(i => i.id === v); openEditor({ name: it.name, lib: it.lib === 'draft' ? 'local' : it.lib, size: VEHICLE_TYPES[it.type]?.size || '中型', from: 'library', refit: it.lib === 'draft' ? { train: 't1', vehicle: 'v2', node: 'n1' } : null }); },
  'lib-copy-edit': v => { const it = LIBRARY.find(i => i.id === v); const copy = { ...it, id: 'l' + Date.now(), name: `${it.name} 副本`, lib: 'local', used: 0 }; LIBRARY.push(copy); openEditor({ name: copy.name, lib: 'local', size: VEHICLE_TYPES[it.type]?.size || '中型', from: 'library' }); },
  'lib-copy-user': v => { const it = LIBRARY.find(i => i.id === v); LIBRARY.push({ ...it, id: 'u' + Date.now(), lib: 'user' }); toast('complete', `已复制「${it.name}」到用户内容库`, null, false); },
  'lib-copy-local': v => { const it = LIBRARY.find(i => i.id === v); LIBRARY.push({ ...it, id: 'l' + Date.now(), lib: 'local' }); toast('complete', `已复制「${it.name}」到本局蓝图库`, null, false); },
  'lib-del': v => { const it = LIBRARY.find(i => i.id === v); S.dialog = { title: it.lib === 'draft' ? `丢弃草稿「${it.name}」？` : `删除「${it.name}」？`, body: it.lib === 'draft' ? '草稿里的修改会丢失。' : `蓝图会从${LIBS[it.lib][0]}删除。已经按它建成的车辆不受影响。`, confirm: it.lib === 'draft' ? '丢弃' : '删除', danger: true, onConfirm: () => { LIBRARY.splice(LIBRARY.indexOf(it), 1); S.workspace.sel = null; } }; },
  'nd-open': () => { S.nd = { name: '新设计 1', kind: '车辆', size: '中型', plot: '中', tags: [], lib: '本局蓝图库' }; S.dialog = { title: '新建设计', body: newDesignBody, confirm: '创建并编辑', onConfirm: () => openEditor({ name: S.nd.name, lib: S.nd.lib === '本局蓝图库' ? 'local' : 'user', size: S.nd.size, from: 'library', blank: true }) }; },
  'nd-set': v => { const [k, x] = v.split(':'); S.nd[k] = x; },
  'nd-tag': v => { const t = S.nd.tags; t.includes(v) ? t.splice(t.indexOf(v), 1) : t.push(v); },
});
