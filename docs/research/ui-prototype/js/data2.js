// 第 2 轮补充数据：自有聚居地、建筑与生产、蓝图、委托、租用仓库、运行报告明细、战斗
Object.assign(GOODS, {
  livestock: { name: '畜产品', color: '#c9965f' },
  rail:      { name: '钢轨',   color: '#6f7a86' },
  ballast:   { name: '道床碎石', color: '#9a9488' },
});

const PROCESSES = {
  grain:     { name: '种植谷物', out: 'grain', per: 12, labor: 30 },
  livestock: { name: '饲养牲畜', out: 'livestock', per: 6, labor: 30, input: { grain: 4 } },
  produce:   { name: '种植果蔬', out: 'produce', per: 10, labor: 40 },
};

const BLUEPRINTS = {
  vehicles: [
    { id: 'bp-custom', type: 'wagonM', name: '加长货车 改', lib: '本局蓝图库', capacity: 55, bom: { machinery: 6, concrete: 4, glass: 2 }, labor: 520, days: 2 },
    { id: 'bp-locoS', type: 'locoS', lib: '内容包', bom: { machinery: 6, electronics: 2 }, labor: 300, days: 1 },
    { id: 'bp-locoM', type: 'locoM', lib: '内容包', bom: { machinery: 10, electronics: 4 }, labor: 500, days: 2 },
    { id: 'bp-locoL', type: 'locoL', lib: '内容包', bom: { machinery: 18, electronics: 8 }, labor: 900, days: 3 },
    { id: 'bp-wagonS', type: 'wagonS', lib: '内容包', bom: { machinery: 3, concrete: 2 }, labor: 160, days: 1 },
    { id: 'bp-wagonM', type: 'wagonM', lib: '内容包', bom: { machinery: 5, concrete: 4 }, labor: 300, days: 1 },
    { id: 'bp-wagonL', type: 'wagonL', lib: '内容包', bom: { machinery: 8, concrete: 6, glass: 2 }, labor: 480, days: 2 },
  ],
  buildings: [
    { id: 'bb-farm', name: '农场', plot: '中', bom: { concrete: 30, machinery: 6 }, days: 3, note: '种植谷物或饲养牲畜' },
    { id: 'bb-orchard', name: '果园', plot: '中', bom: { concrete: 20, machinery: 4 }, days: 2, note: '种植果蔬' },
    { id: 'bb-power', name: '发电站', plot: '中', bom: { concrete: 40, machinery: 12, electronics: 6 }, days: 4, note: '为聚居地供电' },
    { id: 'bb-warehouse', name: '仓库', plot: '小', bom: { concrete: 20 }, days: 2, note: '仓储容量 +300' },
    { id: 'bb-tower', name: '防御塔', plot: '小', bom: { concrete: 15, machinery: 8 }, days: 2, note: '攻击靠近的敌人' },
  ],
};
const bpName = bp => bp.name || VEHICLE_TYPES[bp.type].name;

function extendWorld(w) {
  Object.assign(w.nodes.n2, {
    capacity: 600,
    stock: { grain: [146, 120, +12], produce: [88, 60, +4], livestock: [0, 20, 0], machinery: [6, 20, -1], concrete: [40, 40, 0] },
    needs: { basic: 0.92, improved: 0.64 }, labor: { used: 85, total: 110 },
    buildings: [
      { id: 'b1', name: '农场', plot: '中', processes: ['grain', 'livestock'], process: 'grain', enabled: true, priority: 'mid', state: 'running' },
      { id: 'b2', name: '果园', plot: '中', processes: ['produce'], process: 'produce', enabled: true, priority: 'mid', state: 'blocked', reason: '劳动力不足：需要 40，可用 25' },
      { id: 'b3', name: '小站点', plot: '小站点', station: true },
    ],
    plots: [{ id: 'p4', size: '中' }, { id: 'p5', size: '小' }],
    sites: [{ id: 's1', name: '仓库', plot: '小', progress: 0.45, eta: '约 1 天' }],
    defense: { towers: 0, mode: 'attack' },
  });
  w.nodes.n4.lease = { capacity: 200, goods: { electronics: 40, textiles: 20 }, expires: '第 9 天 08:00', daysLeft: 6 };
  w.nodes.n4.leaseFree = 1800; w.nodes.n1.leaseFree = 1200;
  w.nodes.n6.defense = { towers: 2, morale: 0.62 }; w.nodes.n6.surrendered = false;
  w.jobs = [{ id: 'j1', node: 'n1', kind: '制造', what: '中型货车', progress: 0.6, eta: '约 1 天' }];
  w.trains.t1.report.detail = {
    sales: [['grain', 40, 436]], purchases: [['machinery', 12, -712]], service: 0,
    issues: ['在青川只买到 12/20 件机械部件：库存不足'],
  };
  w.trains.t2.report.detail = { sales: [['ironore', 20, 238]], purchases: [['ironore', 20, -188]], service: 160, issues: [] };
  w.battles = {};
  return w;
}
