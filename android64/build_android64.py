#!/usr/bin/env python3
"""64-bit (arm64-v8a) native build of KnS: libgame.so translated from 32-bit ARM to C.

    python android64/build_android64.py translate          ELF -> C (work/android64/gen)
    python android64/build_android64.py harness [abi]      boot test executable (abi: arm64-v8a | x86_64)
    python android64/build_android64.py assets             browser game files -> work/android64/game_res (harness)
    python android64/build_android64.py all [abi]          translate + harness
    python android64/build_android64.py lib [abi]          JNI shared library work/android64/lib-<abi>/libgame.so
    python android64/build_android64.py apk [abi]          build/KnS-Android64.apk (default abi arm64-v8a)

Kept apart from the 32-bit builds: reads browser/kns.html, browser/kns_data and android/ (smali,
res, manifest, src, stubs, config.json, tools/patch_native.py; all read-only, patch_native runs
on a copy), writes only work/android64/, build/android64/ and build/KnS-Android64.apk.
build.bat, android/ and browser/ are not touched.

Input ELF = browser KNS_ELF + patch_native.py --android-arena + gateway slot, the same native
recipe as android/build_android.py.
"""
import base64
import hashlib
import json
import os
import re
import shutil
import secrets
import subprocess
import sys
import zipfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
HERE = ROOT / "android64"
WORK = ROOT / "work" / "android64"
GEN = WORK / "gen"
OUT = ROOT / "build" / "android64"
BROWSER_HTML = ROOT / "browser" / "kns.html"
PATCH_NATIVE = ROOT / "android" / "tools" / "patch_native.py"
CONFIG = ROOT / "android" / "config.json"
GATEWAY_SLOT = 0x1CB49C   # same slot as android/build_android.py
GATEWAY_LEN = 76
NDK_VERSION = "27.2.12479018"
API = 24
TRIPLES = {"arm64-v8a": "aarch64-linux-android", "x86_64": "x86_64-linux-android"}
CFLAGS = ["-O2", "-fPIC", "-fno-strict-aliasing", "-w"]
CFLAGS_BIG = ["-O1", "-fPIC", "-fno-strict-aliasing", "-w"]   # fn_big_*.c: one huge function each
RUNTIME = ["guest_rt.c", "libc_shims.c", "gles_shims.c", "jni_guest.c"]
ANDROID = ROOT / "android"
APKTOOL = ROOT / "tools" / "kns_tools" / "apktool.jar"
KEYSTORE = ROOT / "keys" / "android-release.jks"   # same signer as android/build_android.py
KEYENV = ROOT / "keys" / "android-keystore.env"
APK_OUT = ROOT / "build" / "KnS-Android64.apk"
SOURCE_ITEMS = ["AndroidManifest.xml", "apktool.yml", "res", "original"]
# Staged-smali substitutions (see android64/java/kns64/Guest.java): (file, old, new, expected count)
SMALI_PATCHES = [
    ("com/com2us/wrapper/kernel/CEventHandler.smali",
     "invoke-static {v0}, Ljava/nio/ByteBuffer;->allocateDirect(I)Ljava/nio/ByteBuffer;",
     "invoke-static {v0}, Lkns64/Guest;->allocateDirect(I)Ljava/nio/ByteBuffer;", 1),
    ("com/com2us/wrapper/font/CFont.smali",
     "invoke-virtual/range {v0 .. v7}, Landroid/graphics/Bitmap;->getPixels([IIIIIII)V",
     "invoke-static/range {v0 .. v7}, Lkns64/Guest;->getPixels(Landroid/graphics/Bitmap;[IIIIIII)V", 2),
    # CSGLFont (the font the game creates: getUseSGL, 1024x1024) draws button/dialog labels in drawTextInRect.
    ("com/com2us/wrapper/font/CSGLFont.smali",
     "invoke-virtual/range {v1 .. v8}, Landroid/graphics/Bitmap;->getPixels([IIIIIII)V",
     "invoke-static/range {v1 .. v8}, Lkns64/Guest;->getPixels(Landroid/graphics/Bitmap;[IIIIIII)V", 1),
]


