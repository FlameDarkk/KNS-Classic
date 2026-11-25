// The Android userland a native .so expects, written in JS: ELF loader, malloc, libc, stdio,
// pthreads on cooperative guest threads, software float helpers.
(function (root) {
'use strict';
const CPU = root.ApkCPU || require('./cpu.js');

const MEM_SIZE = 0x20000000;
const LOAD_BASE = 0x00100000;
const ENOENT = 2, ENOMEM = 12, EINVAL = 22, EBUSY = 16, ETIMEDOUT = 110, EEXIST = 17;
const conv = new DataView(new ArrayBuffer(8));
const d2w = v => { conv.setFloat64(0, v, true); return [conv.getInt32(0, true), conv.getInt32(4, true)]; };
const w2d = (lo, hi) => { conv.setInt32(0, lo, true); conv.setInt32(4, hi, true); return conv.getFloat64(0, true); };
const f2w = v => { conv.setFloat32(0, v, true); return conv.getInt32(0, true); };
const w2f = w => { conv.setInt32(0, w, true); return conv.getFloat32(0, true); };
const big = (lo, hi) => (BigInt(hi >>> 0) << 32n) | BigInt(lo >>> 0);
const sbig = (lo, hi) => BigInt.asIntN(64, big(lo, hi));
const utf8 = new TextDecoder('utf-8');
const utf8e = new TextEncoder();
const CRC32_TAB = (() => {
  const t = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    t[i] = c >>> 0;
  }
  return t;
})();

class GuestThread {
  constructor(id) {
    this.id = id;
    this.r = new Int32Array(16);
    this.f = new Int32Array(4);
    this.wake = null;
    this.done = false;
    this.retval = 0;
    this.tls = new Map();
    this.cw = null;
  }
}

class Runtime {
  constructor(opts) {
    this.opts = opts;
    this.log = opts.log || (s => console.log(s));
    this.buffer = new ArrayBuffer(MEM_SIZE);
    this.cpu = new CPU(this.buffer);
    this.cpu.swi = n => { throw new Error('raw syscall ' + n); };
    this.syms = new Map();
    this.imports = new Map();
    this.missing = new Set();
    this.retry = false;
    this.threads = [];
    this.nextThread = 1;
    this.cur = null;
    this.mutexes = new Map();
    this.conds = new Map();
    this.tlsKeys = 0;
    this.bins = new Map();
    this.sizes = new Map();
    this.brk = 0;
    this.statics = new Map();
    this.fds = new Map();
    this.nextFd = 3;
    this.streams = new Map();
    this.dirs = new Map();
    this.vfs = opts.vfs;
    this.seed = 12345;
    this.strtokPos = 0;
    this.instantCalls = 0;
    this.clock = 0;   // when set, the time the game reads: one value per display refresh
    this.installLibc();
  }

  // ---------- memory helpers ----------
  cstr(a) {
    const m = this.cpu.m8;
    let e = a, ascii = true;
    while (m[e]) {
      if (m[e] >= 0x80) ascii = false;
      e++;
    }
    const len = e - a;
    if (len === 0) return '';
    if (ascii && len <= 128) {
      let s = '';
      for (let i = a; i < e; i++) s += String.fromCharCode(m[i]);
      return s;
    }
    return utf8.decode(m.subarray(a, e));
  }
  writeCstr(a, s) {
    const b = utf8e.encode(s);
    this.cpu.m8.set(b, a);
    this.cpu.m8[a + b.length] = 0;
    return b.length;
  }
  newCstr(s) {
    const b = utf8e.encode(s);
    const p = this.malloc(b.length + 1);
    this.cpu.m8.set(b, p);
    this.cpu.m8[p + b.length] = 0;
    return p;
  }
  staticBlock(name, size) {
    if (!this.statics.has(name)) {
      const p = this.malloc(size);
      this.cpu.m8.fill(0, p, p + size);
      this.statics.set(name, p);
    }
    return this.statics.get(name);
  }
  describeAddr(addr) {
    const a = addr >>> 0;
    let best = null, bd = 1e9;
    for (const [n, s] of this.syms) {
      const d = a - (s & ~1);
      if (d >= 0 && d < bd) { bd = d; best = n; }
    }
    return '0x' + a.toString(16) + (best ? ' ' + best + '+' + bd : '');
  }
  sizeClass(n) {
    if (n <= 1024) return Math.max(16, (n + 15) & ~15);
    return 2 ** Math.ceil(Math.log2(n));
  }
  tryMalloc(n) {
    const req = n >>> 0;
    const setNomem = () => {
      if (this.statics.has('errno') || this.brk + 64 < MEM_SIZE - 0x100000) this.setErrno(ENOMEM);
    };
    if (req >= 0x10000000) {
      this.log(`[malloc] refused ${req} (${n | 0}) bytes at lr=${this.describeAddr(this.cpu.r[14])}`);
      setNomem();
      return 0;
    }
    const size = this.sizeClass(req || 1);
    const bin = this.bins.get(size);
    if (bin && bin.length) {
      const p = bin.pop();
      this.sizes.set(p, size);
      return p;
    }
    const hdr = 16;
    const p = (this.brk + hdr + 15) & ~15;
    const nextBrk = p + size;
    if (nextBrk >= MEM_SIZE - 0x100000) {
      this.log(`[malloc] OOM requesting ${req} bytes (brk=0x${this.brk.toString(16)}) at lr=${this.describeAddr(this.cpu.r[14])}`);
      setNomem();
      return 0;
    }
    this.brk = nextBrk;
    this.sizes.set(p, size);
    return p;
  }
  malloc(n) {
    const p = this.tryMalloc(n);
    if (!p) throw new Error('guest out of memory (requested ' + (n >>> 0) + ' bytes)');
    return p;
  }
  free(p) {
    if (!p) return;
    const size = this.sizes.get(p);
    if (!size) return;
    this.sizes.delete(p);
    let bin = this.bins.get(size);
    if (!bin) this.bins.set(size, bin = []);
    bin.push(p);
  }
  calloc(n) {
    const req = n >>> 0;
    const p = this.malloc(req);
    this.cpu.m8.fill(0, p, p + req);
    return p;
  }

  // ---------- ELF ----------
  loadElf(bytes) {
    const cpu = this.cpu, m8 = cpu.m8;
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const u32 = o => dv.getUint32(o, true), u16 = o => dv.getUint16(o, true);
    const phoff = u32(28), phnum = u16(44), phes = u16(42);
    let end = 0, dyn = 0, dynsz = 0;
    for (let i = 0; i < phnum; i++) {
      const p = phoff + i * phes;
      const type = u32(p), off = u32(p + 4), vaddr = u32(p + 8), filesz = u32(p + 16), memsz = u32(p + 20);
      if (type === 1) {
        m8.set(bytes.subarray(off, off + filesz), LOAD_BASE + vaddr);
        end = Math.max(end, vaddr + memsz);
      } else if (type === 2) { dyn = vaddr; dynsz = memsz; }
    }
    this.brk = (LOAD_BASE + end + 0x10000) & ~0xfff;
    cpu.setCode(LOAD_BASE, end);
    const d = {};
    for (let i = 0; i < dynsz; i += 8) {
      const a = LOAD_BASE + dyn + i;
      const tag = cpu.m32[a >>> 2], val = cpu.m32[(a + 4) >>> 2];
      if (tag === 0) break;
      d[tag] = val >>> 0;
    }
    const symtab = LOAD_BASE + d[6], strtab = LOAD_BASE + d[5];
    const nsyms = (d[5] - d[6]) / 16;
    const u = cpu.u32;
    for (let i = 1; i < nsyms; i++) {
      const s = symtab + i * 16;
      const name = this.cstr(strtab + u[s >>> 2]);
      const val = u[(s + 4) >>> 2];
      const shndx = cpu.m16[(s + 14) >>> 1];
      if (shndx && val) this.syms.set(name, (LOAD_BASE + val) >>> 0);
    }
    const resolve = idx => {
      const s = symtab + idx * 16;
      const name = this.cstr(strtab + u[s >>> 2]);
      const val = u[(s + 4) >>> 2];
      const shndx = cpu.m16[(s + 14) >>> 1];
      if (shndx) return (LOAD_BASE + val) >>> 0;
      return this.resolveImport(name);
    };
    const apply = (start, size) => {
      for (let i = 0; i < size; i += 8) {
        const off = u[(LOAD_BASE + start + i) >>> 2], info = u[(LOAD_BASE + start + i + 4) >>> 2];
        const type = info & 255, sym = info >>> 8;
        const where = (LOAD_BASE + off) >>> 2;
        if (type === 23) u[where] = (u[where] + LOAD_BASE) >>> 0;
        else if (type === 21 || type === 22) u[where] = resolve(sym);
        else if (type === 2) u[where] = (u[where] + resolve(sym)) >>> 0;
        else throw new Error('relocation type ' + type);
      }
    };
    if (d[17]) apply(d[17], d[18]);
    if (d[23]) apply(d[23], d[2]);
    this.initArray = [];
    if (d[25]) {
      for (let i = 0; i < d[27]; i += 4) {
        const f = u[(LOAD_BASE + d[25] + i) >>> 2];
        if (f && f !== 0xffffffff) this.initArray.push(f);
      }
    }
    this.installFloatHooks();
  }

  resolveImport(name) {
    if (this.imports.has(name)) return this.imports.get(name);
    let addr;
    if (name === '__sF') addr = this.calloc(84 * 3);
    else if (name === '__stack_chk_guard') { addr = this.malloc(4); this.cpu.m32[addr >>> 2] = 0x1234ABCD; }
    else if (name === '__page_size') { addr = this.malloc(4); this.cpu.m32[addr >>> 2] = 4096; }
    else if (this.fns[name]) addr = this.fns[name];
    else {
      this.missing.add(name);
      addr = this.cpu.addTrap(name, () => { throw new Error('missing import called: ' + name); });
    }
    this.imports.set(name, addr);
    return addr;
  }

  // Define an imported function. It reads up to four register args, then the stack.
  def(name, fn) {
    const cpu = this.cpu, r = cpu.r, self = this;
    this.fns = this.fns || {};
    const argc = fn.length;
    this.fns[name] = cpu.addTrap(name, () => {
      let v;
      switch (argc) {
        case 0: v = fn(); break;
        case 1: v = fn(r[0]); break;
        case 2: v = fn(r[0], r[1]); break;
        case 3: v = fn(r[0], r[1], r[2]); break;
        case 4: v = fn(r[0], r[1], r[2], r[3]); break;
        default: {
          const args = [r[0], r[1], r[2], r[3]];
          for (let i = 4; i < argc; i++) args.push(cpu.m32[((r[13] + (i - 4) * 4) >>> 0) >>> 2]);
          v = fn.apply(null, args);
        }
      }
      if (self.retry) { self.retry = false; cpu.stop = true; return; }
      if (v !== undefined) r[0] = v;
      r[15] = r[14];
    });
  }

  // Inside a def: suspend this thread, then run the same call again once wake() is true.
  block(wake) {
    this.cur.wake = wake;
    this.retry = true;
  }

  // ---------- threads ----------
  allocStack(size) {
    const p = this.malloc(size);
    return (p + size - 16) & ~7;
  }
  newThread(startPc, arg) {
    const t = new GuestThread(this.nextThread++);
    t.r[13] = this.allocStack(0x100000);
    t.r[14] = this.threadExit;
    t.r[15] = startPc;
    t.r[0] = arg;
    this.threads.push(t);
    return t;
  }
  switchTo(t) {
    const cpu = this.cpu;
    if (this.cur === t) return;
    if (this.cur) { this.cur.r.set(cpu.r); this.cur.f.set(cpu.f); }
    cpu.r.set(t.r);
    cpu.f.set(t.f);
    this.cur = t;
  }
  // Runs guest threads until none is runnable or the time is spent.
  // Returns true if something ran.
  schedule(ms, until) {
    const cpu = this.cpu, end = Date.now() + ms;
    let ranAny = false;
    for (;;) {
      let ran = false;
      for (let i = 0; i < this.threads.length; i++) {
        const t = this.threads[i];
        if (t.done) continue;
        if (t.wake) {
          if (!t.wake()) continue;
          t.wake = null;
        }
        this.switchTo(t);
        cpu.run(t === this.javaThread ? 400000 : 100000);
        ran = ranAny = true;
        if (until && until()) return true;
        if (Date.now() >= end) return true;
      }
      if (!ran) return ranAny;
      if (this.threads.some(t => t.done)) {
        const cur = this.cur;
        this.threads = this.threads.filter(t => !t.done || t === cur);
      }
    }
  }

    // Runs a guest function to completion from inside a trap (qsort comparators and the like).
  callSync(addr, ...args) {
    const cpu = this.cpu, r = cpu.r;
    const saved = r.slice(), savedF = cpu.f.slice();
    r[0] = args[0] | 0; r[1] = args[1] | 0; r[2] = args[2] | 0; r[3] = args[3] | 0;
    const extra = Math.max(0, args.length - 4);
    r[13] = (r[13] - 64 - extra * 4) & ~7;
    for (let i = 4; i < args.length; i++) cpu.m32[((r[13] + (i - 4) * 4) >>> 0) >>> 2] = args[i] | 0;
    r[14] = this.syncRet;
    r[15] = addr;
    const prev = this.syncDone;
    this.syncDone = false;
    while (!this.syncDone) cpu.run(5000000);
    this.syncDone = prev;
    const ret = r[0];
    r.set(saved);
    cpu.f.set(savedF);
    cpu.stop = false;
    return ret;
  }

  // Queue a call on the Java thread: the JS side's way into native code.
  callJava(addr, args, done) {
    this.javaQueue.push({ addr, args, done });
  }

  startJavaThread() {
    const cpu = this.cpu, r = cpu.r;
    this.javaQueue = [];
    this.javaRet = cpu.addTrap('java_ret', () => {
      const job = this.javaJob;
      this.javaJob = null;
      r[15] = this.javaIdle;
      if (job && job.done) job.done(r[0]);
    });
    this.javaIdle = cpu.addTrap('java_idle', () => {
      const job = this.javaQueue.shift();
      if (!job) {
        this.cur.wake = () => this.javaQueue.length > 0;
        cpu.stop = true;
        return;
      }
      this.javaJob = job;
      const a = job.args;
      r[13] = this.javaSp;
      const extra = Math.max(0, a.length - 4);
      r[13] = (r[13] - extra * 4) & ~7;
      for (let i = 4; i < a.length; i++) cpu.m32[((r[13] + (i - 4) * 4) >>> 0) >>> 2] = a[i];
      for (let i = 0; i < 4; i++) r[i] = a[i] | 0;
      r[14] = this.javaRet;
      r[15] = job.addr;
    });
    const t = new GuestThread(this.nextThread++);
    this.javaSp = this.allocStack(0x800000);
    t.r[13] = this.javaSp;
    t.r[15] = this.javaIdle;
    this.threads.push(t);
    this.javaThread = t;
  }

  // Replace the exported function `name` with a JS body: fn reads its arguments from cpu.r and writes
  // the result to r[0]; the hook then returns to the caller. cost is the instruction count it stands for.
  // Call after loadElf. Games put their own fast paths here (see the README).
  hookCost(name, cost, fn) {
    const a = this.syms.get(name);
    if (!a) return 0;
    const r = this.cpu.r;
    this.cpu.hook(a, () => { fn(); r[15] = r[14]; }, cost);
    return a;
  }

  // Compare two STLport std::string objects (begin at +0x14, end at +0x10) like memcmp, then by length.
  stdStrCmp(sA, sB) {
    const m8 = this.cpu.m8, u32 = this.cpu.u32;
    const startA = u32[(sA + 0x14) >>> 2], endA = u32[(sA + 0x10) >>> 2];
    const startB = u32[(sB + 0x14) >>> 2], endB = u32[(sB + 0x10) >>> 2];
    const lenA = (endA - startA) >>> 0, lenB = (endB - startB) >>> 0;
    const minLen = lenA < lenB ? lenA : lenB;
    for (let i = 0; i < minLen; i++) {
      const d = m8[startA + i] - m8[startB + i];
      if (d !== 0) return d;
    }
    return lenA < lenB ? -1 : lenA > lenB ? 1 : 0;
  }

  // ---------- software float: replace the slow library routines ----------
  installFloatHooks() {
    const cpu = this.cpu, r = cpu.r, f = cpu.f, syms = this.syms;
    const hook = (names, fn) => {
      for (const n of names) {
        const a = syms.get(n);
        if (a) cpu.hook(a, () => { fn(); r[15] = r[14]; });
      }
    };
    const F = i => w2f(r[i]);
    const D = i => w2d(r[i], r[i + 1]);
    const retD = v => { const w = d2w(v); r[0] = w[0]; r[1] = w[1]; };
    const retF = v => { r[0] = f2w(v); };
    const toI = v => v !== v ? 0 : v >= 2147483647 ? 2147483647 : v <= -2147483648 ? -2147483648 : v | 0;
    const toU = v => v !== v || v <= 0 ? 0 : v >= 4294967295 ? -1 : Math.trunc(v) | 0;
    const ret64 = v => { r[0] = Number(BigInt.asIntN(32, v)); r[1] = Number(BigInt.asIntN(32, v >> 32n)); };
    const big64 = v => BigInt(Number.isFinite(v) ? Math.trunc(v) : 0);
    hook(['__aeabi_fadd', '__addsf3'], () => retF(F(0) + F(1)));
    hook(['__aeabi_fsub', '__subsf3'], () => retF(F(0) - F(1)));
    hook(['__aeabi_frsub'], () => retF(F(1) - F(0)));
    hook(['__aeabi_fmul', '__mulsf3'], () => retF(F(0) * F(1)));
    hook(['__aeabi_fdiv', '__divsf3'], () => retF(F(0) / F(1)));
    hook(['__aeabi_dadd', '__adddf3'], () => retD(D(0) + D(2)));
    hook(['__aeabi_dsub', '__subdf3'], () => retD(D(0) - D(2)));
    hook(['__aeabi_drsub'], () => retD(D(2) - D(0)));
    hook(['__aeabi_dmul', '__muldf3'], () => retD(D(0) * D(2)));
    hook(['__aeabi_ddiv', '__divdf3'], () => retD(D(0) / D(2)));
    hook(['__aeabi_f2d', '__extendsfdf2'], () => retD(F(0)));
    hook(['__aeabi_d2f', '__truncdfsf2'], () => retF(D(0)));
    hook(['__aeabi_i2f', '__floatsisf'], () => retF(r[0]));
    hook(['__aeabi_ui2f', '__floatunsisf'], () => retF(r[0] >>> 0));
    hook(['__aeabi_i2d', '__floatsidf'], () => retD(r[0]));
    hook(['__aeabi_ui2d', '__floatunsidf'], () => retD(r[0] >>> 0));
    hook(['__aeabi_l2f', '__floatdisf'], () => retF(Number(sbig(r[0], r[1]))));
    hook(['__aeabi_ul2f', '__floatundisf'], () => retF(Number(big(r[0], r[1]))));
    hook(['__aeabi_l2d', '__floatdidf'], () => retD(Number(sbig(r[0], r[1]))));
    hook(['__aeabi_ul2d', '__floatundidf'], () => retD(Number(big(r[0], r[1]))));
    hook(['__aeabi_f2iz', '__fixsfsi'], () => { r[0] = toI(F(0)); });
    hook(['__aeabi_f2uiz', '__fixunssfsi'], () => { r[0] = toU(F(0)); });
    hook(['__aeabi_d2iz', '__fixdfsi'], () => { r[0] = toI(D(0)); });
    hook(['__aeabi_d2uiz', '__fixunsdfsi'], () => { r[0] = toU(D(0)); });
    hook(['__aeabi_f2lz', '__fixsfdi'], () => ret64(big64(F(0))));
    hook(['__aeabi_f2ulz', '__fixunssfdi'], () => ret64(big64(F(0))));
    hook(['__aeabi_d2lz', '__fixdfdi'], () => ret64(big64(D(0))));
    hook(['__aeabi_d2ulz', '__fixunsdfdi'], () => ret64(big64(D(0))));
    const cmpF = (name, test) => hook(name, () => { r[0] = test(F(0), F(1)) ? 1 : 0; });
    cmpF(['__aeabi_fcmpeq'], (a, b) => a === b);
    cmpF(['__aeabi_fcmplt'], (a, b) => a < b);
    cmpF(['__aeabi_fcmple'], (a, b) => a <= b);
    cmpF(['__aeabi_fcmpgt'], (a, b) => a > b);
    cmpF(['__aeabi_fcmpge'], (a, b) => a >= b);
    cmpF(['__aeabi_fcmpun', '__unordsf2'], (a, b) => a !== a || b !== b);
    const cmpD = (name, test) => hook(name, () => { r[0] = test(D(0), D(2)) ? 1 : 0; });
    cmpD(['__aeabi_dcmpeq'], (a, b) => a === b);
    cmpD(['__aeabi_dcmplt'], (a, b) => a < b);
    cmpD(['__aeabi_dcmple'], (a, b) => a <= b);
    cmpD(['__aeabi_dcmpgt'], (a, b) => a > b);
    cmpD(['__aeabi_dcmpge'], (a, b) => a >= b);
    cmpD(['__aeabi_dcmpun', '__unorddf2'], (a, b) => a !== a || b !== b);
    // gcc comparison helpers return a signed order; NaN must fail the matching test.
    const order = (a, b, nan) => a !== a || b !== b ? nan : a < b ? -1 : a > b ? 1 : 0;
    for (const [n, nan] of [['__cmpsf2', 1], ['__eqsf2', 1], ['__nesf2', 1], ['__ltsf2', 1], ['__lesf2', 1], ['__gtsf2', -1], ['__gesf2', -1]])
      hook([n], () => { r[0] = order(F(0), F(1), nan); });
    for (const [n, nan] of [['__cmpdf2', 1], ['__eqdf2', 1], ['__nedf2', 1], ['__ltdf2', 1], ['__ledf2', 1], ['__gtdf2', -1], ['__gedf2', -1]])
      hook([n], () => { r[0] = order(D(0), D(2), nan); });
    // Flag-setting compares: LS is true for a <= b, HI for a > b or unordered. Registers survive.
    const setCmp = (a, b) => {
      if (a !== a || b !== b) { f[0] = 0; f[1] = 0; f[2] = 1; f[3] = 1; }
      else if (a < b) { f[0] = 1; f[1] = 0; f[2] = 0; f[3] = 0; }
      else if (a === b) { f[0] = 0; f[1] = 1; f[2] = 1; f[3] = 0; }
      else { f[0] = 0; f[1] = 0; f[2] = 1; f[3] = 0; }
    };
    hook(['__aeabi_cfcmple', '__aeabi_cfcmpeq'], () => setCmp(F(0), F(1)));
    hook(['__aeabi_cfrcmple'], () => setCmp(F(1), F(0)));
    hook(['__aeabi_cdcmple', '__aeabi_cdcmpeq'], () => setCmp(D(0), D(2)));
    hook(['__aeabi_cdrcmple'], () => setCmp(D(2), D(0)));
    // Integer division
    hook(['__aeabi_idiv', '__divsi3'], () => { r[0] = r[1] ? (r[0] / r[1]) | 0 : 0; });
    hook(['__aeabi_uidiv', '__udivsi3'], () => { r[0] = r[1] ? ((r[0] >>> 0) / (r[1] >>> 0)) | 0 : 0; });
    hook(['__modsi3'], () => { r[0] = r[1] ? (r[0] % r[1]) | 0 : r[0]; });
    hook(['__umodsi3'], () => { r[0] = r[1] ? ((r[0] >>> 0) % (r[1] >>> 0)) | 0 : r[0]; });
    hook(['__aeabi_idivmod'], () => {
      const a = r[0], b = r[1];
      r[0] = b ? (a / b) | 0 : 0; r[1] = b ? (a % b) | 0 : a;
    });
    hook(['__aeabi_uidivmod'], () => {
      const a = r[0] >>> 0, b = r[1] >>> 0;
      r[0] = b ? (a / b) | 0 : 0; r[1] = b ? (a % b) | 0 : a;
    });
    const rem64 = v => { r[2] = Number(BigInt.asIntN(32, v)); r[3] = Number(BigInt.asIntN(32, v >> 32n)); };
    hook(['__aeabi_ldivmod'], () => {
      const a = sbig(r[0], r[1]), b = sbig(r[2], r[3]);
      if (b === 0n) { rem64(a); ret64(0n); return; }
      rem64(a % b); ret64(a / b);
    });
    hook(['__aeabi_uldivmod'], () => {
      const a = big(r[0], r[1]), b = big(r[2], r[3]);
      if (b === 0n) { rem64(a); ret64(0n); return; }
      rem64(a % b); ret64(a / b);
    });
    hook(['__divdi3'], () => { const b = sbig(r[2], r[3]); ret64(b ? sbig(r[0], r[1]) / b : 0n); });
    hook(['__udivdi3'], () => { const b = big(r[2], r[3]); ret64(b ? big(r[0], r[1]) / b : 0n); });
    hook(['__moddi3'], () => { const b = sbig(r[2], r[3]); ret64(b ? sbig(r[0], r[1]) % b : 0n); });
    hook(['__umoddi3'], () => { const b = big(r[2], r[3]); ret64(b ? big(r[0], r[1]) % b : 0n); });
      hook(['__aeabi_lmul', '__muldi3'], () => {
      const al = r[0], ah = r[1], bl = r[2], bh = r[3];
      cpu.umul(al, bl);
      r[0] = cpu.lo;
      r[1] = (cpu.hi + Math.imul(al, bh) + Math.imul(ah, bl)) | 0;
    });

    const m8 = cpu.m8, m16 = cpu.m16, m32 = cpu.m32, u32 = cpu.u32;
    const hookCost = (name, cost, fn) => this.hookCost(name, cost, fn);

    // Checksums: adler32 & crc32
    hookCost('adler32', 16, () => {
      let adler = r[0] >>> 0, buf = r[1] >>> 0, len = r[2] >>> 0;
      if (buf === 0) { r[0] = 1; return; }
      let s1 = adler & 0xffff, s2 = (adler >>> 16) & 0xffff;
      while (len > 0) {
        let k = len < 5552 ? len : 5552;
        len -= k;
        while (k >= 16) {
          s1 += m8[buf]; s2 += s1; s1 += m8[buf + 1]; s2 += s1;
          s1 += m8[buf + 2]; s2 += s1; s1 += m8[buf + 3]; s2 += s1;
          s1 += m8[buf + 4]; s2 += s1; s1 += m8[buf + 5]; s2 += s1;
          s1 += m8[buf + 6]; s2 += s1; s1 += m8[buf + 7]; s2 += s1;
          s1 += m8[buf + 8]; s2 += s1; s1 += m8[buf + 9]; s2 += s1;
          s1 += m8[buf + 10]; s2 += s1; s1 += m8[buf + 11]; s2 += s1;
          s1 += m8[buf + 12]; s2 += s1; s1 += m8[buf + 13]; s2 += s1;
          s1 += m8[buf + 14]; s2 += s1; s1 += m8[buf + 15]; s2 += s1;
          buf += 16; k -= 16;
        }
        while (k > 0) { s1 += m8[buf++]; s2 += s1; k--; }
        s1 %= 65521;
        s2 %= 65521;
      }
      r[0] = ((s2 << 16) | s1) | 0;
    });

    hookCost('crc32', 16, () => {
      let c = r[0] >>> 0, buf = r[1] >>> 0, len = r[2] >>> 0;
      if (buf === 0) { r[0] = 0; return; }
      c = (~c) >>> 0;
      const end = buf + len;
      while (buf < end) c = CRC32_TAB[(c ^ m8[buf++]) & 0xff] ^ (c >>> 8);
      r[0] = (~c) | 0;
    });

    // libpng scanline unfiltering: png_read_filter_row(png_ptr, row_info, row, prev_row, filter)
    hookCost('png_read_filter_row', 32, () => {
      const rowInfo = r[1] >>> 0, row = r[2] >>> 0, prev = r[3] >>> 0;
      const filter = m32[(r[13] >>> 0) >>> 2] | 0;
      if (filter <= 0 || filter > 4) return;
      const istd = u32[(rowInfo + 4) >>> 2];
      const bpp = (m8[rowInfo + 11] + 7) >> 3;
      if (filter === 1) {
        for (let i = bpp; i < istd; i++) m8[row + i] = (m8[row + i] + m8[row + i - bpp]) & 0xff;
      } else if (filter === 2) {
        for (let i = 0; i < istd; i++) m8[row + i] = (m8[row + i] + m8[prev + i]) & 0xff;
      } else if (filter === 3) {
        for (let i = 0; i < bpp; i++) m8[row + i] = (m8[row + i] + (m8[prev + i] >>> 1)) & 0xff;
        for (let i = bpp; i < istd; i++) m8[row + i] = (m8[row + i] + ((m8[prev + i] + m8[row + i - bpp]) >>> 1)) & 0xff;
      } else if (filter === 4) {
        for (let i = 0; i < bpp; i++) m8[row + i] = (m8[row + i] + m8[prev + i]) & 0xff;
        for (let i = bpp; i < istd; i++) {
          const a = m8[row + i - bpp], b = m8[prev + i], c = m8[prev + i - bpp];
          const p = b - c, pc = a - c;
          const pa = p < 0 ? -p : p, pb = pc < 0 ? -pc : pc, ppc = (p + pc) < 0 ? -(p + pc) : (p + pc);
          const pr = (pa <= pb && pa <= ppc) ? a : (pb <= ppc ? b : c);
          m8[row + i] = (m8[row + i] + pr) & 0xff;
        }
      }
    });

    // STLport std::string lexicographical compare
    const cmpStdStr = (sA, sB) => this.stdStrCmp(sA, sB);
    hookCost('_ZStltIcSt11char_traitsIcESaIcEEbRKSbIT_T0_T1_ES8_', 2, () => {
      r[0] = cmpStdStr(r[0] >>> 0, r[1] >>> 0) < 0 ? 1 : 0;
    });
    // zlib inflate_fast(strm, start)
    hookCost('inflate_fast', 64, () => {
      const strm = r[0] >>> 0, start = r[1] >>> 0;
      const state = u32[(strm + 0x1c) >>> 2];
      let _in = (u32[strm >>> 2] - 1) >>> 0;
      const last = (_in + (u32[(strm + 4) >>> 2] - 5)) >>> 0;
      let out = (u32[(strm + 0x0c) >>> 2] - 1) >>> 0;
      const beg = (out - (start - u32[(strm + 0x10) >>> 2])) >>> 0;
      const end = (out + (u32[(strm + 0x10) >>> 2] - 257)) >>> 0;
      const wsize = u32[(state + 0x28) >>> 2];
      const whave = u32[(state + 0x2c) >>> 2];
      const write = u32[(state + 0x30) >>> 2];
      const window = u32[(state + 0x34) >>> 2];
      let hold = u32[(state + 0x38) >>> 2];
      let bits = u32[(state + 0x3c) >>> 2];
      const lcode = u32[(state + 0x4c) >>> 2];
      const dcode = u32[(state + 0x50) >>> 2];
      const lmask = (1 << u32[(state + 0x54) >>> 2]) - 1;
      const dmask = (1 << u32[(state + 0x58) >>> 2]) - 1;
      loop: do {
        if (bits < 15) {
          hold += m8[++_in] << bits; bits += 8;
          hold += m8[++_in] << bits; bits += 8;
        }
        let here = (lcode + ((hold & lmask) << 2)) >>> 0;
        dolen: for (;;) {
          const opBits = m8[here + 1];
          hold >>>= opBits; bits -= opBits;
          const op = m8[here];
          let val = m16[(here + 2) >>> 1];
          if (op === 0) {
            m8[++out] = val;
            break dolen;
          } else if (op & 16) {
            let len = val;
            const extra = op & 15;
            if (extra) {
              if (bits < extra) { hold += m8[++_in] << bits; bits += 8; }
              len += hold & ((1 << extra) - 1);
              hold >>>= extra; bits -= extra;
            }
            if (bits < 15) {
              hold += m8[++_in] << bits; bits += 8;
              hold += m8[++_in] << bits; bits += 8;
            }
            here = (dcode + ((hold & dmask) << 2)) >>> 0;
            for (;;) {
              const dopBits = m8[here + 1];
              hold >>>= dopBits; bits -= dopBits;
              const dop = m8[here];
              const dval = m16[(here + 2) >>> 1];
              if (dop & 16) {
                let dist = dval;
                const dextra = dop & 15;
                if (bits < dextra) {
                  hold += m8[++_in] << bits; bits += 8;
                  if (bits < dextra) { hold += m8[++_in] << bits; bits += 8; }
                }
                dist += hold & ((1 << dextra) - 1);
                hold >>>= dextra; bits -= dextra;
                const maxd = (out - beg) >>> 0;
                if (dist > maxd) {
                  let op2 = dist - maxd;
                  if (op2 > whave) {
                    m32[state >>> 2] = 27;
                    break loop;
                  }
                  let from = (window - 1) >>> 0;
                  if (write === 0) {
                    from = (from + wsize - op2) >>> 0;
                    if (op2 < len) {
                      len -= op2;
                      do { m8[++out] = m8[++from]; } while (--op2);
                      from = (out - dist) >>> 0;
                    }
                  } else if (write < op2) {
                    from = (from + wsize + write - op2) >>> 0;
                    let op3 = op2 - write;
                    if (op3 < len) {
                      len -= op3;
                      do { m8[++out] = m8[++from]; } while (--op3);
                      from = (window - 1) >>> 0;
                      if (write < len) {
                        len -= write;
                        let op4 = write;
                        do { m8[++out] = m8[++from]; } while (--op4);
                        from = (out - dist) >>> 0;
                      }
                    }
                  } else {
                    from = (from + write - op2) >>> 0;
                    if (op2 < len) {
                      len -= op2;
                      do { m8[++out] = m8[++from]; } while (--op2);
                      from = (out - dist) >>> 0;
                    }
                  }
                  while (len > 2) {
                    m8[++out] = m8[++from];
                    m8[++out] = m8[++from];
                    m8[++out] = m8[++from];
                    len -= 3;
                  }
                  if (len) {
                    m8[++out] = m8[++from];
                    if (len > 1) m8[++out] = m8[++from];
                  }
                } else {
                  let from = (out - dist) >>> 0;
                  do {
                    m8[++out] = m8[++from];
                    m8[++out] = m8[++from];
                    m8[++out] = m8[++from];
                    len -= 3;
                  } while (len > 2);
                  if (len) {
                    m8[++out] = m8[++from];
                    if (len > 1) m8[++out] = m8[++from];
                  }
                }
                break dolen;
              } else if ((dop & 64) === 0) {
                here = (dcode + ((dval + (hold & ((1 << dop) - 1))) << 2)) >>> 0;
              } else {
                m32[state >>> 2] = 27;
                break loop;
              }
            }
          } else if ((op & 64) === 0) {
            here = (lcode + ((val + (hold & ((1 << op) - 1))) << 2)) >>> 0;
          } else if (op & 32) {
            m32[state >>> 2] = 11;
            break loop;
          } else {
            m32[state >>> 2] = 27;
            break loop;
          }
        }
      } while (_in < last && out < end);
      const lenBits = bits >>> 3;
      _in = (_in - lenBits) >>> 0;
      bits -= lenBits << 3;
      hold &= (1 << bits) - 1;
      u32[strm >>> 2] = (_in + 1) >>> 0;
      u32[(strm + 0x0c) >>> 2] = (out + 1) >>> 0;
      u32[(strm + 4) >>> 2] = (_in < last ? 5 + (last - _in) : 5 - (_in - last)) >>> 0;
      u32[(strm + 0x10) >>> 2] = (out < end ? 257 + (end - out) : 257 - (out - end)) >>> 0;
      u32[(state + 0x38) >>> 2] = hold >>> 0;
      u32[(state + 0x3c) >>> 2] = bits >>> 0;
    });

    // LZMA SDK 4.x LzmaDecode(CLzmaDecoderState*, inStream, inSize, outStream, outSize)
    hookCost('LzmaDecode', 128, () => {
      const statePtr = r[0] >>> 0, inPtr = r[1] >>> 0, inSize = r[2] >>> 0, outPtr = r[3] >>> 0;
      const outSize = u32[(r[13] >>> 0) >>> 2];
      const lc = m32[statePtr >>> 2], lp = m32[(statePtr + 4) >>> 2], pb = m32[(statePtr + 8) >>> 2];
      const probs = u32[(statePtr + 12) >>> 2] >>> 1;
      const posStateMask = (1 << pb) - 1;
      const literalPosMask = (1 << lp) - 1;
      const numProbs = 1846 + (768 << (lc + lp));
      for (let i = 0; i < numProbs; i++) m16[probs + i] = 1024;
      let buf = inPtr, lim = (inPtr + inSize) >>> 0;
      if (buf === lim) { r[0] = 1; return; }
      let code = m8[buf++], initEnd = (inPtr + 5) >>> 0;
      while (buf !== initEnd) {
        if (buf === lim) { r[0] = 1; return; }
        code = ((code << 8) | m8[buf++]) >>> 0;
      }
      if (outSize === 0) { r[0] = 0; return; }
      let range = 0xffffffff, state = 0, rep0 = 1, rep1 = 1, rep2 = 1, rep3 = 1, nowPos = 0, prevByte = 0;
      while (nowPos < outSize) {
        const posState = nowPos & posStateMask;
        if (range <= 0x00ffffff) {
          if (buf === lim) { r[0] = 1; return; }
          range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
        }
        const pIdx = probs + (state << 4) + posState;
        const prob = m16[pIdx], bound = Math.imul(range >>> 11, prob) >>> 0;
        if (code < bound) {
          range = bound;
          m16[pIdx] = (prob + ((2048 - prob) >> 5)) & 0xffff;
          const litBase = probs + 1846 + 768 * (((nowPos & literalPosMask) << lc) + (prevByte >>> (8 - lc)));
          let symbol = 1;
          if (state >= 7) {
            let matchByte = m8[outPtr + nowPos - rep0];
            do {
              matchByte <<= 1;
              const bit = matchByte & 0x100;
              if (range <= 0x00ffffff) {
                if (buf === lim) { r[0] = 1; return; }
                range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
              }
              const lpIdx = litBase + 0x100 + bit + symbol;
              const lprob = m16[lpIdx], lbound = Math.imul(range >>> 11, lprob) >>> 0;
              if (code < lbound) {
                range = lbound;
                m16[lpIdx] = (lprob + ((2048 - lprob) >> 5)) & 0xffff;
                symbol <<= 1;
                if (bit !== 0) break;
              } else {
                range = (range - lbound) >>> 0; code = (code - lbound) >>> 0;
                m16[lpIdx] = (lprob - (lprob >>> 5)) & 0xffff;
                symbol = (symbol << 1) + 1;
                if (bit === 0) break;
              }
            } while (symbol < 0x100);
          }
          while (symbol < 0x100) {
            if (range <= 0x00ffffff) {
              if (buf === lim) { r[0] = 1; return; }
              range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
            }
            const lpIdx = litBase + symbol;
            const lprob = m16[lpIdx], lbound = Math.imul(range >>> 11, lprob) >>> 0;
            if (code < lbound) {
              range = lbound;
              m16[lpIdx] = (lprob + ((2048 - lprob) >> 5)) & 0xffff;
              symbol <<= 1;
            } else {
              range = (range - lbound) >>> 0; code = (code - lbound) >>> 0;
              m16[lpIdx] = (lprob - (lprob >>> 5)) & 0xffff;
              symbol = (symbol << 1) + 1;
            }
          }
          prevByte = symbol & 0xff;
          m8[outPtr + nowPos++] = prevByte;
          state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
        } else {
          range = (range - bound) >>> 0; code = (code - bound) >>> 0;
          m16[pIdx] = (prob - (prob >>> 5)) & 0xffff;
          if (range <= 0x00ffffff) {
            if (buf === lim) { r[0] = 1; return; }
            range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
          }
          const rIdx = probs + 192 + state;
          const rprob = m16[rIdx], rbound = Math.imul(range >>> 11, rprob) >>> 0;
          let len = 0;
          if (code < rbound) {
            range = rbound;
            m16[rIdx] = (rprob + ((2048 - rprob) >> 5)) & 0xffff;
            rep3 = rep2; rep2 = rep1; rep1 = rep0;
            state = state < 7 ? 0 : 3;
            len = probs + 818;
          } else {
            range = (range - rbound) >>> 0; code = (code - rbound) >>> 0;
            m16[rIdx] = (rprob - (rprob >>> 5)) & 0xffff;
            if (range <= 0x00ffffff) {
              if (buf === lim) { r[0] = 1; return; }
              range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
            }
            const g0Idx = probs + 204 + state;
            const g0prob = m16[g0Idx], g0bound = Math.imul(range >>> 11, g0prob) >>> 0;
            if (code < g0bound) {
              range = g0bound;
              m16[g0Idx] = (g0prob + ((2048 - g0prob) >> 5)) & 0xffff;
              if (range <= 0x00ffffff) {
                if (buf === lim) { r[0] = 1; return; }
                range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
              }
              const s0Idx = probs + 240 + (state << 4) + posState;
              const s0prob = m16[s0Idx], s0bound = Math.imul(range >>> 11, s0prob) >>> 0;
              if (code < s0bound) {
                range = s0bound;
                m16[s0Idx] = (s0prob + ((2048 - s0prob) >> 5)) & 0xffff;
                if (nowPos === 0) { r[0] = 1; return; }
                state = state < 7 ? 9 : 11;
                prevByte = m8[outPtr + nowPos - rep0];
                m8[outPtr + nowPos++] = prevByte;
                continue;
              }
              range = (range - s0bound) >>> 0; code = (code - s0bound) >>> 0;
              m16[s0Idx] = (s0prob - (s0prob >>> 5)) & 0xffff;
            } else {
              range = (range - g0bound) >>> 0; code = (code - g0bound) >>> 0;
              m16[g0Idx] = (g0prob - (g0prob >>> 5)) & 0xffff;
              if (range <= 0x00ffffff) {
                if (buf === lim) { r[0] = 1; return; }
                range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
              }
              const g1Idx = probs + 216 + state;
              const g1prob = m16[g1Idx], g1bound = Math.imul(range >>> 11, g1prob) >>> 0;
              let distance;
              if (code < g1bound) {
                range = g1bound;
                m16[g1Idx] = (g1prob + ((2048 - g1prob) >> 5)) & 0xffff;
                distance = rep1;
              } else {
                range = (range - g1bound) >>> 0; code = (code - g1bound) >>> 0;
                m16[g1Idx] = (g1prob - (g1prob >>> 5)) & 0xffff;
                if (range <= 0x00ffffff) {
                  if (buf === lim) { r[0] = 1; return; }
                  range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
                }
                const g2Idx = probs + 228 + state;
                const g2prob = m16[g2Idx], g2bound = Math.imul(range >>> 11, g2prob) >>> 0;
                if (code < g2bound) {
                  range = g2bound;
                  m16[g2Idx] = (g2prob + ((2048 - g2prob) >> 5)) & 0xffff;
                  distance = rep2;
                } else {
                  range = (range - g2bound) >>> 0; code = (code - g2bound) >>> 0;
                  m16[g2Idx] = (g2prob - (g2prob >>> 5)) & 0xffff;
                  distance = rep3;
                  rep3 = rep2;
                }
                rep2 = rep1;
              }
              rep1 = rep0;
              rep0 = distance;
            }
            state = state < 7 ? 8 : 11;
            len = probs + 1332;
          }
          // Decode length
          if (range <= 0x00ffffff) {
            if (buf === lim) { r[0] = 1; return; }
            range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
          }
          const lc1Prob = m16[len], lc1Bound = Math.imul(range >>> 11, lc1Prob) >>> 0;
          let lenBase, numBits, lenOffset;
          if (code < lc1Bound) {
            range = lc1Bound;
            m16[len] = (lc1Prob + ((2048 - lc1Prob) >> 5)) & 0xffff;
            lenBase = len + 2 + (posState << 3);
            lenOffset = 0;
            numBits = 3;
          } else {
            range = (range - lc1Bound) >>> 0; code = (code - lc1Bound) >>> 0;
            m16[len] = (lc1Prob - (lc1Prob >>> 5)) & 0xffff;
            if (range <= 0x00ffffff) {
              if (buf === lim) { r[0] = 1; return; }
              range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
            }
            const lc2Idx = len + 1, lc2Prob = m16[lc2Idx], lc2Bound = Math.imul(range >>> 11, lc2Prob) >>> 0;
            if (code < lc2Bound) {
              range = lc2Bound;
              m16[lc2Idx] = (lc2Prob + ((2048 - lc2Prob) >> 5)) & 0xffff;
              lenBase = len + 130 + (posState << 3);
              lenOffset = 8;
              numBits = 3;
            } else {
              range = (range - lc2Bound) >>> 0; code = (code - lc2Bound) >>> 0;
              m16[lc2Idx] = (lc2Prob - (lc2Prob >>> 5)) & 0xffff;
              lenBase = len + 258;
              lenOffset = 16;
              numBits = 8;
            }
          }
          let m = 1;
          for (let b = numBits; b > 0; b--) {
            if (range <= 0x00ffffff) {
              if (buf === lim) { r[0] = 1; return; }
              range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
            }
            const bpIdx = lenBase + m, bpProb = m16[bpIdx], bpBound = Math.imul(range >>> 11, bpProb) >>> 0;
            if (code < bpBound) {
              range = bpBound;
              m16[bpIdx] = (bpProb + ((2048 - bpProb) >> 5)) & 0xffff;
              m <<= 1;
            } else {
              range = (range - bpBound) >>> 0; code = (code - bpBound) >>> 0;
              m16[bpIdx] = (bpProb - (bpProb >>> 5)) & 0xffff;
              m = (m << 1) + 1;
            }
          }
          let matchLen = m - (1 << numBits) + lenOffset;
          if (state < 4) {
            state += 7;
            const pSlotBase = probs + 432 + ((matchLen < 4 ? matchLen : 3) << 6);
            let ps = 1;
            for (let b = 6; b > 0; b--) {
              if (range <= 0x00ffffff) {
                if (buf === lim) { r[0] = 1; return; }
                range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
              }
              const spIdx = pSlotBase + ps, spProb = m16[spIdx], spBound = Math.imul(range >>> 11, spProb) >>> 0;
              if (code < spBound) {
                range = spBound;
                m16[spIdx] = (spProb + ((2048 - spProb) >> 5)) & 0xffff;
                ps <<= 1;
              } else {
                range = (range - spBound) >>> 0; code = (code - spBound) >>> 0;
                m16[spIdx] = (spProb - (spProb >>> 5)) & 0xffff;
                ps = (ps << 1) + 1;
              }
            }
            const posSlot = ps - 64;
            if (posSlot >= 4) {
              const numDirectBits = (posSlot >> 1) - 1;
              rep0 = 2 | (posSlot & 1);
              let revBase, revBits;
              if (posSlot < 14) {
                rep0 <<= numDirectBits;
                revBase = probs + 688 + rep0 - posSlot - 1;
                revBits = numDirectBits;
              } else {
                let db = numDirectBits - 4;
                do {
                  if (range <= 0x00ffffff) {
                    if (buf === lim) { r[0] = 1; return; }
                    range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
                  }
                  range >>>= 1;
                  rep0 <<= 1;
                  if (code >= range) { code = (code - range) >>> 0; rep0 |= 1; }
                } while (--db);
                revBase = probs + 802;
                revBits = 4;
                rep0 <<= 4;
              }
              let rm = 1, ri = 1;
              for (let b = revBits; b > 0; b--) {
                if (range <= 0x00ffffff) {
                  if (buf === lim) { r[0] = 1; return; }
                  range = (range << 8) >>> 0; code = ((code << 8) | m8[buf++]) >>> 0;
                }
                const rpIdx = revBase + rm, rpProb = m16[rpIdx], rpBound = Math.imul(range >>> 11, rpProb) >>> 0;
                if (code < rpBound) {
                  range = rpBound;
                  m16[rpIdx] = (rpProb + ((2048 - rpProb) >> 5)) & 0xffff;
                  rm <<= 1;
                } else {
                  range = (range - rpBound) >>> 0; code = (code - rpBound) >>> 0;
                  m16[rpIdx] = (rpProb - (rpProb >>> 5)) & 0xffff;
                  rm = (rm << 1) + 1;
                  rep0 |= ri;
                }
                ri <<= 1;
              }
            } else {
              rep0 = posSlot;
            }
            rep0 = (rep0 + 1) >>> 0;
            if (rep0 === 0) break;
          }
          if (rep0 > nowPos) { r[0] = 1; return; }
          matchLen += 2;
          do {
            prevByte = m8[outPtr + nowPos - rep0];
            m8[outPtr + nowPos++] = prevByte;
            if (nowPos >= outSize) break;
          } while (--matchLen);
        }
      }
      if (range <= 0x00ffffff) {
        if (buf === lim) { r[0] = 1; return; }
        code = ((code << 8) | m8[buf++]) >>> 0;
      }
      r[0] = 0;
    });
  }

  // ---------- printf ----------
  format(fmt, next, next64, nextD) {
    let out = '', i = 0;
    while (i < fmt.length) {
      const c = fmt[i++];
      if (c !== '%') { out += c; continue; }
      let flags = '', width = '', prec = null;
      while ('-+ #0'.includes(fmt[i])) flags += fmt[i++];
      if (fmt[i] === '*') { width = String(next()); i++; }
      else while (fmt[i] >= '0' && fmt[i] <= '9') width += fmt[i++];
      if (fmt[i] === '.') {
        i++; prec = '';
        if (fmt[i] === '*') { prec = String(next()); i++; }
        else while (fmt[i] >= '0' && fmt[i] <= '9') prec += fmt[i++];
        prec = +prec;
      }
      let len = '';
      while ('hlLqzjt'.includes(fmt[i])) len += fmt[i++];
      const conv_ = fmt[i++];
      let s = '';
      const is64 = len === 'll' || len === 'q' || len === 'L';
      switch (conv_) {
        case '%': s = '%'; break;
        case 'c': s = String.fromCharCode(next() & 255); break;
        case 's': {
          const p = next();
          s = p ? this.cstr(p) : '(null)';
          if (prec !== null) s = s.slice(0, prec);
          break;
        }
        case 'd': case 'i': {
          let v;
          if (is64) v = next64(true);
          else { v = next(); if (len === 'h') v = (v << 16) >> 16; else if (len === 'hh') v = (v << 24) >> 24; }
          s = String(v);
          if (prec !== null) { const neg = s[0] === '-'; s = (neg ? '-' : '') + s.slice(neg ? 1 : 0).padStart(prec, '0'); }
          if (flags.includes('+') && s[0] !== '-') s = '+' + s;
          else if (flags.includes(' ') && s[0] !== '-') s = ' ' + s;
          break;
        }
        case 'u': case 'x': case 'X': case 'o': {
          let v;
          if (is64) v = next64(false);
          else { v = next() >>> 0; if (len === 'h') v &= 0xffff; else if (len === 'hh') v &= 0xff; }
          s = v.toString(conv_ === 'u' ? 10 : conv_ === 'o' ? 8 : 16);
          if (conv_ === 'X') s = s.toUpperCase();
          if (prec !== null) s = s.padStart(prec, '0');
          if (flags.includes('#') && conv_ !== 'u' && v) s = (conv_ === 'o' ? '0' : '0' + conv_) + s;
          break;
        }
        case 'p': s = '0x' + (next() >>> 0).toString(16); break;
        case 'f': case 'F': s = nextD().toFixed(prec === null ? 6 : prec); break;
        case 'e': case 'E': s = nextD().toExponential(prec === null ? 6 : prec); break;
        case 'g': case 'G': {
          const v = nextD();
          s = String(+v.toPrecision(prec === null ? 6 : prec || 1));
          break;
        }
        case 'n': next(); break;
        default: s = '%' + conv_;
      }
      if (width) {
        const w = +width;
        if (flags.includes('-')) s = s.padEnd(w);
        else if (flags.includes('0') && conv_ !== 's' && conv_ !== 'c') {
          const neg = s[0] === '-' || s[0] === '+';
          s = (neg ? s[0] : '') + s.slice(neg ? 1 : 0).padStart(w - (neg ? 1 : 0), '0');
        } else s = s.padStart(w);
      }
      out += s;
    }
    return out;
  }
  // Variadic args still in registers and on the stack, starting at register index `first`.
  regArgs(first) {
    const cpu = this.cpu, r = cpu.r;
    let slot = first;
    const sp = r[13];
    const take = () => {
      const v = slot < 4 ? r[slot] : cpu.m32[((sp + (slot - 4) * 4) >>> 0) >>> 2];
      slot++;
      return v;
    };
    const take64 = signed => {
      if (slot & 1) slot++;
      const lo = take(), hi = take();
      return signed ? sbig(lo, hi) : big(lo, hi);
    };
    const takeD = () => {
      if (slot & 1) slot++;
      const lo = take(), hi = take();
      return w2d(lo, hi);
    };
    return [take, take64, takeD];
  }
  // A va_list is a pointer into the argument area.
  vaArgs(ap) {
    const m = this.cpu.m32;
    let p = ap >>> 0;
    const take = () => { const v = m[p >>> 2]; p += 4; return v; };
    const take64 = signed => {
      p = (p + 7) & ~7;
      const lo = take(), hi = take();
      return signed ? sbig(lo, hi) : big(lo, hi);
    };
    const takeD = () => {
      p = (p + 7) & ~7;
      const lo = take(), hi = take();
      return w2d(lo, hi);
    };
    return [take, take64, takeD];
  }
  fmtReg(fmtPtr, first) { return this.format(this.cstr(fmtPtr), ...this.regArgs(first)); }
  fmtVa(fmtPtr, ap) { return this.format(this.cstr(fmtPtr), ...this.vaArgs(ap)); }

  // ---------- files ----------
  // Returns a file record, or null with errno set. May call block() while an asset loads.
  openFile(path, flags) {
    const creat = flags & 0x40, trunc = flags & 0x200;
    const res = this.vfs.open(path, creat, trunc);
    if (res === 'pending') {
      this.block(() => this.vfs.ready(path));
      return null;
    }
    return res;
  }
  newFd(file, flags) {
    const fd = this.nextFd++;
    this.fds.set(fd, { file, pos: flags & 0x400 ? file.size : 0, flags });
    return fd;
  }
  setErrno(e) { this.cpu.m32[this.staticBlock('errno', 4) >>> 2] = e; }
  fdRead(fd, buf, n) {
    const h = this.fds.get(fd);
    if (!h) { this.setErrno(9); return -1; }
    const c = Math.min(n, h.file.size - h.pos);
    if (c <= 0) return 0;
    this.cpu.m8.set(h.file.data.subarray(h.pos, h.pos + c), buf);
    h.pos += c;
    return c;
  }
  fdWrite(fd, buf, n) {
    if (fd === 1 || fd === 2) { this.log(utf8.decode(this.cpu.m8.subarray(buf, buf + n))); return n; }
    const h = this.fds.get(fd);
    if (!h) { this.setErrno(9); return -1; }
    this.vfs.write(h.file, h.pos, this.cpu.m8.subarray(buf, buf + n));
    h.pos += n;
    return n;
  }
  fdSeek(fd, off, whence) {
    const h = this.fds.get(fd);
    if (!h) { this.setErrno(9); return -1; }
    const np = whence === 0 ? off : whence === 1 ? h.pos + off : h.file.size + off;
    if (np < 0) { this.setErrno(EINVAL); return -1; }
    h.pos = np;
    return np;
  }
  newStream(fd) {
    const p = this.calloc(84);
    this.streams.set(p, { fd, eof: false, err: false });
    return p;
  }
  stdFd(p) {
    const base = this.imports.get('__sF');
    if (base && p >= base && p < base + 252) return ((p - base) / 84) | 0;
    return -1;
  }
  statTo(file, buf) {
    const m = this.cpu.m32;
    this.cpu.m8.fill(0, buf, buf + 104);
    const w = (off, v) => { m[(buf + off) >>> 2] = v; };
    w(16, file.isDir ? 0o40755 : 0o100644);
    w(20, 1);
    w(48, file.size);
    w(56, 4096);
    w(64, Math.ceil(file.size / 512));
    w(72, (file.mtime || Date.now()) / 1000);
    w(80, (file.mtime || Date.now()) / 1000);
    w(88, (file.mtime || Date.now()) / 1000);
    w(96, file.ino || 1);
  }
  writeTm(buf, d, utc) {
    const m = this.cpu.m32;
    const v = utc
      ? [d.getUTCSeconds(), d.getUTCMinutes(), d.getUTCHours(), d.getUTCDate(), d.getUTCMonth(), d.getUTCFullYear() - 1900, d.getUTCDay()]
      : [d.getSeconds(), d.getMinutes(), d.getHours(), d.getDate(), d.getMonth(), d.getFullYear() - 1900, d.getDay()];
    v.forEach((x, i) => { m[(buf + i * 4) >>> 2] = x; });
    const start = utc ? Date.UTC(d.getUTCFullYear(), 0, 1) : new Date(d.getFullYear(), 0, 1).getTime();
    m[(buf + 28) >>> 2] = Math.floor((d.getTime() - start) / 86400000);
    m[(buf + 32) >>> 2] = 0;
    m[(buf + 36) >>> 2] = utc ? 0 : -d.getTimezoneOffset() * 60;
    m[(buf + 40) >>> 2] = 0;
  }

  // ---------- pthread mutex ----------
  mutexOf(p) {
    let m = this.mutexes.get(p);
    if (!m) {
      const init = this.cpu.m32[p >>> 2];
      m = { owner: 0, count: 0, kind: init & 0xC000 };
      this.mutexes.set(p, m);
    }
    return m;
  }
  mutexTry(p) {
    const m = this.mutexOf(p), me = this.cur.id;
    if (m.owner === 0) { m.owner = me; m.count = 1; return true; }
    if (m.owner === me && m.kind === 0x4000) { m.count++; return true; }
    return false;
  }
  mutexUnlock(p) {
    const m = this.mutexOf(p);
    if (m.count > 0 && --m.count === 0) m.owner = 0;
  }

  installLibc() {
    const cpu = this.cpu, r = cpu.r;
    const m32 = cpu.m32, m8 = cpu.m8;
    const rt = this;
    const def = (n, f) => rt.def(n, f);
    const uns = v => v >>> 0;
    rt.fns = {};

    // Internal return traps
    rt.threadExit = cpu.addTrap('thread_exit', () => {
      rt.cur.done = true;
      rt.cur.retval = r[0];
      cpu.stop = true;
    });
    rt.syncRet = cpu.addTrap('sync_ret', () => { rt.syncDone = true; cpu.stop = true; });

    // ---------- memory ----------
    def('malloc', n => rt.tryMalloc(uns(n)));
    def('free', p => { rt.free(p); return 0; });
    def('calloc', (a, b) => {
      const total = uns(a) * uns(b);
      if (total >= 0x10000000) {
        rt.log(`[calloc] refused ${uns(a)}*${uns(b)} bytes at lr=${rt.describeAddr(cpu.r[14])}`);
        if (rt.statics.has('errno') || rt.brk + 64 < MEM_SIZE - 0x100000) rt.setErrno(ENOMEM);
        return 0;
      }
      const p = rt.tryMalloc(total);
      if (!p) return 0;
      if (total > 0) m8.fill(0, p, p + total);
      return p;
    });
    def('realloc', (p, n) => {
      n = uns(n);
      if (!p) return rt.tryMalloc(n);
      const old = rt.sizes.get(p) || 0;
      if (n <= old) return p;
      const q = rt.tryMalloc(n);
      if (!q) return 0;
      m8.copyWithin(q, p, p + old);
      rt.free(p);
      return q;
    });
    def('memcpy', (d, s, n) => { m8.copyWithin(d, s, s + uns(n)); return d; });
    def('memmove', (d, s, n) => { m8.copyWithin(d, s, s + uns(n)); return d; });
    def('memset', (d, v, n) => { m8.fill(v & 255, d, d + uns(n)); return d; });
    def('memcmp', (a, b, n) => {
      n = uns(n);
      for (let i = 0; i < n; i++) {
        const c = m8[a + i] - m8[b + i];
        if (c) return c;
      }
      return 0;
    });
    def('mmap', (addr, len, prot, flags, fd, off) => {
      len = uns(len);
      if (len >= 0x10000000) {
        if (rt.statics.has('errno') || rt.brk + 64 < MEM_SIZE - 0x100000) rt.setErrno(ENOMEM);
        return -1;
      }
      const p = rt.tryMalloc(len + 4096);
      if (!p) return -1;
      const a = (p + 4095) & ~4095;
      m8.fill(0, a, a + len);
      if (!(flags & 0x20) && fd >= 0) {
        const h = rt.fds.get(fd);
        if (h) {
          const c = Math.min(len, h.file.size - off);
          if (c > 0) m8.set(h.file.data.subarray(off, off + c), a);
        }
      }
      return a;
    });
    def('munmap', () => 0);

    // ---------- strings ----------
    def('strlen', p => { let e = p; while (m8[e]) e++; return e - p; });
    def('strcpy', (d, s) => { let i = 0; do { m8[d + i] = m8[s + i]; } while (m8[s + i++]); return d; });
    def('strncpy', (d, s, n) => {
      n = uns(n);
      let i = 0;
      for (; i < n && m8[s + i]; i++) m8[d + i] = m8[s + i];
      for (; i < n; i++) m8[d + i] = 0;
      return d;
    });
    def('strcat', (d, s) => {
      let e = d;
      while (m8[e]) e++;
      let i = 0;
      do { m8[e + i] = m8[s + i]; } while (m8[s + i++]);
      return d;
    });
    const cmp = (a, b, n, fold) => {
      for (let i = 0; i < n; i++) {
        let x = m8[a + i], y = m8[b + i];
        if (fold) {
          if (x >= 65 && x <= 90) x += 32;
          if (y >= 65 && y <= 90) y += 32;
        }
        if (x !== y) return x - y;
        if (!x) return 0;
      }
      return 0;
    };
    def('strcmp', (a, b) => cmp(a, b, Infinity, false));
    def('strncmp', (a, b, n) => cmp(a, b, uns(n), false));
    def('strcasecmp', (a, b) => cmp(a, b, Infinity, true));
    def('strncasecmp', (a, b, n) => cmp(a, b, uns(n), true));
    def('strchr', (s, c) => {
      c &= 255;
      for (let p = s; ; p++) {
        if (m8[p] === c) return p;
        if (!m8[p]) return 0;
      }
    });
    def('strrchr', (s, c) => {
      c &= 255;
      let found = 0;
      for (let p = s; ; p++) {
        if (m8[p] === c) found = p;
        if (!m8[p]) return found;
      }
    });
    const find = (h, n, fold) => {
      const hs = rt.cstr(h), ns = rt.cstr(n);
      const i = fold ? hs.toLowerCase().indexOf(ns.toLowerCase()) : hs.indexOf(ns);
      return i < 0 ? 0 : h + utf8e.encode(hs.slice(0, i)).length;
    };
    def('strstr', (h, n) => find(h, n, false));
    def('strcasestr', (h, n) => find(h, n, true));
    def('strdup', s => rt.newCstr(rt.cstr(s)));
    def('strerror', e => rt.newCstr('error ' + e));
    def('strtok', (s, d) => {
      let p = s || rt.strtokPos;
      if (!p) return 0;
      const delim = new Set(rt.cstr(d).split('').map(c => c.charCodeAt(0)));
      while (m8[p] && delim.has(m8[p])) p++;
      if (!m8[p]) { rt.strtokPos = 0; return 0; }
      const tok = p;
      while (m8[p] && !delim.has(m8[p])) p++;
      if (m8[p]) { m8[p] = 0; rt.strtokPos = p + 1; } else rt.strtokPos = 0;
      return tok;
    });
    def('atoi', s => parseInt(rt.cstr(s), 10) | 0);
    def('atol', s => parseInt(rt.cstr(s), 10) | 0);
    const parseLL = (s, base) => {
      const t = s.trim();
      let neg = false, i = 0;
      if (t[0] === '-') { neg = true; i = 1; } else if (t[0] === '+') i = 1;
      if ((base === 0 || base === 16) && t[i] === '0' && (t[i + 1] === 'x' || t[i + 1] === 'X')) { i += 2; base = 16; }
      if (!base) base = t[i] === '0' ? 8 : 10;
      let v = 0n;
      const start = i;
      for (; i < t.length; i++) {
        const d = parseInt(t[i], 36);
        if (Number.isNaN(d) || d >= base) break;
        v = v * BigInt(base) + BigInt(d);
      }
      return [neg ? -v : v, i === start ? 0 : i + (s.length - s.trimStart().length)];
    };
    const ret64 = v => { r[1] = Number(BigInt.asIntN(32, v >> 32n)); return Number(BigInt.asIntN(32, v)); };
    def('atoll', s => ret64(parseLL(rt.cstr(s), 10)[0]));
    def('strtoll', (s, end, base) => {
      const [v, n] = parseLL(rt.cstr(s), base);
      if (end) m32[end >>> 2] = s + n;
      return ret64(v);
    });
    def('strtod', (s, end) => {
      const t = rt.cstr(s);
      const m = /^\s*[+-]?(\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?)/.exec(t);
      if (end) m32[end >>> 2] = s + (m ? m[0].length : 0);
      const w = d2w(m ? parseFloat(m[0]) : 0);
      r[1] = w[1];
      return w[0];
    });
    const sprintfOut = (d, s, cap) => {
      const b = utf8e.encode(s);
      const n = cap === undefined ? b.length : Math.min(b.length, Math.max(0, cap - 1));
      m8.set(b.subarray(0, n), d);
      if (cap === undefined || cap > 0) m8[d + n] = 0;
      return b.length;
    };
    def('sprintf', (d, f) => sprintfOut(d, rt.fmtReg(f, 2)));
    def('snprintf', (d, n, f) => sprintfOut(d, rt.fmtReg(f, 3), uns(n)));
    def('vsprintf', (d, f, ap) => sprintfOut(d, rt.fmtVa(f, ap)));
    def('vsnprintf', (d, n, f, ap) => sprintfOut(d, rt.fmtVa(f, ap), uns(n)));
    def('sscanf', (s, f) => {
      const [take] = rt.regArgs(2);
      const text = rt.cstr(s), fmt = rt.cstr(f);
      let ti = 0, count = 0;
      for (let i = 0; i < fmt.length; i++) {
        const c = fmt[i];
        if (/\s/.test(c)) { while (/\s/.test(text[ti] || '')) ti++; continue; }
        if (c !== '%') { if (text[ti] !== c) return count; ti++; continue; }
        i++;
        let width = '';
        while (fmt[i] >= '0' && fmt[i] <= '9') width += fmt[i++];
        const w = width ? +width : Infinity;
        let len = '';
        while ('hlL'.includes(fmt[i])) len += fmt[i++];
        const cv = fmt[i];
        if (cv !== 'c') while (/\s/.test(text[ti] || '')) ti++;
        if (ti >= text.length) return count || -1;
        if (cv === 'd' || cv === 'i' || cv === 'u' || cv === 'x') {
          const re = cv === 'x' ? /^[+-]?(0[xX])?[0-9a-fA-F]+/ : /^[+-]?\d+/;
          const mt = re.exec(text.slice(ti, w === Infinity ? undefined : ti + w));
          if (!mt) return count;
          ti += mt[0].length;
          const v = BigInt.asIntN(64, cv === 'x'
            ? BigInt('0x' + mt[0].replace(/^[+-]?(0[xX])?/, ''))
            : BigInt(mt[0].replace(/^\+/, '')));
          const p = take();
          if (len === 'll') { m32[p >>> 2] = Number(BigInt.asIntN(32, v)); m32[(p + 4) >>> 2] = Number(BigInt.asIntN(32, v >> 32n)); }
          else if (len === 'h') cpu.m16[p >>> 1] = Number(BigInt.asIntN(16, v));
          else m32[p >>> 2] = Number(BigInt.asIntN(32, v));
          count++;
        } else if (cv === 'f' || cv === 'g' || cv === 'e') {
          const mt = /^[+-]?\d*\.?\d*([eE][+-]?\d+)?/.exec(text.slice(ti));
          if (!mt || !mt[0]) return count;
          ti += mt[0].length;
          const p = take();
          if (len === 'l') { const w2 = d2w(parseFloat(mt[0])); m32[p >>> 2] = w2[0]; m32[(p + 4) >>> 2] = w2[1]; }
          else cpu.f32[p >>> 2] = parseFloat(mt[0]);
          count++;
        } else if (cv === 's') {
          let e = ti;
          while (e < text.length && !/\s/.test(text[e]) && e - ti < w) e++;
          rt.writeCstr(take(), text.slice(ti, e));
          ti = e;
          count++;
        } else if (cv === 'c') {
          m8[take()] = text.charCodeAt(ti++);
          count++;
        } else return count;
      }
      return count;
    });

    // ---------- stdio ----------
    def('__errno', () => rt.staticBlock('errno', 4));
    def('fopen', (path, mode) => {
      const m = rt.cstr(mode), p = rt.cstr(path);
      const flags = m[0] === 'r' ? (m.includes('+') ? 2 : 0) : m[0] === 'w' ? 0x241 : 0x441;
      const file = rt.openFile(p, flags);
      if (rt.retry) return;
      if (!file) { rt.setErrno(ENOENT); return 0; }
      return rt.newStream(rt.newFd(file, flags));
    });
    def('fdopen', fd => rt.newStream(fd));
    def('fclose', p => {
      const s = rt.streams.get(p);
      if (s) {
        const h = rt.fds.get(s.fd);
        if (h && h.flags & 3) rt.vfs.commit(h.file);
        rt.fds.delete(s.fd);
        rt.streams.delete(p);
        rt.free(p);
      }
      return 0;
    });
    def('fread', (buf, size, n, p) => {
      const s = rt.streams.get(p);
      if (!s) return 0;
      size = uns(size); n = uns(n);
      const want = size * n;
      if (!want) return 0;
      const got = rt.fdRead(s.fd, buf, want);
      if (got < want) s.eof = true;
      return Math.floor(got / size);
    });
    def('fwrite', (buf, size, n, p) => {
      const fd = rt.stdFd(p);
      size = uns(size); n = uns(n);
      if (fd >= 0) { rt.log(utf8.decode(m8.subarray(buf, buf + size * n))); return n; }
      const s = rt.streams.get(p);
      if (!s) return 0;
      rt.fdWrite(s.fd, buf, size * n);
      return n;
    });
    def('fseek', (p, off, wh) => {
      const s = rt.streams.get(p);
      if (!s) return -1;
      s.eof = false;
      return rt.fdSeek(s.fd, off, wh) < 0 ? -1 : 0;
    });
    def('ftell', p => {
      const s = rt.streams.get(p);
      if (!s) return -1;
      const h = rt.fds.get(s.fd);
      return h ? h.pos : -1;
    });
    def('feof', p => { const s = rt.streams.get(p); return s && s.eof ? 1 : 0; });
    def('fgetc', p => {
      const s = rt.streams.get(p);
      if (!s) return -1;
      const h = rt.fds.get(s.fd);
      if (h.pos >= h.file.size) { s.eof = true; return -1; }
      return h.file.data[h.pos++];
    });
    def('fgets', (buf, n, p) => {
      const s = rt.streams.get(p);
      if (!s) return 0;
      const h = rt.fds.get(s.fd);
      let i = 0;
      while (i < n - 1 && h.pos < h.file.size) {
        const c = h.file.data[h.pos++];
        m8[buf + i++] = c;
        if (c === 10) break;
      }
      if (!i) { s.eof = true; return 0; }
      m8[buf + i] = 0;
      return buf;
    });
    def('fprintf', (p, f) => {
      const text = rt.fmtReg(f, 2);
      const fd = rt.stdFd(p);
      if (fd >= 0) { rt.log(text); return text.length; }
      const s = rt.streams.get(p);
      if (!s) return -1;
      const b = utf8e.encode(text);
      const h = rt.fds.get(s.fd);
      rt.vfs.write(h.file, h.pos, b);
      h.pos += b.length;
      return b.length;
    });
    def('puts', s => { rt.log(rt.cstr(s)); return 1; });
    def('popen', () => 0);
    def('pclose', () => -1);
    def('system', () => -1);
    def('open', (path, flags) => {
      const p = rt.cstr(path);
      const file = rt.openFile(p, flags);
      if (rt.retry) return;
      if (!file) { rt.setErrno(ENOENT); return -1; }
      return rt.newFd(file, flags);
    });
    def('close', fd => {
      if (sockets.has(fd)) { closeSocket(sockets.get(fd)); sockets.delete(fd); return 0; }
      const h = rt.fds.get(fd);
      if (h && h.flags & 3) rt.vfs.commit(h.file);
      rt.fds.delete(fd);
      return 0;
    });
    def('read', (fd, b, n) => rt.fdRead(fd, b, uns(n)));
    def('write', (fd, b, n) => rt.fdWrite(fd, b, uns(n)));
    def('lseek', (fd, o, w) => rt.fdSeek(fd, o, w));
    def('dup', fd => {
      const h = rt.fds.get(fd);
      if (!h) return -1;
      const n = rt.nextFd++;
      rt.fds.set(n, { file: h.file, pos: h.pos, flags: h.flags });
      return n;
    });
    def('fcntl', () => 0);
    def('fsync', () => 0);
    def('unlink', p => rt.vfs.remove(rt.cstr(p)) ? 0 : -1);
    def('remove', p => rt.vfs.remove(rt.cstr(p)) ? 0 : -1);
    def('rmdir', p => rt.vfs.remove(rt.cstr(p)) ? 0 : -1);
    def('rename', (a, b) => rt.vfs.rename(rt.cstr(a), rt.cstr(b)) ? 0 : -1);
    def('mkdir', p => {
      if (rt.vfs.mkdir(rt.cstr(p))) return 0;
      rt.setErrno(EEXIST);
      return -1;
    });
    const doStat = (path, buf) => {
      const file = rt.vfs.stat(rt.cstr(path));
      if (!file) { rt.setErrno(ENOENT); return -1; }
      rt.statTo(file, buf);
      return 0;
    };
    def('stat', doStat);
    def('lstat', doStat);
    def('fstat', (fd, buf) => {
      const h = rt.fds.get(fd);
      if (!h) { rt.setErrno(9); return -1; }
      rt.statTo(h.file, buf);
      return 0;
    });
    def('statfs', (path, buf) => {
      m8.fill(0, buf, buf + 120);
      m32[(buf + 4) >>> 2] = 4096;
      m32[(buf + 8) >>> 2] = 0x400000;
      m32[(buf + 16) >>> 2] = 0x400000;
      m32[(buf + 24) >>> 2] = 0x400000;
      return 0;
    });
    def('opendir', path => {
      const list = rt.vfs.list(rt.cstr(path));
      if (!list) { rt.setErrno(ENOENT); return 0; }
      const p = rt.calloc(16);
      rt.dirs.set(p, { list, i: 0, ent: rt.calloc(280) });
      return p;
    });
    def('readdir', p => {
      const d = rt.dirs.get(p);
      if (!d || d.i >= d.list.length) return 0;
      const name = d.list[d.i++];
      m32[d.ent >>> 2] = d.i;
      cpu.m16[(d.ent + 16) >>> 1] = 280;
      m8[d.ent + 18] = name.isDir ? 4 : 8;
      rt.writeCstr(d.ent + 19, name.name);
      return d.ent;
    });
    def('closedir', p => { rt.dirs.delete(p); return 0; });

    // ---------- time and environment ----------
    def('time', p => {
      const t = Math.floor(Date.now() / 1000);
      if (p) m32[p >>> 2] = t;
      return t;
    });
    def('gettimeofday', tv => {
      const n = rt.clock || Date.now();
      m32[tv >>> 2] = Math.floor(n / 1000);
      m32[(tv + 4) >>> 2] = Math.floor(n % 1000) * 1000;
      return 0;
    });
    def('difftime', () => {
      const v = r[0] - r[1];
      const w = d2w(v);
      r[1] = w[1];
      return w[0];
    });
    const env = new Map();
    const utcZone = () => /^(UTC|GMT)(0)?$/.test(env.get('TZ') || '');
    const tmBuf = () => rt.staticBlock('tm', 64);
    def('localtime', p => { const b = tmBuf(); rt.writeTm(b, new Date(m32[p >>> 2] * 1000), utcZone()); return b; });
    def('gmtime', p => { const b = tmBuf(); rt.writeTm(b, new Date(m32[p >>> 2] * 1000), true); return b; });
    def('mktime', p => {
      const f = i => m32[(p + i * 4) >>> 2];
      const year = f(5) + 1900;
      // Native event dates temporarily set TZ=UTC before subtracting the Korean
      // timezone offset. Honor that request independently of the host timezone.
      const value = utcZone() ? Date.UTC(year, f(4), f(3), f(2), f(1), f(0))
        : new Date(year, f(4), f(3), f(2), f(1), f(0)).getTime();
      // Like libc, normalize the struct in place: callers read tm_wday/tm_yday after mktime.
      rt.writeTm(p, new Date(value), utcZone());
      return Math.floor(value / 1000);
    });
    const waitUntil = t => {
      const th = rt.cur;
      if (th.sleep === undefined) {
        th.sleep = t;
        rt.block(() => Date.now() >= th.sleep);
        return;
      }
      if (Date.now() < th.sleep) { rt.block(() => Date.now() >= th.sleep); return; }
      th.sleep = undefined;
      return 0;
    };
    def('usleep', us => waitUntil(Date.now() + uns(us) / 1000));
    def('sleep', s => waitUntil(Date.now() + uns(s) * 1000));
    def('nanosleep', ts => waitUntil(Date.now() + m32[ts >>> 2] * 1000 + m32[(ts + 4) >>> 2] / 1e6));
    def('getpid', () => 1000);
    def('kill', () => 0);
    def('raise', sig => { throw new Error('raise ' + sig); });
    def('abort', () => { throw new Error('abort()'); });
    def('exit', c => { throw new Error('exit(' + c + ')'); });
    def('setenv', (k, v) => { env.set(rt.cstr(k), rt.cstr(v)); return 0; });
    def('unsetenv', k => { env.delete(rt.cstr(k)); return 0; });
    def('srand48', s => { rt.seed = uns(s); return 0; });
    def('lrand48', () => {
      rt.seed = (Math.imul(rt.seed, 1664525) + 1013904223) >>> 0;
      return rt.seed >>> 1;
    });
    def('__stack_chk_fail', () => { throw new Error('__stack_chk_fail'); });
    def('__cxa_finalize', () => 0);
    def('__aeabi_atexit', () => 0);
    def('__cxa_pure_virtual', () => { throw new Error('pure virtual call'); });
    def('__cxa_begin_cleanup', () => { throw new Error('__cxa_begin_cleanup'); });
    def('__cxa_type_match', () => 0);
    def('__cxa_call_unexpected', () => { throw new Error('__cxa_call_unexpected'); });
    def('__gnu_Unwind_Find_exidx', () => 0);
    def('setjmp', env_ => {
      for (let i = 4; i <= 14; i++) m32[(env_ + (i - 4) * 4) >>> 2] = r[i];
      return 0;
    });
    def('longjmp', (env_, v) => {
      for (let i = 4; i <= 14; i++) r[i] = m32[(env_ + (i - 4) * 4) >>> 2];
      return v || 1;
    });
    const logPrint = (prio, tag, text) => rt.log(`[${rt.cstr(tag)}] ${text}`);
    def('__android_log_print', (prio, tag, f) => { logPrint(prio, tag, rt.fmtReg(f, 3)); return 0; });
    def('__android_log_write', (prio, tag, s) => { logPrint(prio, tag, rt.cstr(s)); return 0; });

    // ---------- math ----------
    const d1 = fn => () => {
      const w = d2w(fn(w2d(r[0], r[1])));
      r[1] = w[1];
      return w[0];
    };
    def('sqrt', d1(Math.sqrt));
    def('sin', d1(Math.sin));
    def('cos', d1(Math.cos));
    def('pow', () => {
      const w = d2w(Math.pow(w2d(r[0], r[1]), w2d(r[2], r[3])));
      r[1] = w[1];
      return w[0];
    });
    def('ceilf', () => f2w(Math.ceil(w2f(r[0]))));
    def('qsort', (base, n, size, cmpFn) => {
      n = uns(n); size = uns(size);
      if (n < 2) return 0;
      const tmpA = rt.malloc(size), tmpB = rt.malloc(size);
      const items = [];
      for (let i = 0; i < n; i++) items.push(m8.slice(base + i * size, base + (i + 1) * size));
      items.sort((x, y) => {
        m8.set(x, tmpA);
        m8.set(y, tmpB);
        return rt.callSync(cmpFn, tmpA, tmpB);
      });
      items.forEach((it, i) => m8.set(it, base + i * size));
      rt.free(tmpA);
      rt.free(tmpB);
      return 0;
    });

    // ---------- pthread ----------
    def('pthread_create', (out, attr, start, arg) => {
      const t = rt.newThread(start, arg);
      m32[out >>> 2] = t.id;
      return 0;
    });
    def('pthread_self', () => rt.cur.id);
    def('pthread_equal', (a, b) => a === b ? 1 : 0);
    def('pthread_detach', () => 0);
    def('pthread_exit', v => {
      rt.cur.done = true;
      rt.cur.retval = v;
      cpu.stop = true;
    });
    def('pthread_join', (id, out) => {
      const t = rt.threads.find(x => x.id === id);
      if (t && !t.done) { rt.block(() => t.done); return; }
      if (out && t) m32[out >>> 2] = t.retval;
      return 0;
    });
    def('pthread_attr_init', () => 0);
    def('pthread_attr_setdetachstate', () => 0);
    def('pthread_key_create', k => { m32[k >>> 2] = ++rt.tlsKeys; return 0; });
    def('pthread_getspecific', k => rt.cur.tls.get(k) | 0);
    def('pthread_setspecific', (k, v) => { rt.cur.tls.set(k, v); return 0; });
    def('pthread_mutexattr_init', a => { m32[a >>> 2] = 0; return 0; });
    def('pthread_mutexattr_destroy', () => 0);
    def('pthread_mutexattr_settype', (a, t) => { m32[a >>> 2] = t; return 0; });
    def('pthread_mutex_init', (p, a) => {
      const type = a ? m32[a >>> 2] : 0;
      m32[p >>> 2] = 0;
      rt.mutexes.set(p, { owner: 0, count: 0, kind: type === 1 ? 0x4000 : 0 });
      return 0;
    });
    def('pthread_mutex_destroy', p => { rt.mutexes.delete(p); return 0; });
    def('pthread_mutex_lock', p => {
      if (rt.mutexTry(p)) return 0;
      const m = rt.mutexOf(p);
      rt.block(() => m.owner === 0);
    });
    def('pthread_mutex_trylock', p => rt.mutexTry(p) ? 0 : EBUSY);
    def('pthread_mutex_unlock', p => { rt.mutexUnlock(p); return 0; });
    def('pthread_cond_init', () => 0);
    def('pthread_cond_destroy', p => { rt.conds.delete(p); return 0; });
    const condWait = (c, mx, deadline) => {
      const th = rt.cur;
      if (!th.cw) {
        th.cw = { signaled: false, timedOut: false, deadline };
        let q = rt.conds.get(c);
        if (!q) rt.conds.set(c, q = []);
        q.push(th);
        rt.mutexUnlock(mx);
        const w = th.cw;
        rt.block(() => w.signaled || (w.deadline && Date.now() >= w.deadline));
        return;
      }
      if (!th.cw.signaled) {
        th.cw.timedOut = true;
        const q = rt.conds.get(c);
        if (q) { const i = q.indexOf(th); if (i >= 0) q.splice(i, 1); }
        th.cw.signaled = true;
      }
      if (!rt.mutexTry(mx)) {
        const m = rt.mutexOf(mx);
        rt.block(() => m.owner === 0);
        return;
      }
      const res = th.cw.timedOut ? ETIMEDOUT : 0;
      th.cw = null;
      return res;
    };
    def('pthread_cond_wait', (c, mx) => condWait(c, mx, 0));
    def('pthread_cond_timedwait', (c, mx, ts) =>
      condWait(c, mx, m32[ts >>> 2] * 1000 + m32[(ts + 4) >>> 2] / 1e6));
    def('pthread_cond_signal', c => {
      const q = rt.conds.get(c);
      if (q && q.length) q.shift().cw.signaled = true;
      return 0;
    });

    // ---------- sockets: a guest TCP connection becomes a WebSocket to opts.socketUrl ----------
    // opts.socketUrl is a ws:// URL or a function returning one; without it (or without WebSocket) connect succeeds
    // and the socket reads as closed. The bytes are carried unchanged. recv never blocks: an empty socket returns
    // EAGAIN, so the guest has to poll (as a game's per-tick event pump does).
    const sockets = new Map();
    let nextSocketFd = 900;
    const EAGAIN = 11, ENOTCONN = 107, EPIPE = 32;
    const socketUrl = () => {
      const u = rt.opts.socketUrl;
      return typeof u === 'function' ? u() : u;
    };
    const closeSocket = s => { s.closed = true; if (s.ws) try { s.ws.close(); } catch (e) {} };
    def('socket', () => { const fd = nextSocketFd++; sockets.set(fd, { rx: [], tx: [], ws: null, closed: false }); return fd; });
    def('connect', fd => {
      const s = sockets.get(fd);
      if (!s) { rt.setErrno(ENOTCONN); return -1; }
      const url = typeof WebSocket === 'undefined' ? null : socketUrl();
      if (!url) { s.closed = true; return 0; }
      s.ws = new WebSocket(url);
      s.ws.binaryType = 'arraybuffer';
      s.ws.onopen = () => { for (const bytes of s.tx.splice(0)) s.ws.send(bytes); };
      s.ws.onmessage = e => s.rx.push(new Uint8Array(e.data));
      s.ws.onclose = () => { s.closed = true; };
      return 0;
    });
    def('send', (fd, b, n) => {
      const s = sockets.get(fd);
      if (!s || s.closed) { rt.setErrno(EPIPE); return -1; }
      const bytes = m8.slice(b >>> 0, (b >>> 0) + uns(n));
      if (s.ws && s.ws.readyState === 1) s.ws.send(bytes); else s.tx.push(bytes);
      return uns(n);
    });
    def('recvfrom', (fd, b, n) => {
      const s = sockets.get(fd);
      if (!s) { rt.setErrno(ENOTCONN); return -1; }
      if (!s.rx.length) {
        if (s.closed) return 0;
        rt.setErrno(EAGAIN);
        return -1;
      }
      let done = 0;
      const want = uns(n);
      while (done < want && s.rx.length) {
        const head = s.rx[0], take = Math.min(want - done, head.length);
        m8.set(head.subarray(0, take), (b >>> 0) + done);
        done += take;
        if (take === head.length) s.rx.shift(); else s.rx[0] = head.subarray(take);
      }
      return done;
    });
    def('select', () => 0);
    def('getsockopt', () => 0);
    def('getaddrinfo', () => -2);
    def('freeaddrinfo', () => 0);
    def('inet_addr', () => 0x0100007f);
    def('inet_ntoa', () => rt.newCstr('127.0.0.1'));
  }
}

Runtime.helpers = { d2w, w2d, f2w, w2f, big, sbig, LOAD_BASE };
root.ApkRuntime = Runtime;
if (typeof module !== 'undefined') module.exports = Runtime;
})(typeof globalThis !== 'undefined' ? globalThis : this);
