import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("service_logs", ROOT / "deploy/raspberry-pi/read-service-logs.py")
service_logs = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(service_logs)


class ServiceLogTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.request = root / "log-request.json"
        self.status = root / "log-status.json"
        for name, value in (("REQUEST", self.request), ("STATUS", self.status)):
            patcher = patch.object(service_logs, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        if os.name == "nt":
            for flag in ("O_NOFOLLOW", "O_NONBLOCK"):
                patcher = patch.object(service_logs.os, flag, 0, create=True)
                patcher.start()
                self.addCleanup(patcher.stop)
            patcher = patch.object(service_logs.os, "fchmod", lambda *arguments: None, create=True)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_request_allows_only_fixed_units_and_line_counts(self):
        for service, lines in (("/etc/shadow", 100), ("app", 1000), ("app;id", 50)):
            self.request.write_text(json.dumps({"requestId": "a1", "service": service, "lines": lines}))
            with self.assertRaises(ValueError):
                service_logs.read_request()

    def test_logs_redact_common_credential_fields(self):
        content = 'apiKey="private-key" client_secret=private-secret Authorization: BearerToken\n'
        redacted = service_logs.redact_logs(content)
        self.assertNotIn("private-key", redacted)
        self.assertNotIn("private-secret", redacted)
        self.assertNotIn("BearerToken", redacted)

    def test_journal_command_uses_fixed_unit_and_redacts_output(self):
        with patch.object(service_logs.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "apiKey=secret\n", "")) as run:
            output = service_logs.read_logs("audio", 50)
        self.assertIn("[REDACTED]", output)
        arguments = run.call_args.args[0]
        self.assertIn("musicdimension-audio.service", arguments)
        self.assertNotIn("shell", arguments)

    def test_main_writes_requested_logs(self):
        self.request.write_text(json.dumps({"requestId": "a2", "service": "bluetooth", "lines": 50}))
        with patch.object(service_logs, "read_logs", return_value="Bluetooth service log\n"):
            service_logs.main()
        status = json.loads(self.status.read_text())
        self.assertEqual(status["state"], "complete")
        self.assertEqual(status["logs"], "Bluetooth service log\n")


if __name__ == "__main__":
    unittest.main()