const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('native Protocol 311 parser writes EXP boost timestamp and use_item_no, and NetworkEvent consumes the item', () => {
  const h = Headless.bare();
  const L = h.L.bookOfSecrets;
  h.expectCall(L.parseItemNoCall, 'ParsingJsonObjectToInt64');
  h.expectCall(L.lookupItemCall, 'GetItemNodeByUniqueItemID');
  h.expectLiteral(L.expBuffEndLiteral, L.expBuffEndOffset);
  h.expectLiteral(L.useItemNoLiteral, L.useItemNoOffset);
  h.expectLiteral(L.redrawFlagLiteral, L.redrawFlagOffset);

  const reply = h.alloc(0xd000);
  const fight = h.alloc(0x4100);
  const itemList = h.alloc(0x100);
  h.w32(h.got + h.L.gotReply, reply);
  h.w32(h.got + h.L.gotFight, fight);
  h.w32(h.got + 0x0cb8, itemList);

  const endTimestamp = 1800003600;
  const usedItemNo = 4242;
  const payload = h.json({
    buff_etc_info: [{ type: 2, end_date_timestamp: endTimestamp }],
    use_item_no: usedItemNo,
  });

  h.stopAt(L.parseExit);
  h.fragment({ start: L.parse311Start, regs: { 4: h.got, 5: payload, 8: h.L.gotReply } });

  assert.equal(h.u32(fight + L.expBuffEndOffset), endTimestamp);
  assert.equal(h.u32(fight + L.expBuffEndOffset + 4), 0);
  assert.equal(h.u32(reply + L.useItemNoOffset), usedItemNo);
  assert.equal(h.u32(reply + L.useItemNoOffset + 4), 0);

  let lookedUpId = null;
  h.tap('GetItemNodeByUniqueItemID', r => {
    lookedUpId = r[2] >>> 0;
  });
  h.stopAt(L.neExit);
  h.fragment({
    start: L.ne311Start,
    regs: { 4: h.got, 5: h.L.gotReply },
    frame: { 0x3c: 0 }, // errno == 0
  });
  assert.equal(lookedUpId, usedItemNo);
  assert.equal(h.cpu.m8[fight + L.redrawFlagOffset], 1);
  assert.deepEqual(h.stubs, []);
});

test('native DrawResult formats stage EXP as base + (base / 2) only while Book of Secrets is active', () => {
  const h = Headless.game();
  const L = h.L.bookOfSecrets;
  h.expectCall(L.sprintfCall, 'CS_knlSprintk');
  h.expectLiteral(L.expEndLiteral, L.expBuffEndOffset);
  h.expectLiteral(L.syncMsLiteral, L.syncMsOffset);
  h.expectLiteral(L.serverSecLiteral, L.serverSecOffset);

  const fight = h.alloc(0x4100);
  const sys = h.alloc(0x100);
  const stageRow = h.alloc(0x200);
  const textBuf = h.alloc(0x100);

  // Use Headless.game()'s real graphics vtable at got+0x1430; swap only fight/sys/buf slots temporarily.
  const gotFightSlot = h.L.gotFight;
  const gotSysSlot = h.L.systemTable.gotOffset;
  const gotGfxSlot = 0x1430;
  const gotBufSlot = 0x0794;
  const savedFight = h.u32(h.got + gotFightSlot);
  const savedSys = h.u32(h.got + gotSysSlot);
  const savedBuf = h.u32(h.got + gotBufSlot);

  // Server time synced at 1000s (sys clock 5000ms, sync ms 5000ms -> current server_now = 1000s).
  h.w32(fight + L.serverSecOffset, 1000);
  h.w32(fight + L.syncMsOffset, 5000);
  h.w32(sys + 0x50, 5000);
  // Active hero clear flag at fight + 0xad0 + 4 must be > 0 so DrawResult doesn't early-branch.
  h.w32(fight + 0xad4, 1);

  let formatted = null;
  h.tap('CS_knlSprintk', r => {
    formatted = { fmt: h.cstr(r[1]), value: r[2] | 0 };
    return h.STOP;
  });

  try {
    h.w32(h.got + gotFightSlot, fight);
    h.w32(h.got + gotSysSlot, sys);
    h.w32(h.got + gotBufSlot, textBuf);

    const runCase = (baseExp, buffEndTime) => {
      formatted = null;
      // stageRow + (0xae << 1) = stageRow + 0x15c = c87 (stage EXP).
      h.w32(stageRow + 0x15c, baseExp);
      h.w32(fight + L.expBuffEndOffset, buffEndTime);
      h.w32(fight + L.expBuffEndOffset + 4, 0);
      h.fragment({
        start: L.drawExpStart,
        regs: { 4: h.got, 10: 0 }, // sl = 0 before boost check
        frame: {
          0x14: stageRow,
          0x1c: gotGfxSlot,
          0x28: gotFightSlot,
          0x44: gotBufSlot,
          0x48: gotSysSlot,
        },
      });
      return formatted;
    };

    // Active boost (end 4600 > now 1000): 100 -> +150, 89 -> +133 (integer division 89 + 44).
    assert.deepEqual(runCase(100, 4600), { fmt: '+%d', value: 150 });
    assert.deepEqual(runCase(89, 4600), { fmt: '+%d', value: 133 });

    // Expired boost (end 1000 <= now 1000 and end 0): 100 -> +100, 89 -> +89.
    assert.deepEqual(runCase(100, 1000), { fmt: '+%d', value: 100 });
    assert.deepEqual(runCase(89, 0), { fmt: '+%d', value: 89 });
    assert.deepEqual(h.stubs, []);
  } finally {
    h.w32(h.got + gotFightSlot, savedFight);
    h.w32(h.got + gotSysSlot, savedSys);
    h.w32(h.got + gotBufSlot, savedBuf);
  }
});
