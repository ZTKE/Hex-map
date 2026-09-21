#ifndef WW2_SPHERICAL_GAMEPLAY_POLITICS_INCLUDED
#define WW2_SPHERICAL_GAMEPLAY_POLITICS_INCLUDED
TEXTURE2D(_GameplayCountries);
TEXTURE2D(_GameplayNativeLookup);
TEXTURE2D(_GameplayPalette);
SAMPLER(sampler_point_clamp);
SAMPLER(sampler_linear_repeat);
StructuredBuffer<float4> _GameplayNativeEdges;
StructuredBuffer<uint4> _GameplayNativeState;
StructuredBuffer<uint> _GameplayBuildSelection;
float _HexLandBuildSelectionActive;

float2 NativeEarthUV(float3 radial)
{
    float2 uv = SphericalEarthUV(radial);
    return float2(frac(uv.x), saturate(uv.y));
}
uint NativeSeed(float3 radial)
{
    uint3 packed = (uint3)round(SAMPLE_TEXTURE2D_LOD(_GameplayNativeLookup, sampler_point_clamp, NativeEarthUV(radial), 0).rgb * 255);
    return min(packed.x | (packed.y << 8) | (packed.z << 16), (uint)_GameplayGrid.z - 1);
}
uint NativeCell(float3 radial, out uint4 state)
{
    uint cell = NativeSeed(radial);
    // The raster is only a seed. Barycentric-dual edge planes refine to the
    // same five/six-sided polygon used by CPU picking at every latitude.
    [loop] for (int stepIndex = 0; stepIndex < 8; stepIndex++)
    {
        state = _GameplayNativeState[cell];
        float outside = -0.00000002; int next = -1;
        [unroll] for (int edge = 0; edge < 6; edge++)
        {
            float4 plane = _GameplayNativeEdges[cell * 6 + edge];
            float side = dot(radial, plane.xyz);
            if ((uint)edge < state.w && side < outside) { outside = side; next = (int)plane.w; }
        }
        if (next < 0) return cell;
        cell = (uint)next;
    }
    state = _GameplayNativeState[cell];
    return cell;
}
float4 NativeRGBA(uint value)
{
    return float4(value & 255, (value >> 8) & 255, (value >> 16) & 255, value >> 24) / 255.0;
}
float NativePixelSize(float3 radial) { return max(max(length(ddx(radial)), length(ddy(radial))) * _GameplayGrid.w, .0001); }
static const float RegionSelectionOverlayAlpha = 0.127;
static const float RegionSelectionOverlayAlphaTolerance = 0.018;
bool IsRegionSelectionOverlay(half4 overlay)
{
    return abs(overlay.a - RegionSelectionOverlayAlpha) < RegionSelectionOverlayAlphaTolerance;
}
half3 ApplyNativeRegionSelectionOutline(half3 color, uint cell, uint4 state, float3 radial, float pixel)
{
    if (!IsRegionSelectionOverlay(NativeRGBA(state.y))) return color;
    float bestRim = 0;
    [unroll] for (int edge = 0; edge < 6; edge++)
    {
        if ((uint)edge >= state.w) continue;
        float4 plane = _GameplayNativeEdges[cell * 6 + edge];
        uint4 neighborState = _GameplayNativeState[(uint)plane.w];
        if (IsRegionSelectionOverlay(NativeRGBA(neighborState.y))) continue;
        float edgeDist = dot(radial, plane.xyz) * _GameplayGrid.w;
        float aa = max(pixel, .0004);
        float rim = smoothstep(aa * .35, aa * .95, edgeDist) * (1 - smoothstep(aa * 1.05, aa * 2.6, edgeDist));
        bestRim = max(bestRim, rim * rim * (3 - 2 * rim));
    }
    if (bestRim <= .001) return color;
    half3 ink = half3(.018h, .012h, .008h);
    half3 deepGold = half3(.52h, .36h, .10h);
    half3 metalGold = half3(.90h, .68h, .22h);
    color = lerp(color, ink, bestRim * .82h);
    color = lerp(color, deepGold, bestRim * .62h);
    color = lerp(color, metalGold, bestRim * .48h);
    color += metalGold * bestRim * .22h;
    return saturate(color);
}
half3 ApplyNativeOccupationStripes(half3 color, uint cell, uint4 state, float3 radial, float land, float pixel, float cellWidth)
{
    half4 occupation = NativeRGBA(state.z);
    half strength = occupation.a * land;
    if (strength < .01h) return color;
    half occFade = 1;
    [unroll] for (int edge = 0; edge < 6; edge++)
    {
        if ((uint)edge >= state.w) continue;
        float4 plane = _GameplayNativeEdges[cell * 6 + edge];
        half4 neighborOcc = NativeRGBA(_GameplayNativeState[(uint)plane.w].z);
        if (neighborOcc.a > .01h) continue;
        float edgeDist = dot(radial, plane.xyz) * _GameplayGrid.w;
        occFade = min(occFade, smoothstep(0, cellWidth * .42, edgeDist));
    }
    strength *= occFade;
    if (strength < .01h) return color;
    float3 tangentEast = cross(float3(0, 1, 0), radial);
    if (dot(tangentEast, tangentEast) < 1e-4) tangentEast = cross(float3(1, 0, 0), radial);
    tangentEast = normalize(tangentEast);
    float3 tangentNorth = cross(radial, tangentEast);
    float2 surfacePos = float2(dot(radial, tangentEast), dot(radial, tangentNorth));
    float2 hatchDir = normalize(float2(-.72, 1));
    float spacing = .30;
    float along = dot(surfacePos, hatchDir) / spacing;
    float hatchDist = abs(frac(along) - .5) * spacing;
    float aa = max(fwidth(hatchDist), .012);
    half stripe = 1 - smoothstep(.048 - aa * .25, .048 + aa, hatchDist);
    half luma = dot(color, half3(.2126h, .7152h, .0722h));
    half occupationLuma = dot(occupation.rgb, half3(.2126h, .7152h, .0722h));
    half3 occTint = occupation.rgb * (luma / max(occupationLuma, .025h));
    color = lerp(color, occTint, strength * .30h);
    return saturate(lerp(color, occTint, stripe * strength * .82h));
}
half3 ApplyNativeGameplayOverlay(half3 color, uint cell, uint4 state, float3 radial, float pixel)
{
    half4 overlay = NativeRGBA(state.y);
    if (overlay.a < .001h) return color;
    if (IsRegionSelectionOverlay(overlay))
    {
        color = lerp(color, overlay.rgb, overlay.a);
        return ApplyNativeRegionSelectionOutline(color, cell, state, radial, pixel);
    }
    return lerp(color, overlay.rgb, overlay.a * .36h);
}
half3 NativeSelection(half3 color, float3 radial)
{
    if (_GameplaySelection.z < 0) return color;
    float distance = 100000;
    [unroll] for (int e = 0; e < 6; e++)
        if (_NativeSelectionPlanes[e].w > .5)
            distance = min(distance, dot(radial, _NativeSelectionPlanes[e].xyz) * _GameplayGrid.w);
    float pixel = NativePixelSize(radial);
    // Outside the selected cell min edge distance is negative — do not tint the globe.
    if (distance < -pixel * .35) return color;
    // Rim peaks at the selected tile boundary (same convention as pre-sphere fix).
    float rim = smoothstep(-pixel, 0, distance) * (1 - smoothstep(.45 * pixel, 1.7 * pixel, distance));
    rim *= 1 - smoothstep(1.5, 4.5, pixel);
    if (rim <= .001) return color;
    half pulse = .92h + .08h * sin(_Time.y * 2.4);
    half3 velvet = half3(.012h, .009h, .006h);
    half3 deepGold = half3(.52h, .34h, .10h);
    half3 metalGold = half3(.90h, .66h, .20h);
    half3 gradedGold = lerp(deepGold, metalGold, saturate(rim * 1.35h));
    color = lerp(color, velvet, rim * .88h * pulse);
    color = lerp(color, gradedGold, rim * .78h * pulse);
    color += gradedGold * rim * .42h * pulse;
    return saturate(color);
}
half4 NativePalette(float country)
{
    half4 palette = SAMPLE_TEXTURE2D_LOD(_GameplayPalette, sampler_point_clamp,
        (float2(fmod(country, 256), floor(country / 256)) + .5) / 256, 0);
    // Presentation only: retain authored national hues (including neutral
    // countries), without washing every color towards grey and beige.
    half luma = dot(palette.rgb, half3(.2126h, .7152h, .0722h));
    palette.rgb = saturate(luma.xxx + (palette.rgb - luma.xxx) * 1.38h);
    return palette;
}
float NativeCountryBand(float3 radial, float distanceTexels)
{
    float width = max(_GameplayPoliticalStyle.y, .5);
    float inland = 1 - smoothstep(0, width, max(0, distanceTexels));
    // Reuse geographic alpha mips for the coastal shoulder. It is clipped by
    // the caller's land mask, so country colors never spill onto the ocean.
    // No new distance texture, geometry, or camera-driven map rebuild.
    float lod = log2(max(1, width * _SatelliteRelief_TexelSize.z / _GameplayGrid.x));
    float coast = saturate((1-SAMPLE_TEXTURE2D_LOD(_SatelliteRelief, sampler_SatelliteRelief,
        NativeEarthUV(radial), lod).a)*2);
    return max(inland, coast);
}
float NativeCoastalCountry(uint cell, uint4 state, float3 radial, float country, float geographicLand, float satelliteWeight)
{
    // Geographic satellite coastlines can lie within a native water cell.
    // Borrow only its closest adjacent land country's visual tint there;
    // simulation ownership, picking, cell overlays and geometry stay native.
    [branch] if (country < .5 && geographicLand * satelliteWeight > .001)
    {
        float closest = 100000;
        [unroll] for (int e=0; e<6; e++)
        {
            if ((uint)e >= state.w) continue;
            float4 plane = _GameplayNativeEdges[cell*6+e];
            uint4 neighbor = _GameplayNativeState[(uint)plane.w];
            float owner = neighbor.x & 65535;
            float distance = dot(radial,plane.xyz);
            if (owner > .5 && ((neighbor.x >> 16) & 255) > 0 && distance < closest)
            { country = owner; closest = distance; }
        }
    }
    return country;
}
half3 NativePoliticalWash(half3 color, float3 radial, float country, float land,
    float selected, float hovered, float distanceTexels, float nearBand)
{
    half4 palette = NativePalette(country);
    half luma = dot(color, half3(.2126h, .7152h, .0722h));
    // Carry relief in luminance instead of mixing yellow terrain into the hue.
    // The near interior remains clear; strategic views strengthen the wash.
    half3 politicalSurface = palette.rgb * (.48h + saturate(luma) * .90h);
    float band = 0;
    [branch] if (_GameplayPoliticalStyle.x > .001)
        band = lerp(nearBand, NativeCountryBand(radial, distanceTexels),
            smoothstep(.04, .72, _GameplayPoliticalStyle.w)) * _GameplayPoliticalStyle.x;
    float wash = 1-(1-_GameplaySelection.w)*(1-band);
    wash += selected * .055 + hovered * .024;
    return lerp(color, politicalSurface, saturate(wash) * land * palette.a * step(.5, country));
}
half3 NativeNaturalCountries(half3 color, float3 radial, float geographicLand)
{
    float2 uv = NativeEarthUV(radial);
    float4 fieldSample = SAMPLE_TEXTURE2D_LOD(_GameplayCountries, sampler_point_clamp, uv, 0);
    float country = dot(round(fieldSample.rg * 255), float2(1, 256));
    float selected = step(.5, _GameplaySelection.x) * (1 - step(.5, abs(country - _GameplaySelection.x)));
    float hovered = step(.5, _GameplaySelection.y) * (1 - step(.5, abs(country - _GameplaySelection.y)));
    // The geographic surface owns the satellite coastline. The native field
    // alpha is an ownership sampling aid and must not cut polygon-shaped land.
    float land = saturate(geographicLand);
    float2 dx = ddx(uv), dy = ddy(uv); dx.x -= round(dx.x); dy.x -= round(dy.x);
    float footprint = max(length(dx * _GameplayGrid.xy), length(dy * _GameplayGrid.xy));
    // Clamp V explicitly. Longitude alone repeats, including at the dateline.
    uv.y = clamp(uv.y, .5 / _GameplayGrid.y, 1 - .5 / _GameplayGrid.y);
    float4 filteredField = SAMPLE_TEXTURE2D_LOD(_GameplayCountries, sampler_linear_repeat, uv, 0);
    float distance = filteredField.b * 32;
    color = NativePoliticalWash(color, radial, country, land * filteredField.a, selected, hovered, distance, 0);
    float border = 1 - smoothstep(.25, 1.15, distance / max(footprint * _GameplayPoliticalStyle.z, .25));
    half3 ink = half3(.026h,.033h,.039h);
    return lerp(color, ink, border * land * step(.5, country) * saturate(_GameplayPoliticalStrength.x + selected*.18 + hovered*.10));
}
half3 SphericalLandBuildSelectionColor(uint buildState)
{
    if (buildState < 2u)
    {
        return half3(.58h, .62h, .68h);
    }

    if (buildState < 3u)
    {
        return half3(.48h, .84h, .42h);
    }

    if (buildState < 4u)
    {
        return half3(.92h, .34h, .28h);
    }

    if (buildState < 5u)
    {
        return half3(.72h, .98h, .52h);
    }

    if (buildState < 6u)
    {
        return half3(.36h, .70h, .94h);
    }

    if (buildState < 7u)
    {
        return half3(.58h, .90h, 1.0h);
    }

    return half3(.20h, .34h, .52h);
}

