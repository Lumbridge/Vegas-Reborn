"""Normal hosting preconditions/ownership; no retail process is launched."""
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from vegas_reborn import dedicated


class DedicatedPreconditionTests(unittest.TestCase):
    def test_owned_copy_cannot_overlap_original_or_escape_workspace(self):
        source = ROOT / "local/dedicated-precondition-source"
        for owned in (source, source / "nested", ROOT / "src"):
            with self.subTest(owned=owned), self.assertRaisesRegex(dedicated.DedicatedError, "dedicated_copy_path_invalid"):
                dedicated._paths(source, owned)
        with self.assertRaisesRegex(dedicated.DedicatedError, "dedicated_copy_path_invalid"):
            dedicated._paths(source / "nested", source)

    def test_missing_or_nonlocal_session_rejected_before_preparation(self):
        with patch.object(dedicated, "_prepare") as prepare:
            for session in (ROOT / "AGENTS.md", ROOT / "local/nonexistent-dedicated-session.json"):
                with self.subTest(session=session), self.assertRaisesRegex(dedicated.DedicatedError, "private_session_must_be_local"):
                    dedicated.run_dedicated(session)
            prepare.assert_not_called()

    def test_research_time_limit_explicitly_rejects_unbounded_or_invalid_values(self):
        for value in (None, True, 0, 1201, 4.5):
            with self.subTest(value=value), self.assertRaisesRegex(dedicated.DedicatedError, "dedicated_research_duration_outside_bounds"):
                dedicated.run_bounded(seconds=value)

    def test_cancelled_start_never_mints_or_creates_native_process(self):
        event = threading.Event()
        event.set()
        with patch.object(dedicated, "CredentialIPC") as ipc, patch.object(dedicated.ctypes, "WinDLL") as win:
            with self.assertRaisesRegex(dedicated.DedicatedError, "dedicated_start_cancelled"):
                dedicated._run(ROOT / "local/dedicated-reference/game", source=dedicated.DEFAULT_RETAIL,
                    owned=ROOT / "local/dedicated-reference", stop_event=event)
            ipc.assert_not_called()
            win.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "Original hosting uses Windows named ownership mutex")
    def test_second_host_refused_and_owned_lock_released_after_failure(self):
        (ROOT / "local").mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / "local", prefix="dedicated-lock-") as directory:
            with self.assertRaisesRegex(RuntimeError, "fixture_failure"):
                with dedicated._exclusive_host(Path(directory)):
                    with self.assertRaisesRegex(dedicated.DedicatedError, "dedicated_host_already_running"):
                        with dedicated._exclusive_host(Path(directory)):
                            self.fail("Second host acquired an owned running host's lock")
                    raise RuntimeError("fixture_failure")
            with dedicated._exclusive_host(Path(directory)):
                pass  # The failed owner released its exact lock.


if __name__ == "__main__":
    unittest.main()
