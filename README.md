# PULSE//WATCH

WPF (.NET 8) connection monitor for Windows. Checks hosts/IPs by ICMP ping, TCP port, HTTP/HTTPS request, or DNS lookup on a per-target interval and pops slide-in alert tiles at the screen edge when a target goes down (red) or recovers (green). Runs from the tray.

![Main window — target list with live status, latency or failure code, and a paused row](docs/main-window.png)

Alert tiles slide in at the screen edge and dismiss themselves (this one uses a per-target orange override for "down"):

![Alert tiles — TARGET DOWN and TARGET RESTORED](docs/alert-tiles.png)

<p>
  <img src="docs/edit-target.png" alt="Add/Edit target dialog" width="48%">
  <img src="docs/settings.png" alt="Settings dialog" width="48%">
</p>

## Getting started

Windows 10/11. To build you need the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`); `build.ps1` finds it even if your terminal's PATH predates the install.

```
.\build.ps1 -SelfContained         # x64, portable single exe (~150 MB), no .NET needed on the target  -> dist\
.\build.ps1                        # x64, smaller exe; target needs the .NET 8 Desktop Runtime          -> dist\
.\build.ps1 -Arm64                 # Windows on ARM (add -SelfContained for a portable exe)             -> dist-arm64\
.\build.ps1 -DebugBuild            # Debug build only (or just `dotnet run` while developing)
```

Then run `dist\PulseWatch.exe`. A fresh install seeds two starter targets (Google DNS and your default gateway); tick **Start automatically when I log in** in Settings to have it launch minimised at login.

## Command line

- `--minimized` (or `/min`) — start hidden in the tray; monitoring and alerts run as normal.
- `--monitor N` (also `--monitor=N`, `/monitor:2`) — show alert tiles on screen N (1-based, as listed by Windows). Invalid/missing N falls back to the primary screen.
- `--settings <file>` (also `--settings=<file>`) — use a different settings file instead of `%APPDATA%\PulseWatch\settings.json`, e.g. for a separate profile or a portable copy.

Only one instance runs per user; a second launch shows a notice and exits.

## Targets

Each target has: name, address, check method (TCP / PING / HTTP / HTTPS / DNS), IP version, interval, retries, timeout, active flag, optional alert sound, optional tile colours. HTTP(S) targets add a path and the ignore-certificate-errors option; DNS targets add the DNS server to ask and the record type. Double-click a row (or EDIT) to change a target; REMOVE is in the dialog too, and needs a second click within 3 s to confirm.

**Address** (for DNS targets: the domain name to look up) can be a hostname, an IPv4/IPv6 literal (saved in standard form: brackets dropped, a link-local `%scope` kept), a pasted URL (the host is extracted; for HTTP/HTTPS targets the scheme, port and path are taken from it too), or a token:

| Token | Resolves to (re-evaluated every check, so switching networks is picked up) |
|---|---|
| `{gateway}` | the default gateway (IPv6: usually link-local `fe80::…%scope`) |
| `{dns}` | the first DNS server, taken from adapters that have a gateway first, so VMware/WSL/Hyper-V adapters and Windows' `fec0:0:0:ffff::` placeholder servers are never picked |

**IP version** controls which address family is probed. Default is Auto, which behaves as the app always has:

| IP version | Hostname | IP literal | `{gateway}` / `{dns}` |
|---|---|---|---|
| **Auto** | TCP/HTTP/HTTPS: resolves A+AAAA and races candidates, IPv6 first (see below); whichever connects wins. ICMP and DNS: whatever the OS resolver lists first (usually IPv6 if there is an AAAA). | that family | IPv4 if present, else IPv6 |
| **IPv4** | A records only | must be IPv4, else `NO V4` | the IPv4 gateway/DNS, else `NO V4` |
| **IPv6** | AAAA records only | must be IPv6, else `NO V6` | the IPv6 gateway/DNS (usually link-local `fe80::…`), else `NO V6` |

A forced-family row is the way to watch one path explicitly — e.g. two rows, "Gmail v4" and "Gmail v6", make an IPv6 blackhole visible instead of hidden behind fallback. The list shows the family on forced rows (`TCP:443 v6`, `ICMP v4`).

## How checks work

**Scheduling.** Each active target runs its own loop on its interval. Loops start with a random 0.2–8 s offset so many targets don't fire in a synchronized burst every interval. A target is declared offline only after the check fails *and* the configured retries (default 1, spaced 1 s apart) also fail; one success flips it back online immediately. The per-target timeout (default 4000 ms) bounds the whole check, DNS included.

**TCP checks.** A probe is a plain TCP connect to the port — no data is sent.
- *Sticky route:* the address that connected last time is remembered (5 min) and steady-state checks open exactly one socket to it, with no DNS query.
- *Race on failure / first check / expiry:* the name is resolved and up to 4 candidate addresses are tried, IPv6/IPv4 interleaved, Happy Eyeballs style (RFC 8305): each attempt gets a 300 ms head start before the next joins, but one that fails (e.g. IPv6 with no route) hands over immediately, so a dead family costs nothing; the first to connect wins and becomes the sticky route. This stops one dead CDN address (listed first in DNS) from burning the whole timeout even though the host is fine; the race cancels the losers as soon as one connects. The target's IP version setting decides which addresses are eligible.
- *Socket close:* probe sockets are closed abortively (RST, `SO_LINGER` = 0) rather than with a FIN handshake. Because no data was exchanged there is nothing to flush, and a graceful close would leave one `TIME_WAIT` entry per check on the machine for 30–120 s.

**HTTP/HTTPS checks.** A `GET` for the target's path (default `/`, query strings allowed) on the target's port (default 80 / 443).
- *Up* means any response status below 400. The path is sent exactly as entered on the target's own host, so a path starting with `//` can't redirect the Host header or certificate check to another name. Redirects are not followed — a 3xx proves this server answered, and following it could end up measuring a different host. Only the response headers are waited for; the body is never downloaded.
- *Connection:* opened with the same sticky-route / race / RST-close logic as TCP checks, so the IP version setting applies and no `TIME_WAIT` entries pile up. Each check uses a fresh connection (no keep-alive) and ignores any system proxy, so it tests the real path every time. Latency is connect + TLS + time to response headers.
- *Certificates:* validated normally. **Ignore certificate errors** (HTTPS only, off by default) accepts self-signed, expired or wrong-name certificates — handy for a router admin page (`{gateway}`) or a LAN device on an IP address.

