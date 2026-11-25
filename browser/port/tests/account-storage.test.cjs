const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
test('failed account storage reports an error without reloading or clearing the guest', async () => {
  class Element {
    constructor(tag) { this.tagName = tag; this.children = []; }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.children.push(child); }
    addEventListener() {} setAttribute() {} focus() {} remove() {}
  }
  const body = new Element('body');
  const persisted = new Map([['kns:prop:web_did', 'web-existing-guest'], ['kns:prop:hub_guest_uid', '905123']]);
  const localStorage = {
    getItem: key => persisted.get(key) ?? null,
    setItem() { throw new Error('quota'); },
    removeItem: key => persisted.delete(key),
  };
  let reloads = 0;
  const context = { window: {}, localStorage, location: {reload() { reloads++; }}, document: {body, createElement: tag => new Element(tag)},
    fetch: async () => ({ok: true, json: async () => ({login_id:'example', did:'web-account', guest_uid:'906123'})}) };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../account.js'), 'utf8'), context);
  context.window.KnsAccount.open('login');
  const flatten = node => [node, ...((node && node.children) || []).flatMap(flatten)];
  const nodes = flatten(body), form = nodes.find(n => n.tagName === 'form');
  const inputs = nodes.filter(n => n.tagName === 'input');
  inputs.find(n => n.name === 'username').value = 'example';
  inputs.find(n => n.name === 'password').value = 'test-password';
  await form.onsubmit({preventDefault() {}});
  assert.equal(reloads, 0);
  assert.equal(persisted.get('kns:prop:web_did'), 'web-existing-guest');
  assert.equal(persisted.get('kns:prop:hub_guest_uid'), '905123');
  assert.match(nodes.find(n => n.className === 'acct-error').textContent, /Browser storage is unavailable/);
});
test('successful login keeps new identity and removes old guest cache', async () => {
  class Element {
    constructor(tag) { this.tagName = tag; this.children = []; }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.children.push(child); }
    addEventListener() {} setAttribute() {} focus() {} remove() {}
  }
  const body = new Element('body');
  const storage = {'kns:prop:web_did': 'web-old', 'kns:prop:hub_guest_uid': '905123', 'kns:file:old': 'cached'};
  Object.defineProperties(storage, {
    getItem: {value(key) { return this[key] ?? null; }},
    setItem: {value(key, value) { this[key] = String(value); }},
    removeItem: {value(key) { delete this[key]; }},
  });
  let reloads = 0;
  const context = {window: {}, localStorage: storage, location: {reload() { reloads++; }}, document: {body, createElement: tag => new Element(tag)},
    fetch: async () => ({ok: true, json: async () => ({login_id:'example', did:'web-account', guest_uid:'906123'})})};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../account.js'), 'utf8'), context);
  context.window.KnsAccount.open('login');
  const flatten = node => [node, ...((node && node.children) || []).flatMap(flatten)];
  const nodes = flatten(body), form = nodes.find(n => n.tagName === 'form');
  nodes.find(n => n.name === 'username').value = 'example';
  nodes.find(n => n.name === 'password').value = 'test-password';
  await form.onsubmit({preventDefault() {}});
  assert.equal(reloads, 1);
  assert.equal(storage['kns:prop:web_did'], 'web-account');
  assert.equal(storage['kns:prop:account_id'], 'example');
  assert.equal(storage['kns:prop:account_guest'], '906123');
  assert.equal(storage['kns:prop:hub_guest_uid'], undefined);
  assert.equal(storage['kns:file:old'], undefined);
});
test('the native save survives sign-out and goes only when another player signs in', async () => {
  class Element {
    constructor(tag) { this.tagName = tag; this.children = []; }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.children.push(child); }
    addEventListener() {} setAttribute() {} focus() {} remove() {}
  }
  const body = new Element('body');
  const storage = {'kns:prop:web_did': 'web-a', 'kns:file:save': 'guides-done'};
  Object.defineProperties(storage, {
    getItem: {value(key) { return this[key] ?? null; }},
    setItem: {value(key, value) { this[key] = String(value); }},
    removeItem: {value(key) { delete this[key]; }},
  });
  let did = 'web-a';
  const context = {window: {}, localStorage: storage, location: {reload() {}}, document: {body, createElement: tag => new Element(tag)},
    fetch: async () => ({ok: true, json: async () => ({login_id: did, did, guest_uid: '1'})})};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../account.js'), 'utf8'), context);
  const login = async () => {
    body.children.length = 0;
    context.window.KnsAccount.open('login');
    const flatten = node => [node, ...((node && node.children) || []).flatMap(flatten)];
    const nodes = flatten(body), form = nodes.find(n => n.tagName === 'form');
    nodes.find(n => n.name === 'username').value = 'user';
    nodes.find(n => n.name === 'password').value = 'test-password';
    await form.onsubmit({preventDefault() {}});
  };
  await login();
  assert.equal(storage['kns:save_owner'], 'web-a');
  context.window.KnsAccount.signOut();
  assert.equal(storage['kns:prop:web_did'], undefined);
  assert.equal(storage['kns:file:save'], 'guides-done');
  await login();
  assert.equal(storage['kns:file:save'], 'guides-done');
  context.window.KnsAccount.signOut();
  did = 'web-b';
  await login();
  assert.equal(storage['kns:file:save'], undefined);
  assert.equal(storage['kns:save_owner'], 'web-b');
});

