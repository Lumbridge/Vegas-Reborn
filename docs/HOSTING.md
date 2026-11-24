# Dedicated server hosting

Normal hosting is **Create Match** in the game. This page covers running a
dedicated server: an always-on match that players join through
**Online > Custom Match**.

Dedicated servers run the original game's own server program
(`R6VegasServer.exe` for Rainbow Six Vegas), with the companion's server
module injected into it. The host needs:

- Windows 10 or 11, signed in to a desktop session
- the original PC game installed, at the supported Steam version
- the Vegas Reborn launcher release, extracted to a writable folder
- a Discord account to sign in with

Rainbow Six Vegas 2 dedicated hosting is not available yet. Linux and container
hosting are being assessed.

## Set up

Run these from the launcher folder. `0.5.3` stands for the installed version.

```powershell
.\runtime-0.5.3\VegasServer.exe init --game-folder "C:\Program Files (x86)\Steam\steamapps\common\Rainbow Six Vegas"
.\runtime-0.5.3\VegasServer.exe login
```

`init` writes `local\dedicated\vegas1\server.json` and checks the game files.
`login` prints a link. Open it on any device and approve the Discord sign-in.
The session is saved in `local\companion\account-session.json`, the same file
the launcher uses.

## Run

```powershell
.\runtime-0.5.3\VegasServer.exe run
```

The first run copies the game into `local\dedicated\vegas1\game`. The installed
game is only read. The server runs until you press Ctrl+C, close the window, or
run `VegasServer.exe stop` from another window. If the window is closed or the
host process ends, Windows ends the server with it. Each run writes a record,
including the server's log, to `local\dedicated\vegas1\runs\<id>`.

`run --local-test` starts a server bound to 127.0.0.3 without signing in. Use it
to check the setup on one PC.

## Maps Pack

```powershell
.\runtime-0.5.3\VegasServer.exe maps-pack install
.\runtime-0.5.3\VegasServer.exe maps-pack status
.\runtime-0.5.3\VegasServer.exe maps-pack remove
```

`install` downloads Ubisoft's official Maps Pack (about 727 MB), verifies it
and adds it to the server's game copy on the next run. It also adds Terrorist
Hunt, Deathmatch, Team Deathmatch and Attack & Defend to the story missions, for
up to 16 players. The added modes have not been tested in a full online match yet. Players need the pack in their
launcher to join those maps. Stop the server before installing or removing it.
The pack is shared with the launcher on the same PC, so it is downloaded once:
`install` does nothing when the published version is already installed. Use
`install --force` to download it again.

## Settings

`local\dedicated\vegas1\server.json`:

| Setting | Values | Default |
| --- | --- | --- |
| `gameFolder` | Full path of the installed game | from `init` |
| `map` | A map name, such as `MP_Streets_01` | `MP_Streets_01` |
| `gameType` | A game class, such as `R6Game.R6TeamDeathmatchGame` | `R6Game.R6TeamDeathmatchGame` |
| `maxPlayers` | 2–16 | 16 |
| `friendlyFire` | `true` or `false` | `false` |
| `password` | Letters and digits, up to 16; empty for an open server | empty |

Other server options are the original game's defaults. Restart the server after
changing settings. `VegasServer.exe check` validates the file without starting
anything.

## Network

In a local test run (2026-11-24) the original server opened UDP 3074 and 45000
on all interfaces and UDP 3075 on the server address. Allow `R6VegasServer.exe`
in `local\dedicated\vegas1\game\Binaries` through Windows Firewall. When hosting
from home, forward UDP 3074, 3075 and 45000. Which of these a remote player
needs has not yet been confirmed with a join through a router.

## Run at startup

The server needs a signed-in desktop session, so use Task Scheduler rather than
a Windows service: create a task that runs at log on, with the program set to
`VegasServer.exe`, arguments `run`, and the start folder set to the launcher
folder. After a launcher update, point the task at the new `runtime-<version>`
folder.

## Operator tools

`python tools/host_server.py` from a source checkout and the legacy `--host`
argument of the 0.4.x Python companion are still supported.
