"""Build browser/kns.html: the Android game's own libgame.so running on a JS ARM CPU.

    python browser/port/build.py [--out DIR] [--server URL] [--data DIR]

No APK is read. The inputs are all in the repo:
  native  android/native/armeabi/libgame.so (pristine 1.4.0), patched here with
          android/tools/patch_native.py --no-textrel and the gateway relocation of patch_urls.py
          (android/build_android.py writes its server URL into that relocated slot).
  assets  browser/kns_data/<N>.js, one KNS_ASSET(name, base64) script per game file. This folder
          is the source of truth: the index (name -> [size, N]) is read back from it, so the
          numbers and sizes always match the scripts. It is tracked in git.
--out defaults to browser/. --server and --data are for hosted builds: the game server's base
URL, and the folder of the asset scripts relative to the page (name it per asset set, so the
scripts can be cached forever).
"""
import base64
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
ENGINE = ROOT / "apk-cpu-engine"
DATA = HERE.parent / "kns_data"
NATIVE = ROOT / "android" / "native" / "armeabi" / "libgame.so"
ANDROID_TOOLS = ROOT / "android" / "tools"
WORK = ROOT / "work" / "browser-native"
# Placeholder only: the browser sends every request to KNS_SERVER whatever host the game names,
# and the Android build overwrites this slot with its own server.
GATEWAY = b"http://127.0.0.1:8081/1mhero/"
# Page scripts in load order; browser/browser_tools.py (sync-html, deploy packaging) reads the same file.
_SCRIPTS = json.loads((HERE / "scripts.json").read_text(encoding="utf-8"))
ENGINE_SCRIPTS = _SCRIPTS["engine"]
PORT_SCRIPTS = _SCRIPTS["port"]
ASSET_HEAD = re.compile(rb'KNS_ASSET\(("(?:[^"\\]|\\.)*"),"')


def build_native():
    """Pristine libgame.so plus the repo's patches, the same bytes the shipped browser build ran."""
    WORK.mkdir(parents=True, exist_ok=True)
    lib = WORK / "libgame.so"
    shutil.copyfile(NATIVE, lib)
    subprocess.run([sys.executable, str(ANDROID_TOOLS / "patch_native.py"), str(lib), "--no-textrel"],
                   check=True, stdout=subprocess.DEVNULL)
    sys.path.insert(0, str(ANDROID_TOOLS))
    import patch_urls
    data = bytearray(lib.read_bytes())
    if not patch_urls.relocate_gateway(data, GATEWAY):
        sys.exit("gateway relocation failed: android/native/armeabi/libgame.so is not pristine 1.4.0")
    from patch_settings import patch
    from patch_hive import patch as patch_hive
    from patch_friend_invites import patch as patch_friend_invites
    return patch_friend_invites(patch_hive(patch(data)))


def read_index():
    """name -> [decoded size, script number], read from the kns_data scripts themselves."""
    index = {}
    for path in DATA.glob("*.js"):
        if not path.stem.isdigit():
            continue
        with path.open("rb") as f:
            head = f.read(512)
            m = ASSET_HEAD.match(head)
            if not m:
                sys.exit(f"{path}: not a KNS_ASSET script")
            total = path.stat().st_size
            f.seek(total - 5)
            tail = f.read()
        if not tail.endswith(b'");'):
            sys.exit(f"{path}: truncated KNS_ASSET script")
        name = json.loads(m.group(1))
        b64 = total - m.end() - 3
        size = b64 // 4 * 3 - tail[:2].count(b"=")
        if name in index:
            sys.exit(f"{name} is in both kns_data/{index[name][1]}.js and {path.name}")
        index[name] = [size, int(path.stem)]
    if not index:
        sys.exit(f"no asset scripts in {DATA}")
    return dict(sorted(index.items()))


def main():
    args = sys.argv[1:]
    out_dir = Path(args[args.index("--out") + 1]) if "--out" in args else HERE.parent
    OUT = out_dir / "kns.html"
    server = args[args.index("--server") + 1] if "--server" in args else None
    data_dir = args[args.index("--data") + 1] if "--data" in args else None
    index = read_index()
    elf = build_native()

    out_dir.mkdir(parents=True, exist_ok=True)
    parts = ['<!doctype html><html><head><meta charset="utf-8"><title>Knights N Squires</title>',
             '<meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">',
             (HERE / "shell.css.html").read_text(encoding="utf-8"),
             '</head><body><canvas id="game"></canvas><div id="status">Loading...</div>',
             '<script>window.KNS_ELF="' + base64.b64encode(elf).decode() + '";',
             'window.KNS_INDEX=' + json.dumps(index, separators=(",", ":")) + ';',
             (f'window.KNS_PRELOAD={(HERE / "preload.json").read_text(encoding="utf-8")};' if (HERE / "preload.json").exists() else ''),
             (f'window.KNS_SERVER={json.dumps(server)};' if server else ''),
             (f'window.KNS_DATA={json.dumps(data_dir)};' if data_dir else ''),
             '</script>']
    for s in ENGINE_SCRIPTS:
        parts.append("<script>" + (ENGINE / s).read_text(encoding="utf-8") + "</script>")
    for s in PORT_SCRIPTS:
        parts.append("<script>" + (HERE / s).read_text(encoding="utf-8") + "</script>")
    parts.append("</body></html>")
    OUT.write_text("".join(parts), encoding="utf-8")
    print(f"{OUT} {OUT.stat().st_size // 1024} KB, {len(index)} game files")


if __name__ == "__main__":
    main()
