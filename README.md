# Vegas Reborn

Companion app for the **original PC Rainbow Six Vegas**, with account sign-in,
character synchronization, display settings, Discord activity and game launching.
The account and matchmaking backend are maintained separately.

**[Download for Windows](https://github.com/Lumbridge/Vegas-Reborn/releases/latest)**

Vegas Reborn is experimental. Sign-in, character creation, customization and
save/reload have been demonstrated with the original game, along with server
browsing and joining a dedicated lobby. Full multiplayer matches and play across
different networks are still being verified. Discord join invites are not yet
available.

## What you need

- A Windows PC with an owned, installed copy of the original **Rainbow Six Vegas**.
  This companion is for the original game, not the UE5 remake.
- Steam running and signed in.
- A Discord account and an internet connection for sign-in and character sync.
- A writable folder for the companion and its separate game copy.

The download includes the runtime: you do not need to install Python or build
tools. Original game files are not included. Support currently targets the
specific original PC build used in testing; other game versions are unverified.

## Get started

1. Download the Windows ZIP from the
   [latest release](https://github.com/Lumbridge/Vegas-Reborn/releases/latest).
2. Extract the entire `VegasReborn` folder to a writable location, then open
   `VegasReborn.exe`.
3. Open **Display & game** and use **Browse** to select your installed game folder.
4. Open **Account**, choose **Sign in with Discord**, and complete sign-in in your browser. Claim
   your player name if prompted.
5. Open **Overview** and choose **Launch game**. In the game, select **Multiplayer > Online > Login**.
   The companion handles game sign-in; never enter your Discord password in the
   game.
6. When you finish playing, close the game normally and leave the companion open
   until it confirms **Character saved to your account**.

The companion prepares its own game copy and account-specific character folder.
Your installed game and its existing profiles are preserved.

**Skip intro movies** is available in **Display & game** and applies on the next
launch. It is off by default and bypasses the startup logo and splash sequence,
including its waits. Loading screens, the menu background and gameplay movies
are retained. Turning it off restores the original startup behavior.

## Your character and settings

Your account's saved character is downloaded before you play. When the game
closes, the companion uploads your character and progression. If the local and
account copies conflict, the app lets you choose the appearance while preserving
the higher XP. Complete match-earned XP and unlock behavior are still being
verified.

Use **Sync character** to sync without starting the game, or **Character folder**
to open your local character files.

Display options include **Borderless**, **Windowed**, **Fullscreen** and
**Game default**. Borderless fills your selected monitor without keeping the
game always on top. Changes apply the next time you launch the game; Alt-Tab and
multi-monitor gameplay testing are still in progress. You can also have the
companion minimize when the game starts.

**Share my Vegas Reborn activity on Discord** is optional. Turn it off in the app
if you do not want to share your activity. Discord desktop activity sharing is
separate from signing in with your Discord account.

## Hosting

Use **Create Match in the game** for normal hosting. Dedicated hosting is a
separate process and is not in the companion's normal navigation. Operators can
use `python tools/host_server.py` from a prepared source checkout. The explicit
legacy `--host` argument remains supported for existing shortcuts.

Hosting is experimental: reaching a server lobby has been demonstrated, but
hosting a complete match and connecting across different networks are not yet
verified. Starting a server does not guarantee that other players can join.

## Keep the companion up to date

The companion checks for updates when it starts. When one is available, finish
playing, stop any hosted server, then choose **Update and restart**.

Updates are verified before installation and preserve your game copy, character,
settings and saved sign-in. You can disable startup update checks in the app.

## If something goes wrong

- **The game will not launch:** check that Steam is running and signed in, and
  that **Browse** points to your installed original Rainbow Six Vegas folder.
- **Game sign-in fails:** sign in with Discord again in the companion, then retry
  **Multiplayer > Online > Login** in the game.
- **Borderless mode does not work as expected:** select **Windowed** and launch
  the game again.
- **Your character has not synced:** keep the companion open after the game exits
  and check its status message. Use **Sync character** to retry once the game is
  closed.

To report a problem, [open an issue](https://github.com/Lumbridge/Vegas-Reborn/issues)
with your companion version, what you were doing and the error shown. Do not
attach account sessions, tokens or private character files.

## For contributors

See [Development and releases](docs/DEVELOPMENT.md) for source setup, native
helper builds, tests and release signing. This repository contains the Windows
companion; the account and matchmaking backend are maintained separately.
