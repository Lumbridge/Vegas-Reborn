# Vegas 1 PunkBuster settings

PunkBuster's service no longer exists. The Vegas 1 adapter hides the
"ENABLE PUNKBUSTER SERVER" checkbox in Create Match and the PunkBuster client
checkbox in the match browser and Quick Match, removes the PunkBuster match
filter and the match list's PunkBuster column, and keeps PunkBuster off.
Implementation: `tools/companion/native_punkbuster.h`. Vegas 2 is covered
separately in `NATIVE_PUNKBUSTER_VEGAS2.md`.

## Original evidence (client RVAs, base 0x10900000)

### Server

- `R6Menus.int [ServerOptions] PreGamePunkbuster` and `InGamePunkbuster` are
  the checkbox text. The Create Match menu (`MP_RSMenus.mgb`) binds widgets by
  name at 0xae780; `CB_PBServer` lands at menu +0x2f8 and its text at +0x314.
  No other menu binds `CB_PBServer`.
- Three setup methods fill the menu: 0xb1f10 and 0xb21c0 show the checkbox,
  0xb2460 hides it and `B_Link01` (clears +0x18 bit 0 and calls widget slot 9
  with 0). The adapter hides the checkbox the same way after each of them.
- Every PunkBuster server start calls 0x1045090 (PunkBuster command 0x75):
  the checkbox click and three automatic starts gated on the saved
  `m_bPunkBusterSv` flag (0x65376, 0xaf186, 0x4e77a2). The adapter makes that
  routine do nothing, so the server stays off and the menu's save writes
  `m_bPunkBusterSv=False`.

### Client

- The match browser (two menu vtables sharing init 0xa6c80) binds
  `CB_PBClientCustomMatch` to menu +0x4c (0xa6c8f, last of its table). Quick
  Match binds `CB_PBClientQuickMatch` to +0x108 (0xb2a66, last of its table).
  Both are visible from the menu file; no code shows them.
- The client routines use the PunkBuster instance at 0x125a9f10 +4: 0x1046a20
  queries (command 0x71), 0x1046ae0 turns the client on (0x75) and 0x1046b40
  turns it off (0x76). Turning it on is called by both checkboxes, by both
  menus' show methods (0xa76c0, 0xb5850) when a profile flag is set, and by
  PunkBuster's own join path (0x10487a3). A click on a ticked checkbox
  queries and then turns the client off; the menus save the query result as
  `m_bPunkBusterCl`.
- After each show method, the adapter hides that menu's checkbox the same way
  as the server checkbox and, as unticking it would, turns the client off if
  the query says it is on. The on routine does nothing.

### Match filter

- The filters manager init (0x952960, thiscall with a mode) creates eight
  filter options; the PunkBuster option (`[ServerOptions] PunkBusterLabel`,
  vtable 0x12093bf0) is element 6. In mode 1 it then builds the list of
  filters shown on the page (array +0x14, count +0x18), appending element 6
  last (0x952efd).
- The option's value maps through 0x120935ec to -1 (Any), 0 or 1. Its own
  load (vtable slot 14, 0x951760) reads the saved value from the profile; its
  save (slot 13, 0x951730) writes it back. The search reads the option.
- After init, the adapter drops the trailing PunkBuster entry from the shown
  filters. Its load sets the value to Any and saves that with the original
  save routine.

### Match list column

- The match browser init requires the list columns named in the table at
  0x122e7514 (0xa6cfa); the last, `I_PunkBuster`, is column 8. It loads the
  `ic_PunkBuster` image from `MP_RSMenus.mgb` into menu +0xa0.
- The row fill sets column 8 to that image when the match reports PunkBuster,
  or to none (0xa880a), with the list's set-cell-image routine 0xcf2540.
- The adapter passes no image from that call, so the column stays empty. The
  column's space stays; the other columns do not move. The list's column
  headers have no PunkBuster label in code or `R6Menus.int`.

### Left as is

- The PunkBuster row in a match's details shows what a server reports; it is
  not a setting.
- Vegas 1 has no PunkBuster sort button: its match browser's `B_Link03` to
  `B_Link07` switch list tabs (0xa8980-0xa8ab6), and `R6Menus.int` has no
  `PunkBusterSortBtn` key.

## Reading the client code

`R6Vegas_Game.exe` `.text` is encrypted by the same Steam stub as Vegas 2: a
rolling XOR per dword with starting key `0x4d2b2217` from the stub header, over
VA `0x10901000`, size `0x159f000`. Analysis used a private decrypted copy
outside Git; the installed game is not modified.

## Not verified

No retail launch was run: there is no established startup-safe private-audio
route for an automated launch. The byte signatures and widget names match the
decrypted client image, and the adapter reports `nativeSignatureMismatch` if
they do not match at runtime. The launcher requires `punkBusterRemovedVerified`.
