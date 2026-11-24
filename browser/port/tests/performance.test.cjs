const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
const { CPU: ApkCPU, Runtime: ApkRuntime } = require('../../../apk-cpu-engine/index.js');
require('../../../apk-cpu-engine/gl.js');
const ApkGLES1 = globalThis.ApkGLES1;

const elfPath = path.resolve(__dirname, '../../../work/browser_libgame.so');
const htmlPath = path.resolve(__dirname, '../../kns.html');

function loadBrowserElf() {
  if (fs.existsSync(elfPath)) return fs.readFileSync(elfPath);
  const html = fs.readFileSync(htmlPath, 'utf8');
  const m = /window\.KNS_ELF="([^"]+)"/.exec(html);
  assert.ok(m, 'browser/kns.html must embed window.KNS_ELF');
  return Buffer.from(m[1], 'base64');
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

test('Runtime native hooks (adler32, crc32, png_read_filter_row, inflate_fast) match reference', () => {
  const rt = new ApkRuntime({ vfs: {}, log() {} });
  rt.loadElf(loadBrowserElf());
  rt.startJavaThread();
  rt.switchTo(rt.javaThread);

  // 1. adler32 & crc32
  const payload = Buffer.from('Knights N Squires low-spec optimization verification payload 1234567890!');
  const ptr = rt.malloc(payload.length);
  rt.cpu.m8.set(payload, ptr);
  const adlerSym = rt.syms.get('adler32');
  const crcSym = rt.syms.get('crc32');
  assert.equal(rt.callSync(adlerSym, 0, 0, 0), 1);
  assert.equal(rt.callSync(crcSym, 0, 0, 0), 0);
  // Compute expected Adler-32
  let s1 = 1, s2 = 0;
  for (const b of payload) { s1 = (s1 + b) % 65521; s2 = (s2 + s1) % 65521; }
  const expectedAdler = ((s2 << 16) | s1) | 0;
  assert.equal(rt.callSync(adlerSym, 1, ptr, payload.length), expectedAdler);
  assert.equal(rt.callSync(crcSym, 0, ptr, payload.length) >>> 0, zlib.crc32(payload) >>> 0);

  // 2. png_read_filter_row (Paeth filter = 4)
  const rowInfo = rt.calloc(16);
  rt.cpu.u32[(rowInfo + 4) >>> 2] = 8; // rowbytes = 8
  rt.cpu.m8[rowInfo + 11] = 32;        // pixel_depth = 32 -> bpp = 4
  const row = rt.calloc(8);
  const prev = rt.calloc(8);
  rt.cpu.m8.set([10, 20, 30, 40, 5, 10, 15, 20], row);
  rt.cpu.m8.set([1, 2, 3, 4, 2, 4, 6, 8], prev);
  rt.callSync(rt.syms.get('png_read_filter_row'), 0, rowInfo, row, prev, 4);
  assert.deepEqual(Array.from(rt.cpu.m8.subarray(row, row + 8)), [11, 22, 33, 44, 16, 32, 48, 64]);

  // 3. KnS fast paths live in the port, not the engine; region flushes must keep the SGL cache hook.
  const sglAddr = rt.syms.get('SGL_grpCreateStringImageInRect') >>> 0;
  const lessAddr = rt.syms.get('_ZNK16CachedStringLessclERKP12CachedStringS3_') >>> 0;
  assert.ok(sglAddr && lessAddr, 'KnS ELF exports the SGL text-cache symbols');
  assert.ok(!rt.cpu.tab[sglAddr - rt.cpu.codeBase] || !rt.cpu.tab[sglAddr - rt.cpu.codeBase].hooked, 'engine alone installs no KnS hook');
  require('../native_hooks.js').install(rt);
  assert.equal(rt.cpu.tab[sglAddr - rt.cpu.codeBase].hooked, true);
  assert.ok(rt.cpu.tab[lessAddr - rt.cpu.codeBase], 'CachedStringLess hooked');

  // 4. SGL_RenderFontToAlphaImageInRect reads fontPixels with contiguous width stride (not 512)
  const webSrc = fs.readFileSync(path.resolve(__dirname, '../web.js'), 'utf8');
  assert.ok(webSrc.includes('copyOut(pixels, width, rows)'), 'drawLines must write pixels contiguously with stride=width');
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

test('Region JIT runs the browser ELF exactly like the block JIT (same instructions, registers and memory)', () => {
  const { execFileSync } = require('node:child_process');
  const bench = path.resolve(__dirname, '../bench_jit.js');
  const run = regions => JSON.parse(execFileSync(process.execPath, [bench, '300', '--json'], {
    env: { ...process.env, KNS_JIT_REGIONS: regions }, encoding: 'utf8', maxBuffer: 1 << 20,
  }));
  const block = run('0'), region = run('1');
  assert.equal(region.instr, block.instr);
  assert.equal(region.draws, block.draws);
  assert.deepEqual(region.hashes, block.hashes);
});
