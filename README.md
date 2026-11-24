<div align="center">

# KNS-Classic

**Self-hostable Knights N Squires (1.4.0) preservation & restoration**  
Browser Client (ARM-in-JS JIT) · Portable .NET 8 Server · Admin Studio · Native Android APK Builder

[![CI](https://github.com/FlameDarkk/KNS-Classic/actions/workflows/ci.yml/badge.svg)](https://github.com/FlameDarkk/KNS-Classic/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/FlameDarkk/KNS-Classic?display_name=tag)](https://github.com/FlameDarkk/KNS-Classic/releases)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![Browser](https://img.shields.io/badge/Browser-WebGL%20%2B%20ARM%20JIT-F7DF1E?logo=javascript&logoColor=black)](#deploying-the-browser-version)
[![Android](https://img.shields.io/badge/Android-SDK%2024%20%7C%2034%20%7C%20ARM64-3DDC84?logo=android&logoColor=white)](#building-android-apks)
[![Deploy](https://img.shields.io/badge/Deploy-systemd%20%7C%20Docker%20%7C%20Caddy-2496ED?logo=docker&logoColor=white)](#deploying-the-server-to-any-vm)

[Features](#features) · [Architecture](#architecture) · [Quick Start](#quick-start-local) · [Browser Deployment](#deploying-the-browser-version) · [VM Server Hosting](#deploying-the-server-to-any-vm) · [Android APKs](#building-android-apks) · [Admin Studio](#admin-studio) · [Tests](#running-tests)

</div>

---

## Features

- **Classic 1.4.0 Gameplay & Economy** — Authentic 1.4.0 drop tables, quests, achievements, Arena seasons, Weekly Raids, native bag caps, and core game mechanics, with accumulated protocol and crash fixes preserved.
- **Play Directly in Any Modern Browser (`apk-cpu-engine`)** — Runs the native 32-bit ARM `libgame.so` inside a browser page using a bundled zero-dependency JavaScript ARMv5TE JIT + OpenGL ES 1.1-on-WebGL runtime. No plugins, emulators, or external submodules required.
- **Single-Binary Portable Server (`KnS.Server`)** — One cross-platform `.NET 8` executable serves the browser game (`/`), Admin Studio (`/studio`), encrypted game gateway & standalone ID/password accounts (`/api/...`), real-time WebSocket in-game chat (`/api/chat`), and `/health`.
- **Zero-Dependency Persistence** — Uses an embedded SQLite database created and seeded automatically on first boot (with optional MySQL support via `KNS_DATABASE_URL`).
- **Admin Studio** — Full visual web dashboard to inspect and edit player accounts, currencies, heroes, items (with options/grades), allies, equipment, Arena seasons, Weekly Raids, Event Dungeon schedules, Mass Mail, and Chat Notices/Pins.
- **Native Android APK Builder** — Build **Legacy (SDK 24)**, **Modern (SDK 34)**, and **64-bit (`arm64-v8a`)** APKs configured for your own server, all signed with a persistent local keystore so updates preserve player data.

---

## Architecture

```mermaid
flowchart LR
    B["Browser Client (index.html + ARM JIT)"] -->|HTTPS + WebSocket| P["Reverse Proxy (Caddy / nginx, optional)"]
    A["Android APKs (Legacy / Modern / ARM64)"] -->|HTTPS / HTTP| P
    S["Admin Studio (/studio)"] -->|X-Admin-Key| P
    P --> K["KnS.Server (.NET 8 Single Process)"]
    K --> DB[("SQLite (default) or MySQL")]
```

| Endpoint (under `KNS_PUBLIC_BASE`) | Purpose |
| --- | --- |
| `/` | Browser game (`web/index.html`) |
| `/studio` | Admin Studio (`web/studio.html`, requires `KNS_ADMIN_KEY`) |
| `/api/...` | Game gateway (`/1mhero/gateway.php`), accounts (`/api/account/*`), platform routes, admin API (`/api/admin/*`) |
| `/api/chat` | In-game chat relay (WebSocket) |
| `/health` | Health check (also served at host root `/health`) |

### Repository Layout

| Directory / File | Description |
| --- | --- |
| `apk-cpu-engine/` | Bundled 32-bit ARMv5TE JS JIT, Android userland, JNI bridge, and GLES 1.1 → WebGL renderer |
| `browser/` | Game browser adapter (`port/`), game asset scripts (`kns_data/`), Admin Studio (`studio/`), and local dev server (`serve.py`) |
| `server-dotnet/` | Portable `.NET 8` game server (`KnS.Server`), SQLite/MySQL data layer (`KnS.Data`), master-data CSV seeds (`seed/`), and xUnit tests |
| `android/` | 32-bit Android APK source (`smali`, `res`, `native/armeabi/libgame.so`, `KnsHub.java`), patchers, and Legacy/Modern APK builder (`build_android.py`) |
| `android64/` | Static ARM-to-C translator (`arm2c.py`), 64-bit C runtime/JNI bridge, and `arm64-v8a` APK builder (`build_android64.py`) |
| `deploy/` | Production Linux deployment kit (`install.sh`, `systemd` unit, `Dockerfile`, `docker-compose.yml`, `Caddyfile`, `nginx-example.conf`) |
| `scripts/` | Cross-platform server packager (`package-server.py`), Windows remote deploy (`deploy-remote.ps1`), and `.NET` wrapper scripts |
| `tools/` | Admin Studio builder (`build_studio.py`), safe player progress reset tool (`reset_progress.py`), and `apktool` |

---

## Prerequisites

| Requirement | Needed For | Install Notes |
| --- | --- | --- |
| **Python 3.10+** + `pyelftools` | All builds | `pip install pyelftools` |
| **.NET 8 SDK** | Local server & VM server packaging | On `PATH`, or set `KNS_DOTNET_ROOT`, or drop into `tools/dotnet8/` |
| **Node.js 20+** | Running engine & browser tests | Optional |
| **JDK 11+** (`java`, `javac`, `keytool`) | Building Android APKs | Needed only for APK targets |
| **Android SDK** (`build-tools` + a `platforms/android-*` jar) | Building Android APKs | Set `ANDROID_HOME` (or default `%LOCALAPPDATA%\Android\Sdk`) |
| **Android NDK** (27+ recommended) | Building 64-bit `arm64-v8a` APK only | Auto-detected from `$ANDROID_HOME/ndk/`, or set `ANDROID_NDK_HOME` |

---

## Quick Start (Local)

### Windows (One Command)

```bat
pip install pyelftools
startgame.bat
```

`startgame.bat` automatically:
1. Builds `browser\kns.html` (if missing) and `browser\kns-studio.html`.
2. Generates a local 64-hex `adminkey.txt` (gitignored).
3. Starts the `.NET 8` backend on `http://127.0.0.1:8081`.
4. Starts the local web proxy on `http://127.0.0.1:8790` and opens both the **Game** (`http://127.0.0.1:8790/kns.html`) and **Admin Studio** (`http://127.0.0.1:8790/kns-studio.html`).

### Linux / macOS

```bash
pip install pyelftools

# 1. Build the browser game and Admin Studio
python3 browser/port/build.py
python3 tools/build_studio.py

# 2. Generate an admin key and start the .NET 8 backend (terminal 1)
export KNS_ADMIN_KEY=$(python3 -c "import secrets; print(secrets.token_hex(32))")
echo "Admin key: $KNS_ADMIN_KEY"
dotnet run --project server-dotnet/src/KnS.Server -- 8081

# 3. Serve the game & Studio locally on one origin (terminal 2)
python3 browser/serve.py 8790 8081
```

Then open:
- **Game:** `http://127.0.0.1:8790/kns.html`
- **Admin Studio:** `http://127.0.0.1:8790/kns-studio.html` (paste your `KNS_ADMIN_KEY`)

---

## Deploying the Browser Version

The browser build is designed to be served **same-origin** by `KnS.Server` (`KNS_WEB_ROOT`). When packaged, the game page automatically computes its API and WebSocket chat URLs from `location.href` (`<base>api` and `ws(s)://<host>/<base>api/chat`) — so **nothing in the web build hardcodes a domain or IP**, no CORS setup is needed, and pre-compressed `.gz` assets with immutable cache headers are served automatically.

### Option 1: All-in-One Hosted Deployment (Recommended)

Follow [Deploying the Server to Any VM](#deploying-the-server-to-any-vm) below. The server package (`kns-server.tar.gz`) bundles the server binary, the browser game (`web/index.html` + `web/kns_data-<hash>/`), and Admin Studio (`web/studio.html` + `web/studio-assets/<hash>/`) together.

- **Dedicated domain or IP** (e.g. `https://game.example.com/`): keep `KNS_PUBLIC_BASE=/`.
- **Subpath on an existing website** (e.g. `https://example.com/kns/`): set `KNS_PUBLIC_BASE=/kns/` in `kns.env` and forward `/kns/` unchanged from nginx or Caddy (see `deploy/nginx-example.conf`).

### Option 2: Building Only the Browser / Studio Bundles

```bat
:: Windows
build.bat browser       :: Produces browser\kns.html
build.bat studio        :: Produces browser\kns-studio.html + work\studio-assets\<hash>\
```

```bash
# Linux / macOS
python3 browser/port/build.py [--out DIR] [--server URL] [--data DIR]
python3 tools/build_studio.py [--out PATH]
```

> **Note on static-only hosts (e.g. GitHub Pages / Netlify):** `KnS.Server` restricts gateway CORS to `localhost`, `127.0.0.1`, and `file://`, and in-game chat requires a live WebSocket to `/api/chat`. For public hosting, always serve the browser bundle through `KnS.Server` (`KNS_WEB_ROOT`) or behind the same reverse proxy origin as the API.

---

## Deploying the Server to Any VM

`KnS.Server` runs on **any** Linux virtual machine (Ubuntu, Debian, Hetzner, DigitalOcean, Linode, Vultr, AWS EC2/Lightsail, Oracle Cloud Free Tier x64 & ARM64, Azure, GCP, or a home server). Minimum recommended specs: **1 vCPU, 512 MB–1 GB RAM, 2 GB disk**.

### Step 1 — Get or Build the Portable Server Package

#### Fastest: Download a Prebuilt Release (No Build Tools Needed)

Grab the latest `kns-server-linux-x64.tar.gz` (Intel/AMD) or `kns-server-linux-arm64.tar.gz` (ARM64) directly from the [GitHub Releases](https://github.com/FlameDarkk/KNS-Classic/releases) page, or run the prebuilt multi-arch Docker image from GitHub Container Registry:

```bash
docker run -d --name kns-classic \
  -p 8092:8092 \
  -e KNS_ADMIN_KEY="$(openssl rand -hex 32)" \
  -v kns-data:/var/lib/kns \
  ghcr.io/flamedarkk/kns-classic:latest
```

#### Or Build from Source

The package (`build/kns-server/` and `build/kns-server.tar.gz`) contains a self-contained Linux `.NET 8` executable (no `.NET` runtime needed on the target VM), master seeds, pre-compressed browser & Studio assets, and the `deploy/` kit. **It never contains a player database.**

#### Build on Windows (then upload to your VM)

```bat
:: For standard x86_64 Linux VMs (Intel / AMD)
build.bat server

:: For ARM64 Linux VMs (Oracle Cloud Ampere, AWS Graviton, Hetzner CAX, Raspberry Pi 64-bit)
build.bat server --rid linux-arm64

:: Optional: verify the package integrity
python browser\browser_tools.py verify-package
```

Copy `build\kns-server.tar.gz` to your VM:

```bash
scp build/kns-server.tar.gz user@YOUR_VM_IP:~
```

#### Or build directly on the Linux VM

```bash
git clone <your-repo-url> kns-classic
cd kns-classic
pip install pyelftools

python3 browser/port/build.py
python3 tools/build_studio.py

# x86_64 VM:
python3 scripts/package-server.py
# ARM64 VM:
# python3 scripts/package-server.py --rid linux-arm64

python3 browser/browser_tools.py verify-package
```

---

### Step 2 — Install & Run on the VM

Choose **Option A (systemd)** for a minimal bare-VM install or **Option B (Docker Compose)** for containerized hosting with optional one-line automatic HTTPS.

#### Option A: Plain Linux VM (`systemd` via `install.sh`)

```bash
tar -xzf kns-server.tar.gz
cd kns-server
sudo ./install.sh
```

What `install.sh` does:
- Creates a locked-down system user `kns` and hardened systemd service `kns-server`.
- Installs binaries and web assets to `/opt/kns/{server,web,tools}`.
- Creates `/etc/kns/kns.env` on first run and **auto-generates a 64-hex `KNS_ADMIN_KEY`**.
- Stores the SQLite database at `/var/lib/kns/kns.sqlite` (preserved across upgrades).
- Runs a **preflight health check** on a scratch database before touching the running service, and **automatically rolls back** if an upgrade fails health checks.

Check status and retrieve your generated Admin Studio key:

```bash
sudo systemctl status kns-server
sudo grep KNS_ADMIN_KEY /etc/kns/kns.env
```

By default the server listens on `127.0.0.1:8092` (ready for a reverse proxy). To expose HTTP directly without a proxy:

```bash
sudo sed -i 's/^KNS_BIND=.*/KNS_BIND=0.0.0.0/' /etc/kns/kns.env
sudo systemctl restart kns-server
# Remember to allow TCP port 8092 in your VM firewall / cloud security group!
```

To **upgrade** later, extract a newer `kns-server.tar.gz` and re-run `sudo ./install.sh` — your config and player database stay intact.

#### Option B: Docker Compose (with Optional Automatic HTTPS)

```bash
tar -xzf kns-server.tar.gz
cd kns-server
cp deploy/kns.env.example deploy/kns.env
```

Edit `deploy/kns.env` and set a strong 64-hex `KNS_ADMIN_KEY` (generate one with `openssl rand -hex 32`):

```bash
# HTTP on port 8092:
docker compose -f deploy/docker-compose.yml up -d --build
```

The game is live at `http://<VM_IP>:8092/`, Admin Studio at `http://<VM_IP>:8092/studio`, and player data persists in the `kns-data` Docker volume.

For **automatic HTTPS via Caddy** on your own domain (point your domain's DNS `A` record to the VM and open ports `80` and `443`):

```bash
echo "KNS_DOMAIN=game.example.com" >> deploy/kns.env
docker compose -f deploy/docker-compose.yml --profile https up -d --build
```

---

### Step 3 — Reverse Proxy (HTTPS + WebSocket Chat)

If using Option A (`systemd` on `127.0.0.1:8092`), put **Caddy** or **nginx** in front for TLS:

- **Caddy:** see [deploy/Caddyfile](deploy/Caddyfile) (change `kns:8092` to `127.0.0.1:8092` when running outside Docker).
- **nginx:** see [deploy/nginx-example.conf](deploy/nginx-example.conf) — includes the required `Upgrade` / `Connection` WebSocket headers for in-game chat (`/api/chat`) and subpath support (`KNS_PUBLIC_BASE`).

---

### Optional: One-Command Remote Deploy from Windows (`deploy.bat`)

To build on Windows, upload over SSH, run `install.sh`, and run automated post-deploy health checks against your public URL:

1. Copy `deploy/remote.example.json` to `deploy/remote.local.json` (gitignored):
   ```json
   {
     "host": "ubuntu@203.0.113.10",
     "key": "%USERPROFILE%\\.ssh\\id_ed25519",
     "public_url": "https://game.example.com/"
   }
   ```
2. Run:
   ```bat
   deploy.bat                  :: Build package, upload, install, verify
   deploy.bat -SkipBuild       :: Upload existing build\kns-server.tar.gz
   deploy.bat -PrepareOnly     :: Build & verify package without connecting over SSH
   deploy.bat -ResetProgress   :: Deploy and wipe gameplay progress (accounts kept; DB backed up first)
   ```

---

### Server Configuration Reference

All settings live in `/etc/kns/kns.env` (systemd) or `deploy/kns.env` (Docker):

| Variable | Default | Description |
| --- | --- | --- |
| `KNS_PORT` | `8092` (`8080` raw) | HTTP listen port |
| `KNS_BIND` | `127.0.0.1` (`0.0.0.0` raw) | Bind address (`127.0.0.1` behind proxy, `0.0.0.0` for direct access) |
| `KNS_PUBLIC_BASE` | `/` | Public URL prefix (`/` for root, or `/kns/` for subpath hosting) |
| `KNS_WEB_ROOT` | `/opt/kns/web` | Directory of packaged browser/Studio files; unset = API-only mode |
| `KNS_DB` | `/var/lib/kns/kns.sqlite` | SQLite database path (auto-created & seeded on first start) |
| `KNS_DATABASE_URL` | *(unset)* | Optional `mysql://user:pass@host:3306/db` to use MySQL instead of SQLite |
| `KNS_ADMIN_KEY` | *(auto-generated)* | 64-hex secret for Admin Studio & `/api/admin/*` (`X-Admin-Key` header) |
| `KNS_QUIET` | `1` (hosted) | Disables unbounded per-request logs on hosted servers |
| `KNS_LOG_DIR` | `/var/lib/kns/logs` | Log output directory |
| `KNS_TESTERS_ONLY` | *(unset)* | Set `1` to restrict access to allowlisted devices managed via `/api/admin/testers` |
| `KNS_TRUSTED_PROXIES` | *(loopback only)* | Comma-separated reverse-proxy IPs/CIDRs trusted for `X-Forwarded-For` / `X-Forwarded-Proto` (e.g. `172.16.0.0/12`) |
| `KNS_DOMAIN` | *(unset)* | Domain for Docker Compose Caddy `--profile https` |

### Backing Up & Moving to Another VM

```bash
# On the old VM:
sudo systemctl stop kns-server
# Copy /var/lib/kns/kns.sqlite and /etc/kns/kns.env to the new VM

# On the new VM (after copying both files to the same paths):
sudo ./install.sh
```

See [deploy/README.md](deploy/README.md) for additional hosting notes.

---

## Building Android APKs

Three APK variants are supported, all sharing the same package ID and signing key (so installing a newer build updates in-place and preserves player data):

| Variant | Output File | Architecture | Target SDK | Best For |
| --- | --- | --- | --- | --- |
| **Legacy** | `build/KnS-Android-Legacy.apk` | 32-bit (`armeabi`, `armeabi-v7a`) | SDK 24 | Older Android phones & 32-bit emulators |
| **Modern** | `build/KnS-Android-Modern.apk` | 32-bit (`armeabi`, `armeabi-v7a`) | SDK 34 | Android 12–15 devices that support 32-bit ABIs |
| **64-bit** *(experimental)* | `build/KnS-Android64.apk` | 64-bit (`arm64-v8a`) | SDK 24 | 64-bit-only phones (Pixel 7+, Snapdragon 8 Gen 2+, Android 15 64-bit emulators). See [android64/README.md](android64/README.md) for current status. |

### Step 1 — Configure Your Server URL

Point the APK at your server's **`/api` base URL** (for a packaged VM host at `https://game.example.com/`, that is `https://game.example.com/api`; for a subpath host at `https://example.com/kns/`, use `https://example.com/kns/api`; for local testing, use `http://<YOUR_PC_LAN_IP>:8081`).

> **URL length limit:** Because `libgame.so` stores the gateway URL (`<server>/1mhero/`) in a fixed 76-byte slot, keep your `"server"` URL **67 characters or shorter**.

Either edit `android/config.json`:

```json
{"server": "https://game.example.com/api"}
```

Or override it per build with an environment variable (no tracked file edits needed):

```bat
:: Windows (cmd)
set KNS_ANDROID_SERVER=https://game.example.com/api
```

```bash
# Linux / macOS
export KNS_ANDROID_SERVER="https://game.example.com/api"
```

At build time, `build_android.py` and `build_android64.py` automatically configure the native `libgame.so` gateway, `KnsHub.java` account endpoints, and all staged smali platform URLs to match this URL.

### Step 2 — Build the APKs

#### Windows

```bat
build.bat android          :: Legacy (SDK 24) + Modern (SDK 34)
build.bat android-legacy   :: Legacy only
build.bat android-modern   :: Modern only
build.bat android-64       :: 64-bit arm64-v8a (requires Android NDK)
build.bat all-android      :: All three APKs
```

*(You can also double-click `build.bat` for an interactive build menu.)*

#### Linux / macOS

```bash
# 1. Build the browser payload first (source of truth for patched libgame.so and game assets)
python3 browser/port/build.py

# 2. Build 32-bit Legacy and/or Modern APKs
python3 android/build_android.py --variant legacy
python3 android/build_android.py --variant modern

# 3. Optional: build 64-bit arm64-v8a APK (requires Android NDK)
python3 android64/build_android64.py translate
python3 android64/build_android64.py apk
```

### Step 3 — Signing Key & Installation

- On your first APK build, a persistent release keystore is automatically generated at `keys/android-release.jks` (with its password in `keys/android-keystore.env`). Both are gitignored.
- **Back up `keys/` safely** — keeping the same keystore lets players upgrade future APKs over installed copies without uninstalling.
- Install to a connected device or emulator:
  ```bash
  adb install -r build/KnS-Android-Modern.apk
  ```

---

## Admin Studio

Open Admin Studio at:
- **Hosted VM:** `https://<your-domain>/studio` (enter `KNS_ADMIN_KEY` from `/etc/kns/kns.env` or `deploy/kns.env`)
- **Local (`startgame.bat`):** `http://127.0.0.1:8790/kns-studio.html` (auto-loaded with `adminkey.txt`)

Capabilities:
- Search and inspect any registered player account
- Grant/edit Gold, Gems, Hero Points, Stamina, Level/EXP, items (with custom grades/options), allies, and ally equipment
- Configure Arena seasons, Weekly Raids, and Event Dungeon schedules
- Send targeted or server-wide Mass Mail
- Broadcast and manage pinned in-game Chat Notices

### Resetting Player Progress (Keep Accounts)

To wipe gameplay progress across all accounts while keeping logins, usernames, and schedules (takes an automatic SQLite backup first):

```bash
# Dry run (inspect what will change)
python3 tools/reset_progress.py --db /var/lib/kns/kns.sqlite

# On a systemd VM (stops server, backs up, resets, restarts):
sudo ./install.sh --reset-progress
```

---

## Running Tests

```bash
# 1. ARM CPU / GLES engine tests (Node.js 20+)
node --test "apk-cpu-engine/tests/*.test.cjs"

# 2. Browser adapter & headless native ELF tests (build browser first)
python3 browser/port/build.py
node --test "browser/port/tests/*.test.cjs"

# 3. Tools regression tests (schema / reset_progress.py)
python3 -m unittest discover -s tools/tests -v

# 4. .NET 8 server unit & integration tests
# Windows:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dotnet.ps1 test server-dotnet\tests\KnS.Tests -o work\test-build
# Linux / macOS:
dotnet test server-dotnet
```

---

## Contributing & License

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for local setup and test instructions.

This repository is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — see [LICENSE](LICENSE).
