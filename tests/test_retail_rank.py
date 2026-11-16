"""Original-source rank boundaries; no copyrighted game data committed."""
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.retail_profile import patch_xp
from vegas_reborn.retail_rank import DEFAULT_RETAIL, RankSourceError, load_rank_table, parse_rank_table, read_rank


@unittest.skipUnless((DEFAULT_RETAIL / "KellerGame/pec/PECInfoFile.bin").is_file(), "Original reference installation unavailable")
class OriginalRankTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.carrier = (DEFAULT_RETAIL / "KellerGame/pec/PECInfoFile.bin").read_bytes()
        cls.menus = (DEFAULT_RETAIL / "KellerGame/Localization/INT/R6Menus.int").read_bytes()
        cls.table = load_rank_table()

    def test_actual_original_threshold_boundaries_and_labels(self):
        self.assertEqual(len(self.table.records), 102)
        cases = [(0, 0, 1, 0), (60, 0, 1, 0), (499, 0, 1, 0),
                 (500, 1, 1, 1), (2999, 5, 1, 5), (3000, 6, 2, 0)]
        for xp, index, ordinal, pips in cases:
            with self.subTest(xp=xp):
                rank = self.table.rank_for_experience(xp)
                self.assertEqual((rank.index, rank.ordinal, rank.subrank), (index, ordinal, pips))
        self.assertEqual(self.table.rank_for_experience(60).label, "PRIVATE SECOND CLASS")
        self.assertEqual(self.table.rank_for_experience(3000).label, "PRIVATE FIRST CLASS")

    def test_retained_actual_native_probe_1100(self):
        rank = self.table.rank_for_experience(1100)
        self.assertEqual(rank.index, 2)
        self.assertEqual(rank.next_threshold, 1500)
        self.assertAlmostEqual(rank.progress_to_next, .2)

    def test_maximum_is_bounded_without_next_record_access(self):
        for xp in [850000, 2000000, 0xffffffff]:
            rank = self.table.rank_for_experience(xp)
            self.assertEqual((rank.index, rank.ordinal, rank.subrank, rank.label), (101, 17, 5, "ELITE"))
            self.assertIsNone(rank.next_threshold)
            self.assertIsNone(rank.progress_to_next)

    def test_modified_or_truncated_sources_fail_closed(self):
        altered = bytearray(self.carrier)
        altered[108] ^= 1
        for data, menus in [(altered, self.menus), (self.carrier[:-1], self.menus),
                            (self.carrier, self.menus + b"\n")]:
            with self.subTest(length=len(data), labels=len(menus)):
                with self.assertRaises(RankSourceError):
                    parse_rank_table(data, menus)

    def test_xp_requires_uint32(self):
        for xp in [-1, 0x100000000, True, 1.5]:
            with self.assertRaises(ValueError):
                self.table.rank_for_experience(xp)

    def test_native_archive_crc_is_required_without_writes(self):
        # Existing private earned profile is optional. The archive remains
        # copied to a temporary directory; no installed/global profile writes.
        root = Path(__file__).resolve().parents[1]
        source = root / "local/two-player-reference/runs/36bde089e77c420aa00f8a93904acac1/player1/profile/Ubisoft/R6Vegas/R6_PECPEC"
        if not source.is_file():
            self.skipTest("Private original saved-PEC acceptance fixture unavailable")
        original = source.read_bytes()
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "R6_PECPEC"
            target.write_bytes(patch_xp(original, 60))
            before = target.read_bytes()
            self.assertEqual(read_rank(target, table=self.table).experience, 60)
            self.assertEqual(target.read_bytes(), before)
            bad = bytearray(before);bad[17] ^= 1;target.write_bytes(bad)
            with self.assertRaises(ValueError):
                read_rank(target, table=self.table)
        self.assertEqual(source.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
