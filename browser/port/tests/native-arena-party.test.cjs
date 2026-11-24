const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('native Arena parses three ordered opponent slots and skips empty slots before spawning', () => {
  const h = Headless.bare();
  const L = h.L.arenaParty;
  h.expectCall(L.parseFellowCall, 'ParsingDataToFellowInfo');
  h.expectCall(L.parseIdCall, 'ParsingJsonObjectToInt32');
  h.expectCall(L.nextFieldCall, 'json_object_get');
  h.expectCall(L.initFellowCall, 'InitFellow');
  h.expectCall(L.destroyCall, 'DestroyHeroGameData');
  h.expectLiteral(L.partyLiteral, L.replyParty);
  h.expectLiteral(L.fightLiteral, h.L.gotFight);
  h.expectLiteral(L.spawnedLiteral, L.spawnedOffset);

  const reply = h.alloc(L.replyParty + 4 * L.stride);
  const friend = h.alloc(L.friendParty + 3 * L.stride);
  const fight = h.alloc(L.spawnedOffset + 12);
  const slots = reply + L.replyParty;
  const party = friend + L.friendParty;
  const sentinel = slots + 3 * L.stride;
  const afterParty = h.addr(L.nextFieldCall + 4);
  h.w32(h.got + h.L.gotFight, fight);
  h.tap('json_object_get', r => {
    if (r[14] === afterParty) return h.STOP;
  });

  let spawned;
  h.tap('InitFellow', (_r, fixture) => {
    const selected = fixture.u32(fixture.r[13]);
    spawned = [fixture.u32(selected), fixture.u32(selected + L.fellowId)];
    return fixture.STOP;
  });
  h.tap('DestroyHeroGameData', () => h.STOP);

  for (const ids of [[100404, 0, 100399], [0, 0, 100403], [0, 0, 0]]) {
    const numbers = ids.map(id => id ? id + 9000 : 0);
    const fellow_info = ids.map((id, i) => ({ fellow_no: numbers[i], fellow_id: id }));
    const snapshot = h.json({ fellow_info });
    h.w32(sentinel, 0x12345678);
    h.fragment({ start: L.parseStart, regs: { 7: reply }, frame: { 8: snapshot } });
    assert.equal(h.u32(sentinel), 0x12345678);
    for (let i = 0; i < ids.length; i++) {
      const row = slots + i * L.stride;
      assert.equal(h.u32(row), numbers[i]);
      assert.equal(h.u32(row + L.fellowId), ids[i]);
    }

    // UpDateDataDetailFriendInfo copies these three records into the opponent.
    h.cpu.m8.set(h.bytes(slots, 3 * L.stride), party);
    spawned = null;
    h.fragment({ start: L.createStart, regs: { 6: friend, 7: h.got, 8: h.got } });
    const first = ids.findIndex(Boolean);
    assert.deepEqual(spawned, first === -1 ? null : [numbers[first], ids[first]]);
  }
  assert.deepEqual(h.stubs, []);
});
