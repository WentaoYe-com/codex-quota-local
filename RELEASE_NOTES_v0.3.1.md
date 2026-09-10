# Codex Quota Local v0.3.1

This release adds an optional follow-Codex launcher.

## Changes

- Added `--follow-codex`: a lightweight watcher that starts the overlay when the ChatGPT/Codex desktop app is running and closes the overlay when it exits.
- Added `--exit-with-codex`: run one overlay instance that exits automatically after the ChatGPT/Codex host closes.
- Added `Run-Follow-Codex.cmd` for double-click use with reset radar enabled.
- The watcher has a system tray icon with an `Exit follower` command.
- The follow mode does not create startup entries, services, registry keys, telemetry, or background persistence.

## Download

Download `CodexQuotaLocal-v0.3.1-win-x64-portable.zip`, unzip it, then double-click:

```text
Run-Follow-Codex.cmd
```
