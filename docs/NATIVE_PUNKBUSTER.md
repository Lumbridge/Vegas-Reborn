# Vegas 1 PunkBuster server setting

PunkBuster's service no longer exists. The Vegas 1 adapter hides the
"ENABLE PUNKBUSTER SERVER" checkbox in Create Match and keeps the PunkBuster
server off. Implementation: `tools/companion/native_punkbuster.h`. Vegas 2 is
covered separately in `NATIVE_PUNKBUSTER_VEGAS2.md`.

## Original evidence (client RVAs, base 0x10900000)

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
- The other "PUNKBUSTER" text in Vegas 1 (`PunkBusterLabel`) is a server
  browser match filter (Any/Yes/No), not a setting that enables the server, and
  is left as it is.

## Reading the client code

`R6Vegas_Game.exe` `.text` is encrypted by the same Steam stub as Vegas 2: a
rolling XOR per dword with starting key `0x4d2b2217` from the stub header, over
VA `0x10901000`, size `0x159f000`. Analysis used a private decrypted copy
outside Git; the installed game is not modified.

## Not verified

No retail launch was run: there is no established startup-safe private-audio
route for an automated launch. The byte signatures match the decrypted client
image, and the adapter reports `nativeSignatureMismatch` if they do not match
at runtime. The launcher requires `punkBusterRemovedVerified`.
