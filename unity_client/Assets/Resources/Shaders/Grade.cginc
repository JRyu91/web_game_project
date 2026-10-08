// PIL ImageEnhance.Color(sat) → Brightness(br) → Image.blend(tint, a) 와 동일
float3 Grade(float3 rgb, float sat, float br, float4 tint) {
    float l = dot(rgb, float3(0.299, 0.587, 0.114));
    rgb = saturate(lerp(l.xxx, rgb, sat));
    rgb = saturate(rgb * br);
    return lerp(rgb, tint.rgb, tint.a);
}
