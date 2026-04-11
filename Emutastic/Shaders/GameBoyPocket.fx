sampler2D input : register(s0);

// Game Boy Pocket lighter gray-green palette
static const float3 palette[4] = {
    float3(0.200, 0.220, 0.180),  // darkest
    float3(0.430, 0.475, 0.390),  // dark
    float3(0.690, 0.740, 0.640),  // light
    float3(0.830, 0.870, 0.780),  // lightest
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
