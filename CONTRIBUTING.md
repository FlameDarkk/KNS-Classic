# Contributing to KNS-Classic

Thanks for your interest in contributing to **KNS-Classic**!

## Prerequisites

- **Python 3.10+** with `pyelftools` (`pip install pyelftools`)
- **.NET 8 SDK** (for building and testing `server-dotnet/`)
- **Node.js 20+** (for `apk-cpu-engine/` and `browser/port/` tests)
- **JDK 11+ & Android SDK** (only needed when modifying `android/` or `android64/`)

## Local Development

On Windows, run `startgame.bat` to build the browser client and Admin Studio, start the local `.NET 8` backend on `127.0.0.1:8081`, and serve both pages on `127.0.0.1:8790`.

On Linux / macOS, see the [Quick Start](README.md#linux--macos) section in `README.md`.

## Running Tests Before Opening a PR

All pull requests run through GitHub Actions CI. Please run the relevant suites locally before pushing:

```bash
# 1. ARM CPU / GLES engine tests
node --test "apk-cpu-engine/tests/*.test.cjs"

# 2. Rebuild browser & Studio bundles, verify assets, and run headless ELF tests
python3 browser/port/build.py
python3 tools/build_studio.py
python3 browser/browser_tools.py inspect
python3 browser/browser_tools.py verify-effects
node --test "browser/port/tests/*.test.cjs"

# 3. Tools regression tests (schema / reset_progress.py)
python3 -m unittest discover -s tools/tests -v

# 4. .NET 8 server unit & integration tests (TreatWarningsAsErrors is enabled)
dotnet test server-dotnet/KnS.Server.sln
```

On Windows, you can run the `.NET` tests via:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dotnet.ps1 test server-dotnet\tests\KnS.Tests -o work\test-build
```

## Guidelines

- **Zero compiler warnings**: `server-dotnet/Directory.Build.props` enables `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` with `8.0-recommended` analyzers.
- **Keep `apk-cpu-engine/` game-agnostic**: `apk-cpu-engine/tests/` enforces that no game-specific symbols or package names leak into the engine.
- **Schema changes**: If you add or rename a table in `server-dotnet/src/KnS.Data/Schema/GameSchema.cs`, classify it in `tools/reset_progress.py` (`KEEP` or `WIPE_GLOBAL`) and verify `tools/tests/test_reset_progress.py` passes.
- **Never commit secrets or local runtime files**: Keep `adminkey.txt`, `deploy/kns.env`, `deploy/remote.local.json`, `keys/`, `work/`, `build/`, and `*.sqlite` out of commits.
- **Add tests for fixes**: Include a focused regression test whenever you fix a bug or change protocol/endpoint behavior.

## License

By contributing to KNS-Classic, you agree that your contributions will be licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — see [LICENSE](LICENSE).
