# Vegas 2 host network rate

The Vegas 2 port of `NATIVE_HOST_NETWORK_RATE.md`. Grounded in the Steam
`R6Vegas2_Game.exe` (SHA-256 `e8ab9430…`, image base `0x10900000`, timestamp
`0x484efa91`). Its `.text` is encrypted on disk; analysis used a private decrypted copy
outside Git (see `NATIVE_PUNKBUSTER_VEGAS2.md`). Implementation:
`tools/companion/native_host_network_rate_vegas2.h`. The cause is established from the
code; the fix has not yet been tried in an online match.

## Cause (same as Vegas 1)

- `UGameEngine::GetMaxTickRate` (`0x113d1b70`, `UGameEngine` vtable `0x11ed4d64` + `0xdc`)
  returns `Clamp(NetDriver->NetServerMaxTickRate, 10, 120)` for a dedicated server and the
  net speed for a client. A listen-server host (no `ServerConnection`) gets 0: no limit.
- `UNetDriver::TickFlush` (`0x112fb880`; `UNetDriver` vtable `0x11e3363c` + `0xe0` and
  `UTcpNetDriver` vtable `0x11e3373c` + `0xe0`, identified by RTTI) ticks
  `ServerConnection` and each of `ClientConnections` through connection vtable `+0xf0`.
- `UNetConnection::Tick` (`0x11301420`, `UTcpipConnection` vtable `0x11e33974` + `0xf0`)
  ends with `if (TimeSensitive || Driver->Time - LastSendTime > KeepAliveTime) FlushNet()`.
- `UTcpipConnection` sets a 32-byte packet overhead (`+0xa4`, `0x10f99562`), which
  `FlushNet` (`0x11301cf0`) charges per packet. `PCKellerEngine.ini` sets
  `ConfiguredInternetSpeed=10000` and `NetServerMaxTickRate=30`.

## Vegas 2 layout

The `UNetDriver` field offsets are 4 lower than in Vegas 1. The property registration at
`0x112fc010` lists `KeepAliveTime` at `+0x60` and `NetServerMaxTickRate` at `+0x78`. The
code above reads `ClientConnections` `+0x34`, count `+0x38`, `ServerConnection` `+0x40`,
`Time` (double) `+0x4c`. The connection's `Driver` is `+0x48`, `LastSendTime` `+0xe4` and
`TimeSensitive` `+0x11c`. `FlushNet` is connection vtable `+0xec`.

Vegas 2's `TickFlush` first counts down a grace counter (`0x124c1700`). `0x10acf46a`
sets it to 30, and the connection tick skips its timeout check while it is not 0
(`0x113018f0`).

## Fix (host only)

The `UTcpNetDriver` `TickFlush` slot is replaced. When the driver has client connections
and no server connection, `TickFlush` runs at most at the driver's `NetServerMaxTickRate`
with the original 10-120 clamp, using Vegas 1's tested cadence (`NativeHostNetworkRate::Due`).
On a skipped tick the grace counter is counted down as the original does on every tick.
Everything else, including clients, runs the original. Install checks the image identity,
both driver vtable slots, the full `TickFlush` code (with the counter address relocated),
the connection slot and flush sequence, and the `GetMaxTickRate` slot and clamp. A mismatch
keeps the original rate and does not block launch. The adapter reports
`hostNetworkRateVerified` and `hostNetworkRateStatus`.

## Not verified

No retail launch or online match was made. `tests/native/vegas2_network_test.cpp` covers
the cadence, the grace countdown and image rejection against a synthetic image region.
The signatures were checked against the decrypted client.
