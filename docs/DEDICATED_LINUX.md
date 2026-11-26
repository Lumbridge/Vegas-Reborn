# Vegas 1 dedicated server under Wine on Linux

Feasibility spike, 2026-11-24. The question: can the retail Vegas 1 dedicated
server (`R6VegasServer.exe`), started the way `src/vegas_reborn/dedicated.py`
starts it, run under Wine on Linux, so that a Docker image for Linux VPS hosts
is viable?

## Answer

Yes, for the server-side steps. Under Wine 11.0 with no display server at all,
the server starts, takes the companion's suspended-launch-and-inject sequence,
writes a `compat-status.json` that meets every condition `_run` checks, binds
its UDP ports and stays up. A Wine-based image is viable with:

- Wine 11.0 stable from WineHQ (`winehq-stable`, with the `i386` architecture
  enabled, because the server is 32-bit). No winetricks components were needed.
- No X server. Xvfb works too but adds nothing.
- Audio disabled in the prefix (see Audio).
- A different shutdown path than `WM_CLOSE` (see check 2).
- Host networking, or explicit UDP port publishing, for real players (see check 5).

Two things are still unproven: a Windows client actually joining (check 5 is
blocked), and long-running stability beyond a 3-minute soak.

## Environment

| Item | Value |
|---|---|
| Host | Windows 11 Pro 10.0.26200, WSL2 kernel 5.15.153.1-microsoft-standard-WSL2 |
| Linux | Ubuntu 24.04 LTS, official WSL rootfs, imported as the throwaway distro `vegas-spike` |
| Wine | `wine-11.0` (`winehq-stable 11.0.0.0~noble-1`, `wine-stable-amd64`, `wine-stable-i386`) |
| Prefix | Default WoW64 prefix created by `wineboot -i`; no Mono or Gecko |
| Extra packages | `xvfb xauth cabextract iproute2 procps netcat-openbsd` |
| Game | Companion-owned LAN copy made by `prepare_dedicated()` (server SHA-256 `f4388d30…84cb`), copied onto the distro's ext4 disk at `~/game`. The installed original was not touched. |
| Adapter | `VegasDedicated-4.dll` built from this commit with `Build-Companion.ps1 -Dedicated` (SHA-256 `66a99483…fce9`) |
| Injector | Existing `VegasInjector.exe` from the main checkout's `local/companion/bin` |

Docker Desktop was the first choice but would not start on this machine: its
backend crashed on Windows AF_UNIX socket files (`remove …\Docker\run\dockerInference:
The file cannot be accessed by the system`, Win32 error 1920), even for socket
files it had just created itself. That is a host problem unrelated to this
spike, and a reboot will probably clear it. The tests ran in a plain WSL2 distro instead,
which is the same Linux kernel Docker Desktop uses.

## Audio

Every server run in this spike had audio disabled in two ways:

```sh
# once, when creating the prefix
wine reg add 'HKCU\Software\Wine\Drivers' /v Audio /d '' /f
# on every run
export WINEDLLOVERRIDES="winepulse.drv,winealsa.drv,wineoss.drv=d"
unset DISPLAY WAYLAND_DISPLAY PULSE_SERVER
```

A `WINEDEBUG=+loaddll` run confirmed that the server loads `dsound.dll` and
`mmdevapi.dll`, which it imports, but no Wine audio driver (`winepulse`,
`winealsa` or `wineoss`) is ever loaded, so no audio device is opened. The
image should ship the prefix with this registry value and set the override in
its entrypoint.

## Harness

`dedicated.py` uses Windows `ctypes` and refuses to run off Windows, so it
cannot run under Linux Python. A small Win32 program (`winehost.exe`) was
built with the repo's MSVC toolchain and run under Wine instead. It makes the same
calls with the same arguments as `_run` and `stop_owned`:

- `CreateProcessW` with `CREATE_SUSPENDED | CREATE_NO_WINDOW`, `STARTF_USESHOWWINDOW`/`SW_HIDE`,
  `lpDesktop = "winsta0\\Default"`, working directory `Binaries`, the same
  `TEXTSERVER` URL and `MULTIHOME=127.0.0.3`
- `VegasInjector.exe <pid> <tid> <dll> <runtime> <game> <source>` with a 30 s wait
- a 65 s wait for `compat-status.json` while watching the server process
- `EnumWindows` → `PostMessageW(WM_CLOSE)` to windows owned by the server PID,
  a 5 s wait, then `TerminateProcess` and a 10 s wait

The runtime directory got the same LAN `native-route.txt` and empty `profile`
directory as `_run`. The harness source and the shell scripts are kept outside
the repo in the session notes folder, not committed.

## Results

