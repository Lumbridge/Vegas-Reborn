"""Copied retail INI changes; these checks do not launch the game."""
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from vegas_reborn.startup_movies import MOVIE_FILES, configure_startup_movies
from vegas_reborn.companion import CompanionError


class StartupMovieTests(unittest.TestCase):
    def test_toggle_restores_original_and_keeps_loading_and_menu_movies(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            originals = {}
            for relative, enabled in zip(MOVIE_FILES, ("TRUE", "FALSE")):
                path = root / "KellerGame/Config" / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                data = ("\ufeff[INTRO]\r\nEnable="+enabled+" ; startup\r\nFilename=logos_PC.bik\r\nChain=INTRO1\r\n"
                        "[INTRO1]\r\nEnable=TRUE\r\nFilename=LoadingE3.bik\r\n"
                        "[LOADING]\r\nEnable=TRUE\r\nFilename=LoadingE3.bik\r\n"
                        "[MENU]\r\nEnable=TRUE\r\nFilename=SplashScreen.bik\r\n").encode("utf-8")
                path.write_bytes(data)
                originals[path] = data
            configure_startup_movies(root, False)
            self.assertFalse((root / ".companion-movies.json").exists())
            for _ in range(2):
                configure_startup_movies(root, True)
                for path, original in originals.items():
                    self.assertEqual(path.read_bytes(), original.replace(b"Enable=TRUE ; startup", b"Enable=FALSE ; startup"))
            configure_startup_movies(root, False)
            for path, original in originals.items():
                self.assertEqual(path.read_bytes(), original)
            self.assertFalse((root / ".companion-movies.json").exists())

    def test_unsupported_config_does_not_partially_change_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for relative in MOVIE_FILES:
                path = root / "KellerGame/Config" / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("[INTRO]\nEnable=TRUE\n" if relative == MOVIE_FILES[0] else "[LOADING]\nEnable=TRUE\n")
            with self.assertRaises(CompanionError):
                configure_startup_movies(root, True)
            self.assertEqual((root / "KellerGame/Config/MovieLoading.ini").read_text(), "[INTRO]\nEnable=TRUE\n")
            self.assertFalse((root / ".companion-movies.json").exists())
