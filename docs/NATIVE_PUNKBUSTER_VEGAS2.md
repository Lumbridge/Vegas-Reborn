# Vegas 2 PunkBuster settings

PunkBuster's service no longer exists. The Vegas 2 adapter removes the
"ENABLE PUNKBUSTER SERVER" and "ENABLE PUNKBUSTER CLIENT" rows, the
PunkBuster match filter and the server browser's PunkBuster sort button, and
keeps PunkBuster off.
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

## Match filter

- `R6MatchFilterPunkBuster` (vtable `0x11f93f8c`, id `0x29`) is one of the
  filters manager's options. The shown filters are built by `0x11ab8940`,
  which skips an option whose `+0x10` flag is set; the game sets that flag
  itself to hide filters that do not apply.
- The option's value maps through `0x11f92b8c` to -1 (Any), 0 or 1. Its own
  load (`0x11ac3a10`) reads the saved value from the profile; its save
  (`0x11ac39e0`) writes it back.
- Before each build the adapter sets the PunkBuster option's `+0x10` flag.
  Its load sets the value to Any and saves that with the original save
  routine.

## Sort button

- The server browser (`R6MagmaSelectMatch`, vtable `0x11ef0a5c`) labels its
  `B_Link07` button with `R6Menus.int [Punkbuster] PunkBusterSortBtn`, read
  through `0x121bcf0c` only by the menu's refresh. Clicking `B_Link07` sorts
  the list by the PunkBuster filter id `0x29` (`0x114c3667`).
- The menu's bind (slot 5, `0x114c3b90`) looks up the elements named in the
  table at `0x121bce0c` in order; `B_Link07` (entry 16) lands at menu `+0xa0`.
- Its show method (slot 19, `0x114c6020`) runs each time the menu is shown and
  hides two unused elements by clearing bit 0 of element `+0x18`.
- After the show method, the adapter hides `B_Link07` the same way. The
  PunkBuster column in the match list shows what each server reports and is
  left as is.

The adapter reports `punkBusterRemovedVerified`; the launcher requires it.

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
