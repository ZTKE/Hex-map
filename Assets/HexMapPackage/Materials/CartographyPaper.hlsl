#ifndef HEX_CARTOGRAPHY_PAPER_INCLUDED
#define HEX_CARTOGRAPHY_PAPER_INCLUDED

// The same geographic paper texture on the flat sheet and sphere. Sampling a
// direction instead of screen pixels keeps the fibers fixed while panning and
// avoids a visible join at the longitude seam. No animated water highlights.
float AtlasHash(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float AtlasNoise(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(
        lerp(lerp(AtlasHash(i), AtlasHash(i + float3(1,0,0)), f.x),
             lerp(AtlasHash(i + float3(0,1,0)), AtlasHash(i + float3(1,1,0)), f.x), f.y),
        lerp(lerp(AtlasHash(i + float3(0,0,1)), AtlasHash(i + float3(1,0,1)), f.x),
             lerp(AtlasHash(i + float3(0,1,1)), AtlasHash(i + 1), f.x), f.y), f.z);
}

float3 AtlasDirection(float2 flatUV)
{
    float longitude = (flatUV.x * 2.0 - 1.0) * PI;
    float latitude = (lerp(0.15, 0.8888889, flatUV.y) - 0.5) * PI;
    return float3(cos(latitude) * cos(longitude), sin(latitude), cos(latitude) * sin(longitude));
}

half AtlasGrain(float3 direction, half strength)
{
    float pulp = AtlasNoise(direction * 92.0 + 11.3);
    float3 fibers = direction * float3(410.0, 115.0, 410.0) + 37.0;
    // Fade subpixel detail rather than allowing paper to sparkle during zoom.
    float footprint = max(length(ddx(fibers)), length(ddy(fibers)));
    float tooth = lerp(AtlasNoise(fibers), 0.5, saturate(footprint - 0.6));
    return 1.0h + (half)(pulp * 0.68 + tooth * 0.32 - 0.5) * strength;
}

half3 AtlasOcean(float3 direction, half3 paper, half coast)
{
    half age = (half)AtlasNoise(direction * 13.0 + 4.7);
    half3 color = paper * lerp(0.94h, 1.035h, age);
    // Small absorbed-ink shadow around shorelines, like an engraved globe.
    color *= 1.0h - saturate(coast) * 0.055h;
    return color * AtlasGrain(direction, 0.105h);
}

half3 AtlasPigmentFill(half3 pigment, half inland, half rimStrength, half interiorLift)
{
    half edge = 1.0h - smoothstep(0.0h, 0.20h, inland);
    half3 color = pigment * (1.0h - edge * rimStrength * 0.30h);
    return color * (1.0h + smoothstep(0.10h, 0.85h, inland) * interiorLift * 0.08h);
}

#endif
