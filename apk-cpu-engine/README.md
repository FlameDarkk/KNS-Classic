# apk-cpu-engine

Run a 32-bit Android ARM native game library (`lib/armeabi/lib*.so`) in a browser page (`file://` or HTTP) or in headless Node.js. Plain JavaScript, no dependencies, no build step.

The engine is game-agnostic. Each game supplies a thin adapter: its Java classes, its asset VFS, its page shell, and optional native fast paths. Knights N Squires (KnS) was the first port, and its `browser/port/` folder is the reference adapter.

## Files

| File | Global / export | Role |
|---|---|---|
| `cpu.js` | `ApkCPU` | ARMv5TE (ARM + Thumb, soft float) to JavaScript JIT. It compiles basic blocks, and optionally regions, on first execution. It also provides traps and hooks. |
| `runtime.js` | `ApkRuntime` | Android userland. It provides the ELF loader (`ET_DYN`, GOT/PLT relocations), malloc, bionic libc/libm, cooperative pthreads, stdio/dirent over a VFS, zlib/LZMA/PNG fast paths, `__aeabi_*` float helpers, and sockets as WebSockets. |
| `jni.js` | `ApkJava` | `JNIEnv*` / `JavaVM*` in guest memory. Java classes are plain JS objects: `java.classes['a/b/C'] = { 'name (sig)ret': fn }`. |
| `gl.js` | `ApkGLES1` | OpenGL ES 1.1 fixed function on WebGL 1. It handles GL_FIXED/float matrix stacks, client arrays, VBO shadowing, alpha test, `glTexEnv` combiners and draw batching. |
| `gl_null.js` | `makeNullGL` | Headless GL stand-in that counts calls. |
| `index.js` | `{ CPU, Runtime, Java, GL, makeNullGL }` | Node entry point. |

In a page, load the scripts in this order: `cpu.js`, `runtime.js`, `jni.js`, `gl.js`. You can also inline them into one self-contained `.html`.

## Runtime options

```js
const rt = new ApkRuntime({
  log: s => console.log(s),          // engine log
  vfs,                               // file system: open/read/write/stat/list/mkdir/remove/rename (see a reference adapter)
  socketUrl: () => 'ws://host/chat', // optional: guest TCP sockets become WebSockets to this URL
});
```

Without `socketUrl` (or without `WebSocket`), `connect` succeeds and the socket reads as closed. `recv` never blocks: an empty socket returns `EAGAIN`, so the guest has to poll for data.

## Booting a game

1. Extract `lib/armeabi/lib<game>.so` and the APK `assets/` folder. Embed the ELF as Base64 and serve the assets through your `vfs`.
2. Define the imports the engine does not provide before `loadElf`. For example, bind every `gl*` import to an `ApkGLES1` instance with `rt.def(name, fn)`.
3. Run `const java = new ApkJava(rt); rt.java = java;`, then register the game's JNI classes on `java.classes`.
4. Run `rt.loadElf(elfBytes)`, then install the game's own fast paths (below), then call `rt.startJavaThread()`. Run `rt.initArray` and call `JNI_OnLoad` and the activity's native lifecycle entry points (`nativeInit`, `nativeResize`, `nativeRender`…).
5. Drive frames from `requestAnimationFrame`, using `rt.schedule(ms)` for the guest threads.

KnS's `browser/port/game.js` and `java_shim.js` show all of this step by step.

## Game-specific fast paths

Never put a game's symbols, addresses or Java class names in the engine. Install them from the adapter after `loadElf`:

```js
rt.hookCost('SomeHotFunction', 8, () => {   // JS body replaces the guest function; returns to the caller
  const r = rt.cpu.r;                       // r[0..3] args, r[13] sp for the rest
  r[0] = 0;                                 // result
});
rt.stdStrCmp(a, b);                         // compare two STLport std::string objects (helper for such hooks)
```

`rt.hookCost` returns the hooked address, or 0 when the symbol is missing. Hooks survive JIT code flushes. KnS's `browser/port/native_hooks.js` is a worked example: a native text-cache lookup and CFont measuring.

## Tests

```bash
npm test
```

The tests cover the CPU JIT, GLES batching, sockets, and a guard that keeps the engine free of game names. Each game's repo runs its own boot and determinism gates on top. KnS uses `browser/port/bench_jit.js`, which hashes guest memory per 100 frames.

## Bundled runtime

This directory is bundled directly in the repository so the browser build has no external submodule dependencies.
