const assert = require('node:assert/strict');
const test = require('node:test');
const { installJava, PKG } = require('../java_shim.js');

test('a new authenticated game session wakes session listeners once per identity', async () => {
  const previous = { fetch: global.fetch, dispatchEvent: global.dispatchEvent, KnsSession: global.KnsSession };
  const events = [];
  try {
    global.KnsSession = undefined;
    global.dispatchEvent = event => { events.push(event.type); };
    global.fetch = async () => ({ ok: true, text: async () => '{}' });
    const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
    installJava({ log() {}, callJava() {} }, java, { w: 2048, h: 1184 }, { sym: name => name, cls: name => name });
    const network = java.classes[PKG + '/CUserDefined'];
    const send = { fields: { strSendURL: 'http://original/1mhero/gateway.php', strSendMsg: '{}', i32SendProtocol: 60 } };
    for (const [user, key, count] of [[0, '', 0], [42, 'first', 1], [42, 'first', 1], [42, 'renewed', 2], [99, 'other', 3]]) {
      network.SetHeaderInfo({ fields: { i32UserNo: user, strSessionKey: key } });
      network.RequestServer(send);
      assert.equal(events.length, count);
      await new Promise(resolve => setImmediate(resolve));
    }
    assert.deepEqual(events, ['kns-session-changed', 'kns-session-changed', 'kns-session-changed']);
  } finally { Object.assign(global, previous); }
});

test('gateway replies retain the original protocol when native send fields are reused', async () => {
  const oldFetch = global.fetch;
  const pending = [], callbacks = [], errors = [];
  global.fetch = (url, options) => new Promise(resolve => pending.push({ url, options, resolve }));
  try {
    const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
    const rt = { log: message => errors.push(message), callJava: (name, args) => callbacks.push({ name, args }) };
    installJava(rt, java, { w: 2048, h: 1184 }, { sym: name => name, cls: name => name });
    const network = java.classes[PKG + '/CUserDefined'];
    const send = { fields: { strSendURL: 'http://original/1mhero/gateway.php', strSendMsg: 'request-801', i32SendProtocol: 801 } };
    network.RequestServer(send);
    Object.assign(send.fields, { strSendMsg: 'request-804', i32SendProtocol: 804 });
    network.RequestServer(send);
    pending[1].resolve({ ok: true, text: async () => 'response-804' });
    pending[0].resolve({ ok: true, text: async () => 'response-801' });
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(errors, []);
    const responses = callbacks.filter(row => row.name.endsWith('nativeResponseServer'));
    assert.equal(responses.length, 2);
    const protocols = Object.fromEntries(responses.map(row => [new TextDecoder().decode(row.args[2]), row.args[4]]));
    assert.deepEqual(protocols, { 'response-804': 804, 'response-801': 801 });
    assert.deepEqual(pending.map(row => row.options.body), ['request-801', 'request-804']);
    assert.ok(pending.every(row => row.url.startsWith('http://127.0.0.1:8081/')));
  } finally {
    global.fetch = oldFetch;
  }
});

test('arena entry and settlement queue authoritative goods after native response, excluding refused battles', async () => {
  const oldFetch = global.fetch, events = [];
  try {
    let reply;
    global.fetch = async () => ({ok: true, text: async () => JSON.stringify(reply)});
    const java = {classes: {}, env: 1, ref: value => value, bytes: value => value};
    const rt = {log() {}, callJava: name => events.push(name)};
    installJava(rt, java, {w: 2048, h: 1184}, {
      sym: name => name, cls: name => name, arenaGoods: body => events.push(body.update_item_etc_info[0].count),
    });
    const network = java.classes[PKG + '/CUserDefined'];
    for (const [protocol, errno, expected] of [[402, 0, 9], [403, 0, 9], [402, 1, null]]) {
      events.length = 0;
      reply = {errno, update_item_etc_info: [{item_id: 1900017, count: 9, user_last_stamina_gain_time: 1794000000}]};
      network.RequestServer({fields: {strSendURL: 'http://original/1mhero/gateway.php', strSendMsg: 'arena', i32SendProtocol: protocol}});
      await new Promise(resolve => setImmediate(resolve));
      const response = events.findIndex(name => typeof name === 'string' && name.endsWith('nativeResponseServer'));
      assert.ok(response >= 0);
      if (expected === null) assert.equal(events.filter(value => typeof value === 'number').length, 0);
      else assert.equal(events[response + 1], expected);
    }
  } finally { global.fetch = oldFetch; }
});

