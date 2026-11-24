# Vegas Reborn

Launcher for the original PC **Rainbow Six Vegas** and **Rainbow Six Vegas 2**,
with account sign-in, online play, character synchronization, display settings,
Discord activity and game launching. The account and matchmaking backend are
maintained separately.

**[Download the latest Windows release](https://github.com/Lumbridge/Vegas-Reborn/releases/latest)**

## What you need

- A Windows PC with an owned, installed copy of the original PC **Rainbow Six Vegas** or
  **Rainbow Six Vegas 2**.
- Steam running and signed in.
- A Discord account and an internet connection for sign-in and character sync.
- A writable folder for the launcher and its separate game copy.

The download includes the runtime: you do not need to install Python or build
tools. Original game files are not included. Support targets the specific
original PC build of each game used in testing; other game versions are
unverified.

## Get started

1. Download `VegasReborn-<version>-win64.zip` from the
   [latest release](https://github.com/Lumbridge/Vegas-Reborn/releases/latest).
2. Extract the entire `VegasReborn` folder to a writable location, then open
   `VegasReborn.exe`.
3. Open **Games**, choose your game and use **Browse** to select its installed folder.
4. Open **Account**, choose **Sign in with Discord**, and complete sign-in in your browser. Claim
   your player name if prompted.
5. Open **Games** and choose **Launch game**. In Rainbow Six Vegas, select
   **Multiplayer > Online > Login**. In Rainbow Six Vegas 2, choose **Login**
   from the main menu. The launcher handles game sign-in; never enter your
   Discord password in the game.
6. When you finish playing, close the game normally and leave the launcher open
   until it confirms **Character saved to your account**.

The launcher prepares its own game copy and account-specific character folder.
Your installed game and its existing profiles are preserved.

## Game fixes

Each game's page lists its fixes. Changes apply the next time you launch the game.

- **Display mode:** **Borderless** (default), **Windowed**, **Fullscreen** or
  **Game default**. Borderless fills your selected monitor without keeping the
  game always on top.
- **Monitor:** where the game window opens.
- **Skip intro movies:** enabled by default. Bypasses the startup logo and
  splash sequence, including its waits. Loading screens, the menu background
  and gameplay movies are retained. Turning it off restores the original
  startup behavior.

## Your character and settings

Your account's saved character is downloaded before you play. When the game
closes, the launcher uploads your character and progression. If the local and
account copies conflict, the app lets you choose which character to keep while
preserving the higher XP.

Use **Sync now** on the **Account** page to sync without starting the game.

**Settings** has **Minimise while playing**, **Discord activity** and
**Check for updates at startup**. Discord activity is optional and separate
from signing in with your Discord account. While it is on, your Discord status
shows a **Get Vegas Reborn** button that opens the launcher's download page.
Discord join invites are not available.

## Extra Maps

The Rainbow Six Vegas page has an **Extra Maps** section. **Download Extra Maps**
downloads Ubisoft's official extra maps (about 727 MB), verifies it and adds it
to the launcher's game copy. The installed game is not changed. With the pack
installed, story missions can also be played as Terrorist Hunt, Deathmatch,
Team Deathmatch and Attack & Defend for up to 16 players. The added modes have not been tested in a full online match yet.
Everyone in a match needs the pack. **Remove** takes it out again. Dedicated servers use
`VegasServer.exe extra-maps install`; see [docs/HOSTING.md](docs/HOSTING.md).

## Hosting

Use **Create Match in the game** for normal hosting. Dedicated servers run
from a separate console program, `runtime-<version>\VegasServer.exe`, that is
not in the launcher's normal navigation. Rainbow Six Vegas is supported; see
[docs/HOSTING.md](docs/HOSTING.md). Operators can still use
`python tools/host_server.py` from a prepared source checkout, and the explicit
legacy `--host` argument remains supported for existing shortcuts.

## Keep the launcher up to date

The launcher checks for updates when it starts. Choose **Update and restart**
to download one while you play. Installation waits for games to close. If a
game blocks it, close the game normally or choose **Force-close games and
update**. Force-close ends running original Vegas games and servers; unsaved
match progress may be lost. Progress is shown through download, verification,
game closing, installation and restart.

Updates are verified before installation and preserve your game copy, character,
settings and saved sign-in. You can disable startup update checks in **Settings**.

## If something goes wrong

- **The game will not launch:** check that Steam is running and signed in, and
  that **Browse** points to your installed original game folder.
- **Game sign-in fails:** sign in with Discord again in the launcher, then retry
  **Login** in the game.
- **Borderless mode does not work as expected:** select **Windowed** and launch
  the game again.
- **Your character has not synced:** keep the launcher open after the game exits
  and check its status message. Use **Sync now** to retry once the game is
  closed.

To report a problem, [open an issue](https://github.com/Lumbridge/Vegas-Reborn/issues)
with your launcher version, what you were doing and the error shown. Do not
attach account sessions, tokens or private character files.

## For contributors

See [Development and releases](docs/DEVELOPMENT.md) for source setup, native
helper builds, tests and release signing. This repository contains the Windows
launcher; the account and matchmaking backend are maintained separately.