**DNS checks.** Looks up the target's address (a domain name, e.g. `www.google.com`) by sending its own query by UDP straight to the chosen DNS server (port 53 by default), so Windows' resolver cache is never involved. This catches "online, but nothing loads because DNS is broken", which ICMP or TCP:53 to the same server can't: those only prove the server is reachable.
- *Up* means the server answered with a record of the chosen type (A or AAAA, class IN, correct size) owned by the name asked for or by the end of its CNAME chain. A record for some other name elsewhere in the answer doesn't count.
- *Validation:* the query ID is cryptographically random, and a reply is only accepted if it is a standard response echoing the exact question; anything else is ignored. A reply that matches but is malformed reports `BADREPLY`.
- *Truncated answers* (TC flag) are retried over TCP within the remaining timeout, as the DNS spec intends; TC on its own proves nothing.
- *Retransmit:* UDP can lose packets, so the query is resent every second until the timeout.
- *Server:* `{dns}` (the default) for whatever server your network hands out, `{gateway}` for your router, an IP such as `1.1.1.1`, or a name such as `dns.google` (resolved by Windows, first address of the chosen family).
- International names are converted to punycode (`bücher.de` → `xn--bcher-kva.de`). On load, a DNS target whose name isn't a valid domain (e.g. hand-edited to an IP) is paused with a warning.

**ICMP checks.** One echo request with a 32-byte payload (the .NET/Windows `ping` default) per check. Auto lets the OS choose the family; IPv4/IPv6 resolve the name and ping the first address of that family. Pings use the synchronous API on a pool thread on purpose — the async one leaks a kernel handle per call on Windows.

