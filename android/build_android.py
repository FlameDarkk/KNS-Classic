#!/usr/bin/env python3
"""Build the KnS Android APK from android/ (source) plus the browser build (native payload and assets).

    python android/build_android.py [--variant legacy|modern] [--out build/KnS-Android-<Variant>.apk]
    python android/build_android.py import    one-time: decode original/ APK into android/ (refuses if already imported)

Build inputs (original/ is never read here):
  android/            apktool source: smali*, res/, AndroidManifest.xml, apktool.yml (tracked in git)
  android/src/        our Java overlay (KnsHub); android/stubs/ are compile-only stand-ins
  android/config.json server base URL, or KNS_ANDROID_SERVER (baked into the gateway slot and KnsHub)
  browser/kns.html    browser native payload: KNS_ELF (libgame.so) and KNS_INDEX (asset index). Source of truth.
  browser/kns_data/   browser asset scripts, one per game file
Variants (same package and key, so one replaces the other and keeps player data):
  legacy  target SDK 24 as imported (android/tools/patch_android.py)
  modern  target SDK 34: android/tools/patch_modern.py runs on the staged copy only
Both ship lib/armeabi and lib/armeabi-v7a (identical bytes). 64-bit-only phones use android64/build_android64.py instead.
Output: build/KnS-Android-<Variant>.apk, signed with keys/android-release.jks (created once, then reused; never commit it).
"""
import base64
import hashlib
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[1]
ANDROID = ROOT / "android"
TOOLS = ANDROID / "tools"
APKTOOL = ROOT / "tools" / "kns_tools" / "apktool.jar"
ORIGINAL = ROOT / "original" / "Knights+N+Squires_1.4.0.apk"
ORIGINAL_SHA = "b9fd464e2fc736c76d77607f7882b1111d2165c02fe3aca31bd419d81905c10e"
BROWSER_HTML = ROOT / "browser" / "kns.html"
BROWSER_DATA = ROOT / "browser" / "kns_data"
STAGE = ROOT / "build" / "android-stage"
WORK = ROOT / "work"
KEYS = ROOT / "keys"  # ignored; signing key must survive work/ cleanups
KEYSTORE = KEYS / "android-release.jks"
KEYENV = KEYS / "android-keystore.env"
CONFIG = ANDROID / "config.json"
GATEWAY_SLOT = 0x1CB49C  # libgame.so slot holding the server gateway URL
GATEWAY_LEN = 76
SOURCE_ITEMS = ["AndroidManifest.xml", "apktool.yml", "res", "original"]


def run(*cmd, **kw):
    print("[run]", " ".join(str(c) for c in cmd), flush=True)
    subprocess.run([str(c) for c in cmd], check=True, **kw)


def server_base():
    """The KnS server's API base the APK talks to: KNS_ANDROID_SERVER, else android/config.json "server"."""
    return (os.environ.get("KNS_ANDROID_SERVER") or json.loads(CONFIG.read_text(encoding="utf-8"))["server"]).rstrip("/")


def sdk_paths():
    sdk = Path(os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
               or Path(os.environ.get("LOCALAPPDATA", "")) / "Android" / "Sdk")
    tools = sorted((sdk / "build-tools").glob("*"), key=lambda p: [int(x) for x in re.findall(r"\d+", p.name)])
    platforms = sorted((sdk / "platforms").glob("android-*/android.jar"),
                       key=lambda p: int(re.findall(r"\d+", p.parent.name)[0]))
    if not tools or not platforms:
        sys.exit(f"Android SDK build-tools/platform not found under {sdk}")
    bt = tools[-1]
    ext = ".bat" if os.name == "nt" else ""
    exe = ".exe" if os.name == "nt" else ""
    return {"d8": bt / f"d8{ext}", "apksigner": bt / f"apksigner{ext}", "zipalign": bt / f"zipalign{exe}",
            "aapt": bt / f"aapt{exe}", "android_jar": platforms[-1]}


def browser_payload():
    """ELF and asset index from the browser page; the page is the single source of truth."""
    html = BROWSER_HTML.read_text(encoding="utf-8")
    elf = base64.b64decode(re.search(r'window\.KNS_ELF="([A-Za-z0-9+/=]+)";', html)[1])
    index = json.loads(re.search(r"window\.KNS_INDEX=(\{.*?\});", html, re.S)[1])
    return elf, index


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


