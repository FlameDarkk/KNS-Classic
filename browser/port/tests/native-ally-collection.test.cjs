const test = require('node:test');
const assert = require('node:assert/strict');
const Headless = require('../headless.js');

test('native Collect branch sends the species ID and applies gems and claimed book state', async () => {
  const h = Headless.replay('guest-stage-3000001'), id = 110006, layout = h.L.bookClaim;
  h.expectCall(layout.requestCall, 'Request');
  h.expectLiteral(layout.epilogueLiteral, layout.frame);
  const book = { fellow_id: id, recv_reward: 0, level: 1, exp: 0 };
  // Only a historical collection entry: the ally has already been consumed.
  for (const entry of h.net.queues.get('POST /1mhero/gateway.php #60').list) {
    const reply = JSON.parse(entry.response);
    reply.fellow_book_info = [book];
    entry.response = JSON.stringify(reply);
  }
  let requests = 0;
  h.net.on(308, req => {
    assert.equal(req.json.params.fellow_id, id);
    requests++;
    return { protocol: 308, errno: 0, fellow_book_info: { ...book, recv_reward: 1 },
      update_item_etc_info: [{ item_id: 1900009, count: 10 }] };
  });
  await h.login();
  await h.tapArea('touchPopChar', 9); await h.run(40);
  assert.equal(h.u32(h.ptr('GetFellowBookInfoByID', id) + 16), 0);
  const gems = h.i32(h.fight + layout.gems);
  // Run the real Collect request branch; popup-grid coordinate conversion is outside this fragment.
  h.fragment({ start: layout.start, frameSize: 0x6000, returnAt: layout.frame + 0x20,
    regs: { 4: h.got, 6: id } });
  await h.run(150);
  assert.equal(requests, 1);
  assert.equal(h.i32(h.fight + layout.gems), gems + 10);
  assert.equal(h.u32(h.ptr('GetFellowBookInfoByID', id) + 16), 1);
  const texts = h.texts();
  assert.ok(texts.includes('You reached the Maximum Level!'));
  assert.ok(texts.includes('Sent.'));
  assert.deepEqual(h.net.unanswered, []);
  assert.deepEqual(h.stubs, []);
});
