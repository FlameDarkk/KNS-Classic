# Hosting KNS-Classic

One process serves everything: the browser game, Admin Studio, in-game chat (WebSocket), and the game API. It keeps its own accounts and player progress in one SQLite file and needs no external database or backend.

| URL (under `KNS_PUBLIC_BASE`) | What |
| --- | --- |
| `/` | Browser game (`web/index.html`) |
| `/studio` | Admin Studio (requires `KNS_ADMIN_KEY`) |
| `/api/...` | Game gateway, accounts (`/api/account/*`), admin API (`/api/admin/*`) |
| `/api/chat` | In-game chat (WebSocket) |
| `/health` (also at host root) | Health check |

## Build the server package

On Windows (with Python 3 + `pyelftools` and the .NET 8 SDK):

```bat
build.bat server                     :: Linux x64 (Intel / AMD VMs)
build.bat server --rid linux-arm64   :: Linux ARM64 (Oracle Ampere, AWS Graviton, Hetzner ARM)
```

Or directly on Linux / macOS:

```bash
python3 browser/port/build.py
python3 tools/build_studio.py
python3 scripts/package-server.py                  # linux-x64 (default)
# python3 scripts/package-server.py --rid linux-arm64
```

This builds the browser page, Admin Studio, and a self-contained Linux server into `build/kns-server/` and `build/kns-server.tar.gz` (`server/`, `web/`, `tools/`, `deploy/`, `install.sh`). Verify with:

```
python browser/browser_tools.py verify-package
```

The package never contains a player database.

## Deploy to a Linux VM

### Option A: Plain Linux VM (systemd)

```bash
tar -xzf kns-server.tar.gz && cd kns-server
sudo ./install.sh
```

- Installs to `/opt/kns`, settings in `/etc/kns/kns.env` (auto-generates `KNS_ADMIN_KEY`), database in `/var/lib/kns/kns.sqlite`, service `kns-server`.
- Re-run with a newer package to upgrade: settings and database are preserved, and a build that fails health checks rolls back automatically.
- `sudo ./install.sh --reset-progress` resets all player progress after backing up the database to `/var/lib/kns/`.
- Listens on `127.0.0.1:8092` by default. Set `KNS_BIND=0.0.0.0` for direct HTTP or put Caddy/nginx in front for HTTPS.

### Option B: Docker Compose

```bash
cd kns-server
cp deploy/kns.env.example deploy/kns.env      # set KNS_ADMIN_KEY (64 hex characters)
docker compose -f deploy/docker-compose.yml up -d --build
```

The game is served at `http://<machine>:8092/`, Admin Studio at `http://<machine>:8092/studio`, and the SQLite database persists in the `kns-data` volume.
For automatic HTTPS on your domain, set `KNS_DOMAIN=game.example.com` in `deploy/kns.env` and pass `--profile https` (Caddy).

## Android APKs

The Android APKs point at `<public URL>api`. Set `"server"` in `android/config.json` (or `KNS_ANDROID_SERVER`) before running:

```
build.bat android        # Legacy (SDK 24) + Modern (SDK 34)
build.bat all-android    # Legacy + Modern + 64-bit arm64-v8a
```

## Moving to another machine

1. Stop the old server (`sudo systemctl stop kns-server`) and copy `/var/lib/kns/kns.sqlite` and `/etc/kns/kns.env`.
2. Put both files in the same paths on the new VM and run `sudo ./install.sh`.

## Remote deploy from Windows

Copy `deploy/remote.example.json` to `deploy/remote.local.json` (gitignored), fill in SSH details and `public_url`, and run `deploy.bat`.