test('stage responses queue achievement totals after their native callback', async () => {
  const oldFetch = global.fetch, events = [];
  try {
    global.fetch = async () => ({ ok: true, text: async () => JSON.stringify({ errno: 0, update_achievement_info: [{ type: 40006, sub_type: 0, count: 12 }] }) });
    const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
    installJava({ log() {}, callJava: name => events.push(name) }, java, { w: 2048, h: 1184 }, {
      sym: name => name, cls: name => name, achievements: body => events.push(body.update_achievement_info[0].count),
    });
    java.classes[PKG + '/CUserDefined'].RequestServer({ fields: { strSendURL: 'http://original/1mhero/gateway.php', strSendMsg: 'stage', i32SendProtocol: 72 } });
    await new Promise(resolve => setImmediate(resolve));
    const response = events.findIndex(value => typeof value === 'string' && value.endsWith('nativeResponseServer'));
    assert.equal(events[response + 1], 12);
    assert.ok(events[response + 2].endsWith('nativeSetNetState'));
  } finally { global.fetch = oldFetch; }
});

// The real gateway answers with base64(AES-128-CBC(JSON)) (server-dotnet GameGatewayCipher).
const nodeCrypto = require('node:crypto');
const GATEWAY_KEY = Buffer.from('1m!1HUB2@hero#Re'), GATEWAY_IV = Buffer.from('de295106d3872f949783942fdc9f01e2', 'hex');
const encryptReply = text => { const c = nodeCrypto.createCipheriv('aes-128-cbc', GATEWAY_KEY, GATEWAY_IV); return Buffer.concat([c.update(text, 'utf8'), c.final()]).toString('base64'); };
const decryptReply = b64 => { const d = nodeCrypto.createDecipheriv('aes-128-cbc', GATEWAY_KEY, GATEWAY_IV); return Buffer.concat([d.update(Buffer.from(b64, 'base64')), d.final()]).toString('utf8'); };
const settle = async (n = 5) => { for (let i = 0; i < n; i++) await new Promise(resolve => setImmediate(resolve)); };

test('encrypted gateway replies reach the achievement and Arena sync; native still gets the original text', async () => {
  const previous = { fetch: global.fetch, KnsGateway: global.KnsGateway, KnsAchievementSync: global.KnsAchievementSync };
  const events = [];
  try {
    let wire;
    global.fetch = async () => ({ ok: true, text: async () => wire });
    // achievement-sync.js provides KnsGateway.decode (WebCrypto); a page hook that throws must not look like a disconnect.
    global.KnsGateway = { decode: async text => JSON.parse(decryptReply(text)) };
    global.KnsAchievementSync = { control: { onGateway() { throw new Error('page hook broke'); } } };
    const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
    const logs = [];
    installJava({ log: s => logs.push(s), callJava: (name, args) => events.push([name, args]) }, java, { w: 2048, h: 1184 }, {
      sym: name => name, cls: name => name,
      achievements: body => events.push(['achievements', body.update_achievement_info?.[0]?.count]),
      arenaGoods: body => events.push(['arena', body.update_item_etc_info[0].count]),
    });
    const network = java.classes[PKG + '/CUserDefined'];
    const send = protocol => network.RequestServer({ fields: { strSendURL: 'http://original/1mhero/gateway.php', strSendMsg: 'x', i32SendProtocol: protocol } });
    const kinds = () => events.map(([name, args]) => name.endsWith('nativeResponseServer') ? 'response'
      : name.endsWith('nativeSetNetState') ? 'state' + args[2] : name);

    wire = encryptReply(JSON.stringify({ errno: 0, update_achievement_info: [{ type: 40006, sub_type: 0, count: 12 }] }));
    send(72); await settle();
    assert.deepEqual(kinds(), ['state6', 'response', 'achievements', 'state8']);
    assert.equal(events.find(e => e[0] === 'achievements')[1], 12);
    assert.equal(new TextDecoder().decode(events.find(e => e[0].endsWith('nativeResponseServer'))[1][2]), wire);

    events.length = 0;
    wire = encryptReply(JSON.stringify({ errno: 0, update_item_etc_info: [{ item_id: 1900017, count: 9, user_last_stamina_gain_time: 1794000000 }] }));
    send(403); await settle();
    assert.deepEqual(kinds(), ['state6', 'response', 'achievements', 'arena', 'state8']);
    assert.equal(events.find(e => e[0] === 'arena')[1], 9);
    assert.ok(logs.some(s => /achievement list hook failed/.test(s)));
    assert.ok(!kinds().includes('state10'));

    // Undecodable text still reaches native, and nothing else runs.
    events.length = 0;
    wire = 'not-a-reply';
    send(403); await settle();
    assert.deepEqual(kinds(), ['state6', 'response', 'state8']);
  } finally { Object.assign(global, previous); }
});

