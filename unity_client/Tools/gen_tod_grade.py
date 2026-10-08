"""시간대 그레이딩 파라미터(compose grade: saturation → brightness → blend(tint,a))를 레이어별로 모아
Assets/Resources/Config/tod_grade.json 생성. 런타임 셰이더(GradeSprite / ActorSprite)가 같은 식을 그대로 계산.
실행: python3 unity_client/Tools/gen_tod_grade.py"""
import json, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPR = f'{ROOT}/Assets/Resources/Sprites'
A = json.load(open(f'{SPR}/Zones/A/manifest.json'))
B = {'dawn': dict(bg=dict(tint=(255,152,92), a=.34, sat=.92, br=.97), obj=dict(tint=(248,152,104), a=.28, sat=.86, br=.80),
                  char=dict(tint=(255,178,138), a=.14, sat=.96, br=.92)),
     'night': dict(bg=dict(tint=(26,40,100), a=.50, sat=.48, br=.46), obj=dict(tint=(38,58,120), a=.34, sat=.60, br=.70),
                   char=dict(tint=(90,110,160), a=.10, sat=.92, br=1.0))}   # zoneB_ruins/compose_final.py TOD
C = {'day': dict(char=dict(tint=(150,60,30), a=.14, sat=.96, br=.92))}       # zoneC manifest character_grading
groups = {'A': {l['name']: l['tint_group'] for l in A['layers'] if l.get('file') and l.get('tint_group')},
          'B': {n: ('bg' if n in ('clouds', 'far', 'mid') else 'obj') for n in ('clouds', 'far', 'mid', 'ground', 'props_back', 'props_front', 'ground_front')},
          'C': {}}
out = []
for zone, table in (('A', A['tint_presets']), ('B', B), ('C', C)):
    for tod, P in table.items():
        if not P or P.get('char') is None: continue
        for name, g in list(groups[zone].items()) + [('char', 'char')]:
            p = P[g]; out.append(dict(k=f'{zone}/{tod}/{name}', tint=[v / 255 for v in p['tint']], a=p['a'], sat=p['sat'], br=p['br']))
json.dump(dict(_note='gen_tod_grade.py 생성물', entries=out), open(f'{ROOT}/Assets/Resources/Config/tod_grade.json', 'w'), indent=1)
print(len(out), 'entries')
