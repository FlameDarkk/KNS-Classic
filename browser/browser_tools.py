"""Inspect/sync the generated browser page and verify local deployment artifacts.

Read-only unless extract-elf or sync-html is explicitly selected. Never contacts a server.
"""
import argparse
import base64
import csv
import hashlib
import gzip
import json
import os
import re
import tarfile
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# Page scripts in load order, shared with browser/port/build.py.
_SCRIPTS = json.loads((ROOT / 'browser' / 'port' / 'scripts.json').read_text(encoding='utf-8'))
ENGINE_SCRIPTS = tuple(_SCRIPTS['engine'])
PORT_SCRIPTS = tuple(_SCRIPTS['port'])
SCRIPTS = ENGINE_SCRIPTS + PORT_SCRIPTS


def page_path(root):
    candidate = root / 'browser' / 'kns.html'
    if candidate.exists() or ((root / 'browser').is_dir() and not (root / 'kns.html').exists()):
        return candidate
    return root / 'kns.html'


def data_dir(root):
    candidate = root / 'browser' / 'kns_data'
    if candidate.exists() or ((root / 'browser').is_dir() and not (root / 'kns_data').exists()):
        return candidate
    return root / 'kns_data'


def port_dir(root):
    for folder in (root / 'browser/port', root / 'tools/kns/port'):
        if folder.exists():
            return folder
    return root / 'browser/port'


def script_path(root, name):
    for folder in (root / 'apk-cpu-engine', root / 'browser/port', root / 'tools/kns/port'):
        candidate = folder / name
        if candidate.exists():
            return candidate
    return port_dir(root) / name


def payload(html):
    elf = re.search(r'window\.KNS_ELF="([A-Za-z0-9+/=]+)";', html)
    index_start = html.find('window.KNS_INDEX=')
    if elf is None or index_start < 0:
        raise ValueError('Page lacks its native ELF/index pair')
    index, _ = json.JSONDecoder().raw_decode(html[index_start + len('window.KNS_INDEX='):])
    if not isinstance(index, dict) or not index:
        raise ValueError('Invalid or empty browser asset index')
    native = base64.b64decode(elf[1], validate=True)
    if not native.startswith(b'\x7fELF'):
        raise ValueError('Embedded native payload is not an ELF')
    return native, index


def read_page(root):
    return page_path(root).read_text(encoding='utf-8')


def workspace_output(root, name):
    path = (root / name).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError('Output must stay within the selected repository')
    return path


