#!/usr/bin/env python3
"""Android 11+ patches for a decoded KnS 1.4.0 tree (after patch_urls/patch_native/patch_smali_debug).

Usage: python apk-build/patch_android.py <decoded_dir> [target_sdk]

  * HubBridge: auth, asyncRequest, guestAcquireUid, guestBind, logout, showDialog and socialRequest delegate to
    kns.KnsHub (apk-build/android/src), the Android twin of the browser's java_shim.js HubBridge and account panel:
    ID/password accounts on the KnS server instead of the Com2uS Hub/HIVE screens.
  * CUtility's constructor hands its instance to KnsHub, which needs the game's property store (hub_guest_uid).
  * TelephonyManager id getters become KnsHub wrappers: from target SDK 23 READ_PHONE_STATE is a runtime permission
    and the originals throw SecurityException.
  * apktool.yml targetSdkVersion (default 24): Android 14 refuses installs below 23 and Android 15 below 24.

Each patch is idempotent and fails loudly if its pattern is missing.
"""
import io
import re
import sys
from pathlib import Path

HUB = "smali/com/com2us/peppermint/HubBridge.smali"
UTILITY = "smali/com/com2us/wrapper/utility/CUtility.smali"
MARK = "Lkns/KnsHub;"

# (method declaration in HubBridge, argument registers, KnsHub signature)
DELEGATES = [
    (".method public static auth(II)I", "p0, p1", "auth(II)I"),
    (".method public static asyncRequest(Ljava/lang/String;Ljava/lang/String;II)I", "p0 .. p3",
     "asyncRequest(Ljava/lang/String;Ljava/lang/String;II)I"),
    (".method public static guestAcquireUid(II)I", "p0, p1", "guestAcquireUid(II)I"),
    (".method public static guestBind(Ljava/lang/String;Ljava/lang/String;II)I", "p0 .. p3",
     "guestBind(Ljava/lang/String;Ljava/lang/String;II)I"),
    (".method public static logout(II)I", "p0, p1", "logout(II)I"),
    (".method public static showDialog(Ljava/lang/String;II)I", "p0 .. p2", "showDialog(Ljava/lang/String;II)I"),
    (".method public static socialRequest(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;II)I", "p0 .. p4",
     "socialRequest(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;II)I"),
]

PHONE = {"getDeviceId": "deviceId", "getSubscriberId": "subscriberId",
         "getLine1Number": "line1Number", "getSimSerialNumber": "simSerialNumber"}
PHONE_RE = re.compile(r"invoke-virtual(/range)? \{([^}]*)\}, Landroid/telephony/TelephonyManager;->("
                      + "|".join(PHONE) + r")\(\)Ljava/lang/String;")

_EOL = {}


def rd(path):
    raw = io.open(path, "rb").read()
    _EOL[path] = "\r\n" if b"\r\n" in raw else "\n"
    return raw.decode("utf-8").replace("\r\n", "\n")


def wr(path, text):
    io.open(path, "wb").write(text.replace("\n", _EOL[path]).encode("utf-8"))


def patch_hub(root):
    path = root / HUB
    s = rd(path)
    for decl, regs, sig in DELEGATES:
        name = sig.split("(")[0]
        call = f"invoke-static {{{regs}}}, {MARK}->{sig}" if ".." not in regs else f"invoke-static/range {{{regs}}}, {MARK}->{sig}"
        if call in s:
            print(f"[skip]  HubBridge.{name}: already delegated")
            continue
        m = re.search(re.escape(decl) + r"\n    \.locals (\d+)\n", s)
        if not m or int(m.group(1)) < 1:
            sys.exit(f"[FAIL]  HubBridge.{name}: declaration/.locals not found")
        insert = f"\n    {call}\n\n    move-result v0\n\n    return v0\n"
        s = s[:m.end()] + insert + s[m.end():]
        print(f"[patch] HubBridge.{name} -> KnsHub")
    wr(path, s)


def patch_utility(root):
    path = root / UTILITY
    s = rd(path)
    call = f"invoke-static {{p0}}, {MARK}->setUtility(Ljava/lang/Object;)V"
    if call in s:
        print("[skip]  CUtility constructor hook: already applied")
        return
    decl = ".method public constructor <init>(Landroid/app/Activity;Ljava/lang/String;)V"
    start = s.find(decl)
    end = s.find(".end method", start)
    ret = s.rfind("    return-void", start, end)
    if start < 0 or ret < 0 or s.count("    return-void", start, end) != 1:
        sys.exit("[FAIL]  CUtility constructor: expected one return-void")
    s = s[:ret] + f"    {call}\n\n" + s[ret:]
    wr(path, s)
    print("[patch] CUtility constructor -> KnsHub.setUtility")


def patch_phone(root):
    total = 0
    for path in root.glob("smali*/**/*.smali"):
        s = rd(path)
        new, n = PHONE_RE.subn(lambda m: f"invoke-static{m.group(1) or ''} {{{m.group(2)}}}, {MARK}->"
                               f"{PHONE[m.group(3)]}(Landroid/telephony/TelephonyManager;)Ljava/lang/String;", s)
        if n:
            wr(path, new)
            total += n
    print(f"[patch] {total} TelephonyManager id calls -> KnsHub wrappers")


def patch_sdk(root, target):
    path = root / "apktool.yml"
    s = rd(path)
    new, n = re.subn(r"(\n  targetSdkVersion: )'?\d+'?", rf"\g<1>{target}", s)
    if n != 1:
        sys.exit("[FAIL]  apktool.yml: targetSdkVersion not found")
    wr(path, new)
    print(f"[patch] targetSdkVersion -> {target}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    root = Path(sys.argv[1])
    target = int(sys.argv[2]) if len(sys.argv) > 2 else 24
    patch_hub(root)
    patch_utility(root)
    patch_phone(root)
    patch_sdk(root, target)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
