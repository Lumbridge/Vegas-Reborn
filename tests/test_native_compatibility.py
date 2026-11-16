"""Classify the actual startup report without weakening code/name/display guards."""
import unittest
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.companion import CompanionError, validate_compatibility


class NativeCompatibilityTests(unittest.TestCase):
    def test_reported_hook_failure_is_not_a_code_mismatch(self):
        report = {"ready":False,"signatures":16383,"unlockHookStatus":7,"taskHookStatus":-1,"prefillHookStatus":-1}
        with self.assertRaisesRegex(CompanionError,"executable code"):
            validate_compatibility(report, display_mode="Windowed")

    def test_actual_mismatch_and_other_setup_failure_remain_blocked(self):
        for report, error in (({"ready":False,"signatures":8191},"does not match"),
                              ({"ready":False,"signatures":16383,"executablePagesReady":True},"code matched")):
            with self.assertRaisesRegex(CompanionError,error):
                validate_compatibility(report)

    def test_success_still_requires_name_and_borderless_verification(self):
        report={"ready":True,"signatures":16383}
        validate_compatibility(report, display_mode="Windowed")
        with self.assertRaisesRegex(CompanionError,"player-name"):
            validate_compatibility(report, claimed_name="Tester")
        with self.assertRaisesRegex(CompanionError,"Borderless"):
            validate_compatibility(report, display_mode="Borderless")
