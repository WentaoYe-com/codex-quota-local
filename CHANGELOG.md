# Changelog

## 0.4.0

- Added optional Codex credits balance display with `--balance` and a tray toggle.
- Added compact `Credits 769.65`, `Credits unlimited`, and `Credits --` states.
- Added CLI `credits_balance` output and double-click balance launchers.
- Balance mode reuses the allowlisted authenticated usage endpoint and never sends credentials or balance data to radar providers.
- Kept `--offline-only` strictly offline by disabling balance mode.
- Added credit parsing and mode behavior regression coverage; the suite now contains 26 synthetic tests.
- Consolidated release metadata into `CHANGELOG.md`, switched GitHub Releases to generated notes, and reduced bundled launchers to one full-feature script.

## 0.3.2

- Fixed auto mode repeatedly displaying expired local quota instead of querying live usage.
- Validate the log timestamp (60-second freshness limit) and all window reset times; stale data is unavailable in offline-only mode.
- Anchor relative reset times to the log timestamp, not the current polling time.
- Expire visible snapshots while waiting for refresh and discard results from a previous quota mode.
- Disable usage HTTP caching and redirects; use the configured interval on failed refreshes too.
- Add CLI observation timestamps and 18 synthetic regression tests.

## 0.3.1

- Added optional `--follow-codex` watcher mode to start the overlay when ChatGPT/Codex is running and close it when the host exits.
- Added `--exit-with-codex` for one overlay instance that exits automatically after ChatGPT/Codex closes.
- Added `Run-Follow-Codex.cmd` for double-click use.
- Added a system tray exit command for the watcher.
- Kept follow mode non-persistent: no startup entry, service, registry write, or telemetry.

## 0.3.0

- Changed reset radar to show multiple 24-hour readings in one compact line: `Radar 24h oracle/signal/watch`.
- Removed the 48-hour radar value from the overlay.
- Added `codexreset.app` and `savemetibo.com` as optional public radar sources.
- Kept partial radar results visible when one public source is unavailable.
- Added per-source radar values, update times, and URLs to CLI snapshot output.

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
