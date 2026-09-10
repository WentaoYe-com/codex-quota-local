# v0.3.2

Fixes the quota overlay showing old percentages and yesterday's reset time even while refreshing.

- Auto mode queries live usage when local logs are missing, at least 60 seconds old, or past a quota reset.
- Offline mode and failed live requests no longer present stale percentages as current quota.
- Relative reset times are calculated from the original log timestamp.
- CLI snapshots include the observation time; tray diagnostics explain unavailable readings.
- Usage HTTP caching and redirects are disabled. No new network destinations or stored credentials.

Extract the portable zip and run `Run-Follow-Codex.cmd` for the follower with radar, or `CodexQuotaLocal.exe` for the default overlay. Exit the previous version before replacing its files.

The default quota interval remains 10 seconds, and radar remains 10 minutes. Recent local readings can still lag by up to 60 seconds; use `--live` to query the usage endpoint first.
