"""Force-close safety with fake process handles; never terminates a real game."""
import unittest
import sys
from pathlib import Path
from unittest.mock import Mock, patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"src"))
from vegas_reborn import updater as u


class CloseGameTests(unittest.TestCase):
    def kernel(self,image):
        kernel=Mock()
        kernel.OpenProcess.return_value=100
        kernel.WaitForSingleObject.side_effect=[258,0]
        kernel.TerminateProcess.return_value=True
        def query(handle,flags,buffer,size):
            buffer.value=image
            return True
        kernel.QueryFullProcessImageNameW.side_effect=query
        return kernel

    def test_close_verified_image_and_wait_on_same_handle(self):
        kernel=self.kernel(r"C:\Owned Vegas\R6Vegas_Game.exe")
        with patch.object(u,"game_process_ids",return_value=[123]),patch.object(u,"game_processes_running",return_value=False),patch.object(u.ctypes,"WinDLL",return_value=kernel):
            u.close_running_games()
        kernel.TerminateProcess.assert_called_once_with(100,0)
        kernel.CloseHandle.assert_called_once_with(100)

    def test_reused_pid_with_other_image_is_not_terminated(self):
        kernel=self.kernel(r"C:\Other\Steam.exe")
        with patch.object(u,"game_process_ids",return_value=[123]),patch.object(u,"game_processes_running",return_value=False),patch.object(u.ctypes,"WinDLL",return_value=kernel):
            u.close_running_games()
        kernel.TerminateProcess.assert_not_called()
        kernel.CloseHandle.assert_called_once_with(100)

    def test_empty_snapshot_has_no_termination_side_effect(self):
        with patch.object(u,"game_process_ids",return_value=[]),patch.object(u.ctypes,"WinDLL") as api:
            u.close_running_games()
        api.assert_not_called()

    def test_game_exiting_during_termination_is_already_closed(self):
        kernel=self.kernel(r"C:\Owned Vegas\R6VegasServer.exe")
        kernel.TerminateProcess.return_value=False
        kernel.WaitForSingleObject.side_effect=[258,0,0]
        with patch.object(u,"game_process_ids",return_value=[123]),patch.object(u,"game_processes_running",return_value=False),patch.object(u.ctypes,"WinDLL",return_value=kernel):
            u.close_running_games()
        kernel.CloseHandle.assert_called_once_with(100)
