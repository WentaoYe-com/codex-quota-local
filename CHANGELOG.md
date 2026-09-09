# Changelog

## 0.2.2

- Added tray menu controls for quota mode, reset radar, quota refresh interval, and radar refresh interval.
- Changing mode or refresh intervals now triggers an immediate refresh.

## 0.2.1

- Changed the default quota reader to local-first auto mode: read Codex local logs first, then fall back to the live usage endpoint if logs do not contain quota data.
- Added `--offline-only` for the previous strictly local behavior.
- Updated offline double-click launchers to pass `--offline-only`.
- Added CLI diagnostics showing whether auto mode used local logs or live fallback.

## 0.2.0

- Added optional reset radar support with `--radar`.
- Split refresh cadence: quota defaults to 10 seconds, radar defaults to 10 minutes.
- Added `--quota-interval-seconds` and `--radar-interval-minutes`.
- Improved compact overlay labels.
- Added privacy, security, and release documentation.
- Added portable zip creation in `build.ps1`.

## 0.1.0

- Initial local-only quota overlay.
- Added offline parsing from Codex local logs.
- Added optional live usage endpoint mode with `--live`.
