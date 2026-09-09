# Codex Quota Local v0.2.2

Portable Windows build.

## Highlights

- Adds tray menu controls for quota mode, reset radar, quota refresh interval, and radar refresh interval.
- Lets you switch between auto, offline-only, and live-first quota reads without restarting.
- Lets you turn reset radar on or off from the tray menu.
- Lets you choose quota refresh intervals of 5 seconds, 10 seconds, 30 seconds, or 1 minute.
- Lets you choose radar refresh intervals of 1 minute, 5 minutes, 10 minutes, or 30 minutes.
- Refreshes immediately after mode or interval changes.

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
