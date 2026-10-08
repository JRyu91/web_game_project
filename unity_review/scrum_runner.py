#!/usr/bin/env python3
"""Single-flight scrum runner. Defaults to paused; no automatic deployment."""
import fcntl, json, os, subprocess, sys, time
from datetime import datetime
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
STATE = ROOT / 'unity_review/scrum_state.json'
LOCK = STATE.with_suffix('.lock')
LOG = ROOT / 'unity_client/Logs/scrum-runner.jsonl'

def save(state):
    temp = STATE.with_suffix('.tmp')
    temp.write_text(json.dumps(state, ensure_ascii=False, indent=2) + '\n')
    os.replace(temp, STATE)

def main():
    with LOCK.open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return
        if '--hold' in sys.argv:
            heartbeat = STATE.with_suffix('.heartbeat')
            heartbeat.touch()
            print('Interactive runner lock held; releases 15 minutes after last heartbeat', flush=True)
            while time.time() - heartbeat.stat().st_mtime < 900:
                time.sleep(15)
            return
        state = json.loads(STATE.read_text())
        if '--status' in sys.argv:
            print(json.dumps(state, ensure_ascii=False, indent=2)); return
        if '--start' in sys.argv:
            state['status'] = 'ready'; state['retry_after'] = 0; save(state); return
        if '--pause' in sys.argv:
            state['status'] = 'paused'; save(state); return
        if state['status'] not in ('ready', 'waiting_limit') or time.time() < state.get('retry_after', 0):
            return
        if state.get('target_scrum') is not None and state.get('completed_scrum', 0) >= state['target_scrum']:
            state['status'] = 'paused'; state['phase'] = 'report'; save(state); return
        if state.get('finish_at') and time.time() >= datetime.fromisoformat(state['finish_at']).timestamp():
            state['phase'] = 'final_report'
            state['next_action'] = '새 기능 작업을 중지하고 검증 범위를 정리해 Obsidian Projects/Personal_Project에 최종 보고 저장. 완료/미완료/PixelLab보류/사용자결정대기/검증근거를 구분. 보고 후 status=complete.'
            save(state)
        prompt = f'''프로젝트 {ROOT}의 scrum_state.json을 읽고 딱 한 스크럼을 수행하라.
최신 사용자 지침 및 Obsidian Projects/Personal_Project 핸드오프가 정본이다.
메인 PM + 서버 개발자/Unity 개발자/독립 QA 3명 구조를 사용하되 실제 위임 도구가 없으면 진행을 멈추고 needs_input으로 기록하라.
완료 스크럼 {state.get('completed_scrum', 0)}, 다음 {state.get('next_scrum', 1)}, 모드 {state.get('mode', '스크럼')}. 매 스크럼 시작 전 작업/파일 경계/완료 조건, 종료 후 해결/남은 결함/검증/다음 작업을 state에 원자적으로 저장하라.
매 회차 상태를 저장하라. state에 finish_at/report_by가 있을 때만 해당 마감 시간을 적용한다. target_scrum={state.get('target_scrum')}에 도달하면 추가 스크럼 금지, 보고 후 paused로 저장하라. final_report 단계면 새 구현 금지, 보고만 완료하라. 결정이 필요한 경우 needs_input, 전체 완료는 complete, 다음 작업 가능은 ready로 저장하라.
범위: 맵/스킬북/분해/가방·자동판매/성장 UI·직업 방어구/전투·기존 FX/보스·실패복구/채널·저장 안정성/처치검증/밸런스/빌드자동화/장기 QA.
미확정 수치를 임의 결정하지 말라. 결정 대기와 무관한 항목은 계속 작업하라. PixelLab 신규 UI/아트는 2026-10-09로 보류, 기존 자산 UI와 모바일/Safari fullscreen·적응형 화면은 지금 작업. 새로 발견한 관련 결함도 해결하라. Git commit/push, 운영 배포, 인프라 변경, 계정 삭제는 금지. 실제 검증 브라우저 창 금지, 잠깐의 headless 검증은 허용. Unity 배치 실행은 PM만 담당. 코드/검사/로그/빌드는 로컬 저장 허용, 보고자료는 Obsidian 고정경로. 기존 파일을 reset/clean하지 말라.
상태 파일과 이 runner를 변경해 제한을 우회하지 말라. 로그에 비밀번호나 토큰을 남기지 말라.'''
        command = ['/opt/homebrew/bin/codex', '-a', 'never', 'exec', '--json']
        if state.get('session_id'):
            command += ['resume', state['session_id'], prompt]
        else:
            command += ['--sandbox', 'workspace-write', prompt]
        LOG.parent.mkdir(exist_ok=True)
        # ponytail: one append log; rotate if long-term runs make it large.
        with LOG.open('a') as log:
            os.chmod(LOG, 0o600)
            result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            log.write(result.stdout)
        current = json.loads(STATE.read_text())
        for line in result.stdout.splitlines():
            try:
                event = json.loads(line)
                if event.get('type') == 'thread.started':
                    current['session_id'] = event['thread_id']
            except (ValueError, KeyError):
                pass
        if result.returncode:
            limited = any(term in result.stdout.lower() for term in ('usage limit', 'rate limit', 'rate_limit', 'quota exceeded'))
            current['status'] = 'waiting_limit' if limited else 'needs_review'
            current['retry_after'] = time.time() + 3600 if limited else 0
        elif current.get('completed_scrum') == state.get('completed_scrum') and current['status'] == state['status']:
            current['status'] = 'needs_review'
        current['updated_at'] = time.strftime('%Y-%m-%dT%H:%M:%S%z')
        save(current)

if __name__ == '__main__':
    main()
