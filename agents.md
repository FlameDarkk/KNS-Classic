# KNS-Classic agent instructions

## Context
- [README.md](README.md): layout, local quick start, hosting, Android APKs.
- [CONTRIBUTING.md](CONTRIBUTING.md): test commands, analyzer rules, schema guidelines.
- [deploy/README.md](deploy/README.md): packaging, systemd install, Docker, remote deploy.
- [server-dotnet/README.md](server-dotnet/README.md): environment variables and server configuration.

## Rules
- **Commit and push after every successful change:** After every verified successful change made by an AI agent, commit the relevant changes with a clear message and push to `origin` automatically so uncommitted local changes do not accumulate.
- **Deploy only when asked:** Deploy to production only when the user explicitly requests it. Never pass `-ResetProgress` unless explicitly instructed.
- **Never commit secrets or local runtime data:** Always inspect `git status` before committing. Never commit `adminkey.txt`, `deploy/kns.env`, `deploy/remote.local.json`, `keys/`, `work/`, `build/`, or `*.sqlite`.
- **Keep `apk-cpu-engine/` game-agnostic:** `apk-cpu-engine/` is a git submodule (`FlameDarkk/apk-cpu-engine`). KnS-specific symbols, addresses, Java class names, and server URLs belong in `browser/port/`, never in the engine. Engine changes must be committed in that repo first, then the submodule pointer updated here.
- **Zero `.NET` warnings:** `server-dotnet/Directory.Build.props` sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` with `8.0-recommended` analyzers. On Windows, run `.NET` builds/tests via `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dotnet.ps1 test server-dotnet\tests\KnS.Tests -o work\test-build` (setting `$env:KNS_DOTNET_ROOT='C:\Users\YM\Desktop\KNS\tools\dotnet8'` if no local `tools\dotnet8` is installed).
- **Schema changes:** If you add or rename a table in `server-dotnet/src/KnS.Data/Schema/GameSchema.cs`, classify it in `tools/reset_progress.py` (`KEEP` or `WIPE_GLOBAL`) and verify `python -m unittest discover -s tools/tests -v`.
- **Studio changes:** Edit `browser/studio/studio.template.html` and `browser/studio/studio.js` (never hand-edit generated `browser/kns-studio.html`), then rebuild with `python tools/build_studio.py`.
- **Headless-first verification:** Verify native client behavior against `libgame.so` using `browser/port/headless.js` and `browser/port/tests/native-*.test.cjs`, pinning raw ELF addresses with `expectCall`/`expectLiteral`. Preserve unrelated changes and test only against disposable databases.
- **Builds and deploys on Windows:** Run `& .\build.bat <target> --no-pause` / `& .\deploy.bat --no-pause` from PowerShell in the repo root (not via Bash or `cmd /c`).
