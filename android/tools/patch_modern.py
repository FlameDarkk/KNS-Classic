#!/usr/bin/env python3
"""Modern-Android patches for a staged KnS tree (build_android.py --variant modern). Never run on android/ itself.

Usage: python android/tools/patch_modern.py <staged_dir> [target_sdk]

  * apktool.yml targetSdkVersion (default 34), via patch_android.patch_sdk.
  * Manifest: android:exported on every component with an intent-filter (target 31+ refuses to install without it);
    optional org.apache.http.legacy library (ServerPost and ActiveUserNetwork use DefaultHttpClient, off the boot
    classpath from target 28); installLocation auto.
  * PendingIntent.getActivity/getActivities/getBroadcast/getService -> KnsHub.pending* (adds FLAG_IMMUTABLE, target 31+).
  * Context/Activity.registerReceiver -> KnsHub.registerReceiver (adds RECEIVER_EXPORTED, target 34).

Each patch is idempotent and fails loudly if its pattern is missing.
"""
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from patch_android import MARK, patch_sdk, rd, wr  # noqa: E402

PI = "Landroid/app/PendingIntent;"
PENDING = {"getActivity": "pendingActivity", "getActivities": "pendingActivities",
           "getBroadcast": "pendingBroadcast", "getService": "pendingService"}
PENDING_RE = re.compile(r"invoke-static(/range)? \{([^}]*)\}, " + re.escape(PI) + r"->(" + "|".join(PENDING)
                        + r")\(([^)]*)\)" + re.escape(PI))
RECEIVER_RE = re.compile(r"invoke-virtual(/range)? \{([^}]*)\}, Landroid/(?:content/Context|app/Activity);"
                         r"->registerReceiver\((Landroid/content/BroadcastReceiver;Landroid/content/IntentFilter;"
                         r"(?:Ljava/lang/String;Landroid/os/Handler;)?)\)Landroid/content/Intent;")
EXPORT_TAGS = ("activity", "activity-alias", "receiver", "service")
APACHE = '<uses-library android:name="org.apache.http.legacy" android:required="false"/>'


def patch_manifest(root):
    path = root / "AndroidManifest.xml"
    s = rd(path)
    count = 0

    def export(m):
        nonlocal count
        head, body = m.group(1), m.group(3)
        if "android:exported=" in head or "<intent-filter" not in body:
            return m.group(0)
        # Only the launcher/deep-link activity must be reachable from outside; system broadcasts reach either way.
        public = "android.intent.category.LAUNCHER" in body
        count += 1
        return head.replace(" ", f' android:exported="{"true" if public else "false"}" ', 1) + body

    s = re.sub(r"(<(%s)\b[^>]*[^/]>)(.*?</\2>)" % "|".join(EXPORT_TAGS), export, s, flags=re.S)
    print(f"[patch] android:exported added to {count} component(s)")
    if APACHE not in s:
        s, n = re.subn(r"(<application\b[^>]*>)", r"\1\n        " + APACHE, s, count=1)
        if n != 1:
            sys.exit("[FAIL]  AndroidManifest.xml: <application> not found")
        print("[patch] uses-library org.apache.http.legacy")
    s = s.replace('android:installLocation="preferExternal"', 'android:installLocation="auto"')
    wr(path, s)


def patch_calls(root):
    pending = receivers = 0
    for path in root.glob("smali*/**/*.smali"):
        s = rd(path)
        s, n1 = PENDING_RE.subn(lambda m: f"invoke-static{m.group(1) or ''} {{{m.group(2)}}}, {MARK}->"
                                f"{PENDING[m.group(3)]}({m.group(4)}){PI}", s)
        s, n2 = RECEIVER_RE.subn(lambda m: f"invoke-static{m.group(1) or ''} {{{m.group(2)}}}, {MARK}->"
                                 f"registerReceiver(Landroid/content/Context;{m.group(3)})Landroid/content/Intent;", s)
        if n1 or n2:
            wr(path, s)
            pending += n1
            receivers += n2
    print(f"[patch] {pending} PendingIntent calls -> KnsHub.pending*, {receivers} registerReceiver calls -> KnsHub")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    root = Path(sys.argv[1])
    target = int(sys.argv[2]) if len(sys.argv) > 2 else 34
    patch_sdk(root, target)
    patch_manifest(root)
    patch_calls(root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
