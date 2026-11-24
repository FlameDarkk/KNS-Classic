# android64: 64-bit native KnS (arm64-v8a)

Status 2026-11-08: **`build/KnS-Android64.apk` (arm64-v8a) installs on MuMu Android 15, runs the
translated library against the real Java layer, shows the title screen and opens the login dialog.**
Login, gameplay and a real 64-bit-only phone are not tested yet. The 32-bit builds (`build.bat android`, `android/`, `browser/`,
`apk-cpu-engine/`) are untouched; this folder only reads them.

Why: 64-bit-only devices (Pixel 7+, Snapdragon 8 Gen 2+, MuMu Android 15) have no 32-bit ARM
runtime, so the original `libgame.so` (ARMv5TE, armeabi) cannot load there. There is no source,
so the library is recompiled: ARM/Thumb machine code -> C -> arm64.

## What works (2026-11-08)

Automated proof (MuMu Android 15, ABIs `x86_64,arm64-v8a,x86`, Adreno GLES1 via EGL pbuffer),
using the x86_64 build of the same generated C:

- All of `.text` translated: 4606 functions, 131k blocks, 89.5% of `.text` bytes reached as code
  (the rest is literal pools, padding and unreachable code). Compiles with NDK 27 clang for
  `arm64-v8a` and `x86_64` without errors (full build ~2.5-3.5 min on 20 cores, cached after).
- `init_array` (6 constructors), `JNI_OnLoad` (returns `JNI_VERSION_1_6`), the 25 Java startup
  natives of `browser/port/java_shim.js` `startup()` including `nativeStartClet` (1031 JNI calls:
  data tables, language files, fonts, modules), then 120 `nativeProcess` frames: 213 GL draws, the
  title screen in the saved frame (`work/android64/frame120.png`).
- APK (`build_android64.py apk`): `JNI_OnLoad` through the real `JavaVM`, startup natives from the
  real Java classes, `nativeProcess` on `GLThread` about every 10 ms, title screen rendered, touch
  opens the KnsHub account dialog (MuMu Android 15, arm64 lib through houdini).
- Button and dialog labels: the game's font is `CSGLFont` (1024x1024 buffer, real
  `drawTextInRect`); its `Bitmap.getPixels` is also routed through `kns64.Guest.getPixels`
  (`SMALI_PATCHES`), so the native copy of the buffer sees the drawn text.
- Not proven yet: login, gameplay, sound, HTTP callbacks, arm64 hardware, performance.

## Layout

| Path | What |
| --- | --- |
| `tools/arm2c.py` | Static translator: patched ELF -> `work/android64/gen/` (C, image, tables, report) |
| `runtime/guest.h` | CPU struct and the macros translated code uses (memory, ALU, call protocol) |
| `runtime/guest_rt.c` | Guest memory, image load, block dispatch, host->guest calls, guest heap, threads, crash report |
| `runtime/libc_shims.c` | libc/libm/liblog/pthread imports with 32-bit struct conversion |
| `runtime/gles_shims.c` | GLES1 imports, passthrough to the device GLESv1_CM |
| `runtime/jni_guest.c` | Guest-side `JavaVM`/`JNIEnv` tables (233 + 8 slots as host traps) with a pluggable backend |
| `runtime/image.S` | Embeds the relocated guest image |
| `runtime/jni_bridge.c` | `jni_guest.c` backend on the device JNIEnv/JavaVM, `JNI_OnLoad`, `kns64.Guest` natives |
| `runtime/stack_switch.S` | `kns64_run_on_stack`: guest code on a 64 MB stack, JNI calls back on the Java stack |
| `tools/gen_jni.py` | Exported `Java_*` wrappers from the `native` methods in `android/smali*` -> `gen/jni_exports.c` |
| `java/kns64/Guest.java` | Guest-memory direct ByteBuffer and font `getPixels` sync (staged smali calls them) |
| `harness/main.c` | Boot test: fake Java, EGL pbuffer, N frames, PPM dump |
| `build_android64.py` | `translate`, `assets`, `harness [abi]`, `all [abi]`, `lib [abi]`, `apk [abi]` |

Generated: `work/android64/` (patched ELF, `gen/`, objects, asset pack, `lib-<abi>/libgame.so`),
`build/android64/` (harness executables), `build/KnS-Android64.apk`.

## Build and run

```bash
python android64/build_android64.py translate
python android64/build_android64.py harness x86_64
python android64/build_android64.py assets
```

Push `build/android64/kns64_harness-x86_64` as `/data/local/tmp/kns64`, plus
`work/android64/game_res.pack` and `game_res.idx` to `/data/local/tmp/`, then on the device:
`KNS64_QUIET=1 KNS64_FAULTS=1 /data/local/tmp/kns64 120` (frames; the last frame goes to
`/data/local/tmp/kns64_frame.ppm`). Debug switches: `KNS64_TRACE=1` logs import calls,
`KNS64_WATCH=1` prints the main guest thread's last call site every 2 s, `KNS64_FAULTS=1`
reports SIGSEGV with host symbol and guest registers.

APK (arm64-v8a only, signed with `keys/android-release.jks`, same package as the 32-bit APKs, so
installing it replaces them and keeps player data):