test('a refused guest creation is not stored; a login without a result keeps the server error code', async () => {
  const previous = { fetch: global.fetch, localStorage: global.localStorage };
  const stored = new Map();
  global.localStorage = { get length() { return stored.size; }, key: i => [...stored.keys()][i],
    getItem: k => stored.get(k) ?? null, setItem: (k, v) => stored.set(k, String(v)), removeItem: k => stored.delete(k) };
  try {
    let reply;
    global.fetch = async () => ({ ok: true, text: async () => JSON.stringify(reply) });
    const callbacks = [];
    const java = { classes: {}, env: 1, ref: value => value, bytes: value => value, jstr: value => value };
    installJava({ log() {}, callJava: (name, args) => { if (name.endsWith('hubCallbackWithJSON')) callbacks.push(JSON.parse(args[2])); } },
      java, { w: 2048, h: 1184 }, { sym: name => name, cls: name => name });
    const hub = java.classes['com/com2us/peppermint/HubBridge'];

    reply = { error_code: 7, error_detail: 'busy' };
    hub.asyncRequest('guest/create', '{}', 1, 2); await settle();
    assert.equal(stored.get('kns:prop:hub_guest_uid'), undefined);
    assert.deepEqual(callbacks.pop(), { error_code: 7, error_detail: 'busy' });

    reply = { error_code: 0 };
    hub.asyncRequest('guest/create', '{}', 1, 2); await settle();
    assert.equal(stored.get('kns:prop:hub_guest_uid'), undefined);
    assert.equal(callbacks.pop().error_code, 1);

    reply = { error_code: 0, type: 'guest/create', did: 'server-did', guest_uid: '905001' };
    hub.asyncRequest('guest/create', '{}', 1, 2); await settle();
    assert.equal(stored.get('kns:prop:hub_guest_uid'), '905001');
    const created = callbacks.pop();
    assert.equal(created.error_code, 0);
    assert.equal(created.guest_uid, '905001');

    stored.set('kns:prop:account_id', 'example');
    reply = { error_code: 9, error_detail: 'locked' };
    hub.auth(1, 2); await settle();
    assert.deepEqual(callbacks.pop(), { error_code: 9, error_detail: 'locked' });
    reply = { error_code: 0, uid: '77', session_key: 'sk_77', result: { did: 'web-x' } };
    hub.auth(1, 2); await settle();
    assert.deepEqual(callbacks.pop(), { error_code: 0, uid: '77', did: 'web-x', sessionkey: 'sk_77' });
  } finally { Object.assign(global, previous); }
});

test('getBase64encode encodes UTF-8 like Android, unchanged for ASCII', () => {
  const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
  installJava({ log() {}, callJava() {} }, java, { w: 2048, h: 1184 }, { sym: name => name, cls: name => name });
  const encode = java.classes[PKG + '/CUserDefined'].getBase64encode;
  for (const text of ['', 'abc123', 'user=42&key=sk_77', 'Café', '기사단', 'emoji 🛡️'])
    assert.equal(encode(text), Buffer.from(text, 'utf8').toString('base64'), text);
  assert.equal(encode('abc123'), btoa('abc123'));
});