def run(*cmd, **kw):
    print("[run]", " ".join(str(c) for c in cmd), flush=True)
    subprocess.run([str(c) for c in cmd], check=True, **kw)


def server_base():
    """The KnS server's API base the APK talks to: KNS_ANDROID_SERVER, else android/config.json "server"."""
    return (os.environ.get("KNS_ANDROID_SERVER") or json.loads(CONFIG.read_text(encoding="utf-8"))["server"]).rstrip("/")


def ndk_clang():
    sdk = Path(os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
               or Path(os.environ.get("LOCALAPPDATA", "")) / "Android" / "Sdk")
    ndk_env = os.environ.get("ANDROID_NDK_HOME") or os.environ.get("ANDROID_NDK_ROOT")
    if ndk_env:
        ndk = Path(ndk_env)
    elif (sdk / "ndk" / NDK_VERSION).is_dir():
        ndk = sdk / "ndk" / NDK_VERSION
    else:
        ndks = sorted((sdk / "ndk").glob("*"), key=lambda p: [int(x) for x in re.findall(r"\d+", p.name)]) if (sdk / "ndk").is_dir() else []
        ndk = ndks[-1] if ndks else sdk / "ndk" / NDK_VERSION
    host = "windows-x86_64" if os.name == "nt" else ("darwin-x86_64" if sys.platform == "darwin" else "linux-x86_64")
    clang = ndk / "toolchains" / "llvm" / "prebuilt" / host / "bin" / ("clang.exe" if os.name == "nt" else "clang")
    if not clang.exists():
        sys.exit(f"NDK clang not found: {clang} (install Android NDK or set ANDROID_NDK_HOME)")
    return clang


def prepare_elf():
    """Browser ELF + Android native patches + gateway, written to work/android64/lib/libgame.so."""
    html = BROWSER_HTML.read_text(encoding="utf-8")
    elf = base64.b64decode(re.search(r'window\.KNS_ELF="([A-Za-z0-9+/=]+)";', html)[1])
    lib = WORK / "lib" / "libgame.so"
    lib.parent.mkdir(parents=True, exist_ok=True)
    lib.write_bytes(elf)
    run(sys.executable, PATCH_NATIVE, lib, "--android-arena")
    base = server_base()
    gateway = (base + "/1mhero/").encode("latin1")
    if len(gateway) > GATEWAY_LEN:
        sys.exit(f"gateway URL too long: {len(gateway)} > {GATEWAY_LEN}")
    data = bytearray(lib.read_bytes())
    data[GATEWAY_SLOT:GATEWAY_SLOT + GATEWAY_LEN] = gateway.ljust(GATEWAY_LEN, b"\0")
    lib.write_bytes(data)
    print(f"[elf] {lib} sha256 {hashlib.sha256(data).hexdigest()[:16]} gateway {base}/1mhero/")
    return lib


def translate():
    lib = prepare_elf()
    run(sys.executable, HERE / "tools" / "arm2c.py", lib, GEN)


def assets():
    """Game files from the browser build (browser/kns_data), for the harness: work/android64/game_res."""
    html = BROWSER_HTML.read_text(encoding="utf-8")
    index = json.loads(re.search(r"window\.KNS_INDEX=(\{.*?\});", html, re.S)[1])
    out = WORK / "game_res"
    out.mkdir(parents=True, exist_ok=True)
    data = ROOT / "browser" / "kns_data"
    n = 0
    for name, (size, number) in sorted(index.items()):
        dst = out / name
        if dst.exists() and dst.stat().st_size == size:
            continue
        text = (data / f"{int(number)}.js").read_text(encoding="ascii")
        embedded, encoded = json.loads("[" + text[len("KNS_ASSET("):-2] + "]")
        payload = base64.b64decode(encoded, validate=True)
        if embedded != name or len(payload) != size:
            sys.exit(f"browser asset mismatch: {name}")
        dst.write_bytes(payload)
        n += 1
    # The game reads assets as (offset, length) inside the APK file (CResource.getFileDescriptorFromAsset
    # + getApkFilePath). The harness serves one pack file the same way: game_res.pack + game_res.idx.
    pack, idx = WORK / "game_res.pack", WORK / "game_res.idx"
    if n or not pack.exists():
        lines, off = [], 0
        with pack.open("wb") as f:
            for name in sorted(index):
                b = (out / name).read_bytes()
                f.write(b)
                lines.append(f"{name} {off} {len(b)}")
                off += len(b)
        idx.write_text("\n".join(lines) + "\n", encoding="ascii")
    print(f"[assets] {out}: {len(index)} files ({n} written), pack {pack.stat().st_size >> 20} MB")
    return out


