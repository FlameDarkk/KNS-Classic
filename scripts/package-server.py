"""Build the portable KnS-Classic server package: build.bat server -> build/kns-server/ and build/kns-server.tar.gz.

    python scripts/package-server.py [--out DIR] [--rid linux-x64|linux-arm64] [--no-studio] [--skip-server]

Layout (deploy/README.md explains how to run it):
    server/  self-contained Linux KnS.Server (default linux-x64; --rid linux-arm64 for ARM64 VMs) with seed/
    web/     the browser build served by the server at KNS_PUBLIC_BASE:
             index.html (game), studio.html (Admin Studio), kns_data-<hash>/,
             studio-assets/<hash>/ (Studio icons); text files also as .gz
    tools/   reset_progress.py
    deploy/  install.sh, kns-server.service, kns.env.example, Dockerfile, docker-compose.yml, Caddyfile, nginx-example.conf
Nothing in it names a host: the pages find the API (<base>api) from their own address.
"""
import argparse
import base64
import gzip
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'browser'))
import browser_tools as tools  # noqa: E402

API_SETUP = 'window.KNS_SERVER=new URL("api",location.href).href.replace(/\\/$/,"");'
TOOLS = ['reset_progress.py']
DEPLOY = ['install.sh', 'kns-server.service', 'kns.env.example', 'Dockerfile', 'docker-compose.yml', 'Caddyfile',
          'nginx-example.conf', 'README.md']


def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8', newline='\n')
    with gzip.open(path.with_name(path.name + '.gz'), 'wb', compresslevel=9) as stream:
        stream.write(path.read_bytes())


def build_game(root, web):
    port = tools.port_dir(root)
    local = tools.read_page(root)
    elf = re.search(r'window\.KNS_ELF="([A-Za-z0-9+/=]+)";', local)
    index_match = re.search(r'window\.KNS_INDEX=(\{.*?\});', local, re.S)
    if not elf or not index_match:
        raise ValueError('browser/kns.html must contain KNS_ELF and KNS_INDEX (run build.bat browser)')
    index = json.loads(index_match[1])
    digest = hashlib.sha256()
    assets = []
    for name, (size, number) in sorted(index.items()):
        path = tools.data_dir(root) / f'{int(number)}.js'
        data = path.read_bytes()
        text = data.decode('ascii')
        if not text.startswith('KNS_ASSET(') or not text.endswith(');'):
            raise ValueError(f'Asset/index mismatch: {path}')
        embedded, encoded = json.loads('[' + text[len('KNS_ASSET('):-2] + ']')
        if embedded != name or len(base64.b64decode(encoded, validate=True)) != size:
            raise ValueError(f'Asset/index mismatch or size drift: {name} ({path})')
        digest.update(name.encode('utf-8') + b'\0' + hashlib.sha256(data).digest())
        assets.append(path)
    data_dir = 'kns_data-' + digest.hexdigest()[:16]
    parts = ['<!doctype html><html><head><meta charset="utf-8"><title>Knights N Squires</title>',
             '<meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no">',
             (port / 'shell.css.html').read_text(encoding='utf-8'),
             '</head><body><canvas id="game"></canvas><div id="status">Loading...</div>',
             '<script>' + elf[0] + index_match[0], API_SETUP, f'window.KNS_DATA="{data_dir}";']
    if (port / 'preload.json').exists():
        parts.append('window.KNS_PRELOAD=' + (port / 'preload.json').read_text(encoding='utf-8') + ';')
    parts.append('</script>')
    for name in tools.SCRIPTS:
        parts.append('<script>' + tools.script_path(root, name).read_text(encoding='utf-8') + '</script>')
    parts.append('</body></html>')
    write_text(web / 'index.html', ''.join(parts))
    target = web / data_dir
    target.mkdir(parents=True)
    for path in assets:
        shutil.copy2(path, target / path.name)
        with gzip.open(target / (path.name + '.gz'), 'wb', compresslevel=6) as stream:
            stream.write(path.read_bytes())
    return data_dir, len(assets)


