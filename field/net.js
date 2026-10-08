//채널 서버(server.js)와 붙는 클라이언트 쪽 연결. game.js 가 이걸로 채팅·채널이동을 한다.
//서버가 없으면(file://, 아티팩트) 연결 실패를 오류가 아니라 "혼자 모드" 로 취급한다.

const Net = {
  ws: null,
  connected: false,
  me: null,          //서버가 발급한 접속 id (= 계정 id)
  channel: null,
  index: null,       //지금 붙어있는 채널 번호
  roster: [],
  on: {},            //game.js 가 채운다: chat, system, roster, drain, full, resume, transferring

  _name: null,
  _level: 1,
  _gender: 'male',
  _accountId: null,
  _getSnapshot: null, //game.js 가 넘겨주는 "지금 내 상태 통째로" 반환 함수
  _stateTimer: null,
  _posTimer: null,
  _knocking: false,   //채널 순차 노크 중복 방지
  _base: '/',

  emit(type, payload) {
    const fn = this.on[type];
    if (fn) fn(payload);
  },

  //opts: { id, getSnapshot }
  connect(name, level, opts = {}) {
    if (location.protocol === 'file:') return; //붙을 서버가 없다. 혼자 모드
    this._name = name;
    this._level = level || 1;
    this._gender = opts.gender === 'female' ? 'female' : 'male';
    this._accountId = opts.id || this._accountId;
    if (opts.getSnapshot) this._getSnapshot = opts.getSnapshot;
    this._open('/', false);
  },

  //base 로 접속한다. base 가 '/ch1' 같은 경로면 거기에, 'http://localhost:8081' 이면 그 호스트에 붙는다.
  _open(base, resume) {
    let wsUrl;
    try {
      if (base.startsWith('http')) {
        const u = new URL(base);
        const scheme = u.protocol === 'https:' ? 'wss' : 'ws';
        wsUrl = `${scheme}://${u.host}${u.pathname.replace(/\/$/, '')}`;
      } else {
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        wsUrl = `${scheme}://${location.host}${base === '/' ? '' : base}`;
      }
    } catch {
      return;
    }

    let ws;
    try {
      ws = new WebSocket(wsUrl);
    } catch {
      return; //주소가 이상해도 게임은 계속 돌아가야 한다
    }
    this.ws = ws;
    this._base = base;

    ws.onopen = () => {
      this.connected = true;
      ws.send(JSON.stringify({
        type: 'join',
        id: this._accountId,
        name: this._name,
        level: this._level,
        gender: this._gender,
        resume,            //true 면 서버가 사물함에서 내 짐을 꺼내준다
      }));
      this._startStateLoop();
      this._startPosLoop();
    };

    ws.onmessage = ev => {
      let msg;
      try {
        msg = JSON.parse(ev.data);
      } catch {
        return;
      }

      if (msg.type === 'welcome') {
        this.me = msg.id;
        this.channel = msg.channel;
        this.index = msg.index;
        this.roster = msg.roster || [];
        this.emit('system', { text: `${msg.channel} 접속 (정원 ${msg.cap}명)` });
        this.emit('roster', this.roster);
        return;
      }
      if (msg.type === 'roster') {
        this.roster = msg.roster || [];
        this.emit('roster', this.roster);
        return;
      }
      //다른 채널에서 넘어온 내 상태. game.js 가 applyTransfer 로 이어붙인다.
      if (msg.type === 'resume') {
        this.emit('resume', msg.state);
        return;
      }
      //서버가 "저 채널로 옮겨라" 고 한다. 드레인이든 수동 이동이든 여기로 온다.
      if (msg.type === 'transfer') {
        this._moveTo(msg);
        return;
      }
      //정원이 꽉 찬 채널에 붙었을 때. 순차 노크 중이면 다음 채널로 넘어간다.
      if (msg.type === 'full') {
        if (this._knocking) { this._knockNext(); return; }
        this.emit('full', msg);
        return;
      }
      this.emit(msg.type, msg);
    };

    ws.onclose = () => {
      this.connected = false;
      this._stopStateLoop();
      this._stopPosLoop();
    };
    ws.onerror = () => { this.connected = false; };
  },

  //서버가 지정한 채널로 갈아탄다. 헌 연결을 닫고 resume=true 로 새로 붙는다.
  _moveTo(msg) {
    this.emit('transferring', { to: msg.to, reason: msg.reason });
    try { this.ws.close(); } catch {}
    const base = msg.base || `/ch${msg.to}`;
    setTimeout(() => this._open(base, true), 200); //헌 소켓이 확실히 닫히고 나서 새로 연다
  },

  //게임 안에서 "채널 이동" 버튼을 눌렀을 때 호출한다.
  switchChannel(n) {
    if (!this.connected) return false;
    this.ws.send(JSON.stringify({ type: 'switch', to: n }));
    return true;
  },

  //0번 채널이 꽉 찼을 때 1번, 2번... 순서대로 두드려본다. /channels 로 살아있는 채널만 골라서 돈다.
  async knockChannels() {
    if (this._knocking) return;
    this._knocking = true;
    try {
      const res = await fetch('/channels');
      const data = await res.json();
      this._knockList = (data.channels || []).filter(c => !c.full && !c.draining).map(c => c.base || `/ch${c.index}`);
    } catch {
      this._knockList = [];
    }
    this._knockNext();
  },

  _knockNext() {
    const next = (this._knockList || []).shift();
    if (!next) {
      this._knocking = false;
      this.emit('full', { text: '들어갈 수 있는 채널이 없다.' });
      return;
    }
    this._open(next, false);
  },

  //1초마다 내 상태를 통째로 서버에 보낸다. 채널을 옮길 때 이 마지막 스냅샷이 넘어간다.
  _startStateLoop() {
    this._stopStateLoop();
    this._stateTimer = setInterval(() => {
      if (!this.connected) return;
      const snapshot = this._getSnapshot ? this._getSnapshot() : null;
      this.ws.send(JSON.stringify({ type: 'state', level: this._level, snapshot }));
    }, 1000);
  },

  _stopStateLoop() {
    if (this._stateTimer) { clearInterval(this._stateTimer); this._stateTimer = null; }
  },

  //채널 안 다른 사람 눈에 내가 "움직이는 걸로" 보이게 하는 용도. 전투·이관은 여전히 _stateTimer(1초) 스냅샷이 맡는다 —
  //이건 좌표만 가볍게, 자주 쏜다. 서버는 받은 좌표를 그대로 남에게 뿌리기만 하고 판정엔 안 쓴다(접촉 없음).
  _startPosLoop() {
    this._stopPosLoop();
    this._posTimer = setInterval(() => {
      if (!this.connected || !this._getSnapshot) return;
      const s = this._getSnapshot();
      if (!s) return;
      this.ws.send(JSON.stringify({ type: 'pos', x: s.x, face: s.face }));
    }, 150);
  },

  _stopPosLoop() {
    if (this._posTimer) { clearInterval(this._posTimer); this._posTimer = null; }
  },

  chat(text) {
    if (!this.connected) return false;
    this.ws.send(JSON.stringify({ type: 'chat', text }));
    return true;
  },

  syncLevel(level) {
    this._level = level;
  },

  //관리자 메뉴에서 가짜 접속자 늘리고 줄이기. 채널 포화·KEDA 스케일 테스트용.
  bot(delta) {
    if (!this.connected) return false;
    this.ws.send(JSON.stringify({ type: 'bot', delta, level: this._level }));
    return true;
  },
};

if (typeof window !== 'undefined') window.Net = Net;
