# Codex Quota Local v0.3.0

This release changes reset radar from one forecast into a compact multi-source 24h view.

## Changes

- The overlay now shows `Radar 24h oracle/signal/watch` values as three 24-hour readings, for example `Radar 24h 25%/67%/86%`.
- Removed the 48-hour radar value from the overlay to keep the display compact.
- Added public radar sources:
  - `https://codex-reset.com/api/forecast`
  - `https://codexreset.app/api/signal`
  - `https://savemetibo.com/status.json`
- If a source is temporarily unavailable, its slot is shown as `--` while the other sources continue to display.
- CLI snapshot output now prints each radar source separately with its update time and source URL.

## Download

Download `CodexQuotaLocal-v0.3.0-win-x64-portable.zip`, unzip it, then run:

```powershell
.\CodexQuotaLocal.exe --radar
```
