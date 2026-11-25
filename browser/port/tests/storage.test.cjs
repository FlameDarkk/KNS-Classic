const assert = require('node:assert/strict');
const test = require('node:test');
const { installJava } = require('../java_shim.js');
test('preferences keep their last value when writes fail but reads still work', () => {
  const previous = global.localStorage;
  const persisted = new Map([['knsclassic:prop:device', 'old-device']]);
  global.localStorage = {
    get length() { return persisted.size; }, key: i => [...persisted.keys()][i],
    getItem: key => persisted.get(key) ?? null,
    setItem() { throw new Error('QuotaExceededError'); },
    removeItem() { throw new Error('storage unavailable'); },
  };
  try {
    const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
    const rt = { log() {}, callJava() {} };
    installJava(rt, java, {w: 2048, h: 1184}, {sym: name => name, cls: name => name});
    const props = java.classes['com/com2us/wrapper/utility/CUtility'];
    const bytes = text => new TextEncoder().encode(text);
    const read = key => { const result = props.getAppProperty(bytes(key)); return result === null ? null : new TextDecoder().decode(result); };
    props.setAppProperty(bytes('device'), bytes('new-device'));
    assert.equal(read('device'), 'new-device');
    props.setAppProperty(bytes('new'), bytes('session-only'));
    assert.equal(read('new'), 'session-only');
    props.removeAppProperty(bytes('device'));
    assert.equal(read('device'), null);
  } finally { global.localStorage = previous; }
});

// web.js vfs.rename: the game saves through a temp file renamed over the real one.
function loadVfs(localStorage) {
  const fs = require('node:fs'), vm = require('node:vm'), path = require('node:path');
  const source = fs.readFileSync(path.join(__dirname, '../web.js'), 'utf8');
  const section = (from, to) => { const at = source.indexOf(from); return source.slice(at, source.indexOf(to, at)); };
  const code = section('// ---------- files ----------', '// ---------- settings')
    + section('const toB64 = bytes', '// ---------- text: the font') + '\n({ vfs, files })';
  const logs = [];
  const { vfs, files } = vm.runInNewContext(code, { window: { KNS_INDEX: {} }, localStorage, btoa, log: s => logs.push(s), assetName: () => null });
  // A written temp file, as open/write/close leave it.
  const temp = (p, text) => {
    const data = new TextEncoder().encode(text), f = { path: p, data, size: data.length };
    files.set(p, f); vfs.commit(f);
    return f;
  };
  return { vfs, files, logs, temp };
}
const b64 = text => Buffer.from(text).toString('base64');
function storage() {
  const stored = new Map();
  return {
    stored,
    getItem: k => stored.get(k) ?? null,
    setItem: (k, v) => { stored.set(k, String(v)); },
    removeItem: k => { stored.delete(k); },
  };
}

test('a save renamed over the real file replaces it and drops the temp copy', () => {
  const ls = storage();
  ls.stored.set('knsclassic:file:/save.dat', b64('OLD'));
  const { vfs, files, temp } = loadVfs(ls);
  temp('/save.tmp', 'NEW');
  assert.equal(vfs.rename('/save.tmp', '/save.dat'), true);
  assert.equal(ls.stored.get('knsclassic:file:/save.dat'), b64('NEW'));
  assert.equal(ls.stored.has('knsclassic:file:/save.tmp'), false);
  assert.equal(files.has('/save.tmp'), false);
  assert.equal(files.get('/save.dat').path, '/save.dat');
});

test('a nearly full storage frees the temp copy and still stores the renamed save', () => {
  const key = 'knsclassic:file:/save.dat', tmp = 'knsclassic:file:/save.tmp';
  const ls = storage();
  ls.stored.set(key, b64('OLD-SAVE-VALUE'));
  const { vfs, logs, temp } = loadVfs(ls);
  temp('/save.tmp', 'NEW-SAVE-VALUE');
  // Room for one copy of the new save only: writing the new name first fails until the temp copy is gone.
  const limit = key.length + b64('NEW-SAVE-VALUE').length + tmp.length + 4;
  ls.setItem = (k, v) => {
    const before = ls.stored.get(k);
    ls.stored.set(k, String(v));
    const used = [...ls.stored].reduce((n, [a, b]) => n + a.length + b.length, 0);
    if (used > limit) { if (before === undefined) ls.stored.delete(k); else ls.stored.set(k, before); throw new Error('QuotaExceededError'); }
  };
  vfs.rename('/save.tmp', '/save.dat');
  assert.equal(ls.stored.get(key), b64('NEW-SAVE-VALUE'));
  assert.equal(ls.stored.has(tmp), false);
  assert.deepEqual(logs, []);
});

test('a refused rename keeps the previous save and says so in the log', () => {
  const ls = storage();
  ls.stored.set('knsclassic:file:/save.dat', b64('OLD'));
  const { vfs, logs, temp } = loadVfs(ls);
  temp('/save.tmp', 'NEW');
  ls.setItem = () => { throw new Error('QuotaExceededError'); };
  vfs.rename('/save.tmp', '/save.dat');
  assert.equal(ls.stored.get('knsclassic:file:/save.dat'), b64('OLD'));
  assert.equal(logs.length, 1);
  assert.match(logs[0], /could not be stored/);
  // Renaming a file onto itself keeps it.
  ls.setItem = (k, v) => ls.stored.set(k, v);
  const { vfs: same, temp: tempSame } = loadVfs(ls);
  tempSame('/keep.dat', 'KEEP');
  same.rename('/keep.dat', '/keep.dat');
  assert.equal(ls.stored.get('knsclassic:file:/keep.dat'), b64('KEEP'));
});