| # | Check | Result |
|---|---|---|
| 1 | Server starts headless | Pass, with no X server and with Xvfb |
| 2 | `CreateProcessW` / `EnumWindows` / `WM_CLOSE` | `CreateProcessW` passes. `EnumWindows`/`WM_CLOSE` does not stop the server: it creates no windows, so shutdown always falls through to `TerminateProcess` |
| 3 | Injection | Pass |
| 4 | `compat-status.json` ready | Pass |
| 5 | Windows client joins over UDP (LAN) | Blocked |

### 1. Headless start: pass

```sh
cd ~/game/Binaries
wine R6VegasServer.exe TEXTSERVER 'MP_Streets_01?AgO=0?AgU=?AgP=?SrvOptionFile=R6VegasDedicatedLAN?RG=0?RC=3?TBR=0?PB=0?GAME=R6Game.R6TeamDeathmatchGame?TKPM=5?FF=0?TL=180?MP=16?AR=1?ML=3?TR=0?GPL=0?LT=0?TKP=1?TB=5?GD=2?PW=' MULTIHOME=127.0.0.3
```

With `DISPLAY` and `WAYLAND_DISPLAY` unset, the process stayed up for the full
run (60 s, and 180 s in the soak). It bound:

```
UDP 0.0.0.0:3074
UDP 127.0.0.3:3075     (game port on the MULTIHOME address)
UDP 0.0.0.0:45000
```

and wrote `Binaries/Log_R6VegasDedicatedLAN.log` with `Restart` followed by
`PlayerJoin: 0`. The only errors were Wine's expected no-display messages:

```
err:winediag:nodrv_CreateWindow Application tried to create a window, but no driver could be loaded.
err:winediag:nodrv_CreateWindow L"The explorer process failed to start."
err:systray:initialize_systray Could not create tray window
```

They are harmless: the server creates no windows (see check 2). With
`Xvfb :99 -screen 0 1024x768x24 -nolisten tcp` and `DISPLAY=:99`, the
behaviour was the same and those errors went away.

Resource use during a 3-minute soak with no players: about 185 MB RSS, with
lifetime-average CPU falling from 9.4% to 3.6% of one core.

### 2. Process and window APIs: partial

- **`CreateProcessW` (suspended, desktop `winsta0\Default`, `CREATE_NO_WINDOW`): pass.**
  Returned a PID and TID. `SuspendThread` reported a previous suspend count of
  1, so the main thread really was suspended until the injector resumed it.
- **`EnumWindows` + `WM_CLOSE`: does not stop the server.** With no display, `EnumWindows`
  returned 0 top-level windows. Under Xvfb it returned 8, all belonging to
  Wine's `explorer.exe`, and none to the server PID, so no `WM_CLOSE` was
  posted. A `WINEDEBUG=+pid,+win` trace showed the server process initialising
  user32 but calling `CreateWindowEx` zero times in TEXTSERVER mode. The only
  non-Wine window in the trace was `WineConsoleClass` from `conhost.exe`, which
  appears only when Wine is started from a terminal without `CREATE_NO_WINDOW`.
- **`TerminateProcess` fallback: pass.** After the 5 s wait the harness called
  `TerminateProcess`, and the process exited within the 10 s wait every time.

So under Wine, `stop_owned` always takes its forced path and records
`forced_termination=True`. This was not compared with Windows: the spike did not
launch the server there. Since the server creates no windows under Wine,
Windows probably also has none for `WM_CLOSE` to reach, but that is unverified.
For the Linux package, either treat `TerminateProcess` (or `wineserver -k`) as
the normal stop, or add a graceful stop that does not depend on windows, for
example the injected adapter watching the runtime's `stop.request` file.

### 3. Injection: pass

`VegasInjector.exe` exited with code 0 inside its 30 s wait. Its
`CreateRemoteThread(LoadLibraryW)` load, the Toolhelp module lookup, the export
call and the `ResumeThread` of the suspended main thread all worked under Wine
11.0's WoW64 prefix with no extra setup.

### 4. `compat-status.json`: pass

The adapter wrote `compat-status.json` (plus `agora-session`) into the runtime
directory within the setup window, under both no display and Xvfb:

```json
{"ready":true,"executablePagesReady":true,"nativeVersion":4,"signatures":31,
 "nativeStatisticsVerified":true,"nativeRankedHostVerified":true,"nativeRankedHostStatus":"none",
 "nativeSoloHuntVerified":true,"nativeSoloHuntStatus":"none","taskHookStatus":0,
 "progression":"nativeDedicatedDefaults","disabledFeatures":[], ...}
```

That satisfies every condition `_run` enforces (`ready`, `nativeVersion == 4`,
`nativeStatisticsVerified`, `nativeRankedHostVerified`,
`nativeSoloHuntVerified`). The server kept running for the 30 s after setup in
each run.

### 5. Windows client join: blocked

Not tested. Three things are needed:

1. **A retail client launch.** Joining needs the retail Vegas client running on
   Windows, and none was launched for this spike.
