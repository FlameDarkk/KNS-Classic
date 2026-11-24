// Item options through the game's own loader and lookup (dt_itemoption_pkg_140 table 0,
// GetEquipItemOptionValueByCategory@0xf9d48), checked against server-dotnet/seed/master_item_options.csv.
const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

const seed = Headless.seed('master_item_options');
const category = id => Math.floor(id / 100) % 10000;

test('power-up preview adds both native option gains to stored values', () => {
  const h = Headless.game(), p = h.L.optionPreview;
  [0x6986697b, 0x18f66a82, 0x18d26afb, 0xe6494692].forEach((word, i) => h.expectLiteral(p.start + 4 * i, word));
  const row = h.alloc(0x48), option = h.alloc(0x40);
  h.w32(row + 0x18, 10); h.w32(row + 0x28, 20);
  h.w32(option + 0x14, 93); h.w32(option + 0x2c, 7);
  h.stopAt(p.end);
  const result = h.fragment({ start: p.start, regs: { 0: row, 7: option } });
  assert.equal(result.r[6], 103);
  assert.equal(result.r[10], 27);
  assert.deepEqual(h.stubs, []);
});

test('seed CSV equals native item-option table 0, row for row', () => {
  const h = Headless.game(), t = h.table(h.L.packages.itemOption, 0);
  assert.equal(t.rowSize, 0x48);
  assert.equal(t.count, seed.length);
  const columns = Object.keys(seed[0]).filter(k => k !== 'name');   // the name comes from text table 5
  assert.equal(columns.length, t.rowSize / 4);
  seed.forEach((o, i) => assert.deepEqual(h.ints(t.row(i), columns.length), columns.map(k => +o[k]), 'row ' + i + ' option ' + o.option_id));
});

test('table 0 is ordered by category, which the lookup needs for its early exit', () => {
  // 0xf9daa: a row whose category is above the wanted one ends the scan.
  const ids = seed.map(o => +o.option_id);
  for (let i = 1; i < ids.length; i++) assert.ok(category(ids[i - 1]) <= category(ids[i]), ids[i - 1] + ' before ' + ids[i]);
});

test('lookup sums value1 and value2 over one category only', () => {
  const h = Headless.game();
  const set = h.equipOptions({ 2041001: 30, 2041004: [450, 3], 2041101: 500, 2000001: [12, 9] });
  assert.deepEqual(h.optionValue(set, 410), { found: 1, value1: 480, value2: 3 });
  assert.deepEqual(h.optionValue(set, 411), { found: 1, value1: 500, value2: 0 });
  assert.deepEqual(h.optionValue(set, category(2000001)), { found: 1, value1: 12, value2: 9 });
  assert.deepEqual(h.optionValue(set, 412), { found: 0, value1: 0, value2: 0 });
  assert.deepEqual(h.optionValue(0, 410), { found: 0, value1: 0, value2: 0 }, 'no option set');
});

test('every seed family resolves through the native lookup', () => {
  const h = Headless.game();
  const set = h.equipOptions(Object.fromEntries(seed.map((o, i) => [o.option_id, [i + 1, 2]])));
  const want = new Map();
  seed.forEach((o, i) => {
    const c = category(+o.option_id), sum = want.get(c) || [0, 0];
    want.set(c, [sum[0] + i + 1, sum[1] + 2]);
  });
  for (const [c, [value1, value2]] of want) assert.deepEqual(h.optionValue(set, c), { found: 1, value1, value2 }, 'category ' + c);
  assert.deepEqual(h.stubs, [], 'no native function was stubbed');
});