**Failure codes** (shown in the latency column while a target is down):

| Code | Meaning |
|---|---|
| `TIMEOUT` | no reply / handshake / HTTP response headers / DNS answer within the timeout |
| `REFUSED` | host answered but the port is closed (host is alive); for DNS also the server refusing the query |
| `DNS` | name did not resolve |
| `UNREACH` | network/host unreachable (a router said no route) |
| `RESET` / `TTL` / `FAIL` | connection reset / TTL expired / other |
| `NO V4` / `NO V6` | forced family, but the target has no address of that family |
| `NO NET` | a `{gateway}`/`{dns}` token has no address right now |
| `HTTP 503` etc. | HTTP/HTTPS: the server answered with a status of 400 or above |
| `CERT` | HTTPS: certificate rejected (untrusted, expired, or wrong name); see *Ignore certificate errors* |
| `TLS` | HTTPS: TLS handshake failed for another reason (e.g. no TLS version or cipher in common) |
| `NXDOMAIN` | DNS: the server says the name doesn't exist |
| `SERVFAIL` | DNS: the server failed to resolve it (often its upstream is down) |
| `NODATA` | DNS: the name exists but has no record of the chosen type (e.g. AAAA on an IPv4-only name) |
| `BADREPLY` | DNS: the server's reply was malformed, or still truncated over TCP |
| `BAD NAME` | DNS: the name to look up isn't a valid domain (normally caught when saving or loading) |

## Alerts and the tray

- Tiles slide in at the right edge of the chosen screen (or the left edge; see Settings) in a small always-on-top overlay that exists only while tiles are showing and is sized to them — nothing sits over your desktop or games the rest of the time. They appear even when the app is minimised, never steal focus, and dismiss on click, after the alert timeout (default 10 s), or automatically when a downed target recovers. Alerts fire only on state transitions, not on every failed check.
- A subtle two-note ping plays with each tile (per-target setting; falling for down, rising for recovery).
- Tray icon: cyan ring when all active targets are up, red ring while any is offline; the tooltip shows the version and offline count. Right-click for Open, History, Mute alert sounds, Suppress tiles (off / until re-enabled / 1 m / 5 m / 15 m / 30 m / 1 h / 12 h — monitoring continues, only tiles are held), Exit.
- Minimising hides to the tray; with "X minimises to tray" on, closing does too.

## History

**HISTORY** (top right of the main window, or History in the tray menu) opens one list of every outage across all targets since the app started, newest first, updating live:

![History — offline and recovery events with time, target, check type and failure reason](docs/history.png)

- Logged: every time a target goes offline (including one that's already down on the first check), with the failure code as the reason, and every recovery from a logged outage. A target that simply starts online logs nothing.
- Each row is a snapshot of the target's name and check type at that moment, so editing or removing a target keeps its past events.
- Tile suppression doesn't affect it: outages are logged even while tiles are held.
- **EXPORT CSV** saves `Date,Time,Target,Type,Event,Reason`, oldest first (UTF-8, opens cleanly in Excel).
- Memory only, nothing written to disk: the history starts fresh each launch. The most recent 10,000 events are kept.

## Settings

⚙ SETTINGS: X-minimises-to-tray, confirm-before-exit, auto-start at login (minimised), alert timeout, which side of the screen alert tiles appear on (right by default, or left), global tile colours (with picker), version/GitHub/licence. Everything — settings and targets — lives in one JSON file, `%APPDATA%\PulseWatch\settings.json` by default (Config Folder link on the main screen) or whatever `--settings` points at, for easy backup or moving between machines. Auto-start is the one setting kept in the registry instead, since it embeds the exe path.

Loading is defensive: out-of-range numbers are clamped, invalid colours reset, duplicate target IDs regenerated, and an unreadable file is preserved as `settings.json.corrupt` with a warning. Saves are atomic.

## Licence

MIT — see `LICENSE`.
