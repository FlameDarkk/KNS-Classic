// ARMv5TE (ARM + Thumb, soft float) to JavaScript block JIT. One flat guest address space.
// A block is compiled the first time its address is reached. r[15] holds the block key:
// the address, with bit 0 set in Thumb state. Blocks and hooks return the instruction count.
(function (root) {
'use strict';

const FN = 1, FZ = 2, FC = 4, FV = 8;
const TRAP_BASE = 0xF0000000;
const MAX_BLOCK = 48;
// Region limits: how far from the entry, how many blocks and instructions one function holds.
const REGION_SPAN = 0x4000;
const REGION_BLOCKS = 160;
const REGION_INSTRS = 1600;
const CALL_DEPTH = 96;
// A single block that has run this often becomes the entry of a region.
const HOT = 50;
const CONDS = [
  ['f[1]', FZ], ['!f[1]', FZ], ['f[2]', FC], ['!f[2]', FC],
  ['f[0]', FN], ['!f[0]', FN], ['f[3]', FV], ['!f[3]', FV],
  ['(f[2]&&!f[1])', FC | FZ], ['(!f[2]||f[1])', FC | FZ],
  ['(f[0]===f[3])', FN | FV], ['(f[0]!==f[3])', FN | FV],
  ['(!f[1]&&f[0]===f[3])', FZ | FN | FV], ['(f[1]||f[0]!==f[3])', FZ | FN | FV],
];
const k = x => '(' + (x | 0) + ')';
// Flags only need writing when something reads them before the next full write. Sets each
// instruction's `out` (flags to write) from the flags live after the block; returns those live before it.
function flagPass(list, live) {
  for (let i = list.length - 1; i >= 0; i--) {
    const ins = list[i];
    ins.out = live & (ins.def || 0);
    if (ins.cond !== undefined) live |= CONDS[ins.cond][1] | (ins.use || 0);
    else live = (live & ~(ins.def || 0)) | (ins.use || 0);
  }
  return live;
}
const rotr = (v, n) => n ? ((v >>> n) | (v << (32 - n))) >>> 0 : v >>> 0;
const nz = mask => (mask & FN ? 'f[0]=t>>>31;' : '') + (mask & FZ ? 'f[1]=t===0?1:0;' : '');

// Result lands in t. A and B are expressions; sc is the shifter carry expression, if any.
function alu(op, A, B, mask, sc) {
  const logicC = (mask & FC) && sc ? `f[2]=${sc};` : '';
  switch (op) {
    case 0: case 8: return `t=${A}&${B};` + nz(mask) + logicC;
    case 1: case 9: return `t=${A}^${B};` + nz(mask) + logicC;
    case 12: return `t=${A}|${B};` + nz(mask) + logicC;
    case 13: return `t=${B}|0;` + nz(mask) + logicC;
    case 14: return `t=${A}&~${B};` + nz(mask) + logicC;
    case 15: return `t=~${B};` + nz(mask) + logicC;
    case 4: case 11: {
      if (!(mask & (FC | FV))) return `t=(${A}+${B})|0;` + nz(mask);
      return `a=${A};b=${B};t=(a+b)|0;` + nz(mask) +
        (mask & FC ? 'f[2]=(a>>>0)+(b>>>0)>4294967295?1:0;' : '') +
        (mask & FV ? 'f[3]=((a^t)&(b^t))<0?1:0;' : '');
    }
    case 2: case 10: {
      if (!(mask & (FC | FV))) return `t=(${A}-${B})|0;` + nz(mask);
      return `a=${A};b=${B};t=(a-b)|0;` + nz(mask) +
        (mask & FC ? 'f[2]=(a>>>0)>=(b>>>0)?1:0;' : '') +
        (mask & FV ? 'f[3]=((a^b)&(a^t))<0?1:0;' : '');
    }
    case 3: return alu(2, B, A, mask, sc);
    case 5:
      return `a=${A};b=${B};c=f[2];t=(a+b+c)|0;` + nz(mask) +
        (mask & FC ? 'f[2]=(a>>>0)+(b>>>0)+c>4294967295?1:0;' : '') +
        (mask & FV ? 'f[3]=((a^t)&(b^t))<0?1:0;' : '');
    case 6:
      return `a=${A};b=${B};c=f[2];t=(a+~b+c)|0;` + nz(mask) +
        (mask & FC ? 'f[2]=(a>>>0)+((~b)>>>0)+c>4294967295?1:0;' : '') +
        (mask & FV ? 'f[3]=((a^b)&(a^t))<0?1:0;' : '');
    case 7: return alu(6, B, A, mask, sc);
  }
  throw new Error('alu');
}
const ALU_DEF = op => {
  if (op === 0 || op === 1 || op === 8 || op === 9 || op >= 12) return FN | FZ | FC;
  return FN | FZ | FC | FV;
};
const ALU_USE = op => (op === 5 || op === 6 || op === 7) ? FC : 0;

// Variable shift. Value in `v`, count in `n`; result in `o`, carry in `h` when wanted.
function shiftReg(type, V, COUNT, wantC) {
  let s = `v=${V};n=${COUNT}&255;`;
  switch (type) {
    case 0:
      s += 'o=n<32?v<<n:0;';
      if (wantC) s += 'h=n===0?f[2]:n<32?(v>>>(32-n))&1:n===32?v&1:0;';
      break;
    case 1:
      s += 'o=n<32?v>>>n:0;';
      if (wantC) s += 'h=n===0?f[2]:n<32?(v>>>(n-1))&1:n===32?v>>>31:0;';
      break;
    case 2:
      s += 'o=n<32?v>>n:v>>31;';
      if (wantC) s += 'h=n===0?f[2]:n<32?(v>>>(n-1))&1:v>>>31;';
      break;
    case 3:
      s += 'm=n&31;o=m?(v>>>m)|(v<<(32-m)):v;';
      if (wantC) s += 'h=n===0?f[2]:m?(v>>>(m-1))&1:v>>>31;';
      break;
  }
  return s;
}

function shiftImm(type, R, amt) {
  switch (type) {
    case 0: return amt ? { val: `(${R}<<${amt})`, c: `(${R}>>>${32 - amt})&1` } : { val: R, c: null };
    case 1: return amt ? { val: `(${R}>>>${amt})`, c: `(${R}>>>${amt - 1})&1` } : { val: '0', c: `${R}>>>31` };
    case 2: return amt ? { val: `(${R}>>${amt})`, c: `(${R}>>>${amt - 1})&1` } : { val: `(${R}>>31)`, c: `${R}>>>31` };
    case 3: return amt ? { val: `((${R}>>>${amt})|(${R}<<${32 - amt}))`, c: `(${R}>>>${amt - 1})&1` }
      : { val: `((${R}>>>1)|(f[2]<<31))`, c: `${R}&1`, useC: true };
  }
}

const load32 = 'a&3?cpu.ru32(a):m32[a>>>2]';
const load16 = 'a&1?cpu.ru16(a):m16[a>>>1]';
const loadS16 = 'a&1?cpu.rs16(a):s16[a>>>1]';
const store32 = v => `if(a&3)cpu.wu32(a,${v});else m32[a>>>2]=${v};`;
const store16 = v => `if(a&1)cpu.wu16(a,${v});else m16[a>>>1]=${v};`;
const INTERWORK = 'r[15]=x&1?x:x&~3;';

class CPU {
  constructor(buffer) {
    this.buffer = buffer;
    this.m8 = new Uint8Array(buffer);
    this.s8 = new Int8Array(buffer);
    this.m16 = new Uint16Array(buffer);
    this.s16 = new Int16Array(buffer);
    this.m32 = new Int32Array(buffer);
    this.u32 = new Uint32Array(buffer);
    this.f32 = new Float32Array(buffer);
    this.r = new Int32Array(16);
    this.f = new Int32Array(4);
    this.lo = 0; this.hi = 0;
    this.codeBase = 0; this.codeSize = 0;
    this.tab = [];
    this.traps = [];
    this.trapNames = [];
    this.stop = false;
    this.count = 0;
    this.compiled = 0;
    this.regions = 0;
    this.codeOffs = [];
    this.depth = 0;
    // Region compilation; false compiles single blocks only (CPU.regions = false for all CPUs).
    this.useRegions = CPU.regions !== false;
    this.ring = new Int32Array(256);
    this.ringPos = 0;
  }

  setCode(base, size) {
    this.codeBase = base;
    this.codeSize = size;
    this.tab = new Array(size);
    this.hits = new Int32Array(size);
  }

  // Replace the code at a symbol with a JS function.
  hook(addr, fn, cost = 1) {
    // Compiled regions may hold the hooked address as a case: start over.
    if (this.codeOffs.length) this.flushCode();
    const h = () => { fn(this); return cost; };
    h.hooked = true;
    this.tab[(addr >>> 0) - this.codeBase] = h;
  }

  addTrap(name, fn) {
    const i = this.traps.length;
    this.traps.push(() => { fn(this); return 1; });
    this.trapNames.push(name);
    return (TRAP_BASE + i * 4) >>> 0;
  }

  // Run until the budget is spent, or a trap sets cpu.stop.
  run(budget) {
    if (this.prof) return this.runProfiled(budget);
    const r = this.r, base = this.codeBase, size = this.codeSize;
    const tab = this.tab, traps = this.traps, ring = this.ring, hits = this.hits;
    let n = budget, rp = this.ringPos;
    this.stop = false;
    this.depth = 0;
    while (n > 0 && !this.stop) {
      const pc = r[15] >>> 0;
      const off = (pc - base) >>> 0;
      ring[rp = (rp + 1) & 255] = pc;
      this.ringPos = rp;
      let b;
      if (off < size) {
        b = tab[off];
        if (b === undefined) b = this.compile(pc);
        else if (b.single && ++hits[off] === HOT && this.useRegions) b = this.compileRegion(pc);
      } else if (pc >= TRAP_BASE) {
        b = traps[(pc - TRAP_BASE) >>> 2];
      } else {
        this.ringPos = rp;
        throw new Error('jump outside code: 0x' + pc.toString(16) + ' lr=0x' + (r[14] >>> 0).toString(16));
      }
      n -= b(n);
      rp = this.ringPos;
    }
    this.ringPos = rp;
    const done = budget - n;
    this.count += done;
    return done;
  }

  // A region's call: run blocks until control comes back to ret, the budget is spent or a trap
  // stops the CPU. The same checks as run(), block by block.
  callIn(ret, lim) {
    const r = this.r, base = this.codeBase, size = this.codeSize, tab = this.tab, traps = this.traps, ring = this.ring, hits = this.hits;
    let g = 0;
    this.depth++;
    while (g < lim && !this.stop && r[15] !== ret) {
      const pc = r[15] >>> 0;
      const off = (pc - base) >>> 0;
      ring[this.ringPos = (this.ringPos + 1) & 255] = pc;
      let b;
      if (off < size) {
        b = tab[off];
        if (b === undefined) b = this.compile(pc);
        else if (b.single && ++hits[off] === HOT && this.useRegions) b = this.compileRegion(pc);
      } else if (pc >= TRAP_BASE) {
        b = traps[(pc - TRAP_BASE) >>> 2];
      } else {
        throw new Error('jump outside code: 0x' + pc.toString(16) + ' lr=0x' + (r[14] >>> 0).toString(16));
      }
      g += b(lim - g);
    }
    this.depth--;
    return g;
  }

  // run() with cpu.prof (a Map) counting instructions per block address. Diagnostics only.
  runProfiled(budget) {
    const r = this.r, base = this.codeBase, size = this.codeSize, prof = this.prof;
    let n = budget;
    this.stop = false;
    while (n > 0 && !this.stop) {
      const pc = r[15] >>> 0;
      const off = (pc - base) >>> 0;
      let b;
      if (off < size) {
        b = this.tab[off];
        if (b === undefined) b = this.compile(pc);
      } else if (pc >= TRAP_BASE) {
        b = this.traps[(pc - TRAP_BASE) >>> 2];
      } else {
        throw new Error('jump outside code: 0x' + pc.toString(16) + ' lr=0x' + (r[14] >>> 0).toString(16));
      }
      const c = b();
      n -= c;
      prof.set(pc, (prof.get(pc) || 0) + c);
    }
    const done = budget - n;
    this.count += done;
    return done;
  }

  ru32(a) { const m = this.m8; return m[a] | (m[a + 1] << 8) | (m[a + 2] << 16) | (m[a + 3] << 24); }
  ru16(a) { const m = this.m8; return m[a] | (m[a + 1] << 8); }
  rs16(a) { return (this.ru16(a) << 16) >> 16; }
  wu32(a, v) { const m = this.m8; m[a] = v; m[a + 1] = v >> 8; m[a + 2] = v >> 16; m[a + 3] = v >> 24; }
  wu16(a, v) { const m = this.m8; m[a] = v; m[a + 1] = v >> 8; }

  umul(a, b) {
    a >>>= 0; b >>>= 0;
    const al = a & 0xffff, ah = a >>> 16, bl = b & 0xffff, bh = b >>> 16;
    const ll = al * bl, lh = al * bh, hl = ah * bl, hh = ah * bh;
    const t = ll + (lh & 0xffff) * 65536 + (hl & 0xffff) * 65536;
    this.lo = t % 4294967296 | 0;
    this.hi = (hh + (lh >>> 16) + (hl >>> 16) + Math.floor(t / 4294967296)) | 0;
  }
  smul(a, b) {
    this.umul(a, b);
    if (a < 0) this.hi = (this.hi - b) | 0;
    if (b < 0) this.hi = (this.hi - a) | 0;
  }
  add64(l, h) {
    const t = (this.lo >>> 0) + (l >>> 0);
    this.lo = t % 4294967296 | 0;
    this.hi = (this.hi + h + (t > 4294967295 ? 1 : 0)) | 0;
  }
  clz(v) { return Math.clz32(v); }
  undefinedInstr(pc, op) {
    throw new Error('undefined instruction 0x' + (op >>> 0).toString(16) + ' at 0x' + (pc >>> 0).toString(16));
  }

  flushCode() {
    for (const off of this.codeOffs) if (this.tab[off] && !this.tab[off].hooked) this.tab[off] = undefined;
    this.codeOffs = [];
    this.hits.fill(0);
  }

  // One basic block: decoded instructions, the flags each must write, and where it can go next.
  decodeBlock(key) {
    const thumb = key & 1;
    let pc = key & ~1;
    if (!thumb) pc = key & ~3;
    const list = [];
    let term = false;
    while (!term && list.length < MAX_BLOCK) {
      const ins = thumb ? this.decodeThumb(pc) : this.decodeArm(pc);
      list.push(ins);
      term = !!ins.term;
      pc = (pc + ins.len) >>> 0;
    }
    const end = pc;
    flagPass(list, FN | FZ | FC | FV);
    const last = list[list.length - 1];
    const next = (end | thumb) | 0;
    // Static successors: branch target, fall-through, and a call's return address.
    const succ = [];
    if (!term) succ.push(next);
    else {
      if (last.target !== undefined) succ.push(last.target | 0);
      if (last.cond !== undefined || last.call) succ.push(next);
      if (last.callTarget !== undefined && !this.pltSlot(last.callTarget)) succ.push(last.callTarget | 0);
    }
    // Where flags can flow on: null when control may reach code outside a region's view
    // (a call, or a jump through a register), which then may read every flag.
    let flow = null;
    if (!term) flow = [next];
    else if (!last.call && last.target !== undefined) flow = last.cond !== undefined ? [last.target | 0, next] : [last.target | 0];
    return { key: key | 0, thumb, list, term, end, next, succ, flow, selfLoop: !!(last && last.cond !== undefined && last.target === (key | 0)) };
  }

  // JS for one block. Ends with r[15] set; `done` closes the block (a return, or a break in a region).
  blockSource(b, done) {
    const { list, key, thumb } = b;
    let src = '';
    if (b.selfLoop) {
      const last = list[list.length - 1];
      src += 'q=0;for(;;){';
      for (let i = 0; i < list.length - 1; i++) {
        const ins = list[i];
        const body = ins.emit(ins.out);
        src += ins.cond !== undefined ? `if(${CONDS[ins.cond][0]}){${body}}` : body;
      }
      // In a region `done` is a break: it leaves this loop, and the break after it leaves the case.
      src += `g+=${list.length};q+=${list.length};if(${CONDS[last.cond][0]}){if(q<4096)continue;r[15]=${key};${done}}r[15]=${b.next};${done}}`;
      return done === 'break;' ? src + 'break;' : src;
    }
    for (const ins of list) {
      const body = ins.emit(ins.out);
      if (ins.cond !== undefined) {
        const next = (ins.pc + ins.len) >>> 0;
        src += ins.term
          ? `r[15]=${(next | thumb) | 0};if(${CONDS[ins.cond][0]}){${body}}`
          : `if(${CONDS[ins.cond][0]}){${body}}`;
      } else {
        src += body;
      }
    }
    if (!b.term) src += `r[15]=${b.next};`;
    return src + `g+=${list.length};${done}`;
  }

  // ARM PLT stub at addr (add ip, pc, #a; add ip, ip, #b; ldr pc, [ip, #c]!): its GOT slot, else 0.
  pltSlot(addr) {
    if (addr & 3) return 0;
    const off = ((addr >>> 0) - this.codeBase) >>> 0;
    if (off + 12 > this.codeSize) return 0;
    const m = this.m32, i = addr >>> 2;
    const a = m[i], b = m[i + 1], c = m[i + 2];
    if ((a & 0x0ffff000) !== 0x028fc000 || (b & 0x0ffff000) !== 0x028cc000 || (c & 0x0ffff000) !== 0x05bcf000) return 0;
    if ((a >>> 28) !== 14 || (b >>> 28) !== 14 || (c >>> 28) !== 14) return 0;
    const imm = op => rotr(op & 255, ((op >>> 8) & 15) * 2);
    return (addr + 8 + imm(a) + imm(b) + (c & 0xfff)) >>> 0;
  }

  // After a call block: run the callee without the dispatcher, with run()'s checks before each step.
  // A PLT stub runs as its three instructions plus the import's trap; a hook is called directly;
  // a callee inside this region is just the next case; anything else runs through callIn.
  callSource(target, ret, member) {
    const ok = 'g<lim&&!cpu.stop';
    let src = '';
    if (target === undefined) return `if(r[15]!==${ret}&&${ok}&&cpu.depth<${CALL_DEPTH})g+=cpu.callIn(${ret},lim-g);break;`;
    target |= 0;
    const slot = this.pltSlot(target);
    const off = ((target >>> 0) - this.codeBase) >>> 0;
    const t = off < this.codeSize ? this.tab[off] : undefined;
    if (slot) {
      src += `if(r[15]===${target}&&${ok}){r[12]=${slot | 0};x=m32[${slot >>> 2}];r[15]=x&1?x:x&~3;g+=3;` +
        `if(${ok}&&(x=r[15]>>>0)>=${TRAP_BASE})g+=cpu.traps[(x-${TRAP_BASE})>>>2]();}`;
    } else if (t !== undefined && t.hooked) {
      src += `if(r[15]===${target}&&${ok})g+=cpu.tab[${off}]();`;
    } else if (member.has(target)) {
      return 'break;';
    }
    return src + `if(r[15]!==${ret}&&${ok}&&cpu.depth<${CALL_DEPTH})g+=cpu.callIn(${ret},lim-g);break;`;
  }

  link(src) {
    return new Function('r', 'f', 'm8', 's8', 'm16', 's16', 'm32', 'cpu',
      'return function(lim){' + src + '}')(this.r, this.f, this.m8, this.s8, this.m16, this.s16, this.m32, this);
  }

  // A single block as a function returning its instruction count.
  compile(key) {
    const b = this.decodeBlock(key);
    const fn = this.link('let a=0,b=0,c=0,t=0,v=0,n=0,m=0,o=0,h=0,x=0,g=0,q=0;' + this.blockSource(b, 'return g;'));
    fn.single = true;
    this.compiled++;
    this.tab[(key >>> 0) - this.codeBase] = fn;
    this.codeOffs.push((key >>> 0) - this.codeBase);
    return fn;
  }

  // A region: the blocks reachable from key by direct branches, fall-throughs and call returns,
  // nearby and not hooked, in one function that switches on r[15] until control leaves it.
  // Entering at any member block runs the same function, so the dispatcher sees far fewer calls.
  compileRegion(key) {
    const base = this.codeBase, size = this.codeSize, tab = this.tab;
    const lo = (key >>> 0) - REGION_SPAN, hi = (key >>> 0) + REGION_SPAN;
    const blocks = [], seen = new Set([key | 0]), todo = [key | 0];
    let instrs = 0;
    while (todo.length && blocks.length < REGION_BLOCKS && instrs < REGION_INSTRS) {
      const k = todo.shift();
      let b;
      try { b = this.decodeBlock(k); } catch (e) { if (k === (key | 0)) throw e; continue; }
      blocks.push(b);
      instrs += b.list.length;
      for (const s of b.succ) {
        const u = s >>> 0, off = (u - base) >>> 0;
        if (seen.has(s) || u < lo || u > hi || off >= size) continue;
        const t = tab[off];
        // Only code that has already run: cold paths leave the region and compile on their own.
        if (t === undefined || t.hooked) continue;
        seen.add(s);
        todo.push(s);
      }
    }
    if (blocks.length === 1) return tab[((key >>> 0) - base) >>> 0];
    const member = new Set(blocks.map(b => b.key));
    // Flags across the region: a write is dropped only when every path overwrites it before a read
    // or before leaving the region (where all flags count as read), so the state stays the same.
    const byKey = new Map(blocks.map(b => [b.key, b]));
    const liveIn = new Map(blocks.map(b => [b.key, 0]));
    for (let changed = true; changed;) {
      changed = false;
      for (let i = blocks.length - 1; i >= 0; i--) {
        const b = blocks[i];
        let out = 0;
        if (!b.flow) out = 15;
        else for (const s of b.flow) out |= byKey.has(s) ? liveIn.get(s) : 15;
        const inn = flagPass(b.list, out);
        if (inn !== liveIn.get(b.key)) { liveIn.set(b.key, inn); changed = true; }
      }
    }
    // lim is the run budget left: the region stops on the same block boundary the dispatcher would.
    let src = 'let a=0,b=0,c=0,t=0,v=0,n=0,m=0,o=0,h=0,x=0,g=0,q=0;for(;;){switch(r[15]){';
    for (const b of blocks) {
      // A call runs the callee from here until it comes back to the return address, so the
      // dispatcher is not involved in call and return. Depth bounds the JS stack.
      const last = b.list[b.list.length - 1];
      const done = b.term && last.call ? this.callSource(last.callTarget, b.next, member) : 'break;';
      src += `case ${b.key}:{` + this.blockSource(b, done) + '}';
    }
    src += 'default:return g;}if(g>=lim||cpu.stop)return g;}';
    const fn = this.link(src);
    this.compiled += blocks.length;
    this.regions++;
    for (const b of blocks) {
      const off = ((b.key >>> 0) - base) >>> 0;
      const t = tab[off];
      if (t === undefined || t.single) { tab[off] = fn; this.codeOffs.push(off); }
    }
    return fn;
  }

  decodeArm(pc) {
    const op = this.m32[pc >>> 2];
    const cc = op >>> 28;
    const ins = { pc, len: 4 };
    if (cc !== 14 && cc !== 15) ins.cond = cc;
    const REG = i => i === 15 ? k(pc + 8) : `r[${i}]`;

    if (cc === 15) {
      if ((op & 0x0e000000) === 0x0a000000) { // BLX imm
        const off = ((op << 8) >> 6) + ((op >> 23) & 2);
        const target = (pc + 8 + off + 1) | 0;
        ins.term = true;
        ins.call = true;
        ins.callTarget = target;
        ins.emit = () => `r[14]=${(pc + 4) | 0};r[15]=${target};`;
        return ins;
      }
      if ((op & 0x0d70f000) === 0x0550f000) { ins.emit = () => ''; return ins; } // PLD
      ins.term = true;
      ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
      return ins;
    }

    const cls = (op >>> 25) & 7;
    const rd = (op >>> 12) & 15, rn = (op >>> 16) & 15, rm = op & 15, rs = (op >>> 8) & 15;

    if ((op & 0x0ffffff0) === 0x012fff10 || (op & 0x0ffffff0) === 0x012fff30) { // BX, BLX reg
      const link = op & 0x20;
      ins.term = true;
      if (link) ins.call = true;
      ins.emit = () => `x=r[${rm}];` + (link ? `r[14]=${(pc + 4) | 0};` : '') + INTERWORK;
      return ins;
    }
    if ((op & 0x0fff0ff0) === 0x016f0f10) { // CLZ
      ins.emit = () => `r[${rd}]=Math.clz32(r[${rm}]);`;
      return ins;
    }
    if ((op & 0x0fbf0fff) === 0x010f0000) { // MRS
      ins.use = 15;
      ins.emit = () => `r[${rd}]=(f[0]<<31)|(f[1]<<30)|(f[2]<<29)|(f[3]<<28)|0x10;`;
      return ins;
    }
    if ((op & 0x0fb0f000) === 0x0320f000 || (op & 0x0fb0fff0) === 0x0120f000) { // MSR
      const flags = (op >>> 19) & 1;
      const val = (op & 0x02000000) ? k(rotr(op & 255, ((op >>> 8) & 15) * 2)) : REG(rm);
      if (flags) ins.def = 15;
      ins.emit = () => flags ? `v=${val};f[0]=v>>>31;f[1]=(v>>>30)&1;f[2]=(v>>>29)&1;f[3]=(v>>>28)&1;` : '';
      return ins;
    }

    if (cls === 0 && (op & 0x90) === 0x90) {
      const b65 = (op >>> 5) & 3;
      if (b65 === 0) {
        if (((op >>> 23) & 31) === 2) { // SWP
          const byte = op & 0x400000;
          ins.emit = () => byte
            ? `a=r[${rn}];x=m8[a];m8[a]=r[${rm}];r[${rd}]=x;`
            : `a=r[${rn}];x=${load32};${store32(`r[${rm}]`)}r[${rd}]=x;`;
          return ins;
        }
        const S = (op >>> 20) & 1;
        const kind = (op >>> 21) & 7;
        const hiR = rn, loR = rd, A = `r[${rm}]`, B = `r[${rs}]`;
        if (S) ins.def = FN | FZ;
        if (kind === 0 || kind === 1) { // MUL, MLA
          const add = kind === 1 ? `+r[${loR}]` : '';
          ins.emit = out => `t=(Math.imul(${A},${B})${add})|0;` + nz(out) + `r[${hiR}]=t;`;
          return ins;
        }
        if (kind >= 4) { // UMULL UMLAL SMULL SMLAL
          const signed = kind >= 6, accum = kind & 1;
          ins.emit = out => `cpu.${signed ? 'smul' : 'umul'}(${A},${B});` +
            (accum ? `cpu.add64(r[${loR}],r[${hiR}]);` : '') +
            `r[${loR}]=cpu.lo;r[${hiR}]=cpu.hi;` +
            (out & FN ? 'f[0]=cpu.hi>>>31;' : '') + (out & FZ ? 'f[1]=(cpu.hi|cpu.lo)===0?1:0;' : '');
          return ins;
        }
      } else {
        // Halfword and doubleword transfers
        const P = (op >>> 24) & 1, U = (op >>> 23) & 1, I = (op >>> 22) & 1, W = (op >>> 21) & 1, L = (op >>> 20) & 1;
        const off = I ? ((op >>> 4) & 0xf0) | (op & 15) : 0;
        const O = I ? k(off) : `r[${rm}]`;
        const sgn = U ? '+' : '-';
        const addr = P ? `a=(${REG(rn)}${sgn}${O})|0;` : `a=${REG(rn)};`;
        const wb = (!P || W) ? (P ? `r[${rn}]=a;` : `r[${rn}]=(a${sgn}${O})|0;`) : '';
        const noWb = L && rn === rd;
        ins.emit = () => {
          if (L) {
            if (b65 === 1) return addr + `x=${load16};` + (noWb ? '' : wb) + `r[${rd}]=x;`;
            if (b65 === 2) return addr + `x=s8[a];` + (noWb ? '' : wb) + `r[${rd}]=x;`;
            return addr + `x=${loadS16};` + (noWb ? '' : wb) + `r[${rd}]=x;`;
          }
          if (b65 === 1) return addr + store16(`r[${rd}]`) + wb;
          if (b65 === 2) return addr + (rn === rd || rn === rd + 1 ? 'c=a;' : '') + `x=${load32};a=(a+4)|0;n=${load32};` +
            (rn === rd || rn === rd + 1 ? 'a=c;' : 'a=(a-4)|0;') + `r[${rd}]=x;r[${rd + 1}]=n;` + wb;
          return addr + store32(`r[${rd}]`) + `c=a;a=(a+4)|0;` + store32(`r[${rd + 1}]`) + 'a=c;' + wb;
        };
        return ins;
      }
    }

    if (cls === 0 && (op & 0x01900000) === 0x01000000 && (op & 0x90) !== 0x90) {
      // DSP multiplies and saturating arithmetic
      if ((op & 0x0f900090) === 0x01000080) {
        const kind = (op >>> 21) & 3;
        const X = (op >>> 5) & 1, Y = (op >>> 6) & 1;
        const half = (R, top) => top ? `(${R}>>16)` : `((${R}<<16)>>16)`;
        const A = `r[${rm}]`, B = `r[${rs}]`;
        if (kind === 0) { ins.emit = () => `r[${rn}]=(Math.imul(${half(A, X)},${half(B, Y)})+r[${rd}])|0;`; return ins; }
        if (kind === 3) { ins.emit = () => `r[${rn}]=Math.imul(${half(A, X)},${half(B, Y)});`; return ins; }
        if (kind === 1) {
          ins.emit = () => `cpu.smul(${A},${half(B, Y)});x=(cpu.lo>>>16)|(cpu.hi<<16);r[${rn}]=${X ? 'x' : `(x+r[${rd}])|0`};`;
          return ins;
        }
        ins.emit = () => `cpu.smul(${half(A, X)},${half(B, Y)});cpu.add64(r[${rd}],r[${rn}]);r[${rd}]=cpu.lo;r[${rn}]=cpu.hi;`;
        return ins;
      }
      if ((op & 0x0f900ff0) === 0x01000050) { // QADD QSUB QDADD QDSUB
        const kind = (op >>> 21) & 3;
        const sat = 'Math.max(-2147483648,Math.min(2147483647,';
        const B = (kind & 2) ? `${sat}r[${rn}]*2))` : `r[${rn}]`;
        ins.emit = () => `r[${rd}]=${sat}r[${rm}]${kind & 1 ? '-' : '+'}${B}));`;
        return ins;
      }
      ins.term = true;
      ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
      return ins;
    }

    if (cls === 0 || cls === 1) { // Data processing
      const code = (op >>> 21) & 15;
      const S = (op >>> 20) & 1;
      const cmp = code >= 8 && code <= 11;
      let op2 = '', B, sc = null, pre = '';
      let useC = false;
      if (cls === 1) {
        const rot = ((op >>> 8) & 15) * 2;
        const imm = rotr(op & 255, rot);
        B = k(imm);
        if (rot) sc = String(imm >>> 31);
      } else if (op & 0x10) {
        const type = (op >>> 5) & 3;
        const RM = rm === 15 ? k(pc + 12) : `r[${rm}]`;
        const wantC = S && (code === 0 || code === 1 || code === 8 || code === 9 || code >= 12);
        pre = shiftReg(type, RM, `r[${rs}]`, wantC);
        B = 'o';
        if (wantC) { sc = 'h'; useC = true; }
      } else {
        const sh = shiftImm((op >>> 5) & 3, REG(rm), (op >>> 7) & 31);
        B = sh.val;
        sc = sh.c;
        useC = !!sh.useC;
      }
      const A = rn === 15 ? k(pc + 8 + ((cls === 0 && (op & 0x10)) ? 4 : 0)) : `r[${rn}]`;
      if (S && rd !== 15) ins.def = ALU_DEF(code);
      ins.use = ALU_USE(code) | (useC ? FC : 0);
      if (rd === 15 && !cmp) ins.term = true;
      ins.emit = out => {
        const body = pre + alu(code, A, B, S ? out : 0, sc);
        if (cmp) return body;
        if (rd === 15) return body + 'x=t;' + INTERWORK;
        return body + `r[${rd}]=t;`;
      };
      return ins;
    }

    if (cls === 2 || cls === 3) { // LDR STR
      if (cls === 3 && (op & 0x10)) {
        ins.term = true;
        ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
        return ins;
      }
      const P = (op >>> 24) & 1, U = (op >>> 23) & 1, B = (op >>> 22) & 1, W = (op >>> 21) & 1, L = (op >>> 20) & 1;
      let O;
      if (cls === 2) O = k(op & 0xfff);
      else {
        const sh = shiftImm((op >>> 5) & 3, REG(rm), (op >>> 7) & 31);
        O = sh.val;
        if (sh.useC) ins.use = FC;
      }
      const sgn = U ? '+' : '-';
      const addr = P ? `a=(${REG(rn)}${sgn}${O})|0;` : `a=${REG(rn)};`;
      const wb = (!P || W) ? (P ? `r[${rn}]=a;` : `r[${rn}]=(a${sgn}${O})|0;`) : '';
      if (L && rd === 15) ins.term = true;
      ins.emit = () => {
        if (L) {
          const got = addr + `x=${B ? 'm8[a]' : load32};`;
          if (rd === 15) return got + wb + INTERWORK;
          return got + (rn === rd ? '' : wb) + `r[${rd}]=x;`;
        }
        const V = rd === 15 ? k(pc + 12) : `r[${rd}]`;
        return addr + (B ? `m8[a]=${V};` : store32(V)) + wb;
      };
      return ins;
    }

    if (cls === 4) { // LDM STM
      const P = (op >>> 24) & 1, U = (op >>> 23) & 1, W = (op >>> 21) & 1, L = (op >>> 20) & 1;
      const regs = [];
      for (let i = 0; i < 16; i++) if (op & (1 << i)) regs.push(i);
      const size = regs.length * 4;
      const start = U ? (P ? 4 : 0) : (P ? -size : -size + 4);
      const after = U ? size : -size;
      if (L && (op & 0x8000)) ins.term = true;
      ins.emit = () => {
        let s = `c=r[${rn}];a=(c+${k(start)})|0;`;
        for (const i of regs) {
          if (L) {
            if (i === 15) s += 'x=m32[a>>>2];';
            else s += `r[${i}]=m32[a>>>2];`;
          } else {
            s += `m32[a>>>2]=${i === 15 ? k(pc + 12) : `r[${i}]`};`;
          }
          s += 'a=(a+4)|0;';
        }
        if (W && !(L && (op & (1 << rn)))) s += `r[${rn}]=(c+${k(after)})|0;`;
        if (L && (op & 0x8000)) s += INTERWORK;
        return s;
      };
      return ins;
    }

    if (cls === 5) { // B BL
      const off = (op << 8) >> 6;
      const target = (pc + 8 + off) | 0;
      const link = (op >>> 24) & 1;
      ins.term = true;
      if (!link) ins.target = target; else { ins.call = true; ins.callTarget = target; }
      ins.emit = () => (link ? `r[14]=${(pc + 4) | 0};` : '') + `r[15]=${target};`;
      return ins;
    }

    if ((op & 0x0f000000) === 0x0f000000) { // SWI
      ins.term = true;
      ins.emit = () => `r[15]=${(pc + 4) | 0};cpu.swi&&cpu.swi(${op & 0xffffff});`;
      return ins;
    }
    ins.term = true;
    ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
    return ins;
  }

  decodeThumb(pc) {
    const op = this.m16[pc >>> 1];
    const ins = { pc, len: 2 };
    const top = op >>> 11;
    const PCV = (pc + 4) | 0;

    if (top <= 2) { // LSL LSR ASR imm
      const type = top, amt = (op >>> 6) & 31, rs = (op >>> 3) & 7, rd = op & 7;
      const sh = shiftImm(type, `r[${rs}]`, amt);
      ins.def = type === 0 && amt === 0 ? FN | FZ : FN | FZ | FC;
      ins.emit = out => `t=${sh.val}|0;` + nz(out) + (out & FC && sh.c ? `f[2]=${sh.c};` : '') + `r[${rd}]=t;`;
      return ins;
    }
    if (top === 3) { // ADD SUB reg or imm3
      const I = (op >>> 10) & 1, sub = (op >>> 9) & 1, f6 = (op >>> 6) & 7, rs = (op >>> 3) & 7, rd = op & 7;
      const B = I ? k(f6) : `r[${f6}]`;
      ins.def = 15;
      ins.emit = out => alu(sub ? 2 : 4, `r[${rs}]`, B, out) + `r[${rd}]=t;`;
      return ins;
    }
    if (top <= 7) { // MOV CMP ADD SUB imm8
      const kind = top - 4, rd = (op >>> 8) & 7, imm = op & 255;
      if (kind === 0) {
        ins.def = FN | FZ;
        ins.emit = out => `t=${imm};` + nz(out) + `r[${rd}]=t;`;
        return ins;
      }
      ins.def = 15;
      ins.emit = out => {
        if (kind === 1) return alu(2, `r[${rd}]`, k(imm), out);
        return alu(kind === 2 ? 4 : 2, `r[${rd}]`, k(imm), out) + `r[${rd}]=t;`;
      };
      return ins;
    }
    if ((op >>> 10) === 16) { // ALU reg
      const code = (op >>> 6) & 15, rs = (op >>> 3) & 7, rd = op & 7;
      const D = `r[${rd}]`, S = `r[${rs}]`;
      switch (code) {
        case 0: case 1: case 12: case 14: case 15: {
          const c = { 0: 0, 1: 1, 12: 12, 14: 14, 15: 15 }[code];
          ins.def = FN | FZ;
          ins.emit = out => alu(c, D, S, out) + `${D}=t;`;
          return ins;
        }
        case 2: case 3: case 4: case 7: {
          const type = { 2: 0, 3: 1, 4: 2, 7: 3 }[code];
          ins.def = FN | FZ | FC;
          ins.use = FC;
          ins.emit = out => shiftReg(type, D, S, !!(out & FC)) + 't=o|0;' + nz(out) + (out & FC ? 'f[2]=h;' : '') + `${D}=t;`;
          return ins;
        }
        case 5: case 6:
          ins.def = 15; ins.use = FC;
          ins.emit = out => alu(code, D, S, out) + `${D}=t;`;
          return ins;
        case 8: ins.def = FN | FZ; ins.emit = out => alu(0, D, S, out); return ins;
        case 9: ins.def = 15; ins.emit = out => alu(2, '0', S, out) + `${D}=t;`; return ins;
        case 10: ins.def = 15; ins.emit = out => alu(2, D, S, out); return ins;
        case 11: ins.def = 15; ins.emit = out => alu(4, D, S, out); return ins;
        case 13: ins.def = FN | FZ; ins.emit = out => `t=Math.imul(${D},${S});` + nz(out) + `${D}=t;`; return ins;
      }
    }
    if ((op >>> 10) === 17) { // Hi-register ops, BX, BLX
      const code = (op >>> 8) & 3;
      const rs = (op >>> 3) & 15;
      const rd = (op & 7) | ((op >>> 4) & 8);
      const RS = rs === 15 ? k(PCV) : `r[${rs}]`;
      const RD = rd === 15 ? k(PCV) : `r[${rd}]`;
      if (code === 3) {
        const link = op & 0x80;
        ins.term = true;
        if (link) ins.call = true;
        ins.emit = () => `x=${RS};` + (link ? `r[14]=${(pc + 3) | 0};` : '') + INTERWORK;
        return ins;
      }
      if (code === 1) { ins.def = 15; ins.emit = out => alu(2, RD, RS, out); return ins; }
      if (rd === 15) ins.term = true;
      ins.emit = () => {
        const val = code === 0 ? `(${RD}+${RS})|0` : RS;
        return rd === 15 ? `r[15]=(${val})|1;` : `r[${rd}]=${val};`;
      };
      return ins;
    }
    if (top === 9) { // LDR literal
      const rd = (op >>> 8) & 7, addr = ((pc + 4) & ~3) + (op & 255) * 4;
      ins.emit = () => `r[${rd}]=m32[${addr >>> 2}];`;
      return ins;
    }
    if ((op >>> 12) === 5) { // Register offset
      const kind = (op >>> 9) & 7, ro = (op >>> 6) & 7, rb = (op >>> 3) & 7, rd = op & 7;
      const addr = `a=(r[${rb}]+r[${ro}])|0;`, D = `r[${rd}]`;
      ins.emit = () => {
        switch (kind) {
          case 0: return addr + store32(D);
          case 1: return addr + store16(D);
          case 2: return addr + `m8[a]=${D};`;
          case 3: return addr + `${D}=s8[a];`;
          case 4: return addr + `${D}=${load32};`;
          case 5: return addr + `${D}=${load16};`;
          case 6: return addr + `${D}=m8[a];`;
          case 7: return addr + `${D}=${loadS16};`;
        }
      };
      return ins;
    }
    if ((op >>> 13) === 3) { // Imm offset word, byte
      const B = (op >>> 12) & 1, L = (op >>> 11) & 1, imm = (op >>> 6) & 31, rb = (op >>> 3) & 7, rd = op & 7;
      const addr = `a=(r[${rb}]+${B ? imm : imm * 4})|0;`, D = `r[${rd}]`;
      ins.emit = () => addr + (L ? `${D}=${B ? 'm8[a]' : load32};` : (B ? `m8[a]=${D};` : store32(D)));
      return ins;
    }
    if ((op >>> 12) === 8) { // Halfword imm offset
      const L = (op >>> 11) & 1, imm = (op >>> 6) & 31, rb = (op >>> 3) & 7, rd = op & 7;
      const addr = `a=(r[${rb}]+${imm * 2})|0;`, D = `r[${rd}]`;
      ins.emit = () => addr + (L ? `${D}=${load16};` : store16(D));
      return ins;
    }
    if ((op >>> 12) === 9) { // SP relative
      const L = (op >>> 11) & 1, rd = (op >>> 8) & 7, imm = (op & 255) * 4;
      const addr = `a=(r[13]+${imm})|0;`, D = `r[${rd}]`;
      ins.emit = () => addr + (L ? `${D}=${load32};` : store32(D));
      return ins;
    }
    if ((op >>> 12) === 10) { // ADD rd, pc/sp, imm
      const SP = (op >>> 11) & 1, rd = (op >>> 8) & 7, imm = (op & 255) * 4;
      ins.emit = () => SP ? `r[${rd}]=(r[13]+${imm})|0;` : `r[${rd}]=${((PCV & ~3) + imm) | 0};`;
      return ins;
    }
    if ((op >>> 8) === 0xb0) { // ADD SP
      const imm = (op & 127) * 4;
      ins.emit = () => `r[13]=(r[13]${op & 0x80 ? '-' : '+'}${imm})|0;`;
      return ins;
    }
    if ((op >>> 12) === 11 && ((op >>> 9) & 3) === 2) { // PUSH POP
      const L = (op >>> 11) & 1, R = (op >>> 8) & 1;
      const regs = [];
      for (let i = 0; i < 8; i++) if (op & (1 << i)) regs.push(i);
      if (L && R) ins.term = true;
      ins.emit = () => {
        if (L) {
          let s = 'a=r[13];';
          for (const i of regs) s += `r[${i}]=m32[a>>>2];a=(a+4)|0;`;
          if (R) s += 'x=m32[a>>>2];a=(a+4)|0;';
          s += 'r[13]=a;';
          if (R) s += INTERWORK;
          return s;
        }
        const n = regs.length + (R ? 1 : 0);
        let s = `a=(r[13]-${n * 4})|0;r[13]=a;`;
        for (const i of regs) s += `m32[a>>>2]=r[${i}];a=(a+4)|0;`;
        if (R) s += 'm32[a>>>2]=r[14];';
        return s;
      };
      return ins;
    }
    if ((op >>> 8) === 0xbe) { // BKPT
      ins.term = true;
      ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
      return ins;
    }
    if ((op >>> 12) === 12) { // LDMIA STMIA
      const L = (op >>> 11) & 1, rb = (op >>> 8) & 7;
      const regs = [];
      for (let i = 0; i < 8; i++) if (op & (1 << i)) regs.push(i);
      ins.emit = () => {
        let s = `a=r[${rb}];`;
        for (const i of regs) {
          s += L ? `r[${i}]=m32[a>>>2];` : `m32[a>>>2]=r[${i}];`;
          s += 'a=(a+4)|0;';
        }
        if (!(L && (op & (1 << rb)))) s += `r[${rb}]=a;`;
        return s;
      };
      return ins;
    }
    if ((op >>> 12) === 13) {
      const cc = (op >>> 8) & 15;
      if (cc === 15) { // SWI
        ins.term = true;
        ins.emit = () => `r[15]=${(pc + 3) | 0};cpu.swi&&cpu.swi(${op & 255});`;
        return ins;
      }
      if (cc === 14) {
        ins.term = true;
        ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
        return ins;
      }
      const target = (pc + 4 + (((op & 255) << 24) >> 23)) | 1;
      ins.cond = cc;
      ins.term = true;
      ins.target = target | 0;
      ins.emit = () => `r[15]=${target | 0};`;
      return ins;
    }
    if (top === 0x1c) { // B
      const target = (pc + 4 + (((op & 0x7ff) << 21) >> 20)) | 1;
      ins.term = true;
      ins.target = target | 0;
      ins.emit = () => `r[15]=${target | 0};`;
      return ins;
    }
    if (top === 0x1e) { // BL, BLX pair
      const next = this.m16[(pc + 2) >>> 1];
      const hi = ((op & 0x7ff) << 21) >> 9;
      const lo = (next & 0x7ff) << 1;
      const base = (pc + 4 + hi + lo) | 0;
      ins.len = 4;
      ins.term = true;
      ins.call = true;
      ins.callTarget = (next >>> 11) === 0x1f ? (base | 1) : (base & ~3);
      if ((next >>> 11) === 0x1f) ins.emit = () => `r[14]=${(pc + 5) | 0};r[15]=${base | 1};`;
      else ins.emit = () => `r[14]=${(pc + 5) | 0};r[15]=${base & ~3};`;
      return ins;
    }
    ins.term = true;
    ins.emit = () => `cpu.undefinedInstr(${pc | 0},${op | 0});`;
    return ins;
  }
}

CPU.TRAP_BASE = TRAP_BASE;
root.ApkCPU = CPU;
if (typeof module !== 'undefined') module.exports = CPU;
})(typeof globalThis !== 'undefined' ? globalThis : this);
