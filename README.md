# PULSE//WATCH

WPF (.NET 8) connection monitor. Checks hosts/IPs by ICMP ping or TCP port on a per-host interval and pops slide-in alert tiles when a target goes down (red) or recovers (green).

## Run

```
dotnet run
```

or build and launch `bin/Debug/net8.0-windows/PulseWatch.exe`.

## Command line

- `--minimized` (or `/min`) — start hidden in the tray; monitoring and alerts run as normal.
- `--monitor N` (also `--monitor=N`, `/monitor:2`) — show alert tiles on screen N (1-based, as listed by Windows). Invalid/missing N falls back to the primary screen.

## Behavior

- **Add / Edit / Remove** targets from the main window (double-click a row to edit).
- Each target: name, host/IP, ICMP or TCP+port, check interval (seconds).
- Alert tiles slide in at the right edge of the primary screen (a transparent, click-through, always-on-top overlay — not the app window), so they appear even when the app is minimised. They dismiss on click, after the alert timeout (header field, default 10 s), or automatically when a downed target recovers.
- Minimising hides the window to a tray icon (cyan ring); double-click or right-click → Open to restore, right-click → Exit to quit.
- Only one instance runs per user; a second launch shows a notice and exits.
- ⚙ SETTINGS (header): X-minimises-to-tray (default on), confirm-before-exit (default on), alert timeout, custom tile colours (hex, with live swatch preview and reset), plus version/GitHub/licence info. Everything — settings and targets — lives in one `settings.json` for easy backup/migration.
- Alerts fire only on state transitions (up→down, down→up), not on every failed check.
- Config persists to `%APPDATA%\PulseWatch\settings.json`.
