"""Export the real male wearable atlases and ClipTable into an offline review page."""
import base64
import json
from pathlib import Path
import re
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
RES = ROOT / 'Assets/Resources'
OUT = ROOT.parent / 'unity_review/stage3/team_v6/f_round/code/final'


def build():
    manifest = json.loads((RES / 'Config/male_wearable_manifest.json').read_text())
    clips = [c for c in json.loads((RES / 'Config/clip_table.json').read_text())['clips'] if c['key'].startswith('main_m/')]
    assert len(manifest['poses']) == 35
    feet = [Image.open(RES / ('Sprites/CharactersBaked/' + p['key'] + '.png')).getbbox()[3] for p in manifest['poses']]
    images = {}
    entries = [manifest[k] for k in ('defaultHead', 'defaultBody', 'staffDefaultBody')]
    if manifest.get('swordTrail'):
        entries.append(manifest['swordTrail'])
    entries += [e for k in ('helmets', 'armors') for e in manifest[k] if e and e.get('path')]
    for entry in entries:
        assert len(entry['frames']) == 35, entry['path']
        path = RES / (entry['path'] + '.png')
        images[entry['path']] = 'data:image/png;base64,' + base64.b64encode(path.read_bytes()).decode()
    for p in manifest['poses']:
        for clean in (False, True):
            clip = ('staff_' + p['clip']) if clean and p['clip'].startswith('attack') else p['clip']
            png = RES / f'Sprites/CharactersBaked/main_m/{clip}/frame_{p["frame"]:02}.png'
            images[('clean:' if clean else 'source:') + p['key']] = 'data:image/png;base64,' + base64.b64encode(png.read_bytes()).decode()
    source = (ROOT / 'Assets/Scripts/Data/GameData.Generated.cs').read_text()
    gear = {}
    for slot, name in [('helmets', 'Helmets'), ('armors', 'Armors')]:
        section = source.split('GearDef[] ' + name)[1].split('};')[0]
        gear[slot] = [dict(level=int(lv), name=n, cls=c) for lv, n, c in re.findall(r'level=(\d+), name="([^"]+)", cls="([^"]+)"', section)]
        assert len(gear[slot]) == 37
    examples = json.loads((OUT / 'male_examples.json').read_text())['examples'] if (OUT / 'male_examples.json').exists() else []
    captures = []
    for i, example in enumerate(examples):
        path = OUT / f'male_example_{i}_h{example["h"]:02}_a{example["a"]:02}_{example["clip"]}.png'
        assert path.is_file(), path
        captures.append('data:image/png;base64,' + base64.b64encode(path.read_bytes()).decode())
    data = json.dumps(dict(manifest=manifest, clips=clips, images=images, gear=gear, examples=examples, captures=captures, feet=feet), ensure_ascii=False).replace('</', '<\\/')
    html = TEMPLATE.replace('__DATA__', data)
    assert OUT.is_dir()
    path = OUT / 'male-equipment-preview.html'
    path.write_text(html)
    print(path)


