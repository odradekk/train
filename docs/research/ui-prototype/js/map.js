// 宏观地图：SVG 绘制、平移缩放、悬停、单击选中、双击进入场景
const MapView = {
  view: { x: 0, y: 0, k: 1 },
  hover: null,
  drag: null,
  layers: { resources: false, platforms: false, creatures: false },

  el() { return document.getElementById('map'); },

  fit() {
    const r = this.el().getBoundingClientRect();
    const left = 64 + (S.drawer ? 340 : 0) + 24, right = (S.selection ? 456 : 0) + 24, top = 44 + 24, bottom = 24;
    const xs = Object.values(W.nodes).map(n => n.x), ys = Object.values(W.nodes).map(n => n.y);
    const minX = Math.min(...xs) - 60, maxX = Math.max(...xs) + 60, minY = Math.min(...ys) - 60, maxY = Math.max(...ys) + 60;
    const aw = r.width - left - right, ah = r.height - top - bottom;
    const k = Math.min(aw / (maxX - minX), ah / (maxY - minY));
    this.view = { k, x: left + (aw - (maxX - minX) * k) / 2 - minX * k, y: top + (ah - (maxY - minY) * k) / 2 - minY * k };
    this.render();
  },

  centerOn(sel) {
    const p = objectPoint(sel); if (!p) return;
    const r = this.el().getBoundingClientRect();
    const left = 64 + (S.drawer ? 340 : 0), right = S.selection ? 456 : 0;
    const cx = left + (r.width - left - right) / 2, cy = 44 + (r.height - 44) / 2;
    this.view.x = cx - p.x * this.view.k; this.view.y = cy - p.y * this.view.k;
    this.render();
  },

  zoom(f, cx, cy) {
    const r = this.el().getBoundingClientRect();
    cx ??= r.width / 2; cy ??= r.height / 2;
    const k = Math.max(0.3, Math.min(4, this.view.k * f)), s = k / this.view.k;
    this.view.x = cx - (cx - this.view.x) * s; this.view.y = cy - (cy - this.view.y) * s; this.view.k = k;
    this.render();
  },

  render() {
    const svg = document.querySelector('#map svg'); if (!svg || S.scene) return;
    const { x, y, k } = this.view;
    const sel = S.selection, hv = this.hover;
    const isSel = (kind, id) => sel && sel.kind === kind && sel.id === id;
    const isHv = (kind, id) => hv && hv.kind === kind && hv.id === id;
    let out = '';
    out += `<g transform="translate(${x},${y}) scale(${k})">`;
    out += `<rect x="-400" y="-300" width="2400" height="1600" fill="#7d8b6f"/>
      <path d="M-100 80 C200 20 380 160 330 330 C280 470 40 420 -100 380Z" fill="#748463"/>
      <path d="M760 520 C900 470 1080 560 1060 760 C1040 900 820 920 740 800Z" fill="#87926f"/>
      <path d="M1250 40 C1380 20 1480 120 1440 300 C1400 380 1270 330 1230 220Z" fill="#8a8670"/>`;
    out += `<path d="M1500 0 C1460 300 1560 520 1620 1000 L2000 1000 L2000 0Z" fill="#6f8ea3" opacity=".75"/>`;

    // 线路：铁轨数 = 平行线条数；未建成为虚线
    for (const [id, l] of Object.entries(W.lines)) {
      const a = W.nodes[l.a], b = W.nodes[l.b];
      const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy), nx = -dy / len, ny = dx / len;
      const hl = isSel('line', id) || isHv('line', id);
      if (hl) out += `<line x1="${a.x}" y1="${a.y}" x2="${b.x}" y2="${b.y}" stroke="${isSel('line', id) ? '#e0a22e' : '#ffffff'}" stroke-opacity="${isSel('line', id) ? .9 : .35}" stroke-width="${14 / k + 4}" stroke-linecap="round"/>`;
      if (l.tracks === 0) {
        out += `<line x1="${a.x}" y1="${a.y}" x2="${b.x}" y2="${b.y}" stroke="#4a5240" stroke-width="2" stroke-dasharray="7 6" opacity=".8"/>`;
      } else {
        for (let i = 0; i < l.tracks; i++) {
          const o = (i - (l.tracks - 1) / 2) * 4;
          out += `<line x1="${a.x + nx * o}" y1="${a.y + ny * o}" x2="${b.x + nx * o}" y2="${b.y + ny * o}" stroke="#2b2f33" stroke-width="2.4"/>`;
        }
      }
      if (l.breaks) {
        const mx = a.x + dx * .55, my = a.y + dy * .55;
        out += `<g transform="translate(${mx},${my})"><circle r="9" fill="#c9423a" stroke="#fff" stroke-width="1.5"/><path d="M-4 -4 4 4M4 -4-4 4" stroke="#fff" stroke-width="2"/></g>`;
      }
      out += `<line class="map-hit" data-kind="line" data-id="${id}" x1="${a.x}" y1="${a.y}" x2="${b.x}" y2="${b.y}" stroke="transparent" stroke-width="${16 / k + 6}"/>`;
    }

    // 节点
    for (const [id, n] of Object.entries(W.nodes)) {
      const hl = isSel('node', id), hov = isHv('node', id);
      if (hl || hov) out += `<circle cx="${n.x}" cy="${n.y}" r="${n.kind === 'settlement' ? 20 : 15}" fill="none" stroke="${hl ? '#e0a22e' : '#fff'}" stroke-width="3" opacity="${hl ? 1 : .6}"/>`;
      if (n.kind === 'settlement') {
        const c = RELATION[n.relation].color;
        out += `<rect x="${n.x - 11}" y="${n.y - 11}" width="22" height="22" rx="5" fill="${c}" stroke="#1b1f23" stroke-width="2"/>`;
        if (n.relation === 'owned') out += `<path d="M${n.x - 5} ${n.y + 1} l4 4 7-8" stroke="#fff" stroke-width="2.2" fill="none"/>`;
        if (n.relation === 'hostile') out += `<path d="M${n.x - 5} ${n.y - 5} l10 10M${n.x + 5} ${n.y - 5} l-10 10" stroke="#fff" stroke-width="2.2"/>`;
        out += `<text class="map-label" x="${n.x}" y="${n.y + 30}" text-anchor="middle">${n.name}</text>`;
        if (this.layers.platforms && n.platforms) {
          const used = trainsAt(id).length;
          out += `<text class="map-label small" x="${n.x}" y="${n.y + 45}" text-anchor="middle">站台 ${used}/${n.platforms}</text>`;
        }
      } else if (n.kind === 'empty') {
        out += `<circle cx="${n.x}" cy="${n.y}" r="7" fill="#e8e4d6" fill-opacity=".35" stroke="#e8e4d6" stroke-width="2" stroke-dasharray="3 2.5"/>`;
        out += `<text class="map-label small" x="${n.x}" y="${n.y + 22}" text-anchor="middle">${n.name}</text>`;
        if (this.layers.resources) out += `<text class="map-label small" x="${n.x}" y="${n.y + 36}" text-anchor="middle">${n.resources.join('、')}</text>`;
      } else {
        out += `<path d="M${n.x} ${n.y - 13} L${n.x + 12} ${n.y + 9} L${n.x - 12} ${n.y + 9}Z" fill="#6b1f1b" stroke="#ec8a84" stroke-width="2"/>`;
        out += `<text class="map-label small" x="${n.x}" y="${n.y + 26}" text-anchor="middle">${n.name}</text>`;
        if (this.layers.creatures) out += `<circle cx="${n.x}" cy="${n.y}" r="70" fill="#c9423a" fill-opacity=".12" stroke="#c9423a" stroke-dasharray="5 4"/>`;
      }
      out += `<circle class="map-hit" data-kind="node" data-id="${id}" cx="${n.x}" cy="${n.y}" r="${Math.max(16, 14 / k)}" fill="transparent"/>`;
    }

    // 列车：同一节点上的列车呈扇形散开，互不遮挡
    for (const [id, t] of Object.entries(W.trains)) {
      const p = trainPoint(id); if (!p) continue;
      const hl = isSel('train', id), hov = isHv('train', id), c = STATUS_COLOR[t.status];
      const num = t.name.replace(/\D/g, '');
      out += `<g transform="translate(${p.x},${p.y}) rotate(${p.angle})">`;
      if (hl || hov) out += `<rect x="-17" y="-12" width="35" height="24" rx="6" fill="none" stroke="${hl ? '#e0a22e' : '#fff'}" stroke-width="2.5"/>`;
      out += `<path d="M-13 -8 H9 L14 0 L9 8 H-13 Z" fill="${c}" stroke="#14181c" stroke-width="1.5" stroke-linejoin="round"/>`;
      out += `</g>`;
      out += `<text x="${p.x - 1}" y="${p.y + 4}" text-anchor="middle" font-size="11" font-weight="700" fill="#fff" style="pointer-events:none">${num}</text>`;
      out += `<circle class="map-hit" data-kind="train" data-id="${id}" cx="${p.x}" cy="${p.y}" r="${Math.max(14, 12 / k)}" fill="transparent"/>`;
    }
    out += battleMarkers(isSel, isHv);
    out += `</g>`;
    svg.innerHTML = out;
  },

  bind() {
    const el = this.el();
    el.addEventListener('pointerdown', e => {
      if (e.button !== 0 && e.button !== 1) return;
      this.drag = { sx: e.clientX, sy: e.clientY, vx: this.view.x, vy: this.view.y, moved: false };
      el.setPointerCapture(e.pointerId);
    });
    el.addEventListener('pointermove', e => {
      if (this.drag) {
        const dx = e.clientX - this.drag.sx, dy = e.clientY - this.drag.sy;
        if (Math.abs(dx) + Math.abs(dy) > 4) { this.drag.moved = true; el.classList.add('dragging'); hideTip(); }
        if (this.drag.moved) { this.view.x = this.drag.vx + dx; this.view.y = this.drag.vy + dy; this.render(); }
        return;
      }
      const t = e.target.closest('.map-hit');
      const h = t ? { kind: t.dataset.kind, id: t.dataset.id } : null;
      if (JSON.stringify(h) !== JSON.stringify(this.hover)) { this.hover = h; this.render(); }
      if (h) showTip(e.clientX, e.clientY, tipFor(h)); else hideTip();
    });
    el.addEventListener('pointerleave', () => { this.hover = null; hideTip(); this.render(); });
    el.addEventListener('pointerup', e => {
      const d = this.drag; this.drag = null; el.classList.remove('dragging');
      if (!d || d.moved) return;
      const hit = document.elementFromPoint(e.clientX, e.clientY)?.closest('.map-hit');
      if (hit) select({ kind: hit.dataset.kind, id: hit.dataset.id });
      else select(null);
    });
    el.addEventListener('dblclick', e => {
      const hit = e.target.closest('.map-hit');
      if (hit) enterScene({ kind: hit.dataset.kind, id: hit.dataset.id });
    });
    el.addEventListener('wheel', e => { e.preventDefault(); this.zoom(e.deltaY < 0 ? 1.2 : 1 / 1.2, e.clientX, e.clientY); }, { passive: false });
  },
};