def compile_all(abi, sources):
    """Compile sources in parallel; objects are cached by content hash of source + headers + flags."""
    clang = ndk_clang()
    target = f"--target={TRIPLES[abi]}{API}"
    obj_dir = WORK / "obj" / abi
    obj_dir.mkdir(parents=True, exist_ok=True)
    incs = [f"-I{HERE / 'runtime'}", f"-I{GEN}"]
    hdr = b"".join(p.read_bytes() for p in sorted((HERE / "runtime").glob("*.h")) + [GEN / "image.h", GEN / "translated.h"])

    def one(src):
        src = Path(src)
        extra = [f"-DIMAGE_BIN={(GEN / 'image.bin').as_posix()}"] if src.suffix == ".S" else []
        flags = CFLAGS_BIG if src.name.startswith("fn_big_") else CFLAGS
        key = hashlib.sha256(src.read_bytes() + hdr + " ".join(flags + extra + [target]).encode()).hexdigest()[:20]
        obj = obj_dir / f"{src.stem}-{key}.o"
        if src.suffix == ".S":
            key2 = hashlib.sha256((GEN / "image.bin").read_bytes()).hexdigest()[:12]
            obj = obj_dir / f"{src.stem}-{key}-{key2}.o"
        if not obj.exists():
            tmp = obj.with_suffix(".tmp")
            subprocess.run([str(clang), target, *flags, *incs, *extra, "-c", str(src), "-o", str(tmp)], check=True)
            tmp.replace(obj)
            print(f"[cc] {src.name}", flush=True)
        return obj

    # biggest first, so the long compiles start early
    sources = sorted(sources, key=lambda p: -Path(p).stat().st_size)
    with ThreadPoolExecutor(max_workers=os.cpu_count() or 4) as ex:
        return list(ex.map(one, sources))


def harness(abi):
    if not (GEN / "image.h").exists():
        translate()
    sources = sorted(GEN.glob("fn_*.c")) + [GEN / "table.c", GEN / "imports.c"]
    sources += [HERE / "runtime" / n for n in RUNTIME] + [HERE / "runtime" / "image.S", HERE / "harness" / "main.c"]
    objs = compile_all(abi, sources)
    OUT.mkdir(parents=True, exist_ok=True)
    exe = OUT / f"kns64_harness-{abi}"
    run(ndk_clang(), f"--target={TRIPLES[abi]}{API}", "-o", exe, *objs, "-lGLESv1_CM", "-lEGL", "-llog", "-lm", "-ldl", "-pie", "-Wl,--export-dynamic")
    print(f"[harness] {exe} ({exe.stat().st_size // 1024} KB)")
    return exe


def gen_jni():
    dst = GEN / "jni_exports.c"
    run(sys.executable, HERE / "tools" / "gen_jni.py", ANDROID, dst)
    return dst


def lib(abi):
    """Shared library for the APK: translated code + runtime + JNI bridge + generated Java_* wrappers."""
    if not (GEN / "image.h").exists():
        translate()
    sources = sorted(GEN.glob("fn_*.c")) + [GEN / "table.c", GEN / "imports.c", gen_jni()]
    sources += [HERE / "runtime" / n for n in RUNTIME + ["jni_bridge.c"]]
    sources += [HERE / "runtime" / "image.S", HERE / "runtime" / "stack_switch.S"]
    objs = compile_all(abi, sources)
    out = WORK / f"lib-{abi}" / "libgame.so"
    out.parent.mkdir(parents=True, exist_ok=True)
    vs = WORK / "exports.map"
    vs.write_text("{ global: JNI_OnLoad; Java_*; local: *; };\n", encoding="ascii")
    run(ndk_clang(), f"--target={TRIPLES[abi]}{API}", "-shared", "-o", out, *objs, "-Wl,-soname,libgame.so",
        f"-Wl,--version-script={vs}", "-Wl,--no-undefined", "-Wl,-z,max-page-size=16384",
        "-lGLESv1_CM", "-lEGL", "-llog", "-lm", "-ldl")
    print(f"[lib] {out} ({out.stat().st_size // 1024} KB)")
    return out


