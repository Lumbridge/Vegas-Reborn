# Vegas 2 PunkBuster settings

PunkBuster's service no longer exists. The Vegas 2 adapter removes the
"ENABLE PUNKBUSTER SERVER" and "ENABLE PUNKBUSTER CLIENT" rows and keeps
PunkBuster off.
Implementation: `tools/companion/native_punkbuster_vegas2.h`.

## Original evidence

- `R6Menus.int` `[Punkbuster] EnablePunkbusterServerLabel` and
  `EnablePunkbusterClientLabel` are read through the data pointers
  `0x121bd264` and `0x121bd268`. Their only code references are the native
  settings menu refresh (`0x114ccd70`, vtable `0x11ef21e4` slot 8). No other
  menu uses the labels; both rows exist only in this menu.
- The menu's open (`0x114cc9e0`, slot 10) adds six list rows with ids 0-5:
  LAN name, tutorial tips, PunkBuster server, PunkBuster client, uPnP and
  custom head port. Each row stores its id at row +0x24, and the activation
  handler switches on that id. The refresh fills rows by position.
- The server row's handler is the only path to the PunkBuster server enable
  routine. `0x11594980` is the game's own routine that turns the server off;
  it does nothing when PunkBuster is not loaded.
- The client row's handler toggles the client with `0x11594960` (on) and
  `0x115949a0` (off, the same shape as the server's). The on routine has two
  other callers: the confirmation after turning the server on, and the server
  browser's join prompt, which joins either way.

## Behavior

- The open calls that add row ids 2 and 3 are skipped, so the menu has four
  rows laid out by the list itself.
- During the menu's refresh, positions for its list are shifted past the
  removed rows and their text and lock calls are dropped.
- Each time the menu opens, the original server and client off routines run.
- The client on routine does nothing.
- The adapter reports `punkBusterRemovedVerified`; the launcher requires it.

## Reading the client code

The installed `R6Vegas2_Game.exe` `.text` is encrypted by the Steam stub as a
rolling XOR (`plain = cipher ^ key; key = cipher`, per dword, starting key
`0x6884022f` from the stub header, over VA `0x10901000`, size `0x1384000`).
Analysis used a private decrypted copy outside Git; the installed game is not
modified. The unprotected `RainbowSixVegas2_SADS.exe` has the same code at
addresses `0x280` lower in this range.

## Not verified

No retail launch was run: there is no established startup-safe private-audio
route for an automated launch. The byte signatures match the decrypted client
image, and the adapter fails closed (`codeMismatch`) if they do not match at
runtime.