function trainsAt(nodeId) { return Object.keys(W.trains).filter(id => W.trains[id].at === nodeId && !W.trains[id].line); }

function trainPoint(id) {
  const t = W.trains[id];
  if (t.line) {
    const a = W.nodes[t.from], b = W.nodes[t.to];
    const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy);
    const nx = -dy / len, ny = dx / len;
    return { x: a.x + dx * t.progress + nx * 9, y: a.y + dy * t.progress + ny * 9, angle: Math.atan2(dy, dx) * 180 / Math.PI };
  }
  if (!t.at) return null;
  const n = W.nodes[t.at], here = trainsAt(t.at), i = here.indexOf(id);
  const ang = (-150 + i * 42) * Math.PI / 180;
  return { x: n.x + Math.cos(ang) * 36, y: n.y + Math.sin(ang) * 36, angle: 0 };
}

function objectPoint(sel) {
  if (!sel) return null;
  if (sel.kind === 'node') return W.nodes[sel.id];
  if (sel.kind === 'train') return trainPoint(sel.id);
  if (sel.kind === 'battle') return battlePoint(sel.id);
  if (sel.kind === 'line') { const l = W.lines[sel.id], a = W.nodes[l.a], b = W.nodes[l.b]; return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }; }
}

function tipFor(h) {
  if (h.kind === 'node') {
    const n = W.nodes[h.id];
    if (n.kind === 'settlement') return `<b>${n.name}</b><br>${n.type} · ${RELATION[n.relation].label} · 人口 ${fmt(n.pop)}<br><span class="faint">单击查看，双击进入场景</span>`;
    if (n.kind === 'empty') return `<b>${n.name}</b><br>空节点 · ${n.terrain} · 资源：${n.resources.join('、')}<br><span class="faint">单击查看，双击进入场景</span>`;
    return `<b>${n.name}</b><br>生物聚落 · ${n.threat}<br><span class="faint">单击查看</span>`;
  }
  if (h.kind === 'line') { const l = W.lines[h.id]; return `<b>${lineName(h.id)}</b><br>${l.tracks ? `铁轨 ${l.tracks}/${l.max}` : '未建成'} · 长度 ${l.length}${l.breaks ? ` · <span style="color:#ec8a84">${l.breaks} 处断点</span>` : ''}`; }
  if (h.kind === 'battle') { const b = W.battles[h.id]; return `<b>战斗 · ${battleName(b)}</b><br>${b.ours.map(t => W.trains[t].name).join('、')} 对 ${b.enemy.name}<br><span class="faint">单击查看</span>`; }
  const t = W.trains[h.id];
  return `<b>${t.name}</b> · ${STATUS[t.status].label}<br>${trainWhere(h.id)}`;
}

