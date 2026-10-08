// 1유닛 = 40px. 기준 화면 720px = 18유닛, 적응형 카메라는 발라인 화면 비율을 유지. 발라인은 존마다 다르다 → ZoneController.SetZone 이 GroundY 갱신.
namespace Game.Data {
public static class WorldConfig {
    public const float PX = 1f / 40f;
    public const float MapWidth = 80f;          // 3200px, 양 끝 벽(USER 결정: 유한 맵)
    public const float MapMargin = 1.5f;        // 액터가 벽에 파묻히지 않게
    public static float GroundY = (360 - 618 - 1) * PX; // 존 A 기본값 (발 픽셀 행 618 의 아래 모서리)
}
}
