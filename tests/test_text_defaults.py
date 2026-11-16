import ast
from pathlib import Path
import tempfile
import unittest
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"src"))
from vegas_reborn.companion_settings import Preferences


class DefaultsAndTextTests(unittest.TestCase):
    def test_new_preferences_and_explicit_choices(self):
        self.assertEqual(Preferences().display_mode,"Borderless")
        self.assertTrue(Preferences().skip_intros)
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/"preferences.json"
            path.write_text('{"display_mode":"Windowed","skip_intros":false}')
            saved=Preferences.load(path)
            self.assertEqual(saved.display_mode,"Windowed")
            self.assertFalse(saved.skip_intros)
            path.write_text('{}')
            self.assertTrue(Preferences.load(path).skip_intros)

    def test_app_strings_have_no_encoding_corruption(self):
        markers=("\u00c3\u00a2","\u00e2\u20ac","\u00e2\u0080","\u00c2\u00a0","\ufffd")
        source=Path(__file__).resolve().parents[1]/"src/vegas_reborn"
        for path in source.rglob('*.py'):
            tree=ast.parse(path.read_text(encoding='utf-8'))
            for node in ast.walk(tree):
                if isinstance(node,ast.Constant) and isinstance(node.value,str):
                    self.assertFalse(any(marker in node.value for marker in markers),f'{path.name}:{node.lineno}')