TEMPLATE = '''<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>남자 캐릭터 · 독립 착용 모션 검토</title><style>
*{box-sizing:border-box}body{margin:0;background:#11151b;color:#e7ecf5;font:15px system-ui,sans-serif}main{max-width:1240px;margin:auto;padding:30px 24px}h1{font-size:28px;margin:0 0 8px}p{color:#a7b3c6;line-height:1.6}button,select{font:inherit;color:inherit;background:#263142;border:1px solid #45556c;border-radius:6px;padding:9px;cursor:pointer}button:focus-visible,select:focus-visible{outline:2px solid #78bbff}header{display:flex;gap:12px;align-items:center;justify-content:space-between}#grid{display:grid;grid-template-columns:repeat(3,1fr);gap:16px;margin-top:24px}.card{background:#1a202b;border:1px solid #354153;border-radius:12px;overflow:hidden}.stage{background:radial-gradient(ellipse at 50% 85%,#303b47,#171d26 65%);display:block;text-align:center;overflow:auto;min-height:320px}canvas{width:296px;height:296px;image-rendering:pixelated;display:block;margin:auto;max-width:none}.info{padding:16px}.info h2{font-size:17px;margin:0 0 10px}.controls{display:flex;flex-wrap:wrap;gap:8px}small{display:block;color:#a7b3c6;margin:10px 0}#status{color:#82d2b1}@media(max-width:950px){#grid{grid-template-columns:repeat(2,1fr)}}@media(max-width:600px){#grid{grid-template-columns:1fr}header{display:block}h1{font-size:23px}}@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}}
</style><main><header><div><h1>남자 캐릭터 · 독립 착용</h1><p>서로 다른 티어의 투구와 갑옷, 공통 모션 6개</p></div><button id="shuffle">새 조합 6개</button></header>
<p>투구와 갑옷을 각각 해제해 기본 머리·복장이 복원되는지 확인해봐. 아래 모션은 Unity에서 사용하는 실제 자산과 프레임 시간을 재생해. 단발 모션은 끝에서 0.6초 쉬고 다시 보여줘. 이 페이지는 전투 입력과 무기 렌더링을 포함하지 않는 착용 검토 화면이야.</p><div class="controls"><button id="pause">일시정지</button><button id="step">한 프레임</button><button id="size">4배 확대</button></div><p id="status">자산 로딩 중</p><section id="grid" aria-label="착용 예시 6개"></section><p>얼굴·목 경계, 어깨·소매 연결, 공격·사망 시 겹침을 실제 게임 캡처와 함께 검토해야 해. 정지 화면만으로 전체 품질을 판정하지 않아.</p></main>
<script>const DATA=__DATA__;
const M=DATA.manifest, G=DATA.gear, imgs={}, cards=[];let paused=matchMedia('(prefers-reduced-motion: reduce)').matches, last=0, scale=2;
const available=k=>M[k].map((e,i)=>e&&e.path?i:-1).filter(i=>i>=0);
const random=a=>a[Math.floor(Math.random()*a.length)];
const label=(k,i)=>i<0?'미착용':`Lv${G[k][i].level} ${G[k][i].name} · ${k==='helmets'?'H':'A'}${String(i).padStart(2,'0')}`;
function entry(card,k){return card[k]<0?M[k==='helmets'?'defaultHead':card.cls==='mage'?'staffDefaultBody':'defaultBody']:M[k][card[k]]}
function draw(card){const clip=DATA.clips[card.clip], frame=clip.frames[card.f], n=M.poses.findIndex(p=>p.clip===clip.key.split('/')[1]&&p.frame===frame);if(n<0)throw Error('Missing pose');
const ctx=card.canvas.getContext('2d');ctx.clearRect(0,0,148,148);ctx.imageSmoothingEnabled=false;
// Same bottom-origin atlas rectangles and source-canvas offsets as GearAttachment.
if(card.armors<0&&card.helmets<0){ctx.drawImage(imgs[(card.cls==='mage'?'clean:':'source:')+M.poses[n].key],0,138-DATA.feet[n])}else for(const k of ['armors','helmets']){const e=entry(card,k),r=e.frames[n],img=imgs[e.path];ctx.drawImage(img,r.x,img.height-r.y-r.height,r.width,r.height,r.cropX,r.cropY+138-DATA.feet[n],r.width,r.height)}
if((card.armors>=0||card.helmets>=0)&&card.cls!=='mage'&&clip.key.includes('/attack')&&M.swordTrail){const e=M.swordTrail,r=e.frames[n],img=imgs[e.path];ctx.drawImage(img,r.x,img.height-r.y-r.height,r.width,r.height,r.cropX,r.cropY+138-DATA.feet[n],r.width,r.height)}
card.detail.textContent=`${clip.key.split('/')[1]} · frame ${String(frame).padStart(2,'0')} · ${clip.ms[card.f]}ms`;card.canvas.dataset.pose=M.poses[n].key;card.canvas.dataset.helmet=card.helmets;card.canvas.dataset.armor=card.armors;
}
function fill(initial=false){const grid=document.getElementById('grid');grid.replaceChildren();cards.length=0;const hs=available('helmets'),as=available('armors');if(!hs.length||!as.length)throw Error('No completed equipment');
for(let i=0;i<6;i++){const armor=random(as),cls=G.armors[armor].cls;const eligible=hs.filter(h=>(G.helmets[h].cls==='common'||cls==='common'||G.helmets[h].cls===cls)&&G.helmets[h].level!==G.armors[armor].level);if(!eligible.length)throw Error('No unequal-tier combination');
const example=initial&&DATA.examples[i], a=example?example.a:armor,h=example?example.h:random(eligible);const card={helmets:h,armors:a,cls:example?(example.kind==='staff'?'mage':'warrior'):(G.armors[a].cls!=='common'?G.armors[a].cls:(G.helmets[h].cls==='mage'?'mage':'warrior')),clip:example?DATA.clips.findIndex(c=>c.key==='main_m/'+example.clip):Math.floor(Math.random()*DATA.clips.length),f:0,t:0};const div=document.createElement('article');div.className='card';div.innerHTML='<div class="stage"><canvas width="148" height="148"></canvas></div><div class="info"><h2></h2><div class="controls"></div><small></small></div>';card.canvas=div.querySelector('canvas');card.canvas.style.width=card.canvas.style.height=(148*scale)+'px';card.detail=div.querySelector('small');const title=div.querySelector('h2');
const update=()=>{title.textContent=label('helmets',card.helmets)+' / '+label('armors',card.armors);draw(card)};
const controls=div.querySelector('.controls');for(const k of ['helmets','armors']){const select=document.createElement('select');select.setAttribute('aria-label',k==='helmets'?'투구':'갑옷');const none=new Option(k==='helmets'?'투구 해제':'갑옷 해제',-1);select.add(none);for(const tier of available(k))select.add(new Option(label(k,tier),tier));select.value=card[k];select.onchange=()=>{card[k]=Number(select.value);if(card[k]>=0&&G[k][card[k]].cls!=='common')card.cls=G[k][card[k]].cls;const other=k==='helmets'?'armors':'helmets';if(card[k]>=0&&card[other]>=0&&G[k][card[k]].cls!=='common'&&G[other][card[other]].cls!=='common'&&G[k][card[k]].cls!==G[other][card[other]].cls){card[other]=-1;controls.querySelectorAll('select')[other==='helmets'?0:1].value=-1}update()};controls.append(select)}
const motion=document.createElement('select');motion.setAttribute('aria-label','모션');DATA.clips.forEach((c,j)=>motion.add(new Option(c.key.split('/')[1],j)));motion.value=card.clip;motion.onchange=()=>{card.clip=Number(motion.value);card.f=card.t=0;draw(card)};controls.append(motion);grid.append(div);cards.push(card);update()}}
function tick(now){const dt=last?Math.min(250,now-last):0;last=now;if(!paused)for(const card of cards){card.t+=dt;const clip=DATA.clips[card.clip],duration=()=>clip.ms[card.f]+(!clip.loop&&card.f===clip.frames.length-1?600:0);while(card.t>=duration()){card.t-=duration();card.f=(card.f+1)%clip.frames.length}draw(card)}requestAnimationFrame(tick)}
document.getElementById('shuffle').onclick=()=>fill(false);document.getElementById('pause').onclick=()=>{paused=!paused;document.getElementById('pause').textContent=paused?'재생':'일시정지'};document.getElementById('step').onclick=()=>{paused=true;document.getElementById('pause').textContent='재생';for(const c of cards){c.f=(c.f+1)%DATA.clips[c.clip].frames.length;c.t=0;draw(c)}};document.getElementById('size').onclick=()=>{scale=scale===2?4:2;for(const c of cards)c.canvas.style.width=c.canvas.style.height=(148*scale)+'px';document.getElementById('size').textContent=scale===2?'4배 확대':'2배 보기'};
Promise.all(Object.entries(DATA.images).map(([key,url])=>new Promise((resolve,reject)=>{const img=new Image;img.onload=()=>{imgs[key]=img;resolve()};img.onerror=reject;img.src=url}))).then(()=>{fill(true);document.getElementById('pause').textContent=paused?'재생':'일시정지';document.getElementById('status').textContent=`실제 자산: 투구 ${available('helmets').length}종 · 갑옷 ${available('armors').length}종 · 공통 35프레임`;requestAnimationFrame(tick)}).catch(e=>{document.getElementById('status').textContent='로딩 실패: '+e;console.error(e)});
if(DATA.captures.length){const section=document.createElement('section');section.innerHTML='<h2>실제 Unity 렌더링 · 같은 6개 조합</h2><p>무기·셰이더를 포함한 선택 모션의 전체 프레임이야. 위 재생 화면은 착용 레이어만 보여줘.</p>';DATA.captures.forEach((url,i)=>{const e=DATA.examples[i],figure=document.createElement('figure');figure.style.margin='20px 0';const caption=document.createElement('figcaption');caption.textContent=label('helmets',e.h)+' / '+label('armors',e.a)+' · '+e.clip+' · '+e.kind;const scroller=document.createElement('div');scroller.style.overflow='auto';const img=new Image;img.src=url;img.alt=caption.textContent+' 전체 프레임';img.style.imageRendering='pixelated';img.style.display='block';scroller.append(img);figure.append(caption,scroller);section.append(figure)});document.querySelector('main').append(section)}
</script></html>'''


if __name__ == '__main__':
    build()
