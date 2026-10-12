// 1bit.kr 앞단 라우터. 루트·www 는 포트폴리오(GitHub Pages), /game 은 GKE 게임 서버로 보낸다.
// 배포: Cloudflare 대시보드 Workers → 이 코드 붙여넣기 → Routes 에 1bit.kr/* 와 www.1bit.kr/* 연결
const PAGES = 'https://jryu91.github.io/1bit.kr';
const GAME = 'https://game-origin.1bit.kr'; // 11월 이전 때 여기 한 줄만 새 서버로 바꾼다

export default {
  async fetch(req) {
    const url = new URL(req.url);
    if (url.hostname === 'www.1bit.kr') { url.hostname = '1bit.kr'; return Response.redirect(url.toString(), 301); }

    if (url.pathname === '/game' || url.pathname.startsWith('/game/')) {
      // WebSocket 업그레이드 포함 그대로 전달. 서버는 PUBLIC_HOSTS 로 Origin(1bit.kr) 을 허용한다
      return fetch(new Request(GAME + url.pathname + url.search, req));
    }

    const res = await fetch(PAGES + url.pathname + url.search, { method: req.method, headers: req.headers, redirect: 'manual' });
    // Pages 가 /1bit.kr/... 로 보내는 리다이렉트(폴더 슬래시 등)를 공개 주소로 되돌린다
    const loc = res.headers.get('location');
    if (loc) {
      const fixed = new Response(res.body, res);
      fixed.headers.set('location', loc.replace(/^https:\/\/jryu91\.github\.io\/1bit\.kr/, '').replace(/^\/1bit\.kr/, '') || '/');
      return fixed;
    }
    return res;
  },
};
