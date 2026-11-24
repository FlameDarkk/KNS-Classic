// Boots libgame.so: what the Android activity and its Java wrappers do at startup, in JS.
(function (root) {
'use strict';
const Runtime = root.ApkRuntime || require('../../apk-cpu-engine/runtime.js');
const Java = root.ApkJava || require('../../apk-cpu-engine/jni.js');
const NativeHooks = root.KnsNativeHooks || require('./native_hooks.js');
const { w2f } = Runtime.helpers;

const GL_ARGS = {
  glAlphaFunc: 2, glBindBuffer: 2, glBindTexture: 2, glBlendFunc: 2, glBufferData: 4, glClear: 1, glClearColor: 4,
  glClearColorx: 4, glClientActiveTexture: 1, glActiveTexture: 1, glColor4x: 4, glColorMask: 4, glColorPointer: 4,
  glCompressedTexImage2D: 8, glCopyTexImage2D: 8, glCopyTexSubImage2D: 8, glDeleteBuffers: 2, glDeleteTextures: 2,
  glDepthMask: 1, glDisable: 1, glDisableClientState: 1, glDrawArrays: 3, glDrawElements: 4, glEnable: 1,
  glEnableClientState: 1, glFinish: 0, glFrustumx: 6, glGenBuffers: 2, glGenTextures: 2, glGetError: 0, glGetFixedv: 2,
  glGetFloatv: 2, glGetIntegerv: 2, glGetPointerv: 2, glGetTexParameteriv: 3, glIsEnabled: 1, glIsTexture: 1,
  glLightxv: 3, glLineWidthx: 1, glLoadIdentity: 0, glLoadMatrixf: 1, glLoadMatrixx: 1, glMatrixMode: 1, glMultMatrixx: 1,
  glNormalPointer: 3, glOrthox: 6, glPointSizex: 1, glPopMatrix: 0, glPushMatrix: 0, glRotatex: 4, glScalef: 3,
  glScalex: 3, glScissor: 4, glShadeModel: 1, glStencilMask: 1, glTexCoordPointer: 4, glTexEnvf: 3, glTexEnvx: 3,
  glTexEnvxv: 3, glTexImage2D: 9, glTexParameterf: 3, glTexParameteri: 3, glTexParameterx: 3, glTexSubImage2D: 9,
  glTranslatef: 3, glTranslatex: 3, glVertexPointer: 4, glViewport: 4,
};

function boot(opts) {
  // The chat client's one TCP connection becomes a WebSocket to the server's /chat.
  const socketUrl = () => {
    const base = new URL(root.KNS_SERVER || 'http://127.0.0.1:8081', root.location && root.location.protocol !== 'file:' ? root.location.href : undefined);
    return base.href.replace(/^http/, 'ws').replace(/\/$/, '') + '/chat';
  };
  const rt = new Runtime({ log: opts.log, vfs: opts.vfs, socketUrl });
  const cpu = rt.cpu, r = cpu.r;
  const gl = opts.gl;
  const screen = opts.screen;
  rt.brk = 0x08000000;
  for (const [name, argc] of Object.entries(GL_ARGS)) {
    const params = Array.from({ length: argc }, (_, i) => 'a' + i);
    const body = `return G.${name}(${params.join(',')});`;
    rt.def(name, new Function('G', `return function(${params.join(',')}){${body}}`)(gl));
  }
  const java = new Java(rt);
  rt.java = java;
  const brkAfterJni = rt.brk;
  rt.loadElf(opts.elf);
  NativeHooks.install(rt);
  rt.brk = brkAfterJni;
  rt.startJavaThread();
  gl.attach && gl.attach(rt);

  // MainTask re-arms its frame timer after each tick: 50 ms in menus, 34 ms in battle. A page that
  // paces the game itself asks for 1 ms, so the timer is always due when nativeProcess runs.
  const timerFn = rt.syms.get('ker_SetTimerTimeout');
  if (timerFn && opts.pacedByPage) {
    cpu.hook(timerFn, () => {
      r[0] = 1;
      r[15] = r[14];
    });
  }

  const sym = n => {
    const a = rt.syms.get(n);
    if (!a) throw new Error('no symbol ' + n);
    return a;
  };
  const env = java.env;
  const cls = n => java.ref(java.cls(n));
  const native = (name, ...args) => new Promise(() => {});
  const call = (name, ...args) => {
    const fn = sym(name);
    rt.callJava(fn, [env, ...args]);
  };
  const arenaGoods = makeArenaGoodsSync(rt);
  const achievements = makeAchievementSync(rt);
  const ctx = { call, cls, sym, arenaGoods, achievements, text: opts.text, audio: opts.audio, ui: opts.ui, present: gl.present ? () => gl.present() : null };
  opts.installJava(rt, java, screen, ctx);

  const game = {
    rt, java,
    pending: false,
    started: false,
    start() {
      for (const f of rt.initArray) rt.callJava(f, []);
      rt.callJava(sym('JNI_OnLoad'), [java.vm, 0]);
      opts.startup({ rt, java, call, cls, sym, screen });
      if (opts.startMs === 0) { this.started = true; return; }
      if (!this.drain(opts.startMs || 15000)) {
        rt.log('startup still running after timeout');
        this.dumpState();
      }
      this.started = true;
    },
    busy() { return this.pending || rt.javaQueue.length > 0 || !!rt.javaJob; },
    drain(ms) {
      const end = ms ? Date.now() + ms : Infinity;
      while (rt.javaQueue.length || rt.javaJob) {
        rt.schedule(50, () => !(rt.javaQueue.length || rt.javaJob));
        if (Date.now() > end) return false;
      }
      return true;
    },
    // tick false only finishes work in progress: no new nativeProcess, so no extra game step.
    frame(ms, tick = true) {
      if (!this.pending && tick) {
        this.pending = true;
        rt.callJava(sym('Java_com_com2us_wrapper_kernel_CWrapperKernel_nativeProcess'), [env, 0]);
        rt.javaQueue[rt.javaQueue.length - 1].done = () => { this.pending = false; };
      }
      rt.schedule(ms, tick ? () => !this.pending : () => !this.busy());
    },
    dumpState() {
      const hex = v => '0x' + (v >>> 0).toString(16);
      const nm = addr => {
        let best = null, bd = 1e9;
        for (const [n, a] of rt.syms) {
          const d = (addr >>> 0) - (a & ~1);
          if (d >= 0 && d < bd) { bd = d; best = n; }
        }
        return `${hex(addr)}${best ? ' ' + best + '+' + bd : ''}`;
      };
      const out = [];
      out.push('threads ' + rt.threads.map(t => `${t.id}${t === rt.cur ? '*' : ''}:${t.done ? 'done' : t.wake ? 'wait' : 'run'}:${hex(t.r[15])}:${hex(t.r[14])}`).join(' '));
      out.push('queue ' + rt.javaQueue.length + ' job ' + (rt.javaJob ? nm(rt.javaJob.addr) : '-'));
      out.push('regs ' + Array.from(r, hex).join(' '));
      out.push('lr ' + nm(r[14]));
      out.push('pc ' + nm(r[15]));
      const ring = [];
      for (let i = 0; i < 24; i++) ring.push(nm(cpu.ring[(cpu.ringPos - i) & 255]));
      out.push('recent blocks:\n  ' + ring.join('\n  '));
      const stack = [];
      const sp = r[13] >>> 0;
      for (let i = 0; i < 400 && stack.length < 24; i++) {
        const w = cpu.u32[(sp + i * 4) >>> 2];
        if (w >= Runtime.helpers.LOAD_BASE && w < Runtime.helpers.LOAD_BASE + 0x1c7320) stack.push(nm(w));
      }
      out.push('stack refs:\n  ' + stack.join('\n  '));
      rt.log(out.join('\n'));
    },
  };
  return game;
}

// The 402/403 native parser ignores goods. Apply the authoritative spirit balance after that
// reply on the same Java queue. Layout verified against browser ELF 60ae11cb... and UpdateUserGoods.
function makeArenaGoodsSync(rt) {
  rt.def('knsArenaGoods', (count, lastGain) => {
    // The symbol is the struct itself: UpdateUserGoods@0x14a4bc loads its address from the GOT and stores +0xb68 directly.
    const info = rt.syms.get('gameInfo') >>> 0;
    if (!info) return;
    rt.cpu.wu32(info + 0xb68, count);
    rt.cpu.wu32(info + 0xb70, lastGain);
    rt.cpu.wu32(info + 0xb74, 0);
  });
  return body => {
    const spirit = body.update_item_etc_info?.find(row => row.item_id === 1900017);
    if (!spirit || !Number.isInteger(spirit.count) || spirit.count < 0 || spirit.count > 0x7fffffff
        || !Number.isInteger(spirit.user_last_stamina_gain_time) || spirit.user_last_stamina_gain_time <= 0) return;
    rt.callJava(rt.fns.knsArenaGoods, [spirit.count, spirit.user_last_stamina_gain_time]);
  };
}

// Some native reply cases (notably stage 72) ignore update_achievement_info. Queue native
// list updates after the real response, using separate records so parser buffer resets cannot erase them.
// Layout: UpdateDataAchieveDoing / ParsingDataToAchieveDoing, ELF a6f81a1f (2026-11-14).
function makeAchievementSync(rt) {
  return body => {
    if (body.errno || !Array.isArray(body.update_achievement_info)) return;
    const info = rt.syms.get('gameData'), update = rt.syms.get('UpdateDataAchieveDoing');
    if (!info || !update) return;
    for (const row of body.update_achievement_info) {
      if (!Number.isSafeInteger(row.type) || row.type <= 0 || row.type > 0x7fffffff
          || !Number.isSafeInteger(row.count) || row.count < 0
          || !Number.isSafeInteger(row.sub_type) || row.sub_type < 0 || row.sub_type > 0x7fffffff) continue;
      const record = rt.calloc(24);
      rt.cpu.wu32(record, row.type);
      rt.cpu.wu32(record + 8, row.count >>> 0);
      rt.cpu.wu32(record + 12, Math.floor(row.count / 0x100000000));
      rt.cpu.wu32(record + 16, row.sub_type);
      rt.callJava(update, [info + 0x2420, record]);
      rt.javaQueue[rt.javaQueue.length - 1].done = () => rt.free(record);
    }
  };
}

root.KnsGame = { boot, makeArenaGoodsSync, makeAchievementSync };
if (typeof module !== 'undefined') module.exports = { boot, makeArenaGoodsSync, makeAchievementSync };
})(typeof globalThis !== 'undefined' ? globalThis : this);
