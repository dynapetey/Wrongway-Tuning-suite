import json
import os
from pathlib import Path
import pty
import select
import shutil
import subprocess
import tempfile
import unittest
from wrongway.core import repair_checksums
from wrongway.backend import BackendRunner
from test_core import fixture

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(shutil.which('mono'), 'Mono is not installed')
class BackendTests(unittest.TestCase):
    def run_backend(self, *args):
        result = subprocess.run(['mono', str(ROOT / 'backend/wrongway-pcm.exe'), *args], capture_output=True, text=True, timeout=20)
        events = [json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
        return result.returncode, events

    def test_load_actual_backend_dependencies(self):
        code, events = self.run_backend('self-test')
        self.assertEqual(code, 0)
        self.assertTrue(events[-1]['success'])
        self.assertGreater(events[-1]['columns'], 5)

    def test_unknown_operation_rejected(self):
        code, events = self.run_backend('arbitrary-command')
        self.assertNotEqual(code, 0)
        self.assertEqual(events[-1]['type'], 'error')

    def test_write_requires_explicit_confirmation(self):
        for op in ['write-calibration', 'write-parameters', 'write-os', 'write-full', 'recover-p59', 'clear-codes']:
            with self.subTest(operation=op):
                code, events = self.run_backend(op)
                self.assertNotEqual(code, 0)
                self.assertIn('--confirm', events[-1]['message'])

    def test_bad_file_cannot_reach_adapter(self):
        code, events = self.run_backend('write-full', '--confirm', '--file', str(ROOT / 'examples/demo.bin'))
        self.assertNotEqual(code, 0)
        self.assertIn('P01/P59', events[-1]['message'])

    def test_checksum_repair_matches_upstream_validator(self):
        for size in [1048576, 524288]:
            with self.subTest(size=size), tempfile.TemporaryDirectory() as folder:
                image = fixture(size)
                image.data[0x8004:0x8006] = b'\x12\x34'
                repair_checksums(image)
                path = Path(folder) / 'synthetic-do-not-flash.bin'
                path.write_bytes(image.data)
                code, events = self.run_backend('validate', '--file', str(path))
                self.assertEqual(code, 0, events)
                self.assertTrue(events[-1]['success'])

    def test_valid_image_still_requires_separate_backup(self):
        with tempfile.TemporaryDirectory() as folder:
            image = fixture()
            repair_checksums(image)
            path = Path(folder) / 'synthetic-do-not-flash.bin'
            path.write_bytes(image.data)
            code, events = self.run_backend('write-full', '--confirm', '--file', str(path), '--backup', str(path))
            self.assertNotEqual(code, 0)
            self.assertIn('separate', events[-1]['message'])

    def test_serial_loopback_native_linux(self):
        master, slave = pty.openpty()
        try:
            process = subprocess.Popen(['mono', str(ROOT / 'backend/wrongway-pcm.exe'), 'serial-test', '--port', os.ttyname(slave)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            ready, _, _ = select.select([master], [], [], 5)
            self.assertTrue(ready)
            self.assertEqual(os.read(master, 100), b'\x12\x34\x56')
            os.write(master, b'\x65\x43\x21')
            stdout, _ = process.communicate(timeout=5)
            self.assertEqual(process.returncode, 0, stdout)
            self.assertTrue(json.loads(stdout)['success'])
        finally:
            os.close(master)
            os.close(slave)

    def test_missing_serial_device_reports_error(self):
        code, events = self.run_backend('identify', '--port', '/dev/wrongway-missing-test-device')
        self.assertNotEqual(code, 0)
        self.assertEqual(events[-1]['type'], 'error')

    def test_argv_preserves_spaces_without_shell(self):
        args = BackendRunner().command('validate', {'file': '/tmp/a folder/$(untouched).bin'})
        self.assertEqual(args[-1], '/tmp/a folder/$(untouched).bin')


if __name__ == '__main__':
    unittest.main()
