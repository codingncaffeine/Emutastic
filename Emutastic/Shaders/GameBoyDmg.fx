sampler2D input : register(s0);

// Classic DMG Game Boy 4-shade green palette
static const float3 palette[4] = {
    float3(0.059, 0.220, 0.059),  // #0F380F darkest
    float3(0.188, 0.384, 0.188),  // #306230 dark
    float3(0.545, 0.675, 0.059),  // #8BAC0F light
    float3(0.608, 0.737, 0.059),  // #9BBC0F lightest
};

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color = tex2D(input, uv);

    // Convert to luminance
    float luma = dot(color.rgb, float3(0.299, 0.587, 0.114));

    // Map to 4 shades (quantize)
    float idx = clamp(luma * 3.0, 0.0, 3.0);
    int lo = (int)floor(idx);
    int hi = min(lo + 1, 3);
    float t = frac(idx);

    // Interpolate between adjacent palette entries for smoother look
    float3 result = lerp(palette[lo], palette[hi], t);

    return float4(result, color.a);
}
