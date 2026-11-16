"""Release ownership across simultaneous linked-worktree builds."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
import release_coordination as releases


class ReleaseCoordinationTests(unittest.TestCase):
    def test_concurrent_worktrees_cannot_reserve_the_same_version(self):
        with tempfile.TemporaryDirectory() as directory:
            common = Path(directory)
            barrier = threading.Barrier(2)
            def build(name):
                barrier.wait()
                try:
                    releases.reserve_version(common, common / name, "0.4.3", name)
                    return True
                except RuntimeError:
                    return False
            with ThreadPoolExecutor(2) as pool:
                results = list(pool.map(build, ("first", "second")))
            self.assertEqual(sorted(results), [False, True])

    def test_owner_can_retry_but_new_commit_or_worktree_needs_new_version(self):
        with tempfile.TemporaryDirectory() as directory:
            common = Path(directory)
            owner = common / "owner"
            releases.reserve_version(common, owner, "0.4.3", "reviewed")
            releases.reserve_version(common, owner, "0.4.3", "reviewed")
            for root, commit in ((owner, "changed"), (common / "other", "reviewed")):
                with self.assertRaises(RuntimeError):
                    releases.reserve_version(common, root, "0.4.3", commit)
            releases.reserve_version(common, owner, "0.4.4", "changed")

    def test_unmerged_head_cannot_build_or_publish(self):
        with patch.object(releases.subprocess, "run"), patch.object(releases.subprocess, "check_output", side_effect=["feature", "merged"]):
            with self.assertRaisesRegex(RuntimeError, "current merged"):
                releases.check_release_source(Path.cwd(), "0.4.3", "Lumbridge/Vegas-Reborn")

    def test_same_or_older_public_version_cannot_build_or_publish(self):
        for version in ("0.4.2", "0.4.1"):
            with patch.object(releases.subprocess, "run"), patch.object(releases.subprocess, "check_output", side_effect=["merged", "merged", "v0.4.2"]):
                with self.assertRaisesRegex(RuntimeError, "newer"):
                    releases.check_release_source(Path.cwd(), version, "Lumbridge/Vegas-Reborn")
