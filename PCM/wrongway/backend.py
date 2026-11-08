"""Subprocess bridge with structured progress and reviewable prompts."""
from pathlib import Path
import hashlib
import json
import queue
import shutil
import subprocess
import threading

ROOT = Path(__file__).resolve().parents[1]


class BackendRunner:
    def __init__(self):
        self.events = queue.Queue()
        self.process = None
        self.operation = None
        self.last_result = None

    @property
    def running(self):
        return self.process is not None and self.process.poll() is None

    @property
    def flashing(self):
        return self.running and (self.operation.startswith('write-') or self.operation == 'recover-p59')

    def command(self, operation, options):
        if not shutil.which('mono'):
            raise RuntimeError('Install Mono using install.sh to enable PCM operations.')
        executable = ROOT / 'backend/wrongway-pcm.exe'
        if not executable.is_file():
            raise RuntimeError('Native PCM backend is missing. Extract the full download.')
        manifest = ROOT / 'backend/SHA256.json'
        if manifest.is_file():
            for name, expected in json.loads(manifest.read_text()).items():
                path = ROOT / 'backend' / name
                if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
                    raise RuntimeError('Backend integrity check failed: ' + name)
        args = ['mono', str(executable), operation]
        for key, value in options.items():
            if value is None or value is False:
                continue
            args.append('--' + key)
            if value is not True:
                args.append(str(value))
        return args

    def start(self, operation, options):
        if self.running:
            raise RuntimeError('An adapter operation is already in progress.')
        command = self.command(operation, options)
        self.operation = operation
        self.last_result = None
        process = subprocess.Popen(command, cwd=ROOT / 'backend', stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, bufsize=1)
        self.process = process
        def read():
            for line in process.stdout:
                try:
                    event = json.loads(line)
                    if not isinstance(event, dict):
                        raise ValueError('Unstructured backend output')
                except (json.JSONDecodeError, ValueError):
                    event = {'type': 'message', 'message': line.rstrip()}
                self.events.put(event)
            code = process.wait()
            self.events.put({'type': 'exit', 'code': code, 'operation': operation})
        threading.Thread(target=read, daemon=True).start()

    def send(self, text):
        if self.running:
            try:
                self.process.stdin.write(text + '\n')
                self.process.stdin.flush()
            except (OSError, BrokenPipeError):
                pass

    def cancel(self):
        if self.flashing:
            raise RuntimeError('Allow an active flash to finish before disconnecting.')
        self.send('cancel')