def stage_source(base, elf, index, variant):
    """Copy android/ source into build/android-stage, then add the browser's native lib and assets."""
    shutil.rmtree(STAGE, ignore_errors=True)
    STAGE.mkdir(parents=True)
    for name in SOURCE_ITEMS + [p.name for p in ANDROID.glob("smali*")]:
        src = ANDROID / name
        if src.is_dir():
            shutil.copytree(src, STAGE / name)
        elif src.exists():
            shutil.copy2(src, STAGE / name)
    patch_staged_smali(STAGE, base)
    # Native: browser ELF plus Android-only fixes, with the gateway set to this build's server.
    lib_dir = STAGE / "lib" / "armeabi"
    lib_dir.mkdir(parents=True)
    lib = lib_dir / "libgame.so"
    lib.write_bytes(elf)
    run(sys.executable, TOOLS / "patch_native.py", STAGE, "--android-arena")
    gateway = (base + "/1mhero/").encode("latin1")
    if len(gateway) > GATEWAY_LEN:
        sys.exit(f"gateway URL too long for libgame.so slot: {len(gateway)} > {GATEWAY_LEN}")
    data = bytearray(lib.read_bytes())
    data[GATEWAY_SLOT:GATEWAY_SLOT + GATEWAY_LEN] = gateway.ljust(GATEWAY_LEN, b"\0")
    lib.write_bytes(data)
    # Same 32-bit code for devices whose ABI list names only armeabi-v7a.
    v7 = STAGE / "lib" / "armeabi-v7a"
    v7.mkdir(parents=True)
    shutil.copy2(lib, v7 / "libgame.so")
    if variant == "modern":
        run(sys.executable, TOOLS / "patch_modern.py", STAGE)
    # Assets: the browser's index (name -> [size, number]) and its per-file scripts.
    res = STAGE / "assets" / "common" / "game_res"
    res.mkdir(parents=True)
    for name, (size, number) in sorted(index.items()):
        text = (BROWSER_DATA / f"{int(number)}.js").read_text(encoding="ascii")
        embedded, encoded = json.loads("[" + text[len("KNS_ASSET("):-2] + "]")
        payload = base64.b64decode(encoded, validate=True)
        if embedded != name or len(payload) != size:
            sys.exit(f"browser asset mismatch: {name}")
        # Stored uncompressed under a .jpg name so AssetManager.openFd() returns a direct APK offset/length.
        file = name if name.endswith(".jpg") else name + ".jpg"
        (res / file).write_bytes(payload)
    print(f"[stage] {STAGE} ({variant}, native {hashlib.sha256(elf).hexdigest()[:16]}, {len(index)} game files, gateway {base}/1mhero/)")


def compile_hub(base, sdk):
    src = WORK / "java-src"
    shutil.rmtree(src, ignore_errors=True)
    shutil.copytree(ANDROID / "src", src)
    hub = src / "kns" / "KnsHub.java"
    hub.write_text(hub.read_text(encoding="utf-8").replace("@KNS_BASE@", base), encoding="utf-8")
    classes = WORK / "classes"
    dex = WORK / "dex"
    for d in (classes, dex):
        shutil.rmtree(d, ignore_errors=True)
        d.mkdir(parents=True)
    sources = [str(p) for p in src.rglob("*.java")] + [str(p) for p in (ANDROID / "stubs").rglob("*.java")]
    run("javac", "--release", "8", "-nowarn", "-encoding", "UTF-8", "-cp", sdk["android_jar"], "-d", classes, *sources)
    # Stubs stand in for the APK's own classes at compile time only.
    ours = [str(p) for p in (classes / "kns").rglob("*.class")]
    run(sdk["d8"], "--release", "--min-api", "21", "--lib", sdk["android_jar"], "--output", dex, *ours)
    return dex / "classes.dex"


def migrate_key():
    """Move a signing key left in the old work/ location to keys/ instead of generating a new one."""
    for new in (KEYSTORE, KEYENV):
        old = WORK / new.name
        if old.exists() and not new.exists():
            KEYS.mkdir(parents=True, exist_ok=True)
            shutil.move(old, new)
            print(f"[sign] moved {old.relative_to(ROOT)} -> {new.relative_to(ROOT)}")