def sdk_paths():
    sdk = Path(os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
               or Path(os.environ.get("LOCALAPPDATA", "")) / "Android" / "Sdk")
    tools = sorted((sdk / "build-tools").glob("*"), key=lambda p: [int(x) for x in re.findall(r"\d+", p.name)])
    platforms = sorted((sdk / "platforms").glob("android-*/android.jar"),
                       key=lambda p: int(re.findall(r"\d+", p.parent.name)[0]))
    if not tools or not platforms:
        sys.exit(f"Android SDK build-tools/platform not found under {sdk}")
    bt = tools[-1]
    ext, exe = (".bat", ".exe") if os.name == "nt" else ("", "")
    return {"d8": bt / f"d8{ext}", "apksigner": bt / f"apksigner{ext}", "zipalign": bt / f"zipalign{exe}",
            "android_jar": platforms[-1]}


def keystore_password():
    """Reuse the Android signing key (same signer as the 32-bit APKs); create it the same way if missing."""
    for new in (KEYSTORE, KEYENV):  # move a key left in the old work/ location instead of generating a new one
        old = ROOT / "work" / new.name
        if old.exists() and not new.exists():
            new.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(old, new)
    if KEYSTORE.exists() and KEYENV.exists():
        return KEYENV.read_text(encoding="utf-8").strip().split("=", 1)[1]
    KEYSTORE.parent.mkdir(parents=True, exist_ok=True)
    password = secrets.token_hex(16)
    os.environ["KNS_KS_PASS"] = password
    run("keytool", "-genkeypair", "-keystore", KEYSTORE, "-storepass:env", "KNS_KS_PASS", "-keypass:env", "KNS_KS_PASS",
        "-storetype", "PKCS12", "-alias", "kns", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
        "-dname", "CN=Knights N Squires restoration")
    KEYENV.write_text(f"KNS_ANDROID_KEYSTORE_PASSWORD={password}\n", encoding="utf-8")
    return password


def patch_staged_smali(stage, base):
    """Fill @KNS_SMALI_BASE@ / @KNS_SMALI_HOST@ placeholders in the staged smali tree for this build's server."""
    netloc = urlparse(base).netloc or base.split("//")[-1]
    changed = 0
    for fp in stage.glob("smali*/**/*.smali"):
        raw = fp.read_bytes()
        if b"@KNS_SMALI_" not in raw:
            continue
        text = raw.decode("utf-8").replace("@KNS_SMALI_BASE@", base).replace("@KNS_SMALI_HOST@", netloc)
        fp.write_bytes(text.encode("utf-8"))
        changed += 1
    if changed:
        print(f"[smali] configured {changed} files -> {base} (host {netloc})")


def stage_apk(abi, so):
    """Copy android/ into work/android64/apk-stage (as android/build_android.py stages it), 64-bit lib only."""
    stage = WORK / "apk-stage"
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)
    for name in SOURCE_ITEMS + [p.name for p in ANDROID.glob("smali*")]:
        src = ANDROID / name
        if src.is_dir():
            shutil.copytree(src, stage / name)
        elif src.exists():
            shutil.copy2(src, stage / name)
    patch_staged_smali(stage, server_base())
    for rel, old, new, count in SMALI_PATCHES:
        f = next(stage.glob(f"smali*/{rel}"))
        text = f.read_text(encoding="utf-8")
        if text.count(old) != count:
            sys.exit(f"smali patch: {rel} has {text.count(old)} x '{old}', expected {count}")
        f.write_text(text.replace(old, new), encoding="utf-8")
    lib_dir = stage / "lib" / abi
    lib_dir.mkdir(parents=True)
    shutil.copy2(so, lib_dir / "libgame.so")
    # Assets as the Android build: assets/common/game_res/<name>.jpg, stored uncompressed (apktool.yml
    # doNotCompress jpg) so AssetManager.openFd() gives the game (offset, length) inside the APK.
    src = assets()
    res = stage / "assets" / "common" / "game_res"
    res.mkdir(parents=True)
    for f in sorted(src.iterdir()):
        name = f.name if f.name.endswith(".jpg") else f.name + ".jpg"
        try:
            os.link(f, res / name)
        except OSError:
            shutil.copy2(f, res / name)
    return stage


