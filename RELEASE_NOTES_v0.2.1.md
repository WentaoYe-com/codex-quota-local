# Codex Quota Local v0.2.1

Portable Windows build.

## Highlights

- Uses local-first auto quota reading by default.
- Reads Codex local logs first and falls back to the live usage endpoint when logs do not contain quota data.
- Adds `--offline-only` for the previous strictly local behavior.
- Keeps `--live` as live-first mode, with local logs as fallback.
- Updates offline double-click launchers to pass `--offline-only`.
- Shows CLI diagnostics for the quota source and fallback path.

## Files

- `CodexQuotaLocal.exe`
- `CodexQuotaLocalCli.exe`
- `Run-Offline.cmd`
- `Run-With-Radar.cmd`
- `Snapshot-Offline.cmd`
- `Snapshot-With-Radar.cmd`
- `README.md`
- `PRIVACY.md`
- `SECURITY.md`
- `CHANGELOG.md`
- `LICENSE`

## Run

```powershell
.\CodexQuotaLocal.exe
.\CodexQuotaLocal.exe --offline-only
.\CodexQuotaLocal.exe --radar
.\CodexQuotaLocalCli.exe --snapshot
```