def write_atomic(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        stream.write(data)
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def inspect(root):
    html = read_page(root)
    native, index = payload(html)
    port = port_dir(root)
    stale = [name for name in SCRIPTS if '<script>' + script_path(root, name).read_text(encoding='utf-8') + '</script>' not in html]
    return {'elf_sha256': hashlib.sha256(native).hexdigest(), 'asset_count': len(index),
            'stale_scripts': stale, 'css_current': (port / 'shell.css.html').read_text(encoding='utf-8') in html}


def inspect_companions(root):
    """Audit native companion resources, excluding item icons from render readiness."""
    native, index = payload(read_page(root))
    resources = {}
    with (root / 'server-dotnet/seed/master_items.csv').open(encoding='utf-8-sig', newline='') as source:
        for row in csv.DictReader(source):
            if row['category'] not in ('500000', '600000'):
                continue
            code = row['resource']
            entry = resources.setdefault(code, {'kind': 'pet' if row['category'] == '500000' else 'mount', 'items': 0})
            entry['items'] += 1
    for code, entry in resources.items():
        entry['missing_assets'] = [code + suffix for suffix in (
            '_info_wc.dat.jpg', '_res_wc.dat.jpg', '_pi_gl.dat.jpg', '_p_gl.dat.jpg')
            if code + suffix not in index]
        entry['resource_files_present'] = not entry['missing_assets']
    return {'elf_sha256': hashlib.sha256(native).hexdigest(), 'resources': resources,
            'note': 'File presence is not gameplay proof. Catalog icons do not supply movement or rider anchors.'}


def extract_elf(root, output):
    native, _ = payload(read_page(root))
    path = workspace_output(root, output)
    if path.exists() and path.read_bytes() != native:
        raise ValueError('Existing ELF differs; choose a fresh --out path rather than overwriting analysis evidence')
    if not path.exists():
        write_atomic(path, native)
    return {'elf': str(path), 'sha256': hashlib.sha256(native).hexdigest()}


def sync_html(root, output='browser/kns.html'):
    html = read_page(root)
    native, index = payload(html)
    scripts = re.findall(r'<script>(.*?)</script>', html, re.S)
    if len(scripts) not in (len(SCRIPTS), len(SCRIPTS) + 1) or 'window.KNS_ELF=' not in scripts[0]:
        raise ValueError('Unexpected HTML structure; inspect before replacing custom content')
    port = port_dir(root)
    result = ''.join([
        '<!doctype html><html><head><meta charset="utf-8"><title>Knights N Squires</title>',
        '<meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">',
        (port / 'shell.css.html').read_text(encoding='utf-8'),
        '</head><body><canvas id="game"></canvas><div id="status">Loading...</div>',
        '<script>' + scripts[0] + '</script>',
        *('<script>' + script_path(root, name).read_text(encoding='utf-8') + '</script>' for name in SCRIPTS),
        '</body></html>',
    ])
    after_native, after_index = payload(result)
    if native != after_native or index != after_index:
        raise ValueError('Native payload/index changed during synchronization')
    if output in ('kns.html', 'browser/kns.html'):
        path = page_path(root)
    else:
        path = workspace_output(root, output)
    encoded = result.encode('utf-8')
    changed = not path.exists() or path.read_bytes() != encoded
    if changed:
        write_atomic(path, encoded)
    return {'html': str(path), 'written': changed, 'native_preserved': True}


def verify_effects(root):
    _, index = payload(read_page(root))
    keys = [name for name in index if 'dt_etc_setting.json' in name]
    if len(keys) != 1:
        raise ValueError('Expected exactly one indexed dt_etc_setting asset')
    name = keys[0]
    text = (data_dir(root) / f'{int(index[name][1])}.js').read_text(encoding='ascii')
    if not text.startswith('KNS_ASSET(') or not text.endswith(');'):
        raise ValueError('Unexpected asset script format')
    arguments = json.loads('[' + text[len('KNS_ASSET('):-2] + ']')
    if len(arguments) != 2 or arguments[0] != name:
        raise ValueError('Asset/index name mismatch')
    setting = json.loads(base64.b64decode(arguments[1], validate=True))
    original = {int(row['SpecialItemID']): int(row['SpecialItemValue']) for row in setting['SpecialItem']}
    with (root / 'server-dotnet/seed/master_special_items.csv').open(newline='', encoding='utf-8-sig') as stream:
        exported = {int(row['item_id']): int(row['value']) for row in csv.DictReader(stream)}
    if original != exported:
        raise ValueError('Exported special-item effects differ from actual browser assets')
    return {'special_item_effects_verified': len(original)}


def verify_package(root, package):
    """Checks a scripts/package-server.py folder against the current sources: no database, a Linux x64 server with current
    seeds, the game page with the current scripts/payload and every indexed asset, the World page and the Studio bundle."""
    package = (root / package).resolve()
    files = [p for p in package.rglob('*') if p.is_file()]
    if any(re.search(r'\.(sqlite(?:-wal|-shm)?|db)$', p.name, re.I) for p in files):
        raise ValueError('Package contains a player database or sidecar')
    web = package / 'web'
    result = {'package': str(package)}
    if (package / 'server').exists():
        header = (package / 'server/KnS.Server').read_bytes()[:20]
        arch = {b'\x3e\x00': 'linux-x64', b'\xb7\x00': 'linux-arm64'}.get(header[18:20])
        if not (header.startswith(b'\x7fELF\x02\x01') and arch):
            raise ValueError('Server executable is not a Linux x64 or arm64 ELF')
        result['server_arch'] = arch
        seeds = list((root / 'server-dotnet/seed').glob('*.csv'))
        if not seeds:
            raise ValueError('Repository seed folder is empty')
        for seed in seeds:
            packaged = package / 'server/seed' / seed.name
            if not packaged.is_file() or packaged.read_bytes() != seed.read_bytes():
                raise ValueError(f'Missing/stale server seed: {seed.name}')
        result['seed_files_verified'] = len(seeds)
    page = (web / 'index.html').read_text(encoding='utf-8')
    if gzip.decompress((web / 'index.html.gz').read_bytes()).decode('utf-8') != page:
        raise ValueError('Game page gzip differs from the page')
    native, index = payload(page)
    if (native, index) != payload(read_page(root)):
        raise ValueError('Package uses a different native payload/index')
    if 'window.KNS_SERVER=new URL("api",location.href)' not in page:
        raise ValueError('Game page API setup is missing')
    folder = re.search(r'window\.KNS_DATA="(kns_data-[a-f0-9]+)";', page)
    if not folder:
        raise ValueError('Game asset folder is missing')
    missing = [int(row[1]) for row in index.values() if not (web / folder[1] / f'{int(row[1])}.js').is_file()]
    if missing:
        raise ValueError(f'Package is missing {len(missing)} indexed browser assets')
    for name in SCRIPTS:
        if '<script>' + script_path(root, name).read_text(encoding='utf-8') + '</script>' not in page:
            raise ValueError(f'Package has stale source: {name}')
    if (port_dir(root) / 'shell.css.html').read_text(encoding='utf-8') not in page:
        raise ValueError('Package has stale CSS')
    result['assets_verified_present'] = len(index)
    if (web / 'studio.html').exists():
        result['studio_artwork_verified'] = verify_studio(root, web)
    return result


def verify_studio(root, web):
    """The packaged Studio page equals browser/kns-studio.html and its hash-named icon bundle is complete."""
    studio = (web / 'studio.html').read_bytes()
    if studio != (root / 'browser/kns-studio.html').read_bytes():
        raise ValueError('Studio page differs from current source')
    if gzip.decompress((web / 'studio.html.gz').read_bytes()) != studio:
        raise ValueError('Studio gzip differs from the page')
    bundle = re.search(rb'window.KNS_STUDIO_ASSETS="(studio-assets/[a-f0-9]{16})"', studio)
    if not bundle:
        raise ValueError('Studio asset reference is missing')
    prefix = web / bundle[1].decode('ascii')
    manifest = json.loads((prefix / 'manifest.json').read_text(encoding='utf-8'))
    for filename in manifest['files']:
        path = prefix / filename
        if not re.fullmatch(r'[a-f0-9]{64}\.png', filename) or not path.is_file():
            raise ValueError('Missing or invalid Studio artwork: ' + filename)
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != filename[:-4] or not raw.startswith(b'\x89PNG\r\n\x1a\n'):
            raise ValueError('Studio artwork hash/format differs: ' + filename)
    return len(manifest['files'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT, help='Repository root; defaults to this script location')
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('inspect')
    commands.add_parser('inspect-companions')
    commands.add_parser('verify-effects')
    commands.add_parser('extract-elf').add_argument('--out', default='work/browser_libgame.so')
    commands.add_parser('sync-html').add_argument('--out', default='browser/kns.html')
    commands.add_parser('verify-package').add_argument('package', nargs='?', default='build/kns-server')
    args = parser.parse_args()
    try:
        root = args.root.resolve()
        if args.command == 'inspect':
            result = inspect(root)
        elif args.command == 'inspect-companions':
            result = inspect_companions(root)
        elif args.command == 'verify-effects':
            result = verify_effects(root)
        elif args.command == 'extract-elf':
            result = extract_elf(root, args.out)
        elif args.command == 'sync-html':
            result = sync_html(root, args.out)
        else:
            result = verify_package(root, args.package)
    except (ValueError, OSError, KeyError, tarfile.TarError) as error:
        parser.exit(1, f'ERROR: {error}\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
