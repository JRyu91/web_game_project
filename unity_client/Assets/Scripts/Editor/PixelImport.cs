// 1x 픽셀 규칙(spec §0): Resources/Sprites 아래 모든 텍스처 = PPU 40, Point, 무압축, 밉맵 없음.
// 캐릭터/몬스터는 Single(캔버스 전체, 중심 피벗) — 프레임마다 슬라이스 중심이 달라 떨리던 것 방지,
// 불투명 bbox(발 정렬/콜라이더) 계산용 Readable, 외곽선 셰이더가 여백까지 그리도록 FullRect.
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools {

public class PixelImport : AssetPostprocessor {
    public override uint GetVersion() => 3; // 올리면 대상 텍스처 전부 재임포트

    void OnPreprocessTexture() {
        if (!assetPath.Contains("/Resources/Sprites/")) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Sprite;
        ti.spritePixelsPerUnit = 40;
        ti.filterMode = FilterMode.Point;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.mipmapEnabled = false;
        ti.maxTextureSize = 4096; // 존 레이어 3200px
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.alphaIsTransparency = true;
        bool actor = assetPath.Contains("/Sprites/Monsters") || assetPath.Contains("/Sprites/Characters"); // *Baked / Characters_100 포함
        if (actor || assetPath.Contains("/Sprites/Zones/") || assetPath.Contains("/Sprites/WeaponsDir/")) { // 무기 방향 스프라이트: 손잡이 = 캔버스 중심 피벗
            ti.spriteImportMode = SpriteImportMode.Single;
            var s = new TextureImporterSettings();
            ti.ReadTextureSettings(s);
            s.spriteMeshType = SpriteMeshType.FullRect;
            s.spriteAlignment = (int)SpriteAlignment.Center;
            ti.SetTextureSettings(s);
        }
        ti.isReadable = actor;
    }
}
}
