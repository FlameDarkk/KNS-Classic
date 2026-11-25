const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { CPU: ApkCPU, Runtime: ApkRuntime, GL } = require('../index.js');
const ApkGLES1 = GL;

// Run a def()'d import the way guest code does: args in r0-r3, through its trap.
function callDef(rt, name, ...args) {
  const cpu = rt.cpu;
  args.forEach((a, i) => { cpu.r[i] = a; });
  cpu.traps[(rt.fns[name] - ApkCPU.TRAP_BASE) / 4]();
  return cpu.r[0] | 0;
}

test('CPU self-looping block JIT executes tight Thumb countdown loop in one dispatch', () => {
  const buf = new ArrayBuffer(0x10000);
  const cpu = new ApkCPU(buf);
  cpu.setCode(0x1000, 0x1000);
  // Thumb at 0x1000:
  // 0x1000: subs r0, #1 (0x3801)
  // 0x1002: bne 0x1000  (0xd1fd)
  // 0x1004: bx lr       (0x4770)
  cpu.m16[0x1000 >>> 1] = 0x3801;
  cpu.m16[0x1002 >>> 1] = 0xd1fd;
  cpu.m16[0x1004 >>> 1] = 0x4770;
  cpu.r[0] = 500;
  cpu.r[14] = cpu.addTrap('done', () => { cpu.stop = true; });
  cpu.r[15] = 0x1001;
  const b = cpu.compile(0x1001);
  const steps = b();
  assert.equal(cpu.r[0], 0);
  assert.equal(steps, 1000);
  assert.equal(cpu.r[15], 0x1005);
});

test('GLES1 packs vertex/texcoord streams into a single bufferUpload and deduplicates bindTexture/uniform4fv', () => {
  const counts = {};
  const mockGl = new Proxy({
    VERTEX_SHADER: 0x8B31, FRAGMENT_SHADER: 0x8B30, COMPILE_STATUS: 0x8B81, LINK_STATUS: 0x8B82,
    TEXTURE_2D: 0x0DE1, RGBA: 0x1908, UNSIGNED_BYTE: 0x1401, TEXTURE_MIN_FILTER: 0x2801,
    TEXTURE_MAG_FILTER: 0x2800, LINEAR: 0x2601, TEXTURE_WRAP_S: 0x2802, TEXTURE_WRAP_T: 0x2803,
    CLAMP_TO_EDGE: 0x812F, FRAMEBUFFER: 0x8D40, COLOR_ATTACHMENT0: 0x8CE0, RENDERBUFFER: 0x8D41,
    DEPTH_STENCIL: 0x84F9, DEPTH_STENCIL_ATTACHMENT: 0x821A, FRAMEBUFFER_COMPLETE: 0x8CD5,
    COLOR_BUFFER_BIT: 0x4000, DEPTH_BUFFER_BIT: 0x0100, STENCIL_BUFFER_BIT: 0x0400,
    ARRAY_BUFFER: 0x8892, ELEMENT_ARRAY_BUFFER: 0x8893, STATIC_DRAW: 0x88E4, STREAM_DRAW: 0x88E0,
    FLOAT: 0x1406, TRIANGLE_STRIP: 0x0005, UNPACK_ALIGNMENT: 0x0CF5, DITHER: 0x0BD0,
    createShader: () => ({}), shaderSource() {}, compileShader() {}, getShaderParameter: () => true,
    createProgram: () => ({}), attachShader() {}, bindAttribLocation() {}, linkProgram() {},
    getProgramParameter: () => true, useProgram() {}, getUniformLocation: (_, n) => n,
    createBuffer: () => ({}), createTexture: () => ({}), createFramebuffer: () => ({}),
    createRenderbuffer: () => ({}), checkFramebufferStatus: () => 0x8CD5,
  }, {
    get(target, prop) {
      if (prop in target) return target[prop];
      return (...args) => { counts[prop] = (counts[prop] || 0) + 1; };
    },
  });
  const canvas = { width: 2048, height: 1184, getContext: () => mockGl };
  const gl = new ApkGLES1(canvas, () => {});
  gl.batching = false; // the unbatched path; batching has its own test
  const cpu = new ApkCPU(new ArrayBuffer(0x10000));
  gl.attach({ cpu });

  // Bind same texture twice -> only 1 WebGL bindTexture call
  const beforeBind = counts.bindTexture || 0;
  gl.glBindTexture(0x0DE1, 7);
  gl.glBindTexture(0x0DE1, 7);
  assert.equal((counts.bindTexture || 0) - beforeBind, 1);

  // Enable vertex + texcoord arrays (GL_FIXED = 0x140C) and draw twice
  gl.glEnable(0x0DE1);
  gl.glEnableClientState(0x8074);
  gl.glEnableClientState(0x8078);
  gl.glVertexPointer(2, 0x140C, 0, 0x100);
  gl.glTexCoordPointer(2, 0x140C, 0, 0x200);
  const beforeBuf = counts.bufferData || 0;
  gl.glDrawArrays(0x0005, 0, 4);
  gl.glDrawArrays(0x0005, 0, 4);
  // Each draw uploads pos + tex together in a single bufferData call instead of 2
  assert.equal((counts.bufferData || 0) - beforeBuf, 2);
});

