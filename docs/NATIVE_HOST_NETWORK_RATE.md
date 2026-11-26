# Vegas 1 host network rate

Grounded in the Steam `R6Vegas_Game.exe` (SHA-256 `667f659b…`, image base `0x10900000`,
timestamp `0x47106421`) and `R6VegasServer.exe`, which is built from the same engine code.
The client's `.text` is encrypted on disk but its `.rdata` is not, so the vtable slots below
were read from the client and the code bytes from the plaintext server image. The adapter
checks the same code bytes in the running client before it changes anything. The cause is
established from the code; the fix has not yet been tried in an online match.

## Symptoms

Players who join a match hosted from Create Match see choppy movement, doors that open late or
not at all, automatic fire sounds that keep looping, and shots that land beside a terrorist on
the host's screen. The host's own game is smooth.

## Cause

- `UGameEngine::GetMaxTickRate` (`0x10d92110`, `UGameEngine` vtable slot `0x11f67548`)
  limits a dedicated server to `Clamp(NetDriver->NetServerMaxTickRate (+0x7c), 10, 120)`
  (30 in the shipped ini) and a joining client to its net speed divided by `MoveRepSize`, at
  most 90. A listen-server host gets 0, which means no limit: it ticks at its full frame rate.
- `UNetDriver::TickFlush` (`0x10cbe3c0`, `UTcpNetDriver` vtable slot `0x11f7cd64`, also the
  `UNetDriver` slot `0x11f4c77c`) ticks `ServerConnection` (`+0x44`) and then each of
  `ClientConnections` (`+0x38`, count `+0x3c`) through connection vtable `+0xf4`.
- `UNetConnection::Tick` (`0x10cc5620`, `UIpConnection` vtable slot `0x11f7ce7c`) ends with
  `if (TimeSensitive || Driver->Time - LastSendTime > Driver->KeepAliveTime) FlushNet()`.
  Any tick that sent a bunch or owes an ack sends a packet.
- `FlushNet` charges each packet's `PacketOverhead` (32 bytes, set by `UIpConnection`) plus
  its payload against `QueuedBytes`, which the connection's `CurrentNetSpeed` drains. An
  internet connection uses `ConfiguredInternetSpeed`, 10000 bytes per second.
- A host at 300 frames per second sends up to 300 packets per second to each client. The
  overhead alone is 300 × 32 = 9600 of the client's 10000 bytes per second. The connection is
  saturated, so actor replication is deferred: positions arrive late (choppy movement and hits
  judged against old positions), door state changes are delayed, and unreliable messages such
  as the fire Ends cue are dropped (see `NATIVE_FIRE_SOUND_LOOP.md`, which handles the sound
  on the client but cannot restore the lost bandwidth).

A dedicated server at 30 ticks per second uses 30 × 32 = 960 bytes per second of overhead per
client and batches each tick's replication into one fuller packet.

## Fix (`native_host_network_rate.h`, host only)

- The `UTcpNetDriver` vtable slot for `TickFlush` is replaced. When the driver has client
  connections and no server connection (a listen-server host), `TickFlush` runs at most at
  the driver's own `NetServerMaxTickRate` with the original 10-120 clamp, timed from the
  driver's `Time` (`+0x50`). Otherwise the original runs on every tick.
- The first tick of a driver, a new driver and a clock that went back (a new level) flush at
  once. A long frame flushes once, not a burst of catch-up flushes. A host at or below the
  rate flushes on every frame, as in the original.
- Replication between flushes is batched by the original `SendRawBunch`, which flushes on its
  own when a packet is full. Rendering and the host's own game are not limited. Joining
  clients are unchanged, so only the host needs the updated companion.
- Install checks the image identity, the three vtable slots, the full `TickFlush` code, the
  flush sequence in `UNetConnection::Tick`, and the dedicated-server clamp in
  `GetMaxTickRate`. Any mismatch leaves the original rate in place. This feature does not
  block launch; the adapter reports `hostNetworkRateVerified` and `hostNetworkRateStatus`.
  It can be turned off with the `host-network-rate` id in `native-features.txt`.

## Not verified

- No retail launch was made: the native signatures have only been checked against the server
  image, and no online match has been played with the fix.
- `tests/native/host_network_rate_test.cpp` covers the flush cadence and image rejection.
  Synthetic tests do not establish retail gameplay interoperability.
