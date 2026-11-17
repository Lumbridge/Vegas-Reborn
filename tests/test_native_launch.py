import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch,Mock
from vegas_reborn.native_launch import require_components,run_component,NativeLaunchError


class NativeLaunchTests(unittest.TestCase):
    def test_missing_helper_is_named_before_launch(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(NativeLaunchError,'VegasSteamControl.exe'):
                require_components(directory)

    def test_security_block_reports_component_and_windows_error_without_arguments(self):
        for code in (225,226):
            error=OSError('sensitive raw diagnostic');error.winerror=code
            with patch('vegas_reborn.native_launch.subprocess.run',side_effect=error):
                with self.assertRaises(NativeLaunchError) as caught:
                    run_component(['VegasSteamControl.exe','private-route-secret'])
            message=str(caught.exception)
            self.assertIn('Windows Security',message);self.assertIn(str(code),message)
            self.assertIn('VegasSteamControl.exe',message)
            self.assertNotIn('secret',message);self.assertNotIn('sensitive',message)

    def test_accessible_components_and_valid_result(self):
        with tempfile.TemporaryDirectory() as directory:
            for name in ('VegasSteamControl.exe','VegasSteamGate.dll','VegasInjector.exe','VegasCompanion-13.dll'):
                (Path(directory)/name).write_bytes(b'authored fixture')
            require_components(directory)
        with patch('vegas_reborn.native_launch.subprocess.run',return_value=Mock(returncode=0,stdout=b'{"ready":true}')):
            self.assertEqual(run_component(['helper.exe']),{'ready':True})

    def test_empty_component_reports_incomplete_installation(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory)/'VegasSteamControl.exe').touch()
            with self.assertRaisesRegex(NativeLaunchError,'component is empty: VegasSteamControl.exe') as caught:
                require_components(directory)
            self.assertNotIn('Repair installation',str(caught.exception))

    def test_failed_exit_and_invalid_result_are_specific_and_sanitized(self):
        for response,expected in ((Mock(returncode=6,stdout=b'',stderr=b'secret'),'exit code 6'),
                                  (Mock(returncode=0,stdout=b'private-secret'),'invalid launch result')):
            with patch('vegas_reborn.native_launch.subprocess.run',return_value=response):
                with self.assertRaisesRegex(NativeLaunchError,expected) as caught:run_component(['helper.exe'])
            self.assertNotIn('secret',str(caught.exception))
