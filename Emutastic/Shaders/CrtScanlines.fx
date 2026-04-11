sampler2D input : register(s0);
float screenHeight : register(c0); // source resolution height in pixels

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color = tex2D(input, uv);

    // Scanline: darken every other source pixel row
    float row = frac(uv.y * screenHeight * 0.5);
    float scanline = smoothstep(0.35, 0.5, row) * 0.35 + 0.65;

    // Slight brightness boost to compensate for darkening
    color.rgb *= scanline * 1.1;

    // Subtle phosphor bloom — soften bright pixels slightly
    float luma = dot(color.rgb, float3(0.299, 0.587, 0.114));
    color.rgb += luma * 0.04;

    return color;
}
