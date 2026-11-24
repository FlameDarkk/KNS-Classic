# KNS-Classic

Self-hostable preservation of *Knights N Squires* (1.4.0) with a browser client, a portable `.NET 8` server, a web Admin Studio, and native Android APK builders.

The browser client runs the original 32-bit ARM `libgame.so` in a web page using a bundled JavaScript ARMv5TE JIT and OpenGL ES 1.1-on-WebGL runtime ([apk-cpu-engine](apk-cpu-engine/README.md)). One `.NET 8` process serves the browser game, Admin Studio (account and inventory editing, event schedules, mail, chat notices), the encrypted game gateway, standalone accounts, and WebSocket chat, backed by SQLite (or optionally MySQL).

## Requirements

- **Python 3.10+** with `pyelftools` (`pip install pyelftools`) for all builds
- **.NET 8 SDK** for running or packaging the server
- **Node.js 20+** for engine and browser tests (optional)
- **JDK 11+ & Android SDK** for Android APKs (plus **Android NDK 27+** for the 64-bit build)

## Local Quick Start

### Windows

```bat
pip install pyelftools
startgame.bat
```

`startgame.bat` builds the browser client and Admin Studio, generates a local `adminkey.txt`, starts the backend on `127.0.0.1:8081`, and opens both the game (`http://127.0.0.1:8790/kns.html`) and Admin Studio (`http://127.0.0.1:8790/kns-studio.html`).

### Linux / macOS

```bash
pip install pyelftools
python3 browser/port/build.py
python3 tools/build_studio.py

# Terminal 1: backend
export KNS_ADMIN_KEY=$(python3 -c "import secrets; print(secrets.token_hex(32))")
echo "Admin key: $KNS_ADMIN_KEY"
dotnet run --project server-dotnet/src/KnS.Server -- 8081

# Terminal 2: local web server (game at :8790/kns.html, Studio at :8790/kns-studio.html)
python3 browser/serve.py 8790 8081
```

## Hosting

In production, `KnS.Server` serves everything from one origin: the browser game at `/`, Admin Studio at `/studio` (unlocked with `KNS_ADMIN_KEY`), the game API at `/api/...`, WebSocket chat at `/api/chat`, and `/health`. Because the browser build resolves its API and WebSocket URLs from `location.href`, serve it through `KnS.Server` or behind the same reverse proxy rather than on a static-only host.

**Docker** (prebuilt `amd64` and `arm64` image):

```bash
docker run -d --name kns-classic \
  -p 8092:8092 \
  -e KNS_ADMIN_KEY="$(openssl rand -hex 32)" \
  -v kns-data:/var/lib/kns \
  ghcr.io/flamedarkk/kns-classic:latest
```

**Linux VM (`systemd`)**: download `kns-server-linux-x64.tar.gz` or `kns-server-linux-arm64.tar.gz` from [Releases](https://github.com/FlameDarkk/KNS-Classic/releases) (or build the package with `build.bat server` on Windows), extract it, and run:

```bash
sudo ./install.sh
```

This installs a self-contained binary and web assets under `/opt/kns`, writes `/etc/kns/kns.env` with a generated `KNS_ADMIN_KEY`, and keeps `/var/lib/kns/kns.sqlite` across upgrades. Re-running `install.sh` with a newer tarball upgrades in place and rolls back automatically if health checks fail.

See [deploy/README.md](deploy/README.md) for building the server package on Linux/macOS, Docker Compose with automatic Caddy HTTPS, nginx/Caddy reverse proxies, subpath hosting, backups, and Windows remote deploy. See [server-dotnet/README.md](server-dotnet/README.md) for all environment variables (including MySQL and tester allowlists).

## Android APKs

Point the APK at your server's `/api` base URL (e.g. `https://game.example.com/api`, or `http://<LAN_IP>:8081` locally) in `android/config.json` or via `KNS_ANDROID_SERVER`. Keep the URL **67 characters or shorter**, as `libgame.so` stores `<server>/1mhero/` in a fixed 76-byte slot.

```bat
:: Windows (or double-click build.bat for a menu)
build.bat android       :: Legacy (SDK 24) + Modern (SDK 34), 32-bit ARM
build.bat all-android   :: Legacy + Modern + experimental 64-bit arm64-v8a
```

On Linux / macOS, run `python3 browser/port/build.py` first, then `python3 android/build_android.py --variant legacy|modern` (or `android64/build_android64.py` for 64-bit; see [android64/README.md](android64/README.md) for current status).

The first build creates a persistent signing key under `keys/`. Back it up so future APKs update in place without losing player data.

## Layout

- `apk-cpu-engine/`: 32-bit ARMv5TE JS JIT, Android userland, JNI bridge, and GLES 1.1-on-WebGL renderer ([README](apk-cpu-engine/README.md))
- `browser/`: game browser adapter, asset scripts, Admin Studio source, and local dev server
- `server-dotnet/`: `.NET 8` server, SQLite/MySQL data layer, CSV seeds, and tests ([README](server-dotnet/README.md))
- `android/`: 32-bit APK source (`smali`, resources, `libgame.so`, `KnsHub.java`) and Legacy/Modern APK builder
- `android64/`: static ARM-to-C translator and `arm64-v8a` APK builder ([README](android64/README.md))
- `deploy/`: `install.sh`, `systemd` unit, Docker/Compose files, Caddy/nginx configs ([README](deploy/README.md))
- `scripts/`: server packager (`package-server.py`) and Windows helper/deploy scripts
- `tools/`: Admin Studio builder, progress reset script (`reset_progress.py`), and `apktool`

## Contributing & License

See [CONTRIBUTING.md](CONTRIBUTING.md) for running tests and contribution guidelines. Licensed under [AGPL-3.0](LICENSE).
