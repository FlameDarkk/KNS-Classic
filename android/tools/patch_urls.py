#!/usr/bin/env python3
"""Redirect every Com2uS backend host in a decoded KnS tree to the mock server.

Usage:
    python patch_urls.py <base_url> [decoded_dir] [--relocate-gateway]
        base_url     e.g. http://192.168.1.50:8080  (your PC's LAN IP + mock port)
        decoded_dir  default: decoded/kns-clean  (produce it with apktool d from the
                     PRISTINE original APK -- never the already-patched decoded tree)
        --relocate-gateway  needed when base_url is longer than http:// + 21 characters
                     (e.g. http://<app>.azurewebsites.net): moves the gateway URL into
                     the flag-icon string's slot (base_url up to 67 characters).

What it does:
  * smali: rewrites any `*.com2us.com` / `*.com2us.net` host (with or without scheme,
    incl. test/dev subdomains) to point at base_url. No length limit in text. This is
    what redirects Mercury (the sunset-notice service), Hub, Wall, member, ActiveUser.
  * libgame.so: rewrites full `http(s)://*com2us*` URLs in-place, null-padded to the
    original length (binary strings can't grow). Skips any that don't fit and warns.

The package id `com.com2us.imhero...` is NOT a host and is left untouched (it contains
`com2us.` but never `com2us.com`/`com2us.net`).
"""
import os
import re
import struct
import sys
from pathlib import Path
from urllib.parse import urlparse

HOST_RE_TEXT = r'(?:[A-Za-z0-9-]+\.)+com2us\.(?:com|net)(?::\d+)?(?![\w-])'
# The lookahead keeps Java class names such as com.com2us.common.CommonTaskRunner intact.
# full URL (with scheme) and bare host (no scheme) variants
URL_WITH_SCHEME = re.compile(r'https?://' + HOST_RE_TEXT)
BARE_HOST = re.compile(r'(?<![/\w.])' + HOST_RE_TEXT)
# in the native binary: a full URL up to the first non-URL byte
NATIVE_URL = re.compile(rb'https?://[A-Za-z0-9._~:/?#\[\]@!$&\'()*+,;=%-]*?com2us\.(?:com|net)'
                        rb'[A-Za-z0-9._~:/?#\[\]@!$&\'()*+,;=%-]*')


def patch_smali(root: Path, base: str, netloc: str) -> int:
    files = list(root.rglob("*.smali"))
    changed = 0
    hosts = set()
    for fp in files:
        text = fp.read_text(encoding="utf-8", errors="ignore")
        for m in URL_WITH_SCHEME.finditer(text):
            hosts.add(m.group(0))
        for m in BARE_HOST.finditer(text):
            hosts.add(m.group(0))
        new = URL_WITH_SCHEME.sub(base, text)          # full URLs -> base (keeps http)
        new = BARE_HOST.sub(netloc, new)               # bare hosts -> host:port
        if new != text:
            fp.write_text(new, encoding="utf-8")
            changed += 1
    print(f"[smali] {changed} files rewritten; hosts seen: {sorted(hosts)[:12]}"
          f"{' ...' if len(hosts) > 12 else ''}")
    return changed


# --relocate-gateway (libgame.so 1.4.0 only). The gateway base URL is a 36-byte string that
# GameNetwork init copies into a 128-byte buffer (obj+0x258, next field at obj+0x2d8) with an
# INLINED strcpy of exactly 37 bytes, then strcat()s "gateway.php". A longer host (e.g.
# <app>.azurewebsites.net) is written into the 76-byte slot of the flag-icon URL
# (hubimage.com2us.com/.../flag/%s.png, only used for country flags), the one pc-relative
# literal that pointed at the old string is repointed, and the fixed-size copy becomes a
# byte loop that stops at the NUL. Every patched spot is checked against the pristine bytes.
GW_STR, GW_LEN = 0x1ccb6c, 36            # "http://hero1m-hub.com2us.net/1mhero/"
FLAG_STR, FLAG_SLOT = 0x1cb49c, 76       # 73 chars + 3 NUL bytes up to the next string
GW_LIT, GW_ADD = 0x14d7c0, 0x14d4de      # ldr r2,[pc,#0x2ec] literal / its `add r2, pc`
GW_COPY = 0x14d4e2                       # 20 bytes: 3x ldm/stm {r0,r1,r4}, ldrb/strb, movs
GW_COPY_ORIG = bytes.fromhex("13ca13c313ca13c313ca13c31278604644461a70")
GW_COPY_LOOP = bytes.fromhex(
    "1078"    # loop: ldrb r0, [r2]
    "0132"    #       adds r2, #1
    "1870"    #       strb r0, [r3]
    "0133"    #       adds r3, #1
    "0028"    #       cmp  r0, #0
    "f9d1"    #       bne  loop
    "6046"    #       mov  r0, ip        (dest, for the strlen that follows)
    "4446"    #       mov  r4, r8        (as the original block left it)
    "c046"    #       nop
    "c046")   #       nop
