"""Repair only unbound computed-board columns in a companion-owned game copy.

The installed carrier is read only. This is an explicit client compatibility
repair: its original base statistics reader remains unchanged, while its local
metadata no longer asks that reader to dereference an absent SPYC property.
No wire column type, rating default or vendor TrueSkill algorithm is invented.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import struct
import tempfile


CODEC = "native-base-v1"
RELATIVE_CARRIER = Path("KellerGame/Content/Agora/runtime/MatchmakingData.bin")
ORIGINAL_SHA256 = "c16445075ce36036be6c1e5c37527561bd6f7f64c2286c759abc6d6c3864ebe7"
ORIGINAL_BYTES = 46170
RETAIL_EXE_SHA256 = "667f659b3c128ab14506a464a462a592e16aca906084cabfc2d890e9e4bd5acc"
DERIVED_SHA256 = "d005f2ce631007e8823b515eedb2ff23aa4097a222a5da7371b6c9182d0d2633"
MODES = ("None", "AttackAndDefend", "Survival", "Sharpshooter", "TeamSurvival",
         "TeamSharpshooter", "Retrieval", "CoopStory", "CoopHunt")
EXPECTED_BOARDS = {
    base + index: f"{mode}_{suffix}_TrueSkill"
    for base, suffix in ((0xffff0000, "Ranked"), (0xfffe0000, "Standard"))
    for index, mode in enumerate(MODES)
}
EXPECTED_COLUMNS = ((61, "Skill", 2, 0xffffffff), (62, "Games_Played", 2, 0xffffffff),
                    (63, "Mu", 2, 0xffffffff), (64, "Sigma", 2, 0xffffffff))


def _sha(data):
    return hashlib.sha256(data).hexdigest()


class _Reader:
    def __init__(self, data):
        self.data, self.offset = data, 0

    def take(self, length):
        if length < 0 or self.offset + length > len(self.data):
            raise ValueError("Truncated original definition")
        value = self.data[self.offset:self.offset + length]
        self.offset += length
        return value

    def number(self, tag, size=4):
        if self.take(1) != bytes((tag,)):
            raise ValueError("Unsupported definition tag")
        return int.from_bytes(self.take(size), "little")

    def string(self):
        if self.take(2) != b"\x0a\x07":
            raise ValueError("Unsupported definition string")
        length = int.from_bytes(self.take(4), "little")
        if not 1 <= length <= 256:
            raise ValueError("Invalid definition string length")
        value = self.take(length)
        if value[-1] or b"\0" in value[:-1]:
            raise ValueError("Invalid definition string")
        return value[:-1].decode("ascii")

    def finish(self):
        if self.offset != len(self.data):
            raise ValueError("Unexpected definition suffix")


def _unwrap(raw):
    if len(raw) < 24 or int.from_bytes(raw[16:20], "little") != len(raw) - 20:
        raise ValueError("Invalid carrier length")
    digest = raw[:16]
    body = bytes(value ^ digest[(index % 32) // 2]
                 for index, value in enumerate(raw[20:]))
    if hashlib.md5(body).digest() != digest:
        raise ValueError("Invalid carrier digest")
    return body


def derive(raw):
    """Deterministically derive bytes and a receipt from the pinned original."""
    if len(raw) != ORIGINAL_BYTES or _sha(raw) != ORIGINAL_SHA256:
        raise ValueError("Unsupported original MatchmakingData carrier")
    body = _unwrap(raw)
    count = int.from_bytes(body[:4], "little")
    if count != 500:
        raise ValueError("Unsupported original chunk count")
    output, offset, changed, untouched = [body[:4]], 4, {}, []
    board_count = property_count = 0
    for _ in range(count):
        if offset + 8 > len(body):
            raise ValueError("Truncated carrier chunk")
        tag = body[offset:offset + 4]
        size = int.from_bytes(body[offset + 4:offset + 8], "little")
        if size < 8 or offset + size > len(body):
            raise ValueError("Invalid carrier chunk size")
        chunk = body[offset:offset + size]
        payload = chunk[8:]
        if tag == b"SPYC":
            property_count += 1
        if tag == b"SLBC":
            board_count += 1
            reader = _Reader(payload)
            if reader.number(6) != 3:
                raise ValueError("Unsupported leaderboard version")
            name, identifier = reader.string(), reader.number(6)
            ranked, selector24, selector28, flag15 = (
                reader.number(11), reader.number(3, 1),
                reader.number(3, 1), reader.number(11))
            sort = reader.number(7)
            count_offset = reader.offset
            field_count = reader.number(5, 2)
            fields = tuple((reader.number(6), reader.string(), reader.number(3, 1),
                            reader.number(6)) for _ in range(field_count))
            reader.finish()
            if identifier in EXPECTED_BOARDS:
                expected_ranked = int(identifier >> 16 == 0xffff)
                if (identifier in changed or name != EXPECTED_BOARDS[identifier] or
                        (ranked, selector24, selector28, flag15, sort) !=
                        (expected_ranked, 0, 1, 0, 0) or fields != EXPECTED_COLUMNS):
                    raise ValueError("Computed leaderboard definition changed")
                # Retain every metadata byte before the original field count.
                # A typed u16 zero makes the native column initializer empty.
                repaired = payload[:count_offset] + b"\x05\x00\x00"
                output.append(tag + struct.pack("<I", len(repaired) + 8) + repaired)
                changed[identifier] = name
            else:
                if selector28 == 1 or any(field[3] == 0xffffffff for field in fields):
                    raise ValueError("Unexpected additional unbound board")
                output.append(chunk)
                untouched.append(chunk)
        else:
            output.append(chunk)
            untouched.append(chunk)
        offset += size
    if (offset != len(body) or board_count != 75 or property_count != 390 or
            set(changed) != set(EXPECTED_BOARDS) or len(untouched) != 482):
        raise ValueError("Unsupported original definition inventory")
    repaired_body = b"".join(output)
    digest = hashlib.md5(repaired_body).digest()
    encoded = bytes(value ^ digest[(index % 32) // 2]
                    for index, value in enumerate(repaired_body))
    result = digest + struct.pack("<I", len(repaired_body)) + encoded
    if _unwrap(result) != repaired_body:
        raise ValueError("Derived carrier encoding failed")
    receipt = {
        "kind": "original_pc_trueskill_carrier_repair", "statisticsCodec": CODEC,
        "sourceSha256": ORIGINAL_SHA256, "sourceBytes": len(raw),
        "derivedSha256": _sha(result), "derivedBytes": len(result),
        "chunkCount": count, "boardCount": board_count, "propertyCount": property_count,
        "modifiedBoardCount": len(changed), "removedColumnCount": len(changed) * 4,
        "modifiedBoards": [{"id": f"0x{identifier:08x}", "name": name}
                           for identifier, name in sorted(changed.items())],
        "unchangedChunkCount": len(untouched),
        "unchangedChunksSha256": _sha(b"".join(untouched)),
    }
    return result, receipt


def _plain_path(path):
    """Reject symlink/junction traversal before resolving an owned write path."""
    path = Path(path).absolute()
    for item in (path, *path.parents):
        if item.exists():
            info = item.lstat()
            if item.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
                raise ValueError("Owned game paths cannot traverse links or junctions")
    return path.resolve()


def ensure(reference_game, owned_game):
    """Install into an existing companion-owned clone; return readback receipt.

    Both arguments are game roots. Unknown destination bytes are preserved and
    rejected. An already repaired, hash-identical clone is accepted idempotently.
    """
    reference_game = Path(reference_game).resolve(strict=True)
    owned_game = _plain_path(owned_game)
    if (not owned_game.is_dir() or reference_game == owned_game or
            owned_game.is_relative_to(reference_game) or
            reference_game.is_relative_to(owned_game)):
        raise ValueError("The owned game copy must be separate from the reference")
    marker = _plain_path(owned_game / ".companion-ready.json")
    ownership = json.loads(marker.read_text(encoding="utf-8"))
    if ownership.get("version") != 1 or ownership.get("sourceSha256") != RETAIL_EXE_SHA256:
        raise ValueError("The destination is not a supported companion-owned game copy")
    executable = _plain_path(owned_game / "Binaries/R6Vegas_Game.exe")
    if _sha(executable.read_bytes()) != RETAIL_EXE_SHA256:
        raise ValueError("The owned game executable failed its identity check")
    source = reference_game / RELATIVE_CARRIER
    target = _plain_path(owned_game / RELATIVE_CARRIER)
    if not target.is_relative_to(owned_game) or not target.is_file():
        raise ValueError("The owned carrier is unavailable")
    if source.samefile(target) or target.stat().st_nlink != 1:
        raise ValueError("The owned carrier must be an independent file")
    repaired, receipt = derive(source.read_bytes())
    if receipt["derivedSha256"] != DERIVED_SHA256:
        raise ValueError("The deterministic derived carrier hash changed")
    current = target.read_bytes()
    if _sha(current) not in (ORIGINAL_SHA256, DERIVED_SHA256):
        raise ValueError("Unknown owned carrier bytes were preserved")
    receipt["changed"] = current != repaired
    if current != repaired:
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(prefix=".trueskill-", suffix=".tmp",
                                             dir=target.parent, delete=False) as stream:
                temporary = Path(stream.name)
                stream.write(repaired)
                stream.flush()
                os.fsync(stream.fileno())
            if _sha(temporary.read_bytes()) != DERIVED_SHA256:
                raise ValueError("Temporary repaired carrier failed readback")
            # Recheck ownership/path before replacing the one independent file.
            if _plain_path(target) != target or target.stat().st_nlink != 1:
                raise ValueError("Owned carrier path changed during preparation")
            if _sha(target.read_bytes()) not in (ORIGINAL_SHA256, DERIVED_SHA256):
                raise ValueError("Owned carrier changed during preparation")
            os.replace(temporary, target)
            temporary = None
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)
    readback = target.read_bytes()
    if _sha(readback) != DERIVED_SHA256 or _unwrap(readback) != _unwrap(repaired):
        raise ValueError("Owned repaired carrier failed final readback")
    if _sha(source.read_bytes()) != ORIGINAL_SHA256:
        raise ValueError("The read-only reference carrier changed")
    receipt["readbackVerified"] = True
    return receipt


def ensure_carrier(source_game_root, owned_game_root):
    """Return capability only after source validation and owned-copy readback."""
    return ensure(source_game_root, owned_game_root)["statisticsCodec"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Original carrier, or game root with --owned-game")
    parser.add_argument("--owned-game", type=Path, help="Existing companion-owned clone root")
    arguments = parser.parse_args()
    if arguments.owned_game:
        report = ensure(arguments.source, arguments.owned_game)
    else:
        _, report = derive(arguments.source.read_bytes())
    print(json.dumps(report, indent=2))