def compile_java(sdk):
    """android/src (KnsHub, server URL filled in) + android64/java (kns64.Guest) -> classes2.dex."""
    base = server_base()
    src = WORK / "java-src"
    shutil.rmtree(src, ignore_errors=True)
    shutil.copytree(ANDROID / "src", src)
    shutil.copytree(HERE / "java", src, dirs_exist_ok=True)
    hub = src / "kns" / "KnsHub.java"
    hub.write_text(hub.read_text(encoding="utf-8").replace("@KNS_BASE@", base), encoding="utf-8")
    classes, dex = WORK / "classes", WORK / "dex"
    for d in (classes, dex):
        shutil.rmtree(d, ignore_errors=True)
        d.mkdir(parents=True)
    sources = [str(p) for p in src.rglob("*.java")] + [str(p) for p in (ANDROID / "stubs").rglob("*.java")]
    run("javac", "--release", "8", "-nowarn", "-encoding", "UTF-8", "-cp", sdk["android_jar"], "-d", classes, *sources)
    ours = [str(p) for d in ("kns", "kns64") for p in (classes / d).rglob("*.class")]
    run(sdk["d8"], "--release", "--min-api", "21", "--lib", sdk["android_jar"], "--output", dex, *ours)
    return dex / "classes.dex"


def apk(abi):
    so = lib(abi)
    stage = stage_apk(abi, so)
    sdk = sdk_paths()
    dex = compile_java(sdk)
    unsigned, aligned = WORK / "unsigned.apk", WORK / "aligned.apk"
    run("java", "-jar", APKTOOL, "b", stage, "-o", unsigned)
    with zipfile.ZipFile(unsigned, "a", compression=zipfile.ZIP_DEFLATED) as z:
        if "classes2.dex" in z.namelist():
            sys.exit("unsigned APK already has classes2.dex")
        z.write(dex, "classes2.dex")
        libs = [n for n in z.namelist() if n.startswith("lib/")]
    if libs != [f"lib/{abi}/libgame.so"]:
        sys.exit(f"unexpected native libraries in APK: {libs}")
    run(sdk["zipalign"], "-p", "-f", "4", unsigned, aligned)
    os.environ["KNS_KS_PASS"] = keystore_password()
    out = APK_OUT if abi == "arm64-v8a" else ROOT / "build" / f"KnS-Android64-{abi}.apk"
    out.parent.mkdir(parents=True, exist_ok=True)
    run(sdk["apksigner"], "sign", "--ks", KEYSTORE, "--ks-key-alias", "kns", "--ks-pass", "env:KNS_KS_PASS",
        "--key-pass", "env:KNS_KS_PASS", "--min-sdk-version", "21", "--out", out, aligned)
    run(sdk["apksigner"], "verify", "--min-sdk-version", "24", out)
    for p in (unsigned, aligned):
        p.unlink(missing_ok=True)
    for d in ("java-src", "classes", "dex", "apk-stage"):
        shutil.rmtree(WORK / d, ignore_errors=True)
    print(f"[apk] {out} ({out.stat().st_size / 1048576:.1f} MB) sha256 {hashlib.sha256(out.read_bytes()).hexdigest()}")
    return out


def main():
    args = sys.argv[1:] or ["all"]
    cmd = args[0]
    abi = args[1] if len(args) > 1 else "arm64-v8a"
    if abi not in TRIPLES:
        sys.exit(f"abi must be one of {list(TRIPLES)}")
    if cmd == "translate":
        translate()
    elif cmd == "harness":
        harness(abi)
    elif cmd == "assets":
        assets()
    elif cmd == "lib":
        lib(abi)
    elif cmd == "apk":
        apk(abi)
    elif cmd == "all":
        translate()
        harness(abi)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