2. **A server address the client can reach.** The LAN run uses
   `MULTIHOME=127.0.0.3`, which inside WSL2 or a container is that machine's own
   loopback and cannot be reached from Windows. A run with
   `MULTIHOME=<eth0 address>` worked under Wine. Wine bound the game port as
   `0.0.0.0%eth0:3075`, using its interface-bound socket form instead of a
   plain `bind(ip)`. UDP probes sent from Windows to ports 3074, 3075 and 45000
   reached the WSL VM but got no reply, which is expected for garbage
   datagrams. That says nothing about joinability.
3. **The client's network has to see the server.** LAN discovery broadcasts do
   not cross WSL2's NAT or Docker's default bridge network. Test from a real Linux
   host on the same LAN, from WSL with mirrored networking, or with
   `docker run --network host`, and set `MULTIHOME` to that host's LAN address.

## What the Docker image needs

- Base: Ubuntu 24.04 (or Debian 12) with `dpkg --add-architecture i386` and
  `winehq-stable` 11.0 from `dl.winehq.org`. Pin the package version.
- No Xvfb, no X libraries required at run time. Keep the no-display `err:`
  lines out of health checks.
- Pre-built prefix in the image, created with
  `WINEDLLOVERRIDES="winepulse.drv,winealsa.drv,wineoss.drv=d;mscoree,mshtml=" wineboot -i`,
  then `HKCU\Software\Wine\Drivers\Audio=""`. Run as a non-root user.
- Entrypoint sets the same `WINEDLLOVERRIDES`, unsets `DISPLAY`,
  `WAYLAND_DISPLAY` and `PULSE_SERVER`, and runs the Windows launcher (the .NET
  `vegas-server.exe`, or equivalent) under Wine. That launcher does
  `CreateProcessW` (suspended) → `VegasInjector.exe` → wait for
  `compat-status.json`.
- Game files come from a host mount, read-only. The launcher must copy them to a
  writable, container-owned directory first. The server rewrites
  `KellerGame/Config/PC/KellerGame.ini` and `Binaries/Log_R6VegasDedicatedLAN.log`
  while it runs, and the launcher writes the `R6VegasDedicatedLAN.ini` options
  file during preparation.
- Shutdown: on `SIGTERM`, stop the server with `TerminateProcess` or
  `wineserver -k`, unless a window-independent graceful stop is added. Don't rely
  on `WM_CLOSE`.
- Networking: `--network host`, or publish UDP 3074, the game port (3075 here)
  and 45000. Set `MULTIHOME` to the address players reach. Online play also needs
  the address players connect to (the public or NAT address) to match what the
  server advertises. That has not been looked at yet.
- Budget about 200 MB RAM per idle server, plus the writable game copy
  (about 6.3 GB, or less if trimmed to what the server loads).

## Open items before building the image

1. Check 5: one manual client join against the Wine server on a reachable
   address, with someone running the Windows client by hand.
2. Run `stop_owned` on Windows once to see whether `WM_CLOSE` ever stops the
   server there. Then decide on the graceful stop path for both platforms.
3. A soak test longer than 3 minutes, with players and map rotation.
4. Repeat checks 1–4 inside an actual container (`--network host`) once
   Docker Desktop starts. WSL2 used the same kernel, but the container's
   filesystem and user setup may differ.

## Commands used

```sh
# distro (Windows side)
wsl --import vegas-spike <dir> ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz --version 2

# Wine (root, inside the distro)
dpkg --add-architecture i386
apt-get update && apt-get install -y wget gnupg ca-certificates xvfb xauth cabextract iproute2 procps netcat-openbsd
mkdir -pm755 /etc/apt/keyrings
wget -qO /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key
wget -qNP /etc/apt/sources.list.d/ https://dl.winehq.org/wine-builds/ubuntu/dists/noble/winehq-noble.sources
apt-get update && apt-get install -y --install-recommends winehq-stable

# prefix (as user vegas)
env -u DISPLAY -u WAYLAND_DISPLAY -u PULSE_SERVER WINEPREFIX=$HOME/prefix \
  WINEDLLOVERRIDES="winepulse.drv,winealsa.drv,wineoss.drv=d;mscoree,mshtml=" wineboot -i
WINEPREFIX=$HOME/prefix wine reg add 'HKCU\Software\Wine\Drivers' /v Audio /d '' /f

# checks 2-4 (paths are Wine Z: paths)
wine 'Z:\home\vegas\spike\bin\winehost.exe' 'Z:\home\vegas\game' '<runtime dir>' \
  'Z:\home\vegas\spike\bin\VegasInjector.exe' 'Z:\home\vegas\spike\bin\VegasDedicated-4.dll' \
  'Z:\home\vegas\source-placeholder' 30 127.0.0.3

# Xvfb variant
Xvfb :99 -screen 0 1024x768x24 -nolisten tcp & export DISPLAY=:99

# window trace and DLL audit
WINEDEBUG=+pid,+win   ...   # CreateWindowEx calls per process
WINEDEBUG=+loaddll    ...   # confirms no Wine audio driver loads
```
