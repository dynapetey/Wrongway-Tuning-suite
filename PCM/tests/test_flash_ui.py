import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch
from test_core import fixture
from wrongway import core
from wrongway.app import App


class FlashUiTests(unittest.TestCase):
    def test_each_write_mode_allows_no_original_backup(self):
        with tempfile.TemporaryDirectory() as folder:
            image = fixture()
            core.repair_checksums(image)
            path = Path(folder) / 'synthetic-do-not-flash.bin'
            path.write_bytes(image.data)
            modes = {'Calibration only': 'write-calibration', 'Parameters only': 'write-parameters',
                     'OS + calibration + boot': 'write-os', 'Full clone': 'write-full', 'P59 recovery': 'recover-p59'}
            for mode, operation in modes.items():
                with self.subTest(mode=mode):
                    fake = SimpleNamespace(file_options=lambda: {'file': str(path)},
                        flash_mode=SimpleNamespace(get=lambda: mode),
                        adapter=SimpleNamespace(get=lambda: 'OBDX'),
                        port=SimpleNamespace(get=lambda: '/dev/never-opened'), run_pcm=Mock())
                    with patch('wrongway.app.messagebox.askyesno', return_value=True) as review:
                        App.flash(fake)
                    review.assert_called_once()
                    fake.run_pcm.assert_called_once_with(operation, {'file': str(path), 'confirm': True})
