const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('native DrawRankingReward draws limited Arena ally face and star grade for 6★ and 5★ rewards', () => {
  const h = Headless.game();
  const L = h.L.arenaLimitedReward;
  h.expectCall(L.drawFellowLookupCall, 'GetFellowDataByID');
  h.expectCall(L.drawFellowFaceCall, 'DrawFellowFace');
  h.expectCall(L.drawGradeStarCall, 'DrawGradeStar');
  h.expectCall(L.drawEtcItemCall, 'DrawItemImageByBundle');

  const faceCalls = [];
  const starCalls = [];
  const bundleCalls = [];
  h.tap('DrawFellowFace', r => {
    faceCalls.push({ fellowId: h.i32(r[1]), grade: h.i32(r[1] + 4), x: r[2] | 0, y: r[3] | 0 });
  });
  h.tap('DrawGradeStar', r => {
    starCalls.push({ x: r[0] | 0, y: r[1] | 0, grade: r[2] | 0 });
  });
  h.tap('DrawItemImageByBundle', r => {
    bundleCalls.push({ itemId: r[0] | 0, x: r[2] | 0, y: r[3] | 0 });
  });

  // Check representative pairs across the 7 Altar Arena limited allies (Caroline, Lyrith, Leon) + ticket fallback.
  const cases = [
    { id: 100499, expectedGrade: 6 }, // Battle Queen Caroline (6★)
    { id: 100498, expectedGrade: 5 }, // Battle Flower Caroline (5★)
    { id: 100512, expectedGrade: 6 }, // Succubus Queen Lyrith (6★)
    { id: 100511, expectedGrade: 5 }, // Succubus Lyrith (5★)
    { id: 105017, expectedGrade: 6 }, // Holy Paladin Leon (6★)
    { id: 105016, expectedGrade: 5 }, // Paladin Leon (5★)
  ];

  for (const { id, expectedGrade } of cases) {
    faceCalls.length = 0;
    starCalls.length = 0;
    bundleCalls.length = 0;
    h.call('DrawRankingReward', id, 1, 100, 200);
    assert.deepEqual(faceCalls, [{ fellowId: id, grade: expectedGrade, x: 100, y: 200 }]);
    assert.deepEqual(starCalls, [{ x: 130, y: 200, grade: expectedGrade }]);
    assert.deepEqual(bundleCalls, []);
  }

  // Fallback 0~1% tier (1900012 Ally Ticket) still draws via DrawItemImageByBundle, not DrawFellowFace.
  faceCalls.length = 0;
  starCalls.length = 0;
  bundleCalls.length = 0;
  h.call('DrawRankingReward', 1900012, 1, 100, 200);
  assert.deepEqual(faceCalls, []);
  assert.deepEqual(starCalls, []);
  assert.deepEqual(bundleCalls, [{ itemId: 1900012, x: 75, y: 200 }]);
  assert.deepEqual(h.stubs, []);
});

test('native Protocol 405 parser reads limited ally IDs into pvp_ranking_reward_info rows', () => {
  const h = Headless.bare();
  const L = h.L.arenaLimitedReward;
  h.expectCall(L.parseRewardRowCall, 'ParsingDataToRewardTable');
  h.expectLiteral(L.countLiteral, L.countOffset);
  h.expectLiteral(L.rowsLiteral, L.rowsOffset);

  const reply = h.alloc(0xd000);
  const fight = h.alloc(0x4000);
  const sys = h.alloc(0x100);
  h.w32(h.got + h.L.gotReply, reply);
  h.w32(h.got + h.L.gotFight, fight);
  h.w32(h.got + h.L.systemTable.gotOffset, sys);

  const payload = h.json({
    week: 3,
    cur_pvp_remain_time: 12345,
    next_pvp_remain_time: 617145,
    pvp_reward: 0,
    last_week_ranking: 1,
    pvp_ranking_reward_info: [
      { Grade: 1, Reward1_Item: 100512, Reward1_Value: 1, Reward2_Item: 1900009, Reward2_Value: 500, Text_Index: 9993501 },
      { Grade: 4, Reward1_Item: 100511, Reward1_Value: 1, Reward2_Item: 1900009, Reward2_Value: 200, Text_Index: 9993504 },
      { Grade: 9, Reward1_Item: 1900012, Reward1_Value: 1, Reward2_Item: 1900009, Reward2_Value: 70, Text_Index: 9993509 },
    ],
    pvp_mission_reward_info: [],
  });

  h.stopAt(L.parseExit);
  h.fragment({ start: L.parse405Start, regs: { 4: h.got, 5: payload, 8: h.L.gotReply } });

  assert.equal(h.i32(reply + L.countOffset), 3);
  const rows = h.u32(reply + L.rowsOffset);
  assert.deepEqual(h.ints(rows + 0 * L.rowStride, 6), [1, 100512, 1, 1900009, 500, 9993501]);
  assert.deepEqual(h.ints(rows + 1 * L.rowStride, 6), [4, 100511, 1, 1900009, 200, 9993504]);
  assert.deepEqual(h.ints(rows + 2 * L.rowStride, 6), [9, 1900012, 1, 1900009, 70, 9993509]);
  assert.deepEqual(h.stubs, []);
});
