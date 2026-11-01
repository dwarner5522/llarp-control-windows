# LLARP Control for Windows

A self-installing Windows app that runs an [LLARP](https://github.com/majestrate/llarp) node
(`llarpd`, the stakeless Lokinet fork) on Windows 10 (2004+) and 11, as a client or as a
router (snode). It's the Windows edition of
[llarp-control](https://github.com/dwarner5522/llarp-control) and uses the same `llarpctl`.

## Install

Download `LLARP-Control.exe` from the
[latest release](https://github.com/dwarner5522/llarp-control-windows/releases/latest), run it
and press **Install**. It copies itself to `%LOCALAPPDATA%\Programs\LLARP Control`, adds
Start menu/desktop shortcuts, an Apps & Features entry and `llarpctl` to your PATH, then sets
everything up:

1. **WSL 2**, if it's missing. This may need one restart; setup picks up again by itself after
   you sign in.
2. **Debian**, downloaded from Microsoft's official WSL distribution list (checksum verified)
   and imported as a separate distro called `LLARP`. Your other WSL distros aren't touched.
3. **llarpd**, built from source inside it at the commit in `payload/LLARP_COMMIT` (10-30 minutes).
4. **llarpctl**, a config, the opennet bootstrap and a permanent address. Then llarpd starts.

It needs about 3 GB of disk space and administrator rights (for routes, DNS rules and the firewall).

## How it works

llarpd has no Windows port (llarp 0.10.0 dropped win32), so it runs in the `LLARP` WSL 2 distro.
The app connects it to Windows:

- **Client:** `.loki` and `.snode` names work in Windows apps. A Windows DNS rule (NRPT) sends
  those names to llarpd's DNS on the distro's address, and llarpd's address range is routed
  into the distro. There, the traffic is forwarded into llarpd's tunnel.
- **Router (snode):** the UDP port (default 1090) is opened in Windows Firewall and relayed into
  WSL, since WSL 2 doesn't pass incoming UDP through. Set your public IP under Settings and
  forward the port on your router to this PC.
- **Exit node:** works, with forwarding/NAT set up inside the distro.

A hidden background process (`LLARP-Control.exe --host`) runs llarpd and sets this up while it
runs, then removes it when llarpd stops. llarpd keeps running when you close the window.
Settings → *Start LLARP when I sign in* starts it at sign-in through Task Scheduler.

The window has the same controls as the Linux GTK app (on/off, address, exit node,
bootstraps, log, settings), plus a **Setup** tab: repair, rebuild llarpd, open a Linux shell,
uninstall.

From a Command Prompt, `llarpctl …` runs llarpctl inside the distro (`llarpctl status`,
`llarpctl log -f`, `llarpctl bootstrap list`, …). `llarpctl start` goes through the app so
Windows gets connected.

### VPN through an exit node

The **VPN via exit** row sends all of Windows' internet traffic through a `.loki` exit node.
Type or pick the exit's address, set an auth code for a private exit, and turn the switch on.
llarpd restarts to connect.

Once llarpd routes into the exit inside WSL, the app routes `0.0.0.0/1` and `128.0.0.0/1` on
Windows into the distro. Your LAN, WSL and other VPN ranges (e.g. Tailscale's) stay direct.
llarpd's own traffic to its relays must not go into the tunnel, or it would loop back on
itself. llarpd adds a host route inside WSL for each relay it connects to. The app copies
those routes to Windows, via the route Windows used before the VPN (your router, or another
VPN's exit). It keeps them in sync while llarpd runs and removes everything when the VPN or
llarpd stops. If the app is killed while the VPN is on, the routes are cleaned up the next
time it opens or starts llarpd.

DNS: lookups go to whatever DNS server Windows already uses. If that's on your LAN (e.g. your
router), your lookups don't go through the exit.

## Files

| Path | What it is |
| --- | --- |
| `src/` | The app (C# 5 / WinForms, .NET Framework 4.x) |
| `payload/` | Files the exe carries into the distro: `llarpctl`, `llarp_core.py`, `wsl-setup.sh`, the opennet bootstrap, and the llarp commit to build |
| `build.ps1` | Builds `dist\LLARP-Control.exe` |
| `app.manifest` | Asks for administrator rights; DPI awareness |

`payload/llarpctl` and `payload/llarp_core.py` come from
[llarp-control](https://github.com/dwarner5522/llarp-control), which has the WSL support (the
`dump` and `winhost` commands and the DNS bind on the WSL address). Copy them over when they
change there.

## Build

On any Windows machine, with no SDK needed (the C# compiler ships with the .NET Framework):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This writes `dist\LLARP-Control.exe`. `build.ps1 -Preview` builds a copy that runs without admin rights
and opens windows directly (`--preview-welcome`, `--preview-main [tab]`) for checking the UI.

`.github/workflows/build.yml` builds the exe on every push and publishes a release for
tags like `v1.0.0`.

## Uninstall

Use Apps & Features → LLARP Control, or Setup → Uninstall. You can keep the `LLARP` distro (and
with it your .loki address) or delete it.