function harness(storage, fetch) {
  class Element {
    constructor(tag) { this.tagName = tag; this.children = []; }
    append(...children) { this.children.push(...children); }
    appendChild(child) { this.children.push(child); }
    addEventListener() {} setAttribute() {} focus() {} remove() {}
  }
  Object.defineProperties(storage, {
    getItem: {value(key) { return this[key] ?? null; }},
    setItem: {value(key, value) { this[key] = String(value); }},
    removeItem: {value(key) { delete this[key]; }},
  });
  const body = new Element('body');
  const calls = [];
  const context = {window: {KNS_SERVER: 'https://example.test/kns/api'}, localStorage: storage, location: {reload() {}},
    document: {body, createElement: tag => new Element(tag)},
    fetch: async (url, init) => { calls.push({url, body: init && init.body ? JSON.parse(init.body) : null}); return fetch(url); }};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../account.js'), 'utf8'), context);
  const flatten = node => [node, ...((node && node.children) || []).flatMap(flatten)];
  return {context, body, calls, nodes: () => flatten(body)};
}

test('login stores the server sign-in token, never the password', async () => {
  const storage = {};
  const h = harness(storage, async () => ({ok: true, json: async () => ({login_id: 'example', did: 'web-account', guest_uid: '906123', token: 'tok-1'})}));
  await h.context.window.KnsAccount.ready;
  h.context.window.KnsAccount.open('login');
  const nodes = h.nodes();
  nodes.find(n => n.name === 'username').value = 'example';
  nodes.find(n => n.name === 'password').value = 'secret-password';
  await nodes.find(n => n.tagName === 'form').onsubmit({preventDefault() {}});
  const login = h.calls.find(c => c.url.endsWith('/account/login'));
  assert.equal(login.url, 'https://example.test/kns/api/account/login');
  assert.deepEqual(JSON.parse(storage.kns_session), {token: 'tok-1', login_id: 'example'});
  assert.ok(!JSON.stringify(storage).includes('secret-password'));
});

