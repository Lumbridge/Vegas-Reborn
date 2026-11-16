"""Read earned rank from the original PEC archive and shipped rank carrier.

Read only. Thresholds are decoded from PECInfoFile.bin, never reconstructed or
hardcoded. Original server loader10e6ac40 -> deserializer10e691a0 checks float
version1.2, then reads defaults10e6c130 and rank array10e6c5c0; record reader
10e6c370 copies threshold/id/ordinal in that order. Original client lookup
10e6d620 selects the last threshold <= unsigned XP. Ordinal getter10e6d6c0,
subrank counter(server10e6c4b0), and PC service-record UI109c81a0 establish the
label enum and five-pip display. This does not change game unlock policy.
"""
from bisect import bisect_right
import configparser
from dataclasses import dataclass
import hashlib
from pathlib import Path
import struct

from .retail_profile import MAX_BYTES, read_xp

DEFAULT_RETAIL = Path("E:/SteamLibrary/steamapps/common/Rainbow Six Vegas")
PEC_INFO_SHA256 = "ce019057071ea0e7ce8dc61b6d2f0710fd0a55ce0f89ea61b9d968bd1fba6194"
ENGLISH_MENUS_SHA256 = "73fe8e0a89ecb635ff752a66ae6b6570cc1d107d1438c49f4e86244374cbc892"
PEC_INFO_BYTES = 13652

# Exact ordinal switch in original PC UI109c81a0, not inferred military order.
RANK_LABEL_KEYS = (
    "RankRecruit", "RankPrivate2ndclass", "RankPrivate1stclass", "RankSpecialist",
    "RankCorporal", "RankSergeant", "RankStaffSergeant", "RankSergeant1stClass",
    "RankMasterSergeant", "Rank1stSergeant", "RankSergeantMajor", "Rank2ndLieutenant",
    "Rank1stLieutenant", "RankCaptain", "RankMajor", "RankLieutenantColonel",
    "RankColonel", "RankElite",
)


class RankSourceError(ValueError):
    """Unsupported source identity or a malformed original-data contract."""


@dataclass(frozen=True)
class RankRecord:
    threshold: int
    record_id: int
    ordinal: int


@dataclass(frozen=True)
class RetailRank:
    experience: int
    index: int
    ordinal: int
    record_id: int
    subrank: int
    label_key: str
    label: str
    threshold: int
    next_threshold: int | None
    experience_to_next: int | None
    progress_to_next: float | None


@dataclass(frozen=True)
class RetailRankTable:
    records: tuple[RankRecord, ...]
    labels: tuple[str, ...]
    source_sha256: str = PEC_INFO_SHA256
    labels_sha256: str = ENGLISH_MENUS_SHA256

    def rank_for_experience(self, experience):
        if type(experience) is not int or not 0 <= experience <= 0xffffffff:
            raise ValueError("experience_must_be_uint32")
        index = bisect_right(tuple(row.threshold for row in self.records), experience) - 1
        if index < 0:
            raise RankSourceError("rank_table_has_no_initial_threshold")
        row = self.records[index]
        subrank = sum(prior.ordinal == row.ordinal for prior in self.records[:index])
        following = self.records[index + 1].threshold if index + 1 < len(self.records) else None
        # Native progress reads the next record. At maximum use an explicit
        # absent-next value rather than copy the original out-of-range access.
        progress = None if following is None else (experience - row.threshold) / (following - row.threshold)
        return RetailRank(experience, index, row.ordinal, row.record_id, subrank,
            RANK_LABEL_KEYS[row.ordinal], self.labels[row.ordinal], row.threshold,
            following, None if following is None else following - experience, progress)


def parse_rank_table(carrier, english_menus):
    """Accept only the inspected original binary and English localization."""
    if len(carrier) != PEC_INFO_BYTES or hashlib.sha256(carrier).hexdigest() != PEC_INFO_SHA256:
        raise RankSourceError("unsupported_original_pec_info")
    if carrier[:4] != struct.pack("<f", 1.2):
        raise RankSourceError("unsupported_original_pec_info_version")
    default_count = struct.unpack_from("<I", carrier, 4)[0]
    rank_count_offset = 8 + default_count * 8
    if default_count != 12 or rank_count_offset != 104:
        raise RankSourceError("unsupported_original_pec_defaults")
    count = struct.unpack_from("<I", carrier, rank_count_offset)[0]
    if count != 102:
        raise RankSourceError("unsupported_original_rank_count")
    records = tuple(RankRecord(*struct.unpack_from("<III", carrier, 108 + 12 * i)) for i in range(count))
    if (records[0].threshold != 0 or any(not 0 <= row.ordinal < len(RANK_LABEL_KEYS) for row in records)
            or any(a.threshold >= b.threshold for a, b in zip(records, records[1:]))):
        raise RankSourceError("invalid_original_rank_records")
    if hashlib.sha256(english_menus).hexdigest() != ENGLISH_MENUS_SHA256:
        raise RankSourceError("unsupported_original_english_labels")
    menus = configparser.RawConfigParser(strict=False, interpolation=None)
    menus.optionxform = str
    try:
        menus.read_string(english_menus.decode("latin1"))
        labels = tuple(menus.get("Pec", key).strip() for key in RANK_LABEL_KEYS)
    except configparser.Error:
        raise RankSourceError("invalid_original_english_labels") from None
    if any(not label for label in labels):
        raise RankSourceError("missing_original_rank_label")
    return RetailRankTable(records, labels)


def load_rank_table(retail_root=DEFAULT_RETAIL):
    """Load the supported installed or isolated reference; writes nothing."""
    root = Path(retail_root)
    with (root / "KellerGame/pec/PECInfoFile.bin").open("rb") as stream:
        carrier = stream.read(PEC_INFO_BYTES + 1)
    with (root / "KellerGame/Localization/INT/R6Menus.int").open("rb") as stream:
        menus = stream.read(1024 * 1024 + 1)
    return parse_rank_table(carrier, menus)


def read_rank(pec_file, *, retail_root=DEFAULT_RETAIL, table=None):
    """Validate original PEC CRC/version, then derive its earned raw-XP rank."""
    with Path(pec_file).open("rb") as stream:
        experience = read_xp(stream.read(MAX_BYTES + 1))
    return (table if table is not None else load_rank_table(retail_root)).rank_for_experience(experience)
