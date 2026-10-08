import fcntl
import importlib.util
import json
import tempfile
from pathlib import Path
spec = importlib.util.spec_from_file_location('runner', Path(__file__).with_name('scrum_runner.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
with tempfile.TemporaryDirectory(dir=runner.ROOT / 'unity_client/Temp') as directory:
    runner.STATE = Path(directory) / 'state.json'
    runner.LOCK = Path(directory) / 'lock'
    runner.save({'status': 'paused'})
    runner.subprocess.run = lambda *a, **k: (_ for _ in ()).throw(AssertionError('Unexpected Codex launch'))
    runner.main()
    assert json.loads(runner.STATE.read_text())['status'] == 'paused'
    runner.save({'status': 'ready', 'completed_scrum': 9, 'target_scrum': 9})
    runner.main()
    assert json.loads(runner.STATE.read_text())['status'] == 'paused'
    runner.save({'status': 'waiting_limit', 'retry_after': 10**12})
    runner.main()
    runner.save({'status': 'ready'})
    with runner.LOCK.open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        runner.main()
    runner.LOG = Path(directory) / 'run.jsonl'
    runner.subprocess.run = lambda *a, **k: type('Result', (), {'returncode': 1, 'stdout': '{"type":"thread.started","thread_id":"test-session"}\nusage limit reached'})()
    runner.main()
    state = json.loads(runner.STATE.read_text())
    assert state['status'] == 'waiting_limit' and state['retry_after'] > 0
    assert state['session_id'] == 'test-session'
print('PASS: pause, retry deadline, atomic state, duplicate lock and limit recovery state')