def ensure_key():
    migrate_key()
    if KEYSTORE.exists() and KEYENV.exists():
        return KEYENV.read_text(encoding="utf-8").strip().split("=", 1)[1]
    KEYS.mkdir(parents=True, exist_ok=True)
    password = secrets.token_hex(16)
    os.environ["KNS_KS_PASS"] = password
    run("keytool", "-genkeypair", "-keystore", KEYSTORE, "-storepass:env", "KNS_KS_PASS", "-keypass:env", "KNS_KS_PASS",
        "-storetype", "PKCS12", "-alias", "kns", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
        "-dname", "CN=Knights N Squires restoration")
    KEYENV.write_text(f"KNS_ANDROID_KEYSTORE_PASSWORD={password}\n", encoding="utf-8")
    print(f"[sign] created {KEYSTORE.relative_to(ROOT)}; keep it to ship updates over installed copies")
    return password


def assemble(out, variant):
    base = server_base()
    elf, index = browser_payload()
    stage_source(base, elf, index, variant)
    sdk = sdk_paths()
    dex = compile_hub(base, sdk)
    unsigned = WORK / "unsigned.apk"
    run("java", "-jar", APKTOOL, "b", STAGE, "-o", unsigned)
    with zipfile.ZipFile(unsigned, "a", compression=zipfile.ZIP_DEFLATED) as apk:
        if "classes2.dex" in apk.namelist():
            sys.exit("unsigned APK already has classes2.dex")
        apk.write(dex, "classes2.dex")
    aligned = WORK / "aligned.apk"
    run(sdk["zipalign"], "-p", "-f", "4", unsigned, aligned)
    os.environ["KNS_KS_PASS"] = ensure_key()
    out.parent.mkdir(parents=True, exist_ok=True)
    run(sdk["apksigner"], "sign", "--ks", KEYSTORE, "--ks-key-alias", "kns", "--ks-pass", "env:KNS_KS_PASS",
        "--key-pass", "env:KNS_KS_PASS", "--min-sdk-version", "21", "--out", out, aligned)
    run(sdk["apksigner"], "verify", "--min-sdk-version", "24", out)
    run(sdk["aapt"], "dump", "badging", out, stdout=subprocess.PIPE)
    print(f"[done] {out} ({out.stat().st_size / 1048576:.1f} MB) sha256 {hashlib.sha256(out.read_bytes()).hexdigest()}")


def import_original():
    """One-time: decode the original APK into android/ and apply the Android-shell patches. Refuses to overwrite."""
    if (ANDROID / "smali").exists():
        sys.exit("android/ already imported; remove android/smali* and android/res to re-import")
    if hashlib.sha256(ORIGINAL.read_bytes()).hexdigest() != ORIGINAL_SHA:
        sys.exit(f"{ORIGINAL} is not the pristine 1.4.0 APK")
    base = server_base()
    tmp = WORK / "import-decode"
    shutil.rmtree(tmp, ignore_errors=True)
    run("java", "-jar", APKTOOL, "d", "-f", ORIGINAL, "-o", tmp, stdout=subprocess.DEVNULL)
    for name in SOURCE_ITEMS + [p.name for p in tmp.glob("smali*")]:
        src = tmp / name
        if src.is_dir():
            shutil.copytree(src, ANDROID / name, dirs_exist_ok=True)
        elif src.exists():
            shutil.copy2(src, ANDROID / name)
    run(sys.executable, TOOLS / "patch_urls.py", base, ANDROID, "--relocate-gateway")
    run(sys.executable, TOOLS / "patch_smali_debug.py", ANDROID)
    run(sys.executable, TOOLS / "patch_android.py", ANDROID, "24")
    shutil.rmtree(tmp, ignore_errors=True)
    print("[import] android/ ready; native and assets come from the browser build at build time")


def clean_intermediates():
    """Remove work/ and build/ intermediates; the signed APK in build/ is the only output kept."""
    for name in ("unsigned.apk", "aligned.apk"):
        (WORK / name).unlink(missing_ok=True)
    for name in ("java-src", "classes", "dex"):
        shutil.rmtree(WORK / name, ignore_errors=True)
    shutil.rmtree(STAGE, ignore_errors=True)


def main():
    args = sys.argv[1:]
    if args[:1] == ["import"]:
        import_original()
        return 0
    variant = args[args.index("--variant") + 1] if "--variant" in args else "legacy"
    if variant not in ("legacy", "modern"):
        sys.exit(f"unknown variant: {variant} (legacy or modern)")
    default = ROOT / "build" / f"KnS-Android-{variant.capitalize()}.apk"
    out = Path(args[args.index("--out") + 1]) if "--out" in args else default
    try:
        assemble(out, variant)
    finally:
        clean_intermediates()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
