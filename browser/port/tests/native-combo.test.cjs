// "%d Bonus Damage for Every 5 Combos" (item option family 20410, native category 410), run in the real
// SetDamageCharToMon code headless. Verifies native 1.4.0 milestone combo behavior: bonus pays once
// whenever floor(combo / 5) exceeds floor(comboMark / 5), then updates comboMark.
const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

const OPTION = 'GetEquipItemOptionValueByCategory';
const family = Headless.seed('master_item_options').filter(o => o.family === '20410');
const max = id => +family.find(o => +o.option_id === id).value1_max;
const EQUIPPED = { 2041003: max(2041003), 2041005: max(2041005) };   // 300 + 100 from the seed CSV
const BONUS = EQUIPPED[2041003] + EQUIPPED[2041005];

let fixture;
function setup() {
  if (fixture) return fixture;
  const h = Headless.game();
  h.expectCall(0x1450bc, OPTION);            // category 401, result left in [sp+0x4c]
  h.expectCall(0x1450d4, OPTION);            // category 410
  h.expectCall(0x1450f0, 'GetSecureValue');  // combo count
  h.expectCall(0x14513e, OPTION);            // category 411: the bonus accumulator (sl) is final here
  h.expectLiteral(0x145210, h.L.gotFight);
  h.expectLiteral(0x145214, h.L.fight.combo);
  h.expectLiteral(0x145218, h.L.fight.comboMark);
  h.setSecureKey(0x5a5a1234);
  const state = { armed: false, categories: [] };
  h.tap(OPTION, r => {
    if (!state.armed) return;
    state.categories.push(r[1]);
    if (r[1] === 411) return h.STOP;
  });
  const hit = (set, combo, carry = 0) => {
    h.combo = combo;
    state.armed = true; state.categories = [];
    try {
      const { r } = h.fragment({
        start: 0x1450c0,
        frame: { 0x4c: carry },
        regs: sp => ({ 5: h.got, 7: set, 8: sp + 0x4c, 9: sp + 0x48 }),
      });
      assert.deepEqual(state.categories, [410, 411], 'lookups made by the fragment');
      return r[10];
    } finally { state.armed = false; }
  };
  return (fixture = { h, hit });
}

test('seed rows of family 20410 are the native category-410 options', () => {
  const { h } = setup();
  assert.deepEqual(family.map(o => +o.option_id), [2041001, 2041002, 2041003, 2041004, 2041005]);
  for (const o of family) assert.equal(Math.floor(+o.option_id / 100) % 10000, 410);
  assert.deepEqual(h.optionValue(h.equipOptions(EQUIPPED), 410), { found: 1, value1: BONUS, value2: 0 });
});

test('native combo option pays once at each new five-combo milestone', () => {
  const { h, hit } = setup(), set = h.equipOptions(EQUIPPED);
  h.comboMark = 0;
  for (let combo = 0; combo <= 20; combo++) {
    const expected = combo > 0 && combo % 5 === 0 ? BONUS : 0;
    assert.equal(hit(set, combo), expected, 'combo ' + combo);
    assert.equal(hit(set, combo), 0, 'repeat combo ' + combo);
  }
});

test('broken streak resets mark and re-arms milestone five', () => {
  const { h, hit } = setup(), set = h.equipOptions(EQUIPPED);
  h.comboMark = 0;
  assert.equal(hit(set, 5), BONUS);
  assert.equal(hit(set, 6), 0);
  assert.equal(hit(set, 0), 0);
  assert.equal(h.comboMark, 0);
  assert.equal(hit(set, 5), BONUS);
});

test('without a positive category-410 option nothing is added and the mark is untouched', () => {
  const { h, hit } = setup();
  const sets = [{}, { 2041101: 500 }, { 2041001: 0 }, { 2041001: -10 }];
  for (const options of sets) {
    const set = h.equipOptions(options);
    h.comboMark = 15;
    assert.equal(hit(set, 2), 0);
    assert.equal(hit(set, 20), 0);
    assert.equal(h.comboMark, 15, 'the absent/nonpositive option skips the whole block');
  }
  assert.equal(setup().h.stubs.length, 0, 'no native function was stubbed');
});
