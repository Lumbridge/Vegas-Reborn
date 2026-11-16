# Claimed name in original Internet sessions

This source change extends the companion's existing claimed-name adapter to
four recovered original PC display-name call sites. It is based on published
0.4.6 source commit `d090bae`; the application version remains 0.4.6 until the
release owner selects the next coordinated release. The changed client module
uses the fresh identity **native10 / `VegasCompanion-10.dll`**. Dedicated4 is
unchanged.

The original Internet host/member presentation paths can obtain the native
account alias even though the existing menu adapter displays the claimed name.
The new adapter substitutes the companion's authenticated claim at those
display-string construction sites and the recovered travel-name site. It uses
the original allocating string constructor or original getter/assignment path.
It does not modify `AgUserInformation.username`, its hashed profile identifier,
the login proof, a remote player's identity or saved character data.

`native_claimed_session_name.h` is included after `claimed_name.h`. Installation
runs immediately after the existing claimed-name setup for the client only.
All four call sites and the original string-constructor entry participate in
the existing executable-page startup preflight. The adapter validates the
pinned executable identity, exact original calls, surrounding instructions and
constructor ABI before replacing its scoped calls. It performs write/readback
checks and rolls back only its own changes if installation fails.

The startup receipt adds `claimedSessionNameVerified` and
`claimedSessionNameStatus`. When the account has a claimed name, launch requires
both the existing claimed-name verification and this additional verification.
Accounts without a claim retain the existing no-claim handling. The receipt
records guarded installation; it does not prove a rendered match name.

Reviewed source identity supplied for this integration:
`9f2a8aa73865183118f28b51bef2ea8ffc32ebcc6cf3797b365da8295a9199ab`.
Independent native-source audit is a separate acceptance step. No native build,
test suite, game launch, package, active installation/profile edit, commit or
publication was performed as part of this source-only integration.

Character/appearance selection is unchanged. In particular, this patch makes
no assumption about the original Coop Appearance option or its Logan model.
