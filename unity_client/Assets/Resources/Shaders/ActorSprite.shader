// 액터 스프라이트: 시간대 char 그레이딩(전역 _CharTint/_CharGrade) + 원본 텍셀 격자 1px 외곽선.
// 외곽선 색은 전역 _ActorOutline (a=0 이면 끔) — ZoneController 가 존/시간대별로 설정.
Shader "Game/ActorSprite" {
    Properties { [PerRendererData] _MainTex ("Sprite", 2D) = "white" {} _Flash ("Hit Flash", Float) = 0 }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off ZWrite Off Blend One OneMinusSrcAlpha
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            sampler2D _MainTex; float4 _MainTex_TexelSize; float4 _ActorOutline;
            float4 _CharTint; float4 _CharGrade; float _Flash; // _Flash: 액터별 흰 플래시(피격) // 전역: tint rgb+a, (sat, br) — ZoneController
            struct a2v { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            v2f vert(a2v v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.color = v.color; return o; }
            float4 frag(v2f i) : SV_Target {
                float4 c = tex2D(_MainTex, i.uv);
                if (_CharGrade.x > 0) c.rgb = Grade(c.rgb, _CharGrade.x, _CharGrade.y, _CharTint); // char 그룹 그레이딩
                c *= i.color;
                c.rgb = lerp(c.rgb, 1.0, _Flash * step(0.001, c.a));
                if (c.a <= 0.0 && _ActorOutline.a > 0.0) {
                    float2 t = _MainTex_TexelSize.xy;
                    float n = tex2D(_MainTex, i.uv + float2(t.x, 0)).a + tex2D(_MainTex, i.uv - float2(t.x, 0)).a
                            + tex2D(_MainTex, i.uv + float2(0, t.y)).a + tex2D(_MainTex, i.uv - float2(0, t.y)).a;
                    if (n > 0.0) c = float4(_ActorOutline.rgb, _ActorOutline.a * i.color.a);
                }
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
