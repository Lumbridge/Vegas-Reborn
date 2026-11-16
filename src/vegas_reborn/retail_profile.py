"""Versioned native PEC raw-XP adapter; leaves unrelated bytes untouched.

Serializer 0x10e66660, XP getter/setter 0x10e66320/0x10e66310,
checksum 0x10dedb60 and validator 0x10d026b0 establish this format.
This does not establish how every native rank/unlock consumer uses raw XP.
"""
import struct

SEED = 0x0645adef
XP_OFFSET = 17
MAX_BYTES = 1024 * 1024


def _table():
    result = []
    for value in range(256):
        crc = value << 24
        for _ in range(8):
            crc = ((crc << 1) ^ 0x04c11db7 if crc & 0x80000000 else crc << 1) & 0xffffffff
        result.append(crc)
    return result


TABLE = _table()


def checksum(data, seed=SEED):
    crc = (~seed) & 0xffffffff
    for value in data:
        crc = ((crc << 8) ^ TABLE[value ^ (crc >> 24)]) & 0xffffffff
    return (~crc) & 0xffffffff


def read_xp(data):
    if not 21 <= len(data) <= MAX_BYTES:
        raise ValueError("PEC length outside supported bounds")
    stored, outer, size = struct.unpack_from("<III", data)
    if outer != 6 or size != len(data) - 12:
        raise ValueError("unsupported outer PEC archive")
    if struct.unpack_from("<I", data, 12)[0] != 0x19:
        raise ValueError("unsupported raw PEC version")
    actual = checksum(data[4:])
    if actual == SEED or stored == SEED or actual != stored:
        raise ValueError("invalid native PEC checksum")
    return struct.unpack_from("<I", data, XP_OFFSET)[0]


def patch_xp(data, experience):
    read_xp(data)
    if type(experience) is not int or not 0 <= experience <= 0xffffffff:
        raise ValueError("raw XP must fit uint32")
    result = bytearray(data)
    struct.pack_into("<I", result, XP_OFFSET, experience)
    struct.pack_into("<I", result, 0, checksum(result[4:]))
    read_xp(result)
    return bytes(result)