test('a stored sign-in restores the account at start; an expired one signs out', async () => {
  const live = {kns_session: JSON.stringify({token: 'tok-live', login_id: 'example'})};
  const h = harness(live, async url => url.endsWith('/account/session')
    ? {ok: true, json: async () => ({login_id: 'example', did: 'web-account', guest_uid: '906123', token: 'tok-live'})}
    : {ok: true, json: async () => ({progress_epoch: 0})});
  await h.context.window.KnsAccount.ready;
  assert.deepEqual(h.calls.find(c => c.url.endsWith('/account/session')).body, {token: 'tok-live'});
  assert.equal(live['kns:prop:web_did'], 'web-account');
  assert.equal(live['kns:prop:account_id'], 'example');

  const expired = {kns_session: JSON.stringify({token: 'tok-old', login_id: 'example'}), 'kns:prop:account_id': 'example', 'kns:prop:web_did': 'web-account'};
  const e = harness(expired, async url => url.endsWith('/account/session')
    ? {ok: false, status: 401, json: async () => ({error: 'expired', code: 'session_expired'})}
    : {ok: true, json: async () => ({progress_epoch: 0})});
  await e.context.window.KnsAccount.ready;
  assert.equal(expired.kns_session, undefined);
  assert.equal(expired['kns:prop:account_id'], undefined);
  assert.equal(expired['kns:prop:web_did'], undefined);
});

test('sign-out revokes the token on the server and forgets it', async () => {
  const storage = {kns_session: JSON.stringify({token: 'tok-out', login_id: 'example'}), 'kns:prop:account_id': 'example'};
  const h = harness(storage, async () => ({ok: true, json: async () => ({})}));
  h.context.window.KnsAccount.signOut();
  assert.deepEqual(h.calls.find(c => c.url.endsWith('/account/logout')).body, {token: 'tok-out'});
  assert.equal(storage.kns_session, undefined);
  assert.equal(storage['kns:prop:account_id'], undefined);
});

test('the signed-in panel changes the password with the stored token', async () => {
  const storage = {kns_session: JSON.stringify({token: 'tok-pw', login_id: 'example'}), 'kns:prop:account_id': 'example'};
  const h = harness(storage, async () => ({ok: true, json: async () => ({changed: true})}));
  h.context.window.KnsAccount.open();
  const nodes = h.nodes();
  const form = nodes.find(n => n.tagName === 'form' && n.className.includes('acct-password'));
  nodes.find(n => n.name === 'current-password').value = 'old-password';
  nodes.find(n => n.name === 'new-password').value = 'new-password-1';
  nodes.find(n => n.name === 'new-password2').value = 'different-1';
  await form.onsubmit({preventDefault() {}});
  assert.equal(h.calls.filter(c => c.url.endsWith('/account/password')).length, 0);
  nodes.find(n => n.name === 'new-password2').value = 'new-password-1';
  await form.onsubmit({preventDefault() {}});
  assert.deepEqual(h.calls.find(c => c.url.endsWith('/account/password')).body,
    {token: 'tok-pw', password: 'old-password', new_password: 'new-password-1'});
  assert.equal(nodes.find(n => n.role === 'status').textContent, 'Password changed.');
});

test('dragging from inside the register popup onto the backdrop does not close it', () => {
  const h = harness({}, async () => ({ok: true, json: async () => ({})}));
  let cancelled = 0;
  h.context.window.KnsAccount.open('register', () => { cancelled++; });
  const nodes = h.nodes();
  const backdrop = nodes.find(n => n.className === 'acct-back');
  const input = nodes.find(n => n.name === 'username');

  // Press inside input, drag outside onto backdrop and release: browser fires click on backdrop.
  backdrop.onpointerdown({target: input});
  backdrop.onpointerup({target: backdrop});
  backdrop.onclick({target: backdrop});
  assert.equal(cancelled, 0);

  // Press on backdrop, drag into dialog and release: browser fires click on backdrop.
  backdrop.onpointerdown({target: backdrop});
  backdrop.onpointerup({target: input});
  backdrop.onclick({target: backdrop});
  assert.equal(cancelled, 0);

  // Direct click on backdrop still closes the popup.
  backdrop.onpointerdown({target: backdrop});
  backdrop.onpointerup({target: backdrop});
  backdrop.onclick({target: backdrop});
  assert.equal(cancelled, 1);
});
