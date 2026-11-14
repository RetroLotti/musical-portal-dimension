import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("bluetooth", ROOT / "deploy/raspberry-pi/configure-bluetooth.py")
bluetooth = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bluetooth)


class BluetoothHelperTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.request = root / "bluetooth-request.json"
        self.status = root / "bluetooth-status.json"
        for name, value in (("REQUEST", self.request), ("STATUS", self.status)):
            patcher = patch.object(bluetooth, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        if os.name == "nt":
            for flag in ("O_NOFOLLOW", "O_NONBLOCK"):
                patcher = patch.object(bluetooth.os, flag, 0, create=True)
                patcher.start()
                self.addCleanup(patcher.stop)
            patcher = patch.object(bluetooth.os, "fchmod", lambda *arguments: None, create=True)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_request_rejects_invalid_operation_and_address(self):
        self.request.write_text(json.dumps({"requestId": "a1", "operation": "shell", "address": "AA:BB:CC:DD:EE:FF"}))
        with self.assertRaises(ValueError):
            bluetooth.read_request()
        self.request.write_text(json.dumps({"requestId": "a1", "operation": "connect", "address": "$(id)"}))
        with self.assertRaises(ValueError):
            bluetooth.read_request()

    def test_scan_outputs_paired_state_and_sanitized_names(self):
        self.request.write_text(json.dumps({"requestId": "a2", "operation": "scan"}))
        command = patch.object(bluetooth, "run_bluetoothctl")
        run = command.start()
        self.addCleanup(command.stop)
        run.side_effect = [
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "Device AA:BB:CC:DD:EE:FF Speaker", ""),
            subprocess.CompletedProcess([], 0, "Alias: Speaker\nPaired: yes\nConnected: no\n", ""),
        ]
        bluetooth.main()
        status = json.loads(self.status.read_text())
        self.assertEqual(status["state"], "complete")
        self.assertEqual(status["devices"], [{"address": "AA:BB:CC:DD:EE:FF", "name": "Speaker", "paired": True, "connected": False}])

    def test_failed_pairing_is_reported_without_echoing_request_data(self):
        self.request.write_text(json.dumps({"requestId": "a3", "operation": "connect", "address": "AA:BB:CC:DD:EE:FF"}))
        command = patch.object(bluetooth, "run_bluetoothctl")
        run = command.start()
        self.addCleanup(command.stop)
        run.side_effect = [
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "Paired: no\n", ""),
            subprocess.CompletedProcess([], 1, "", "pair failed"),
            subprocess.CompletedProcess([], 0, "Paired: no\n", ""),
        ]
        bluetooth.main()
        status = json.loads(self.status.read_text())
        self.assertEqual(status["state"], "error")
        self.assertIn("Pairing failed", status["message"])

    def test_pair_connects_and_returns_paired_device(self):
        address = "AA:BB:CC:DD:EE:FF"
        self.request.write_text(json.dumps({"requestId": "a4", "operation": "connect", "address": address}))
        command = patch.object(bluetooth, "run_bluetoothctl")
        run = command.start()
        self.addCleanup(command.stop)
        run.side_effect = [
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "Paired: no\n", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "", ""),
            subprocess.CompletedProcess([], 0, "Paired: yes\nConnected: yes\n", ""),
            subprocess.CompletedProcess([], 0, f"Device {address} Speaker\n", ""),
            subprocess.CompletedProcess([], 0, "Alias: Speaker\nPaired: yes\nConnected: yes\n", ""),
        ]
        bluetooth.main()
        status = json.loads(self.status.read_text())
        self.assertEqual(status["state"], "complete")
        self.assertEqual(status["devices"][0]["address"], address)
        self.assertTrue(status["devices"][0]["connected"])
        calls = [call.args for call in run.call_args_list]
        self.assertTrue(any("--agent" in call and "NoInputNoOutput" in call for call in calls))


if __name__ == "__main__":
    unittest.main()