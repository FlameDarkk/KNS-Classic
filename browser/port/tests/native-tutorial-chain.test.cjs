const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('native Protocols 602/603 parse quest_infos and NetworkEvent invokes StartTutorial(5000003) only when present', () => {
  const h = Headless.bare();
  const L = h.L.tutorialChain;
  h.expectCall(L.parseQuestCall603, 'ParsingDataToQuestInfo');
  h.expectCall(L.parseQuestCall602, 'ParsingDataToQuestInfo');
  h.expectCall(L.startTutorialCall, 'StartTutorial');
  h.expectLiteral(L.questOffsetLiteral, L.questOffset);
  h.expectLiteral(L.questIdLiteral, L.questIdOffset);
  h.expectLiteral(L.activeQuestLiteral, L.activeQuestOffset);

  const reply = h.alloc(0xd000);
  const fight = h.alloc(0x4000);
  h.w32(h.got + h.L.gotReply, reply);
  h.w32(h.got + h.L.gotFight, fight);

  // 1. Parse Protocol 603 reply WITH quest_infos: [{ quest_id: 5000003 }] (after 5000002 pays 30 Gems).
  h.stopAt(L.parseExit);
  const withNext = h.json({
    quest_infos: [{ quest_id: 5000003, quest_type: 0, quest_sub_type: 0, action_count: 0, try_count: 0, complete: 0 }],
    user_info: { user_cash: 30 },
    update_item_etc_info: [{ item_id: 1900009, count: 30 }],
  });
  h.fragment({ start: L.parse603Start, regs: { 4: h.got, 5: withNext, 8: h.L.gotReply } });
  assert.equal(h.i32(reply + L.questIdOffset), 5000003);

  // 2. Protocol 603 NetworkEvent dispatch reads reply+0xc428 and immediately calls StartTutorial(5000003).
  let startedTutorial = null;
  h.tap('StartTutorial', r => {
    startedTutorial = r[0] | 0;
    return h.STOP;
  });
  h.stopAt(L.neExit);
  h.fragment({
    start: L.ne603Dispatch,
    regs: { 4: h.got, 5: h.L.gotReply, 7: reply },
    frame: { 0x3c: 0 },
  });
  assert.equal(startedTutorial, 5000003);

  // 3. Parse Protocol 603 reply WITHOUT quest_infos (the old bug): reply+0xc428 stays 0, StartTutorial is skipped.
  h.w32(reply + L.questIdOffset, 0);
  h.w32(fight + L.activeQuestOffset, 5000002);
  const withoutNext = h.json({
    user_info: { user_cash: 30 },
    update_item_etc_info: [{ item_id: 1900009, count: 30 }],
  });
  h.fragment({ start: L.parse603Start, regs: { 4: h.got, 5: withoutNext, 8: h.L.gotReply } });
  assert.equal(h.i32(reply + L.questIdOffset), 0);

  startedTutorial = null;
  h.fragment({
    start: L.ne603Dispatch,
    regs: { 4: h.got, 5: h.L.gotReply, 7: reply },
    frame: { 0x3c: 0 },
  });
  assert.equal(startedTutorial, null);
  assert.equal(h.i32(fight + L.activeQuestOffset), 0);

  // 4. Parse Protocol 602 reply WITH quest_infos: [{ quest_id: 5000004 }] (chaining after 5000003 draw).
  const step3Reply = h.json({
    quest_infos: [{ quest_id: 5000004 }],
    user_info: { user_cash: 0 },
    update_item_etc_info: [],
  });
  h.fragment({ start: L.parse602Start, regs: { 4: h.got, 5: step3Reply, 8: h.L.gotReply } });
  assert.equal(h.i32(reply + L.questIdOffset), 5000004);
  assert.deepEqual(h.stubs, []);
});
