"""Build browser/kns-studio.html and its immutable icon bundle from browser/studio/ and server-dotnet/seed/.

Pure stdlib Python — bundles icon PNGs from browser/studio/art/ with no external assets/ directory.
"""
import argparse
import csv
import hashlib
import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def build(output: Path):
    source = ROOT / "browser" / "studio"
    art = source / "art"
    meta = json.loads((art / "studio-data.json").read_text(encoding="utf-8"))

    data = {
        "catalog": meta["catalog"],
        "ui": dict(meta["ui"]),
        "events": {k: dict(v) for k, v in meta["events"].items()},
        "verifiedTextureCount": meta["verifiedTextureCount"],
    }
    css_art = meta.get("cssArt", {})

    # Populate item options and hero option bands live from server-dotnet/seed/
    data["options"] = [
        {k: (v if k == "name" else int(v)) for k, v in row.items()}
        for row in csv.DictReader((ROOT / "server-dotnet/seed/master_item_options.csv").open(encoding="utf-8-sig"))
    ]
    item_rules = {
        int(r["item_id"]): r
        for r in csv.DictReader((ROOT / "server-dotnet/seed/master_items.csv").open(encoding="utf-8-sig"))
    }
    for item in data["catalog"]:
        if item["id"] in item_rules:
            row = item_rules[item["id"]]
            item["optionBand"] = int(row["category"])
            item["fixedOptions"] = (
                [int(row["c" + str(i)]) for i in range(32, 38) if int(row["c" + str(i)]) > 0]
                if int(row["c31"]) == 1
                else None
            )

    files = {}

    def register(filename: str):
        if not filename:
            return None
        if filename not in files:
            path = art / filename
            if not path.is_file():
                raise ValueError(f"Missing Studio icon: {path}")
            raw = path.read_bytes()
            if hashlib.sha256(raw).hexdigest() != filename[:-4]:
                raise ValueError(f"Studio icon hash mismatch: {filename}")
            files[filename] = raw
        return filename

    for item in data["catalog"]:
        if item.get("icon"):
            register(item["icon"])
    for value in data["ui"].values():
        if value:
            register(value)
    for event in data["events"].values():
        for key in ("banner", "image"):
            if event.get(key):
                register(event[key])
    for hashed in css_art.values():
        register(hashed)

    digest = hashlib.sha256("\n".join(sorted(files)).encode("ascii")).hexdigest()[:16]
    folder = "studio-assets/" + digest
    bundle = ROOT / "work" / folder
    if bundle.exists():
        shutil.rmtree(bundle)
    bundle.mkdir(parents=True, exist_ok=True)
    for name, raw in files.items():
        (bundle / name).write_bytes(raw)

    for item in data["catalog"]:
        if item.get("icon"):
            item["icon"] = folder + "/" + item["icon"]
    data["ui"] = {k: (folder + "/" + v if v else None) for k, v in data["ui"].items()}
    for event in data["events"].values():
        for key in ("banner", "image"):
            if event.get(key):
                event[key] = folder + "/" + event[key]

    css = (source / "studio.css").read_text(encoding="utf-8") + "\n" + (source / "classic.css").read_text(encoding="utf-8")
    css = re.sub(
        r"url\(['\"]?(art/[^'\"\)]+)['\"]?\)",
        lambda m: "url('" + folder + "/" + css_art[m[1]] + "')",
        css,
    )

    (bundle / "manifest.json").write_text(
        json.dumps({"files": sorted(files), "documents": [], "verifiedTextures": data["verifiedTextureCount"]}, indent=2),
        encoding="utf-8",
    )

    javascript = (source / "theme.js").read_text(encoding="utf-8") + "\n" + (source / "studio.js").read_text(encoding="utf-8")
    embedded = (
        "window.KNS_STUDIO_ASSETS=" + json.dumps(folder) + ";"
        "window.STUDIO_DATA=" + json.dumps(data, separators=(",", ":"), ensure_ascii=True).replace("<", "\\u003c") + ";"
    )
    html = (source / "studio.template.html").read_text(encoding="utf-8")
    for token, value in [("/*__STUDIO_CSS__*/", css), ("/*__STUDIO_DATA__*/", embedded), ("/*__STUDIO_JS__*/", javascript)]:
        if html.count(token) != 1:
            raise ValueError("Missing or repeated template marker: " + token)
        html = html.replace(token, value)

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(html, encoding="utf-8")
    print(f"Studio built: {output} ({len(html.encode('utf-8')):,} bytes; {len(data['catalog'])} master entries; {len(files)} icons)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "browser" / "kns-studio.html")
    args = parser.parse_args()
    build(args.out.resolve())
