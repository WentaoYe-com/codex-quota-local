# Security

## Reporting

Please do not open a public issue containing:

- Codex access tokens
- account ids
- `auth.json`
- `logs_2.sqlite`
- unredacted diagnostics containing private paths or request ids

Open a private security advisory or share only redacted reproduction steps.

## Endpoint allowlist

The source intentionally hardcodes and checks the only two network destinations used by quota fallback and optional modes:

- `--radar`: `https://codex-reset.com/api/forecast`
- default auto fallback / `--live`: `https://chatgpt.com/backend-api/wham/usage`

`--offline-only` uses neither endpoint.

## Release hygiene

Before publishing a release, run:

```powershell
.\build.ps1
```

Then scan the source and portable package for local private strings such as user names, absolute private paths, tokens, and account ids.