// A WebGL stand-in that logs every call with copies of its array arguments.
function loggingGl() {
  const log = [];
  const consts = {
    VERTEX_SHADER: 0x8B31, FRAGMENT_SHADER: 0x8B30, TEXTURE_2D: 0x0DE1, RGBA: 0x1908, UNSIGNED_BYTE: 0x1401,
    TEXTURE_MIN_FILTER: 0x2801, TEXTURE_MAG_FILTER: 0x2800, LINEAR: 0x2601, TEXTURE_WRAP_S: 0x2802, TEXTURE_WRAP_T: 0x2803,
    CLAMP_TO_EDGE: 0x812F, FRAMEBUFFER: 0x8D40, COLOR_ATTACHMENT0: 0x8CE0, RENDERBUFFER: 0x8D41, DEPTH_STENCIL: 0x84F9,
    DEPTH_STENCIL_ATTACHMENT: 0x821A, FRAMEBUFFER_COMPLETE: 0x8CD5, COLOR_BUFFER_BIT: 0x4000, DEPTH_BUFFER_BIT: 0x0100,
    STENCIL_BUFFER_BIT: 0x0400, ARRAY_BUFFER: 0x8892, ELEMENT_ARRAY_BUFFER: 0x8893, STATIC_DRAW: 0x88E4, STREAM_DRAW: 0x88E0,
    FLOAT: 0x1406, TRIANGLES: 0x0004, TRIANGLE_STRIP: 0x0005, UNPACK_ALIGNMENT: 0x0CF5, DITHER: 0x0BD0,
  };
  const fns = {
    getShaderParameter: () => true, getProgramParameter: () => true, getUniformLocation: (_, n) => n,
    createShader: () => ({}), createProgram: () => ({}), createBuffer: () => ({}), createTexture: () => ({}),
    createFramebuffer: () => ({}), createRenderbuffer: () => ({}), checkFramebufferStatus: () => 0x8CD5,
  };
  const gl = new Proxy({}, {
    get(_, prop) {
      if (prop in consts) return consts[prop];
      return (...args) => {
        log.push([prop, ...args.map(a => ArrayBuffer.isView(a) ? Array.from(a) : a)]);
        return fns[prop] ? fns[prop](...args) : undefined;
      };
    },
  });
  return { gl, log };
}
function quadSetup(cpu, gl, at, x, y) {
  // Four GL_FIXED corners of a 10x10 quad at (x, y), texcoords 0..1.
  const corners = [[0, 0], [10, 0], [10, 10], [0, 10]];
  corners.forEach(([cx, cy], i) => {
    cpu.m32[(at >>> 2) + i * 2] = (x + cx) * 65536;
    cpu.m32[(at >>> 2) + i * 2 + 1] = (y + cy) * 65536;
    cpu.m32[((at + 0x100) >>> 2) + i * 2] = cx / 10 * 65536;
    cpu.m32[((at + 0x100) >>> 2) + i * 2 + 1] = cy / 10 * 65536;
  });
  gl.glVertexPointer(2, 0x140C, 0, at);
  gl.glTexCoordPointer(2, 0x140C, 0, at + 0x100);
}

