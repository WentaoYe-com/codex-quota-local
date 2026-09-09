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
- If local logs do not contain quota data, reads `~/.codex/auth.json` in memory.
- If local logs do not contain quota data, sends an authenticated GET request to `https://chatgpt.com/backend-api/wham/usage`.
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

the app additionally sends an unauthenticated GET request to:

```text
https://codex-reset.com/api/forecast
```

This request does not include Codex credentials, account ids, local quota logs, file paths, or user content.

Radar mode refreshes every 10 minutes by default. The interval can be changed with:

```powershell
.\CodexQuotaLocal.exe --radar --radar-interval-minutes 15
```

## Live-first mode

When launched with:

```powershell
.\CodexQuotaLocal.exe --live
```

the app reads `~/.codex/auth.json` in memory and sends an authenticated GET request to:

```text
https://chatgpt.com/backend-api/wham/usage
```

The access token and account id are used only for that request. They are not logged, displayed, copied into this project, or written to disk. If the live request fails, the app falls back to local logs.

## Data retained

Quota percentages, reset times, and radar probabilities are kept in process memory while the app is running. The app does not create a quota history database or credential cache.

## Local paths

The app resolves Codex data from:

1. `CODEX_QUOTA_DATA_DIR`
2. `CODEX_HOME`
3. `%USERPROFILE%\.codex`

No personal path is embedded in the release package.
