// Deterministic headless run of the browser build's libgame.so: JIT speed and a guest-state hash.
//   node browser/port/bench_jit.js [frames] [--json]      (KNS_JIT_REGIONS=0: block JIT only)
// The clock advances 34 ms per frame and Math.random is seeded, so two runs of the same engine
// produce the same hashes. A changed JIT must produce the same hashes as the old one.
const path = require('path');
global.ApkCPU = require('../../apk-cpu-engine/cpu.js');
// KNS_JIT_REGIONS=0 runs the plain block JIT, the reference the region JIT must match.
if (process.env.KNS_JIT_REGIONS === '0') global.ApkCPU.regions = false;
global.ApkRuntime = require('../../apk-cpu-engine/runtime.js');
global.ApkJava = require('../../apk-cpu-engine/jni.js');

let fake = 1700000000000;
let seed = 12345;
Date.now = () => fake;
Math.random = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x80000000; };
const realNow = performance.now.bind(performance);

const { vfs } = require('./node_boot.js');
const { boot } = require('./game.js');
const { makeNullGL } = require('../../apk-cpu-engine/gl_null.js');
const shim = require('./java_shim.js');
const html = require('fs').readFileSync(path.resolve(__dirname, '..', 'kns.html'), 'utf8');

const frames = +process.argv.find(a => /^\d+$/.test(a)) || 300;
const json = process.argv.includes('--json');
const elf = new Uint8Array(Buffer.from(html.match(/window\.KNS_ELF="([A-Za-z0-9+/=]+)";/)[1], 'base64'));
const counts = {};
const game = boot({
  elf, vfs, gl: makeNullGL(counts),
  installJava: shim.installJava,
  startup: shim.startup,
  log: () => {},
  screen: { w: 2048, h: 1184 },
  pacedByPage: true,
});
const rt = game.rt, cpu = rt.cpu;

// FNV-1a over registers, flags and guest memory up to the heap end.
function stateHash() {
  let h = 0x811c9dc5;
  const mix = v => { h = Math.imul(h ^ (v | 0), 0x01000193); };
  for (const t of rt.threads) { for (const v of t === rt.cur ? cpu.r : t.r) mix(v); }
  for (const v of cpu.f) mix(v);
  const u = cpu.m32, end = Math.min(u.length, (rt.brk >>> 2) + 1);
  for (let i = 0; i < end; i++) mix(u[i]);
  return (h >>> 0).toString(16).padStart(8, '0');
}

let runMs = 0;
const run = cpu.run.bind(cpu);
cpu.run = budget => { const t = realNow(); try { return run(budget); } finally { runMs += realNow() - t; } };

const t0 = realNow();
game.start();
const startCount = cpu.count;
const hashes = [];
// The second half shows steady speed, after the blocks it needs are compiled and optimized.
let halfMs = 0, halfInstr = 0;
for (let i = 0; i < frames; i++) {
  if (i === frames >> 1) { halfMs = runMs; halfInstr = cpu.count; }
  fake += 34;
  rt.clock = fake;
  game.frame(1e9);
  if ((i + 1) % 100 === 0) hashes.push(stateHash());
}
const total = realNow() - t0;
const res = {
  frames, instr: cpu.count, frameInstr: cpu.count - startCount, blocks: cpu.compiled,
  runMs: Math.round(runMs), totalMs: Math.round(total),
  mips: +(cpu.count / runMs / 1000).toFixed(1),
  steadyMips: +((cpu.count - halfInstr) / (runMs - halfMs) / 1000).toFixed(1), draws: counts.glDrawArrays || 0, hashes,
};
console.log(json ? JSON.stringify(res) : res);
