// 존 레이어 시간대 그레이딩 = compose grade 와 같은 식(감마 공간):
// rgb = lerp(lerp(luma, rgb, _Sat) * _Br, _Tint.rgb, _Tint.a). 버텍스 컬러는 곱(알파: B 밤 구름 x.3).
Shader "Game/GradeSprite" {
    Properties { _MainTex ("Sprite", 2D) = "white" {} _Tint ("Tint rgb, a=blend", Color) = (0,0,0,0) _Sat ("Sat", Float) = 1 _Br ("Br", Float) = 1 }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off ZWrite Off Blend One OneMinusSrcAlpha
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Grade.cginc"
            sampler2D _MainTex; float4 _Tint; float _Sat, _Br;
            struct a2v { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            v2f vert(a2v v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.color = v.color; return o; }
            float4 frag(v2f i) : SV_Target {
                float4 c = tex2D(_MainTex, i.uv);
                c.rgb = Grade(c.rgb, _Sat, _Br, _Tint) ;
                c *= i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
