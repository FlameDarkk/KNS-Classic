// A whole stage start played headless from a recorded guest session (fixtures/guest-stage-3000001.json):
// title -> guest login -> hub -> dungeon map -> stage 3000001 -> real attacks. No server, no browser,
// virtual clock, so every run makes the same hits.
const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

const ATTACK = 5, STAGE = 3000001, PRESSES = 34;   // the hero walks to the first monsters before hits land
let plain = null, plainBonus = null;

async function play({ presses, options = null }) {
  const h = Headless.replay('guest-stage-' + STAGE);
  await h.login();
  await h.openDungeons();
  await h.startStage(STAGE);
  const hits = [];
  let hero = 0;
  h.tap('SetDamageCharToMon', r => {
    if (!hero && options) {
      hero = r[0] >>> 0;
      const set = h.equipOptions(options), own = hero + h.L.hero.equipOptions;
      h.w32(own, h.i32(set)); h.w32(own + 0xc, h.u32(set + 0xc));
    }
    hits.push({ comboBefore: h.combo, damage: null });
  });
  h.tap('SetGameTotalDamage', r => { if (hits.length && hits[hits.length - 1].damage === null) hits[hits.length - 1].damage = r[0]; });
  h.tap('GetEquipItemOptionValueByCategory', r => {
    if (r[1] === 411 && hits.length) hits[hits.length - 1].bonus = r[10];
  });
  for (let i = 0; i < presses; i++) { h.combat(ATTACK, true); await h.run(4); h.combat(ATTACK, false); await h.run(8); }
  return { h, hits };
}

test('recorded guest session logs in, opens the dungeon map and starts a stage', async () => {
  const { h, hits } = await play({ presses: PRESSES });
  plain = hits.map(x => x.damage);
  plainBonus = hits.map(x => x.bonus);
  assert.equal(h.scene, Headless.SCENE.FIELD);
  assert.deepEqual(h.net.unanswered, [], 'every request was answered from the tape');
  assert.deepEqual(h.net.protocols.slice(0, 3), [50, 51, 60]);
  assert.equal(h.net.protocols.at(-1), 71, 'stage start is the last request');
  const start = h.net.log.find(e => e.key.endsWith('#71'));
  assert.equal(start.request.params.stage_id, STAGE, 'the game asked for the stage the test selected');
  assert.equal(h.i32(h.u32(h.got + 0x1424)), STAGE, 'map state holds the running stage');
  assert.ok(hits.length >= 6, 'attacks landed: ' + hits.length);
  assert.ok(plain.every(d => d > 0), 'every hit dealt damage: ' + plain);
  assert.deepEqual(hits.map(x => x.comboBefore), hits.map((_, i) => i), 'each hit extends the combo by one');
  assert.equal(h.combo, hits.length);
  assert.equal(h.comboMark, 0, 'no combo option equipped: the mark stays 0');
  assert.deepEqual(h.stubs, []);
});

test('combo option: native milestone hit five pays the bonus once', async () => {
  assert.ok(plain, 'needs the plain run of the first test');
  for (const value of [300, 500]) {
    const { h, hits } = await play({ presses: PRESSES, options: { 2041004: value } });
    const damage = hits.map(x => x.damage);
    assert.equal(damage.length, plain.length, 'same fight, same number of hits');
    const extra = damage.map((d, i) => d - plain[i]);
    assert.deepEqual(hits.map((x, i) => x.bonus - plainBonus[i]),
      hits.map(x => (x.comboBefore + 1) % 5 === 0 ? value : 0), 'native milestone bonus before later damage modifiers');
    assert.ok(extra.every((d, i) => ((i + 1) % 5 === 0 ? d > 0 : d === 0)), `bonus ${value}: extra damage per hit ${extra}`);
    assert.equal(h.comboMark, Math.floor(hits.length / 5) * 5, 'mark records the last triggered milestone');
    assert.ok(extra.length >= 6, 'includes a hit after the fifth milestone');
    console.log(`combo option ${value}: hit 5 dealt ${damage[4]} instead of ${plain[4]} (+${extra[4]})`);
  }
});
