# Codex Quota Local v0.2.0

Portable Windows build.

## Highlights

- Shows Codex 5-hour and weekly quota remaining from local Codex logs.
- Adds optional public reset radar with `--radar`.
- Uses separate refresh cadences: local quota every 10 seconds, radar every 10 minutes.
- Supports `--quota-interval-seconds` and `--radar-interval-minutes`.
- Keeps the default mode offline-only: no `auth.json`, no network request, no telemetry.
- Keeps live usage endpoint access opt-in with `--live`.
- Includes GUI overlay and CLI snapshot executables.
- Includes double-click launchers for offline and radar modes.

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
.\CodexQuotaLocal.exe --radar
.\CodexQuotaLocalCli.exe --snapshot
```
