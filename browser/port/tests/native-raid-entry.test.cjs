// Native raid admission guard and its original exhaustion message. No stubs, server or player DB.
const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('native raid guard allows counts below ten and describes exhaustion as today', () => {
  const h = Headless.game(), L = h.L.raidEntry;
  h.expectLiteral(L.gameLiteral, h.L.gotGame);
  h.expectLiteral(L.usedLiteral, L.used);
  h.expectLiteral(L.limitLiteral, L.limit);
  h.expectLiteral(L.exhaustedTextLiteral, L.exhaustedText);
  h.expectCall(L.exhaustedTextCall, 'GetTextStringInPackage');
  assert.equal(h.i32(h.game + L.limit), 10);
  let outcome, armed = false;
  h.stopAt(L.allowed, () => { outcome = 'allowed'; });
  h.tap('GetTextStringInPackage', r => {
    if (!armed) return;
    outcome = [r[0], r[1]];
    return h.STOP;
  });
  for (const used of [0, 9, 10, 11, 0]) {
    h.w32(h.fight + L.used, used);
    armed = true;
    try {
      h.fragment({ start: L.guard, regs: { 4: h.got, 10: h.L.gotFight } });
    } finally { armed = false; }
    assert.deepEqual(outcome, used < 10 ? 'allowed' : [15, L.exhaustedText]);
  }
  const text = h.alloc(1024);
  h.call('GetTextStringInPackage', 15, L.exhaustedText, text);
  assert.equal(h.cstr(text), "You can't enter anymore today.");
  assert.deepEqual(h.stubs, []);
});