```bash
python android64/build_android64.py apk
adb install -r build/KnS-Android64.apk
```

`apk x86_64` builds `build/KnS-Android64-x86_64.apk` for debugging without an ARM translator.
JNI trace on the device: `adb shell setprop debug.kns64.jni 1`, restart the app, `adb logcat -s KnS64`.

## JNI bridge (`runtime/jni_bridge.c`)

- Guest references are u32 handles: local `idx<<2|1` (per thread, dropped when the native frame
  returns), global `|2`, weak `|3`. Method/field IDs index tables that keep the parsed signature.
- `Call*Method` / `CallNonvirtual*` / `CallStatic*` / `NewObject` in `...`, `V` and `A` forms are
  converted by signature to a host `jvalue[]` (AAPCS: 64-bit values in even pairs or 8-aligned
  stack slots, floats promoted to double in varargs). Results follow the soft-float ABI.
- Strings and array regions are copied. `Get<T>ArrayElements` / critical: one guest copy per array,
  refreshed on every Get, copied back on release 0/`JNI_COMMIT`; copies up to 64 KB are also synced
  around every Java call and native frame, because the game keeps arrays pinned forever.
- Memory shared by pointer: the staged smali (copy only) calls `kns64.Guest.allocateDirect` in
  `CEventHandler` (event ring inside guest memory) and `kns64.Guest.getPixels` in `CFont` and `CSGLFont` (copies the
  drawn rows into the native copy of the font buffer); `CSGLFont` (the font the game uses) gets the same hook.
- Stacks: each Java thread entering the guest gets a 64 MB stack; JNI calls from the guest switch
  back to the thread's own stack, so ART sees its own stack.

## Design

**Input.** Same native recipe as `android/build_android.py`: browser `KNS_ELF` +
`android/tools/patch_native.py --android-arena` (run on a copy) + gateway slot from
`android/config.json`. The anti-tamper, Scudo-delete, kill() and Arena patches are therefore in
the translated code.

**Guest memory.** One 4 GB host reservation (`MAP_NORESERVE`); guest pointer = `mem + u32`.
The image loads at `0x00100000` like the browser engine; below it stays `PROT_NONE`. The guest
heap is a 16-byte-aligned size-class allocator inside the region. Relocations are applied by the
translator (`image.bin` is already relocated); imports become trap addresses `0xFFFE0000 + 4*i`.

**Translation** (`arm2c.py`). ARMv5TE + Thumb-1 decode follows `apk-cpu-engine/cpu.js`
`decodeArm`/`decodeThumb` (the proven reference). Function entries: dynsym functions,
`.ARM.exidx` starts, call targets, code pointers in relocated data, `init_array`. Each entry
becomes `void F_<addr>(CPU *c)`; its blocks are C labels, guest registers are locals
(`R[]`, `N Z C V`), flags are computed only when an instruction sets them (clang drops dead ones).
Literal-pool loads from read-only memory are folded to constants. Recognised and handled:
GCC Thumb-1 far jumps (`bl` inside the same function, 7135 sites), Thumb PIC switch tables
(109, three with the table base spilled to the stack, validated against the function extent),
ARM `add pc, pc, rX, lsl #2` switches. Functions over 2500 instructions get their own file built
at `-O1` (`NetworkEvent`, `JanssonParser`, ... take minutes at `-O2`).

**Control flow contract.** A function can start at any of its blocks (`switch (c->pc)`).
After a call, translated code checks that control came back to the return address; anything
else (longjmp, an exception landing pad, a thunk that changes LR) goes through the local block
switch, then a tail call if it is a function entry, else a return to the C caller, which repeats
the check. The run loop looks up any block key in a sorted table. So control flow the translator
did not predict still works when it lands on a known block; an unknown target stops with
`no translated code at <pc>` (each one so far was a switch table, now recognised).

**Imports.** 209 functions: libc/libm/pthread in `libc_shims.c` (32-bit `stat`, `statfs`,
`tm`, `timeval`, `addrinfo`, `dirent`, guest `FILE*`, printf/scanf with guest varargs, soft-float
ABI doubles in core registers), GLES1 in `gles_shims.c`. Unimplemented ones log
`missing import called` and return 0. Guest threads are host pthreads (64 MB host stacks) with
their own guest stack; guest mutexes/condvars map to host objects by address.

## Next steps

1. **Login and gameplay** on MuMu with a test account; sound, HTTP callbacks, a full stage.
2. **Arm64 hardware run** of the APK (MuMu's `arm64-v8a` is a bridge; a real 64-bit-only phone is
   the target), with frame-time measurement.
3. **Robustness:** parse `.ARM.extab` LSDA call-site tables so C++ landing pads are translated;
   a small fallback interpreter for any remaining unknown PC; `__cxa_*` unwinder imports.
4. **Readable C, one subsystem at a time:** an override table in `arm2c.py` (symbol -> hand-written
   C) starting with libgcc soft-float (`__aeabi_*`, `__divsi3`), zlib/libpng (host libraries with
   struct marshalling), then game subsystems. Each replacement is checked against the translated
   version with the harness.
