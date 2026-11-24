# Vegas 2 PunkBuster server setting

PunkBuster's service no longer exists. The Vegas 2 adapter removes the
"ENABLE PUNKBUSTER SERVER" row and keeps the PunkBuster server off.
Implementation: `tools/companion/native_punkbuster_vegas2.h`.

## Original evidence

- `R6Menus.int` `[Punkbuster] EnablePunkbusterServerLabel` is read through the
  data pointer `0x121bd264`. Its only code reference is the native settings
  menu refresh (`0x114ccd70`, vtable `0x11ef21e4` slot 8). No other menu uses
  the label; the server row exists only in this menu.
- The menu's open (`0x114cc9e0`, slot 10) adds six list rows with ids 0-5:
  LAN name, tutorial tips, PunkBuster server, PunkBuster client, uPnP and
  custom head port. Each row stores its id at row +0x24, and the activation
  handler switches on that id. The refresh fills rows by position.
- The server row's handler is the only path to the PunkBuster server enable
  routine. `0x11594980` is the game's own routine that turns the server off;
  it does nothing when PunkBuster is not loaded.

## Behavior

- The open call that adds row id 2 is skipped, so the menu has five rows laid
  out by the list itself.
- During the menu's refresh, positions for its list are shifted past the
  removed row and the server row's text call is dropped.
- Each time the menu opens, the original off routine runs.
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
