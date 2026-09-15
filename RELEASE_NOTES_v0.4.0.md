# v0.4.0

Adds optional Codex credits balance display.

- Launch with `--balance`, use `Run-With-Balance.cmd`, or toggle `Credit balance` from the tray menu.
- The overlay displays `Credits 769.65`, `Credits unlimited`, or `Credits --` when the field is unavailable.
- CLI snapshots support `--snapshot --balance` and output `credits_balance`.
- Balance mode reuses the existing allowlisted authenticated usage endpoint. It does not add a destination, save balance history, or send credentials or balance data to radar providers.
- `--offline-only` remains strictly offline and disables balance display.
- Includes 26 synthetic regression tests covering quota freshness and credit balance parsing.

The credits field belongs to Codex's current internal usage response and is not documented as a stable public API contract. The UI therefore labels the value as `Credits` without assuming a currency symbol.