GW_BUF_ROOM = 0x80 - len(b"gateway.php") - 1


def relocate_gateway(data: bytearray, new: bytes) -> bool:
    """Point the gateway URL at FLAG_STR and make the copy length-independent."""
    lit = struct.unpack_from("<I", data, GW_LIT)[0]
    checks = [
        (data[GW_STR:GW_STR + GW_LEN] == b"http://hero1m-hub.com2us.net/1mhero/", "gateway string"),
        (data[FLAG_STR:FLAG_STR + 26] == b"http://hubimage.com2us.com", "flag-icon string"),
        (lit + GW_ADD + 4 == GW_STR, "gateway literal"),
        (data[GW_COPY:GW_COPY + 20] == GW_COPY_ORIG, "inlined copy"),
    ]
    bad = [name for ok, name in checks if not ok]
    if bad:
        print(f"[native] relocate: not the pristine 1.4.0 libgame.so ({', '.join(bad)} differ); skipped")
        return False
    if len(new) >= min(FLAG_SLOT, GW_BUF_ROOM):
        print(f"[native] relocate: {new!r} is {len(new)} bytes, max {min(FLAG_SLOT, GW_BUF_ROOM) - 1}")
        return False
    data[FLAG_STR:FLAG_STR + FLAG_SLOT] = new.ljust(FLAG_SLOT, b"\x00")
    struct.pack_into("<I", data, GW_LIT, FLAG_STR - (GW_ADD + 4))
    data[GW_COPY:GW_COPY + 20] = GW_COPY_LOOP
    print(f"[native] gateway relocated to the flag-icon slot: {new!r}")
    return True


def patch_native(lib: Path, base: str, relocate: bool = False) -> int:
    if not lib.exists():
        print(f"[native] {lib} not found, skipping")
        return 0
    data = bytearray(lib.read_bytes())
    count = 0
    gw_new = (base.rstrip("/") + "/1mhero/").encode("latin1")
    if relocate and len(gw_new) > GW_LEN:
        if not relocate_gateway(data, gw_new):
            return 0
        count += 1
    for m in list(NATIVE_URL.finditer(bytes(data))):
        orig = m.group(0)
        path = urlparse(orig.decode("latin1")).path or "/"
        new = (base.rstrip("/") + path).encode("latin1")
        if m.start() == GW_STR and count:
            continue  # relocated: the old slot is no longer referenced
        if len(new) > len(orig):
            hint = " or --relocate-gateway" if m.start() == GW_STR else ""
            print(f"[native] SKIP (too long): {orig!r} -> {new!r} "
                  f"({len(new)}>{len(orig)}); use a shorter base_url{hint}")
            continue
        data[m.start():m.start() + len(orig)] = new.ljust(len(orig), b"\x00")
        count += 1
        print(f"[native] {orig!r} -> {new!r}")
    if count:
        lib.write_bytes(bytes(data))
    print(f"[native] {count} URLs patched in {lib.name}")
    return count


def main() -> int:
    args = [a for a in sys.argv[1:] if a != "--relocate-gateway"]
    relocate = len(args) != len(sys.argv) - 1
    if not args:
        print(__doc__)
        return 2
    base = args[0].rstrip("/")
    decoded = Path(args[1]) if len(args) > 1 else Path("decoded/kns-clean")
    netloc = urlparse(base).netloc or base.split("//")[-1]
    print(f"[patch] base={base}  netloc={netloc}  decoded={decoded}")
    if not decoded.exists():
        print(f"[err] {decoded} does not exist. Re-decode the PRISTINE apk first:\n"
              f"      apktool d original/Knights+N+Squires_1.4.0.apk -o {decoded}")
        return 1
    patch_smali(decoded, base, netloc)
    for lib in decoded.rglob("libgame.so"):
        patch_native(lib, base, relocate)
    print("[patch] done. Rebuild: apktool b -> zipalign -> apksigner (see "
          "docs/build-redirected-apk.md).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
