# Privacy

Codex Quota Local is designed to keep quota reads local-first and inspectable.

## Default mode

When launched without flags:

```powershell
.\CodexQuotaLocal.exe
```

the app:

- Reads `~/.codex/logs_2.sqlite` in read-only mode.
- Parses Codex rate-limit headers already stored by Codex.
- If local quota data is missing, at least 60 seconds old, has an invalid timestamp, or has a passed reset time, reads `~/.codex/auth.json` in memory.
- In that case, sends an authenticated GET request to `https://chatgpt.com/backend-api/wham/usage` at the configured quota refresh interval (10 seconds by default). HTTP caching and automatic redirects are disabled.
- Does not create telemetry, analytics, history, or startup entries.

## Offline-only mode

When launched with:

```powershell
.\CodexQuotaLocal.exe --offline-only
```

the app:

- Reads `~/.codex/logs_2.sqlite` in read-only mode.
- Does not read `~/.codex/auth.json`.
- Does not make network requests.

## Radar mode

When launched with:

```powershell
.\CodexQuotaLocal.exe --radar
```

the app additionally sends unauthenticated GET requests to:

```text
https://codex-reset.com/api/forecast
https://codexreset.app/api/signal
https://savemetibo.com/status.json
```

This request does not include Codex credentials, account ids, local quota logs, file paths, or user content.

Radar mode refreshes every 10 minutes by default. The interval can be changed with:

```powershell
.\CodexQuotaLocal.exe --radar --radar-interval-minutes 15
```

## Follow-Codex mode

When launched with the full-feature convenience script (equivalent to `--follow-codex --radar --balance`):

```powershell
.\Run-Follow-Codex.cmd
```

the app keeps a lightweight local watcher process running. It checks for local `ChatGPT` or `Codex` desktop processes every 2 seconds, starts the overlay while the host exists, and closes the overlay when the host exits. The watcher exposes a tray icon with an `Exit follower` command.

Follow-Codex mode does not create startup entries, services, registry keys, telemetry, analytics, history, or credential caches. The bundled script enables balance and radar; their network behavior is described in the adjacent sections.

## Live-first mode

When launched with:

```powershell
.\CodexQuotaLocal.exe --live
```

the app reads `~/.codex/auth.json` in memory and sends an authenticated GET request to:

```text
https://chatgpt.com/backend-api/wham/usage
```

The access token and account id are used only for that request. They are not logged, displayed, copied into this project, or written to disk. If the live request fails, the app falls back only to fresh local logs. Expired records are not displayed as current quota.

## Credit balance mode

When launched with `--balance`, or enabled from the tray menu, the app reads the `credits` object returned by the same authenticated usage request. It displays only the balance rounded to two decimal places, `unlimited`, or an unavailable marker.

Credit balance mode does not add another endpoint. It requires `auth.json` and makes the usage request at the configured quota refresh interval. The token, account id, full response, and balance history are not stored or sent to radar providers. `--offline-only` disables credit balance mode even if `--balance` is also present.

## Data retained

Quota percentages, reset times, credit balance, and radar probabilities are kept in process memory while the app is running. The app does not create a quota history database or credential cache.

## Local paths

The app resolves Codex data from:

1. `CODEX_QUOTA_DATA_DIR`
2. `CODEX_HOME`
3. `%USERPROFILE%\.codex`

No personal path is embedded in the release package.