function showTip(x, y, html) { const t = document.getElementById('tooltip'); t.innerHTML = html; t.classList.remove('hidden'); t.style.left = (x + 16) + 'px'; t.style.top = (y + 16) + 'px'; }
function hideTip() { document.getElementById('tooltip').classList.add('hidden'); }

// 运行计划编辑器里的路线图：显示全部节点，计划经过的线路高亮，停靠站带序号
function routeMapSvg(plan, w, h) {
  const xs = Object.values(W.nodes).map(n => n.x), ys = Object.values(W.nodes).map(n => n.y);
  const minX = Math.min(...xs) - 50, maxX = Math.max(...xs) + 50, minY = Math.min(...ys) - 50, maxY = Math.max(...ys) + 50;
  const k = Math.min(w / (maxX - minX), h / (maxY - minY));
  const ox = (w - (maxX - minX) * k) / 2 - minX * k, oy = (h - (maxY - minY) * k) / 2 - minY * k;
  const P = n => ({ x: n.x * k + ox, y: n.y * k + oy });
  let out = `<rect width="${w}" height="${h}" fill="#5f6b55"/>`;
  const legs = planLegs(plan);
  for (const [id, l] of Object.entries(W.lines)) {
    const a = P(W.nodes[l.a]), b = P(W.nodes[l.b]);
    const used = legs.some(g => g.lines.includes(id));
    out += `<line x1="${a.x}" y1="${a.y}" x2="${b.x}" y2="${b.y}" stroke="${used ? '#e0a22e' : l.tracks ? '#2b2f33' : '#4a5240'}" stroke-width="${used ? 5 : 2}" ${l.tracks ? '' : 'stroke-dasharray="5 5"'} stroke-linecap="round"/>`;
  }
  for (const g of legs.filter(g => !g.ok)) {
    const a = P(W.nodes[g.from]), b = P(W.nodes[g.to]);
    out += `<line x1="${a.x}" y1="${a.y}" x2="${b.x}" y2="${b.y}" stroke="#c9423a" stroke-width="3" stroke-dasharray="6 5"/>`;
  }
  for (const [id, n] of Object.entries(W.nodes)) {
    const p = P(n), idxs = plan.stops.map((s, i) => s.node === id ? i : -1).filter(i => i >= 0);
    const canStop = n.kind === 'settlement' && n.relation !== 'hostile';
    out += `<g class="map-hit" data-action="plan-add-node" data-id="${id}" style="cursor:${n.kind === 'lair' ? 'not-allowed' : 'pointer'}">`;
    out += `<circle cx="${p.x}" cy="${p.y}" r="16" fill="transparent"/>`;
    if (n.kind === 'settlement') out += `<rect x="${p.x - 8}" y="${p.y - 8}" width="16" height="16" rx="4" fill="${RELATION[n.relation].color}" stroke="#1b1f23" stroke-width="1.5"/>`;
    else if (n.kind === 'empty') out += `<circle cx="${p.x}" cy="${p.y}" r="5" fill="none" stroke="#e8e4d6" stroke-width="1.5" stroke-dasharray="2 2"/>`;
    else out += `<path d="M${p.x} ${p.y - 9} L${p.x + 8} ${p.y + 6} L${p.x - 8} ${p.y + 6}Z" fill="#6b1f1b" stroke="#ec8a84" stroke-width="1.5"/>`;
    out += `<text class="map-label small" x="${p.x}" y="${p.y + 22}" text-anchor="middle">${n.name}</text>`;
    if (idxs.length) {
      const s = plan.stops[idxs[0]];
      out += `<circle cx="${p.x + 12}" cy="${p.y - 12}" r="10" fill="${s.pass ? '#1b1f23' : '#e0a22e'}" stroke="#e0a22e" stroke-width="2"/>`;
      out += `<text x="${p.x + 12}" y="${p.y - 8}" text-anchor="middle" font-size="11" font-weight="700" fill="${s.pass ? '#e0a22e' : '#1a1a1a'}">${idxs.map(i => i + 1).join(',')}</text>`;
    }
    if (!canStop && n.kind !== 'empty') { /* 敌对聚居地和生物聚落只能作为途经点 */ }
    out += `</g>`;
  }
  return out;
}

// 按已建成线路用广度优先搜索求两站之间的路线，供计划编辑器显示与校验
function findPath(from, to) {
  if (from === to) return [];
  const prev = { [from]: null }, q = [from];
  while (q.length) {
    const cur = q.shift();
    for (const [id, l] of Object.entries(W.lines)) {
      if (!l.tracks) continue;
      const nxt = l.a === cur ? l.b : l.b === cur ? l.a : null;
      if (!nxt || nxt in prev) continue;
      prev[nxt] = { node: cur, line: id };
      if (nxt === to) { const lines = []; let c = to; while (prev[c]) { lines.unshift(prev[c].line); c = prev[c].node; } return lines; }
      q.push(nxt);
    }
  }
  return null;
}
function planLegs(plan) {
  const legs = [], s = plan.stops;
  const n = plan.loop && s.length > 1 ? s.length : s.length - 1;
  for (let i = 0; i < n; i++) {
    const from = s[i].node, to = s[(i + 1) % s.length].node, lines = findPath(from, to);
    legs.push({ from, to, ok: !!lines, lines: lines || [], index: i });
  }
  return legs;
}
