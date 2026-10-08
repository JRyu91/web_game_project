# Characters_new — walk 재생성 (2026-09-27, 아트)

## 원인
- 기존 main_m walk 는 **south-east(대각) 방향**으로 생성돼 보폭이 원근으로 짧게 그려짐(≈4.25px/프레임). main_f walk 는 east. 다른 클립 방향과 무관하게 걷기만 방향이 달랐다.

## 생성물 (PixelLab, 둘 다 같은 템플릿 → 같은 스켈레톤 = 같은 움직임 느낌)
| 캐릭터 | character_id | animation group | 방식 |
|---|---|---|---|
| main_m (main 남) | 0eaa7ca7-f153-48ba-b590-0d6d31024968 | 1e420690-f5ab-438d-8e94-01649c5a43ba (walk_east_tpl_w8f) | template `walking-8-frames`, east, ai_freedom 0 |
| main_f (main 여) | 01b1835b-3544-476b-93c3-efaa2c1f4744 | 481785ab-b7dc-471c-ba43-e1abfa9bc692 (walk_east_tpl_w8f) | 동일 |

- 8프레임 루프. 원본 128x128 → 기존 규약대로 캔버스 패딩: main_m 176x176 발 y=150, main_f 172x172 발 y=146 (가로 중앙 유지, 프레임별 최저 픽셀을 발 라인에 스냅).
- 파일: `main_m/walk/frame_00..07.png`, `main_f/walk/frame_00..07.png` (원본 해상도). 기존 Characters/ 는 안 건드림.
- 몸 높이(구운 뒤): m 98–102px, f 100–103px. 상하 bob ≈3px.

## 측정 (구운 5/6 배율 기준 px)
- 디딘 발 이동: m ≈8px/프레임, f ≈8px/프레임 → 사이클(8f) ≈64px. (이전: m 4.25, f 12.7)
- 접지 프레임 슬라이드(v·t=8 기준): m ≤2px. f 는 프레임 0→1(+≈4px), 2→3(≈-4px) 두 구간이 PixelLab 발 배치 불규칙으로 3~5px, 나머지 ≤1px.

## 제안
- **MOVE_SPEED = 114 px/s, walk 프레임 = 70ms (사이클 560ms), m/f 공통.** (현재 118 → -3%)
- 사이클 범위 대안: 80ms → 100px/s, 90ms → 89px/s (v = 8px / t).
- 통합 시 bake_player_scale.py 로 Characters_new 를 CharactersBaked 에 구워야 함(구운 프리뷰: unity_review/stage3/walk_regen/baked_preview).

## 2차 (검토 반영, 2026-09-27) — A(east) vs B(SE 3/4)
- B: 두 캐릭터 모두 template walking-8-frames, south-east. 그룹 m 46022ef9-8469-4421-bd7a-c480418fc511 / f d8650f63-20c7-4359-8355-7f7a9592bb71. 파일 `*/walk_se/`.
- A·B 모두 발 고르기: 디딘 발 이동이 일정하도록 프레임별 가로 오프셋(원본 px, ±4 이내)을 적용함. A m [-1,-1,0,0,1,1,1,-1], A f [0,4,4,-3,-3,-2,-1,-1], B m [1,4,2,0,0,-1,-4,-2], B f [4,4,2,-4,-4,0,0,0]. `walk/` 파일도 이 버전으로 교체됨.
- 수치를 개선된 측정으로 다시 잼(구운 px/프레임, 발끝이 떨어지는 구간 제외): A m 6.8, f 7.0 / B m 3.5, f 5.2.
- 제안: A = 100 px/s @70ms (1차 제안 114를 대체). B = 61 px/s @70ms (공통 D 4.3 기준 절충. m +0.8 / f -0.9 px/프레임 → 접지 구간 누적 ≈3–4px).

## 3차 B2 (2026-09-30) — walk_se_v2 (맨손 기준, 10프레임)
- m: 0eaa7ca7 v3 10f 그룹 2ccb1c07-e142-4b0f-ad3b-ab7be14fc217 (walk_se_v3_10f_r4). f: 01b1835b v3 10f 그룹 627ca76c-51cb-48f3-9e31-a6b13d483c99.
- 원본 캔버스 176/172 가 idle/attack 과 같음(가로 x오프셋 0, 발은 프레임별 최저 픽셀을 y=150/146 에 스냅).
- 공통 MOVE_SPEED 65 px/s + 프레임별 ms(디딘 발이 월드에서 멈추게):
  - m ms = [31,52,52,30,77,77,77,77,77,30] (사이클 580ms)
  - f ms = [46,62,92,30,108,77,77,77,92,108] (사이클 769ms)
- 수치 전체: unity_review/stage3/walk_regen/B2_numbers.json
