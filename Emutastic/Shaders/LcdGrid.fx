sampler2D input : register(s0);
float screenHeight : register(c0); // source resolution height in pixels

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color = tex2D(input, uv);

    // Calculate pixel boundaries in source resolution space
    float pixelY = frac(uv.y * screenHeight);
    float pixelX = frac(uv.x * screenHeight); // approximate square pixels

    // Darken at pixel edges to create grid effect
    float gridX = smoothstep(0.0, 0.08, pixelX) * smoothstep(1.0, 0.92, pixelX);
    float gridY = smoothstep(0.0, 0.08, pixelY) * smoothstep(1.0, 0.92, pixelY);
    float grid = gridX * gridY;

    // Blend: 85% grid visibility (subtle darkening at edges)
    color.rgb *= lerp(0.70, 1.0, grid);

    return color;
}