half3 ApplySphericalNativeBuildSelectionOverlay(half3 color, float3 radial)
{
    if (_HexLandBuildSelectionActive < .5)
    {
        return color;
    }

    uint4 state;
    uint cell = NativeCell(radial, state);
    uint buildState = _GameplayBuildSelection[cell];
    if (buildState == 0u)
    {
        return color;
    }

    half3 selectionColor = SphericalLandBuildSelectionColor(buildState);
    half blend = (buildState == 4u || buildState == 6u) ? .98h : .94h;
    if (buildState >= 5u)
    {
        blend = (buildState == 6u) ? .98h : (buildState == 7u ? .88h : .94h);
    }

    return lerp(color, selectionColor, blend);
}

half3 ApplySphericalGameWaterSelection(half3 color, float3 radial)
{
    if (_UseGameplayPolitics < .5) return color;
    if (_GameplayPoliticalStrength.w < .999 && _GameplayPoliticalStrength.y > .5)
    {
        uint4 state; NativeCell(radial, state);
        half4 overlay = NativeRGBA(state.y);
        color = lerp(color, overlay.rgb, overlay.a * .36);
    }
    color = ApplySphericalNativeBuildSelectionOverlay(color, radial);
    return NativeSelection(color, radial);
}

half3 ApplySphericalSatelliteCoast(half3 color, float geographicCoast)
{
    half3 ink = _UseGameplayPolitics > .5 ? half3(.026h,.033h,.039h) : _NaturalBorderTint.rgb;
    float strength = _UseGameplayPolitics > .5 ? _GameplayPoliticalStrength.x : .42;
    return lerp(color, ink, geographicCoast * strength);
}
half3 ApplySphericalGameplayPolitics(half3 color, float3 radial, float visibleLand,
    float geographicLand, float geographicCoast, float satelliteWeight)
{
    float coast = geographicCoast * satelliteWeight;
    if (_UseGameplayPolitics < .5) return ApplySphericalSatelliteCoast(color, coast);
    // Strategic pixels avoid all per-cell edge/state fetches. Ownership field
    // generation and transfers are revision driven, never camera driven.
    if (_GameplayPoliticalStrength.w >= .999 && _HexLandBuildSelectionActive < .5)
        return NativeSelection(ApplySphericalSatelliteCoast(
            NativeNaturalCountries(color, radial, geographicLand), coast), radial);
    half3 original = color;
    uint4 state; uint cell = NativeCell(radial, state);
    float country = state.x & 65535;
    country = NativeCoastalCountry(cell, state, radial, country, geographicLand, satelliteWeight);
    float land = saturate(visibleLand) * ((state.x >> 16) & 255) / 255.0;
    // Mid satellite views still resolve native cell edges, but their visible
    // coast is the same geographic alpha used by the far surface.
    land = lerp(land, saturate(geographicLand), satelliteWeight);
    float selected = step(.5, _GameplaySelection.x) * (1 - step(.5, abs(country - _GameplaySelection.x)));
    float hovered = step(.5, _GameplaySelection.y) * (1 - step(.5, abs(country - _GameplaySelection.y)));
    float2 countryUV = NativeEarthUV(radial);
    countryUV.y = clamp(countryUV.y, .5/_GameplayGrid.y, 1-.5/_GameplayGrid.y);
    float countryDistance = SAMPLE_TEXTURE2D_LOD(_GameplayCountries, sampler_linear_repeat, countryUV, 0).b * 32;
    float borderDistance = 100000;
    uint boundaryMask = (state.x >> 24) & 63;
    // Country revisions refresh this six-bit mask on the changed cell and its
    // neighbors. Most fragments are country interiors and perform no extra
    // boundary reads; the shader never fetches six neighboring country states.
    [branch] if (boundaryMask != 0)
    {
        [unroll] for (int edge = 0; edge < 6; edge++)
        {
            [branch] if ((boundaryMask & (1u << edge)) != 0)
            {
                float4 plane = _GameplayNativeEdges[cell * 6 + edge];
                borderDistance = min(borderDistance, dot(radial, plane.xyz) * _GameplayGrid.w);
            }
        }
    }
    float pixel = NativePixelSize(radial);
    float lineWidth = pixel * _GameplayPoliticalStyle.z;
    // Average hex flat-to-flat size from sphere area per cell. Keep the inward
    // wash in world units so zooming in does not squeeze it into a pixel strip.
    float cellWidth = _GameplayGrid.w * sqrt(14.510395 / _GameplayGrid.z);
    float nearWidth = max(cellWidth, lineWidth * 3.2);
    float nearBand = 1 - smoothstep(0, nearWidth, max(0, borderDistance));
    nearBand *= nearBand;
    color = NativePoliticalWash(color, radial, country, land, selected, hovered, countryDistance, nearBand);
    float border = 1 - smoothstep(.25 * lineWidth, 1.1 * lineWidth, borderDistance);
    float close = 1 - smoothstep(.20, .90, _GameplayPoliticalStyle.w);
    half3 national = NativePalette(country).rgb;
    float shoulder = 1 - smoothstep(lineWidth, lineWidth * 3.2, borderDistance);
    // A restrained luminous core and colored shoulder, evaluated on each
    // country's own side. No second border mesh or bloom/blur pass.
    float strength = land * step(.5, country) * saturate(_GameplayPoliticalStrength.x + selected * .18 + hovered * .10);
    color += national * nearBand * close * strength * .10h;
    color += lerp(national, half3(1,1,1), .30h) * shoulder * close * strength * .06h;
    half3 core = lerp(half3(.026h,.033h,.039h), lerp(national, half3(1,.98h,.92h), .50h), close);
    color = lerp(color, core, border * strength);
    color = ApplyNativeOccupationStripes(color, cell, state, radial, land, pixel, cellWidth);
    color = ApplyNativeGameplayOverlay(color, cell, state, radial, pixel);
    if (_GameplayPoliticalStrength.w > .001)
        color = lerp(color, NativeNaturalCountries(original, radial, geographicLand), _GameplayPoliticalStrength.w);
    color = ApplySphericalNativeBuildSelectionOverlay(color, radial);
    return NativeSelection(ApplySphericalSatelliteCoast(color, coast), radial);
}
half3 ApplySphericalGameplayPolitics(half3 color, float3 radial, float visibleLand)
{
    return ApplySphericalGameplayPolitics(color, radial, visibleLand, visibleLand, 0, 0);
}
#endif