def build_studio(root, web):
    """The Admin Studio page (tools/build_studio.py output) and its hash-named icon bundle from work/."""
    page = (root / 'browser' / 'kns-studio.html').read_text(encoding='utf-8')
    if '/admin/accounts' not in page:
        raise ValueError('browser/kns-studio.html must call /admin/accounts')
    folder = re.search(r'window\.KNS_STUDIO_ASSETS="(studio-assets/[a-f0-9]{16})"', page)
    if not folder:
        raise ValueError('Studio asset reference is missing; run build.bat studio')
    source = root / 'work' / folder[1]
    manifest = json.loads((source / 'manifest.json').read_text(encoding='utf-8'))
    target = web / folder[1]
    target.mkdir(parents=True)
    for name in ['manifest.json', *manifest['files']]:
        shutil.copy2(source / name, target / name)
    raw = (root / 'browser' / 'kns-studio.html').read_bytes()
    (web / 'studio.html').write_bytes(raw)
    with gzip.open(web / 'studio.html.gz', 'wb', compresslevel=9) as stream:
        stream.write(raw)
    return len(manifest['files'])


def find_dotnet_unix(root):
    candidates = []
    if os.environ.get('KNS_DOTNET_ROOT'):
        candidates.append(Path(os.environ['KNS_DOTNET_ROOT']) / 'dotnet')
    candidates.append(root / 'tools' / 'dotnet8' / 'dotnet')
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    found = shutil.which('dotnet')
    if found:
        return found
    raise FileNotFoundError('No .NET 8 SDK found. Install the .NET 8 SDK on PATH or set KNS_DOTNET_ROOT.')


def publish_server(root, out, rid='linux-x64'):
    project = str(root / 'server-dotnet' / 'src' / 'KnS.Server')
    publish_args = ['publish', project, '-c', 'Release', '-r', rid, '--self-contained', 'true', '-o', str(out)]
    if os.name == 'nt':
        command = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                   str(root / 'scripts' / 'dotnet.ps1'), *publish_args]
    else:
        command = [find_dotnet_unix(root), *publish_args]
    subprocess.run(command, check=True)
    if not (out / 'KnS.Server').is_file() or not (out / 'seed' / 'master_daily_quests.csv').is_file():
        raise ValueError('dotnet publish output is incomplete')


def package(root, out, include_studio=True, include_server=True, rid='linux-x64'):
    if out.exists():
        shutil.rmtree(out)
    web = out / 'web'
    web.mkdir(parents=True)
    data_dir, asset_count = build_game(root, web)
    studio_count = build_studio(root, web) if include_studio else 0
    if include_server:
        publish_server(root, out / 'server', rid=rid)
    (out / 'tools').mkdir()
    for name in TOOLS:
        shutil.copy2(root / 'tools' / name, out / 'tools' / name)
    (out / 'deploy').mkdir()
    for name in DEPLOY:
        text = (root / 'deploy' / name).read_bytes().replace(b'\r\n', b'\n')
        (out / 'deploy' / name).write_bytes(text)
    (out / 'install.sh').write_bytes((out / 'deploy' / 'install.sh').read_bytes())
    if any(re.search(r'\.(sqlite(?:-wal|-shm)?|db)$', p.name, re.I) for p in out.rglob('*')):
        raise ValueError('Package contains a database file')
    archive = out.with_suffix('.tar.gz')
    with tarfile.open(archive, 'w:gz', compresslevel=6) as tar:
        def mode(info):
            if info.name.endswith(('.sh', '/KnS.Server', '/createdump')) or info.name == 'kns-server/server/KnS.Server':
                info.mode = 0o755
            return info
        tar.add(out, arcname='kns-server', filter=mode)
    print(f'Packaged {out} ({data_dir}, {asset_count} game files, {studio_count} Studio icons, '
          f'server {rid if include_server else "skipped"}) and {archive}')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--out', type=Path, default=ROOT / 'build' / 'kns-server')
    parser.add_argument('--rid', default='linux-x64', help='target .NET runtime identifier (default: linux-x64; e.g. linux-arm64)')
    parser.add_argument('--no-studio', action='store_true', help='leave out the Studio page and icons')
    parser.add_argument('--skip-server', action='store_true', help='web files only (no dotnet publish)')
    args = parser.parse_args(argv)
    package(ROOT, args.out.resolve(), include_studio=not args.no_studio, include_server=not args.skip_server, rid=args.rid)


if __name__ == '__main__':
    main()
