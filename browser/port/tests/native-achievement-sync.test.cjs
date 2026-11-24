const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');
const sync = require('../achievement-sync.js');

test('collected achievements from the server reach the native finish list after login, once per session', async () => {
  const h = Headless.replay('guest-stage-3000001');
  const pin = h.L.achievementSync;
  h.expectCall(pin.parseCall, 'ParsingDataToAchieveFinish');
  h.expectCall(pin.finishLoopCall, 'UpdateDataAchieveFinish');
  h.expectCall(pin.login121Request, 'Request');
  await h.login();
  // Each queued native call runs on the game thread; keep frames going until the sync settles (bounded).
  const settle = async promise => {
    let done = false;
    promise.then(() => { done = true; }, () => { done = true; });
    for (let i = 0; i < 400 && !done; i++) await h.run(1);
    assert.ok(done, 'sync finished');
    return promise;
  };

  const collected = [4000101, 4000102, 4000301];
  assert.deepEqual(collected.map(id => h.ptr('GetAchieveFinishByID', id)), [0, 0, 0], 'a fresh save has none collected');

  const calls = [];
  let session = 'session-a';
  const control = sync.install(h.rt, async (protocol, params) => {
    calls.push({ protocol, params });
    return { protocol: 122, errno: 0, achievement_reward_infos: [
      ...collected.map(achievement_id => ({ achievement_id, idx: 0, reg_date: '1795527589' })),
      { achievement_id: 0, idx: 0, reg_date: '1' }, { achievement_id: 'bad' }, null,
    ] };
  }, () => session);

  assert.equal(control.onGateway(51), null, 'only the login/load reply triggers it');
  const running = control.onGateway(60);
  assert.ok(running);
  assert.equal(control.onGateway(60), null, 'one request per game session');
  assert.equal(await settle(running), collected.length, 'three valid rows applied');

  assert.deepEqual(calls, [{ protocol: 122, params: {} }]);
  for (const id of collected) {
    const record = h.ptr('GetAchieveFinishByID', id);
    assert.notEqual(record, 0, `achievement ${id} is collected in the native list`);
    assert.equal(h.u32(record), id);
  }
  assert.equal(h.ptr('GetAchieveFinishByID', 0), 0, 'invalid rows are skipped');

  // A repeat upserts by id: still one record each.
  const before = collected.map(id => h.ptr('GetAchieveFinishByID', id));
  session = 'session-b';
  await settle(control.onGateway(60));
  assert.equal(calls.length, 2, 'a new session asks again');
  assert.deepEqual(collected.map(id => h.ptr('GetAchieveFinishByID', id)), before);
});