test('GLES1 batches same-state fans into one TRIANGLES draw with the modelview applied on the CPU', () => {
  const { gl: mock, log } = loggingGl();
  const gl = new ApkGLES1({ width: 2048, height: 1184, getContext: () => mock }, () => {});
  const cpu = new ApkCPU(new ArrayBuffer(0x10000));
  gl.attach({ cpu });
  gl.glBindTexture(0x0DE1, 3);
  gl.glEnable(0x0DE1);
  gl.glEnableClientState(0x8074);
  gl.glEnableClientState(0x8078);
  log.length = 0;
  for (let i = 0; i < 3; i++) {
    gl.glLoadIdentity();
    gl.glTranslatex(100 * 65536 * i, 0, 0);
    gl.glColor4x(65536, 32768, 0, 65536);
    quadSetup(cpu, gl, 0x1000 + i * 0x400, 1, 2);
    gl.glDrawArrays(0x0006, 0, 4);
  }
  assert.equal(log.filter(c => c[0] === 'drawArrays').length, 0, 'nothing drawn before a flush point');
  gl.present();
  const draws = log.filter(c => c[0] === 'drawArrays');
  assert.equal(draws.length, 2, 'one batch, then the present quad');
  assert.deepEqual(draws[0], ['drawArrays', 0x0004, 0, 18]);
  const data = log.find(c => c[0] === 'bufferData')[2];
  assert.equal(data.length, 18 * 10);
  const v = i => data.slice(i * 10, i * 10 + 10);
  // Fan (0, 1, 2): corner 0 of the third quad is (1 + 200, 2), z 0, w 1; color per vertex; texcoord.
  assert.deepEqual(v(12), [201, 2, 0, 1, 1, 0.5, 0, 1, 0, 0]);
  assert.deepEqual(v(14), [211, 12, 0, 1, 1, 0.5, 0, 1, 1, 1]);
  // The modelview uniform is identity while the batch draws.
  const mv = log.filter(c => c[0] === 'uniformMatrix4fv' && c[1] === 'u_mv').pop();
  assert.deepEqual(mv[3], [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
});

test('GLES1 flushes a batch before state changes and unbatchable draws, keeping order', () => {
  const { gl: mock, log } = loggingGl();
  const gl = new ApkGLES1({ width: 2048, height: 1184, getContext: () => mock }, () => {});
  const cpu = new ApkCPU(new ArrayBuffer(0x10000));
  gl.attach({ cpu });
  gl.glBindTexture(0x0DE1, 3);
  gl.glEnable(0x0DE1);
  gl.glEnableClientState(0x8074);
  gl.glEnableClientState(0x8078);
  log.length = 0;
  quadSetup(cpu, gl, 0x1000, 0, 0);
  gl.glDrawArrays(0x0006, 0, 4);
  gl.glBindTexture(0x0DE1, 4);          // texture change: flush, then bind
  gl.glDrawArrays(0x0006, 0, 4);
  gl.glBlendFunc(0x0302, 0x0303);       // blend change: flush
  gl.glDrawArrays(0x0006, 0, 4);
  gl.glAlphaFunc(0x0204, 0);            // emulator state only: compared at the next draw
  gl.glEnable(0x0BC0);
  gl.glDrawArrays(0x0006, 0, 4);
  gl.glDrawArrays(0x0001, 0, 4);        // GL_LINES is not batched: flush, then draw directly
  const seq = log.filter(c => ['drawArrays', 'bindTexture', 'blendFunc'].includes(c[0])).map(c => c[0] + ':' + c[1]);
  assert.deepEqual(seq, [
    'drawArrays:4', 'bindTexture:3553',
    'drawArrays:4', 'blendFunc:770',
    'drawArrays:4',
    'drawArrays:4', 'drawArrays:1',
  ]);
});

test('mktime normalizes the struct in place, filling tm_wday like libc', () => {
  const rt = new ApkRuntime({ vfs: {}, log() {} });
  const m = rt.cpu.m32;
  const tm = rt.staticBlock('test-tm', 64);
  // 2014-07-03 00:00:00 (a Thursday); tm_wday preset to garbage.
  [0, 0, 0, 3, 6, 114, 99].forEach((v, i) => { m[(tm >>> 2) + i] = v; });
  callDef(rt, 'mktime', tm);
  assert.equal(m[(tm >>> 2) + 6], 4);
  assert.equal(m[(tm >>> 2) + 3], 3);
});

test('Runtime without socketUrl: connect succeeds, the socket reads as closed and nothing is opened', () => {
  const rt = new ApkRuntime({ vfs: {}, log() {} });
  const opened = [];
  const saved = globalThis.WebSocket;
  globalThis.WebSocket = class { constructor(u) { opened.push(u); } };
  try {
    const call = (name, ...args) => callDef(rt, name, ...args);
    const fd = call('socket', 2, 1, 0);
    assert.equal(call('connect', fd, 0, 0), 0);
    assert.equal(call('recvfrom', fd, 0, 16), 0);
    assert.deepEqual(opened, []);
  } finally {
    globalThis.WebSocket = saved;
  }
});

test('Runtime with socketUrl opens a WebSocket to it', () => {
  const opened = [];
  const saved = globalThis.WebSocket;
  globalThis.WebSocket = class { constructor(u) { opened.push(u); this.readyState = 0; } };
  try {
    const rt = new ApkRuntime({ vfs: {}, log() {}, socketUrl: () => 'ws://example.test/chat' });
    const call = (name, ...args) => callDef(rt, name, ...args);
    const fd = call('socket', 2, 1, 0);
    assert.equal(call('connect', fd, 0, 0), 0);
    assert.deepEqual(opened, ['ws://example.test/chat']);
  } finally {
    globalThis.WebSocket = saved;
  }
});

test('allocator guards against double-free aliasing and returns NULL+ENOMEM on impossible guest sizes', () => {
  const logs = [];
  const rt = new ApkRuntime({ vfs: {}, log: s => logs.push(s) });
  rt.brk = 0x01000000;
  const errnoPtr = callDef(rt, '__errno') >>> 0;

  // Double-free must not duplicate the pointer in the free bin.
  const p = callDef(rt, 'malloc', 64) >>> 0;
  assert.ok(p > 0);
  callDef(rt, 'free', p);
  callDef(rt, 'free', p);
  const a = callDef(rt, 'malloc', 64) >>> 0;
  const b = callDef(rt, 'malloc', 64) >>> 0;
  assert.equal(a, p);
  assert.notEqual(b, a, 'double-free must not hand out the same block twice');

  // Negative 32-bit size (-2264 = 0xfffff728) returns NULL + ENOMEM without corrupting brk.
  const brkBefore = rt.brk;
  rt.cpu.r[14] = 0x00123456;
  assert.equal(callDef(rt, 'malloc', -2264), 0);
  assert.equal(rt.cpu.m32[errnoPtr >>> 2], 12);
  assert.equal(rt.brk, brkBefore);
  assert.ok(logs.some(l => l.includes('4294965032') && l.includes('-2264') && l.includes('0x123456')));

  // Subsequent normal allocation still works.
  const ok = callDef(rt, 'malloc', 128) >>> 0;
  assert.ok(ok > 0);

  // realloc failure returns NULL and preserves the original block contents.
  rt.cpu.m8[ok] = 0xAB;
  rt.cpu.m32[errnoPtr >>> 2] = 0;
  assert.equal(callDef(rt, 'realloc', ok, -2264), 0);
  assert.equal(rt.cpu.m32[errnoPtr >>> 2], 12);
  assert.equal(rt.cpu.m8[ok], 0xAB);

  // calloc overflow returns NULL + ENOMEM.
  rt.cpu.m32[errnoPtr >>> 2] = 0;
  assert.equal(callDef(rt, 'calloc', 0x10000, 0x10000), 0);
  assert.equal(rt.cpu.m32[errnoPtr >>> 2], 12);
});

test('engine sources stay game-agnostic', () => {
  const root = path.resolve(__dirname, '..');
  for (const name of fs.readdirSync(root).filter(n => n.endsWith('.js'))) {
    const src = fs.readFileSync(path.join(root, name), 'utf8');
    assert.doesNotMatch(src, /kns|com2us|KNS_/i, name + ' names a specific game');
  }
});
