# Multiplayer PEC appearance default

This integration starts from the published 0.4.8 source at `d8c5c4b` and adds
the client appearance policy as **native11 / `VegasCompanion-11.dll`**.
Dedicated4 and the application version are unchanged during source work.
The prior claimed-name, startup/display, rank, progression and updater behavior
remain in place.

The replacement policy defaults cooperative multiplayer appearance to the
saved custom PEC character once the original PEC readiness checks succeed.
The original adversarial appearance path already uses PEC and is preserved.
Single-player appearance, solo-hunt's separate appearance field, character
archives, XP, account identity and other players' options are not changed.
The adapter does not force a model on every spawn.

Players can override the default through the original Coop Appearance menu.
The adapter records explicit native dropdown choices, including Logan, and
custom-creation completion. It preserves identifiable legacy presets when
loading original settings. Historical explicit Logan and the original default
Logan use identical stored bytes, so that old intent cannot be recovered;
future explicit Logan choices have their own recorded provenance.

## Account boundary and startup

`NativePecDefaults::Install(base, Output)` runs for the client only after the
existing native setup. `Output` is the companion's persistent authenticated
account directory, not a per-launch directory. The policy stores at most two
small versioned appearance-intent files there. They remain outside the signed
updater's managed-file set along with the existing account data.

The startup receipt exposes `nativePecDefaultsVerified` and
`nativePecDefaultsStatus`; the client launcher requires verification before
allowing normal travel. All six hooks and four native PEC readiness callees
participate in the existing executable-page preflight. The build source-hash
record includes the adapter header.

The adapter preserves original getter, restore, settings-application, menu and
completion bodies. Its guarded policy runs on their calling threads and uses
the original PEC readiness sequence. The final canonical header passed the
independent native ABI, thread-state and persistence-edge source audit with
SHA-256 `6d89ee686bbd0412100664fa2e54fca51f3e5a62ab6f97e5617c81eac7454f4f`.
That audit remains separate from original-client runtime acceptance.

Verification flags describe guarded installation, not proof of a rendered PEC
character or a successful original save. This port does not add a companion
appearance screen, require a manual Custom Character selection or launch the
game to modify an active profile.

No tests, games, native build, package, active installation/profile edits,
commits or publication were performed as part of this source integration.
