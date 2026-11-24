// Headless test run under Node: boots the browser build's libgame.so and logs what it asks for.
//   node node_boot.js [frames]        reads browser/kns.html (KNS_ELF, KNS_INDEX) and browser/kns_data
const fs = require('fs');
const path = require('path');
global.ApkCPU = require('../../apk-cpu-engine/cpu.js');
global.ApkRuntime = require('../../apk-cpu-engine/runtime.js');
global.ApkJava = require('../../apk-cpu-engine/jni.js');
const { boot } = require('./game.js');
const { makeNullGL } = require('../../apk-cpu-engine/gl_null.js');
const shim = require('./java_shim.js');

const BROWSER = path.resolve(__dirname, '..');
const html = fs.readFileSync(path.join(BROWSER, 'kns.html'), 'utf8');
const INDEX = JSON.parse(html.match(/window\.KNS_INDEX=(\{.*?\});/s)[1]);
const readAsset = name => {
  const text = fs.readFileSync(path.join(BROWSER, 'kns_data', INDEX[name][1] + '.js'), 'ascii');
  return new Uint8Array(Buffer.from(JSON.parse('[' + text.slice('KNS_ASSET('.length, -2) + ']')[1], 'base64'));
};

const files = new Map();
const vfs = {
  normalize(p) { return p.replace(/\/+/g, '/'); },
  assetName(p) {
    const base = p.split('/').pop();
    if (INDEX[base + '.jpg']) return base + '.jpg';
    if (INDEX[base]) return base;
    return null;
  },
  open(p, creat, trunc) {
    p = this.normalize(p);
    let f = files.get(p);
    if (!f) {
      const a = this.assetName(p);
      if (a && !creat) {
        const data = readAsset(a);
        f = { data, size: data.length };
        files.set(p, f);
      } else if (creat) {
        f = { data: new Uint8Array(1024), size: 0 };
        files.set(p, f);
      } else return null;
    }
    if (trunc) f.size = 0;
    return f;
  },
  ready() { return true; },
  write(f, pos, bytes) {
    if (pos + bytes.length > f.data.length) {
      const n = new Uint8Array(Math.max(pos + bytes.length, f.data.length * 2));
      n.set(f.data);
      f.data = n;
    }
    f.data.set(bytes, pos);
    f.size = Math.max(f.size, pos + bytes.length);
  },
  commit() {},
  remove(p) { return files.delete(this.normalize(p)); },
  rename(a, b) {
    const f = files.get(this.normalize(a));
    if (!f) return false;
    files.delete(this.normalize(a));
    files.set(this.normalize(b), f);
    return true;
  },
  mkdir() { return true; },
  stat(p) {
    p = this.normalize(p);
    if (files.has(p)) return files.get(p);
    const a = this.assetName(p);
    if (a) return { size: INDEX[a][0] };
    if (/\/$|files|cache|sdcard|1\.4\.0$/.test(p)) return { size: 0, isDir: true };
    return null;
  },
  list() { return []; },
};

module.exports = { vfs, files, html, INDEX };
if (require.main !== module) return;

const frames = +process.argv[2] || 60;
const elf = new Uint8Array(Buffer.from(html.match(/window\.KNS_ELF="([A-Za-z0-9+/=]+)";/)[1], 'base64'));
const counts = {};
const gl = makeNullGL(counts);
const game = boot({
  elf, vfs, gl,
  installJava: shim.installJava,
  startup: shim.startup,
  log: s => console.log(String(s).replace(/\n$/, '')),
  screen: { w: 2048, h: 1184 },
});
try {
  game.start();
  const t0 = Date.now();
  // The game's frame timer only comes due if time passes between calls.
  const nap = new Int32Array(new SharedArrayBuffer(4));
  for (let i = 0; i < frames; i++) { game.frame(2000); Atomics.wait(nap, 0, 0, 16); }
  console.log('frames done', Date.now() - t0, 'ms', game.rt.cpu.count, 'instr', game.rt.cpu.compiled, 'blocks');
} catch (e) {
  console.log('CRASH', e.stack);
  game.dumpState();
}
console.log('missing imports:', [...game.rt.missing].join(' '));
console.log('gl calls:', JSON.stringify(counts));
