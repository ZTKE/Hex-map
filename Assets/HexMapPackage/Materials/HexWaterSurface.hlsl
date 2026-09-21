#ifndef HEX_WATER_SURFACE_INCLUDED
#define HEX_WATER_SURFACE_INCLUDED

// Adapted from locally available Civ6 wave textures and SDK material settings.
// This is a Unity implementation, not recovered Civ6 shader source. The SDK's
// exponent 850 / F0 .004 differs from the game's Water.artdef 5000 / .001.
// Four linear RGBAHalf EXR textures contain filtered slope moments:
// RG = .5 + .5 * slope; B = .5 * dot(slope,slope); A = original wave height.
// All four textures use repeat wrapping and mip filtering, so share one sampler.
TEXTURE2D(_Civ6WaterDeep0);
TEXTURE2D(_Civ6WaterDeep1);
TEXTURE2D(_Civ6WaterCoast0);
TEXTURE2D(_Civ6WaterCoast1);
SAMPLER(sampler_Civ6WaterDeep0);
// R8: 0 deep, 128/255 coastal water, 1 dry. Authored by direct adjacency,
// independently of the two-ring HF coast/tessellation influence flags.
TEXTURE2D(_HexWaterTopologyData);

// Both shaders declare identical Properties defaults and include this one layout.
CBUFFER_START(UnityPerMaterial)
    float _Civ6WaterWorldScale;
    float _Civ6WaterScrollSpeed;
    float _Civ6WaterDeepStrength;
    float _Civ6WaterCoastStrength;
    float _Civ6WaterSpecularExponent;
    float _Civ6WaterF0;
    float _Civ6WaterSunStrength;
    float _Civ6WaterSkyStrength;
    float _Civ6WaterDeepDarkening;
    float _Civ6WaterShallowDarkening;
    float _Civ6WaterHeightTone;
    float _Civ6WaterShelfWidth;
    float _Civ6WaterShelfStrength;
    float _Civ6WaterFoamStrength;
    float _Civ6WaterFoamWidth;
    float _Civ6WaterFoamSpeed;
    float _Civ6WaterClarity;
    float _Civ6WaterBedRelief;
    float _Civ6WaterBedDetail;
CBUFFER_END

struct HexWaveMoments
{
    float2 meanSlope;
    float variance;
    float height;
};

struct HexWaterSurface
{
    float3 normalWS;
    float slopeVariance;
    float height;
};

float2 HexWaterUV(float2 worldXZ, float2 direction, float speed)
{
    float targetScale = max(_Civ6WaterWorldScale, 0.0001);
    float mapWidth = max(_HexCellData_TexelSize.z * (2.0 * OUTER_RADIUS * OUTER_TO_INNER), 1.0);
    // The global ocean has translated map copies and near chunks wrap in X.
    // An integer repeat count keeps the phase identical across these copies.
    // Texture axes stay aligned with world axes; only scrolling is directional.
    float xScale = max(round(mapWidth * targetScale), 1.0) / mapWidth;
    float2 scroll = frac(direction * (_Time.y * _Civ6WaterScrollSpeed * speed));
    // Do not frac the spatial UV: its discontinuous derivatives corrupt mip LOD.
    return worldXZ * float2(xScale, targetScale) + scroll;
}

HexWaveMoments DecodeHexWaveMoments(float4 sampleValue)
{
    HexWaveMoments wave;
    wave.meanSlope = sampleValue.rg * 2.0 - 1.0;
    // Exported texture slope Y follows image rows; UV V rises in the opposite direction.
    wave.meanSlope.y = -wave.meanSlope.y;
    wave.variance = max(2.0 * sampleValue.b - dot(wave.meanSlope, wave.meanSlope), 0.0);
    wave.height = sampleValue.a;
    return wave;
}

HexWaterSurface EvaluateHexWaterSurface(float2 worldXZ, float coastWeight)
{
    // SDK scroll directions 98 / 45 degrees; second deep layer speed is 1.3.
    // Coast speeds are 1 / .5. World scale and timing are adapted to this map.
    const float2 direction0 = float2(-0.1391731, 0.9902681);
    const float2 direction1 = float2(0.70710678, 0.70710678);
    HexWaveMoments deep0 = DecodeHexWaveMoments(SAMPLE_TEXTURE2D(
        _Civ6WaterDeep0, sampler_Civ6WaterDeep0, HexWaterUV(worldXZ, direction0, 1.0)));
    HexWaveMoments deep1 = DecodeHexWaveMoments(SAMPLE_TEXTURE2D(
        _Civ6WaterDeep1, sampler_Civ6WaterDeep0, HexWaterUV(worldXZ, direction1, 1.3)));
    HexWaveMoments coast0 = DecodeHexWaveMoments(SAMPLE_TEXTURE2D(
        _Civ6WaterCoast0, sampler_Civ6WaterDeep0, HexWaterUV(worldXZ, direction0, 1.0)));
    HexWaveMoments coast1 = DecodeHexWaveMoments(SAMPLE_TEXTURE2D(
        _Civ6WaterCoast1, sampler_Civ6WaterDeep0, HexWaterUV(worldXZ, direction1, 0.5)));

    float coast = saturate(coastWeight);
    float deepAmplitude = (1.0 - coast) * max(_Civ6WaterDeepStrength, 0.0) * 0.5;
    float coastAmplitude = coast * max(_Civ6WaterCoastStrength, 0.0) * 0.5;
    float2 meanSlope = (deep0.meanSlope + deep1.meanSlope) * deepAmplitude +
        (coast0.meanSlope + coast1.meanSlope) * coastAmplitude;
    // Independent scrolling layers: variances combine with squared weights,
    // rather than averaging squared moments from unrelated deep/coast means.
    float variance = (deep0.variance + deep1.variance) * deepAmplitude * deepAmplitude +
        (coast0.variance + coast1.variance) * coastAmplitude * coastAmplitude;
    HexWaterSurface surface;
    surface.normalWS = normalize(float3(-meanSlope.x, 1.0, -meanSlope.y));
    surface.slopeVariance = variance;
    surface.height = lerp((deep0.height + deep1.height) * 0.5,
        (coast0.height + coast1.height) * 0.5, coast);
    return surface;
}

// The one-cell COAST category is still authored by TerrainGenerator-style dry
// adjacency. It must not be painted as a solid shelf-shaped polygon. Reconstruct
// a separate continuous bed, with shoals and erosion channels, for optics only.
// This is our height field and absorption model, not Civ6 engine shader source.
struct HexWaterBed
{
    float depth;
    float coastWeight;
    float sand;
    float ripple;
    float3 normalWS;
};

float HexWaterBedHash(float2 p)
{
    uint h = (uint)(int)p.x * 0x8da6b343u ^ (uint)(int)p.y * 0xd8163841u;
    h ^= h >> 16u; h *= 0x7feb352du; h ^= h >> 15u;
    return (h & 0x00ffffffu) * (1.0 / 16777215.0);
}

float2 HexWaterBedCoordinates(float2 worldXZ, float scale)
{
    float mapWidth = max(_HexCellData_TexelSize.z * (2.0 * OUTER_RADIUS * OUTER_TO_INNER), 1.0);
    return worldXZ * float2(max(round(mapWidth * scale), 1.0) / mapWidth, scale);
}

float HexWaterBedNoise(float2 worldXZ, float scale)
{
    float mapWidth = max(_HexCellData_TexelSize.z * (2.0 * OUTER_RADIUS * OUTER_TO_INNER), 1.0);
    float period = max(round(mapWidth * scale), 1.0);
    float2 p = HexWaterBedCoordinates(worldXZ, scale);
    float2 i = floor(p), f = frac(p);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    // Wrap lattice addresses, not texture derivatives; translated map copies
    // have identical bed heights, channel phases, and sand normals.
    float x0 = fmod(fmod(i.x, period) + period, period);
    float x1 = fmod(x0 + 1.0, period);
    return lerp(lerp(HexWaterBedHash(float2(x0, i.y)), HexWaterBedHash(float2(x1, i.y)), f.x),
        lerp(HexWaterBedHash(float2(x0, i.y + 1.0)), HexWaterBedHash(float2(x1, i.y + 1.0)), f.x), f.y);
}

float HexWaterReadTopology(float2 requested)
{
    float2 resolved;
    if (!HFResolveOffset(requested, resolved)) return 0.0;
    return SAMPLE_TEXTURE2D_LOD(_HexWaterTopologyData, HF_TERRAIN_POINT_SAMPLER,
        (resolved + 0.5) * _HexCellData_TexelSize.xy, 0).r;
}

float2 HexWaterDryKernel(float2 delta, float topology)
{
    // Support is two center spacings. The nearest omitted third-ring center
    // is more than 2.08 spacings from any point in the current hex. The full
    // nineteen-cell neighborhood therefore gives the same field at every seam.
    // A broad C2 area-convolution kernel distributes a bank across several
    // land cells. Do not take a fourth root of individual radial supports:
    // that recovers near-nearest-center circles and creates scalloped shelves.
    float k = saturate(1.0 - dot(delta, delta) * 0.25);
    float weight = k * k * k;
    return float2(weight * step(0.75, topology), weight);
}

float HexWaterLandField(HexGridData grid, float2 rootOffset)
{
    float root = HexWaterReadTopology(rootOffset);
    float2 land = HexWaterDryKernel(grid.localPosition, root);
    float coastalNeighborhood = root;
    [unroll] for (int direction = 0; direction < 6; direction++)
    {
        float first = HexWaterReadTopology(HFNeighborOffset(rootOffset, direction));
        coastalNeighborhood += first;
        land += HexWaterDryKernel(grid.localPosition - HFDirection(direction), first);
    }
    // Any second-ring dry cell necessarily gives a first-ring COAST neighbor.
    // This skips the outer twelve reads on open ocean without changing support.
    [branch] if (coastalNeighborhood > 0.1)
    {
        [unroll] for (int direction = 0; direction < 6; direction++)
        {
            float2 firstOffset = HFNeighborOffset(rootOffset, direction);
            float2 firstCenter = HFDirection(direction);
            int next = (direction + 1) % 6;
            land += HexWaterDryKernel(grid.localPosition - firstCenter * 2.0,
                HexWaterReadTopology(HFNeighborOffset(firstOffset, direction)));
            land += HexWaterDryKernel(grid.localPosition - firstCenter - HFDirection(next),
                HexWaterReadTopology(HFNeighborOffset(firstOffset, next)));
        }
    }
    return saturate(land.x / max(land.y, 0.00001));
}

float2 HexWaterWorldGradient(float2 worldXZ, float value)
{
    float2 px = ddx(worldXZ), py = ddy(worldXZ);
    float determinant = px.x * py.y - px.y * py.x;
    float divisor = (determinant < 0.0 ? -1.0 : 1.0) * max(abs(determinant), 0.000001);
    return float2(ddx(value) * py.y - ddy(value) * px.y,
        ddy(value) * px.x - ddx(value) * py.x) / divisor;
}

HexWaterBed EvaluateHexWaterBed(float2 worldXZ, HexGridData grid, float2 rootOffset,
    float actualShoreDepth, float seaInfluence)
{
    float land = HexWaterLandField(grid, rootOffset);
    // Smooth land-area density measures the continental bank. Log depth makes
    // sparse outer support become optically deep before its circular support
    // edges can be seen; the logical one-cell COAST label is never thresholded.
    float offshore = max(-log2(max(land * 2.0, 0.0001)), 0.0);
    float basin = HexWaterBedNoise(worldXZ, 0.024);
    float banks = HexWaterBedNoise(worldXZ + float2(17.1, -9.3), 0.061);
    float grain = HexWaterBedNoise(worldXZ, 0.17);
    float2 channelUV = HexWaterBedCoordinates(worldXZ, 0.038);
    float channelPhase = (channelUV.x + channelUV.y * 0.61 + basin * 0.85) * (2.0 * PI);
    float channel = smoothstep(0.50, 0.93, 0.5 + 0.5 * cos(channelPhase)) *
        (1.0 - smoothstep(1.0, 3.0, fwidth(channelPhase)));
    float shoal = smoothstep(0.38, 0.78, banks) * (0.55 + basin * 0.45);
    float slopePower = lerp(1.28, 1.04, saturate((_Civ6WaterShelfWidth - 0.12) / 0.33));
    float slopeDepth = 0.22 + 5.4 * pow(offshore, slopePower);
    float relief = ((basin - 0.5) * 1.8 + (grain - 0.5) * 0.24 + channel * 1.65 - shoal * 1.1) *
        max(_Civ6WaterBedRelief, 0.0) * smoothstep(0.03, 0.32, offshore);
    // The HF coast can extend inside the >=50% land-area contour. Give its
    // visible bottom a real water column instead of displaying nearly dry
    // olive sand at depth .22; the narrow intersection binding below still
    // exposes warm sand immediately along the beach.
    float landscapeDepth = 1.10 + max(slopeDepth + relief - 0.22, 0.0);
    // Bind only the thin actual intersection strip. Blending HF seaInfluence
    // across the whole shelf projects each original stamp's radial shoulders
    // onto the bottom as fixed angular streaks and an over-wide gray apron.
    float bankBlend = (1.0 - smoothstep(0.03, 0.22, actualShoreDepth)) *
        (1.0 - smoothstep(0.62, 0.90, seaInfluence));
    HexWaterBed bed;
    bed.depth = max(lerp(landscapeDepth, actualShoreDepth, bankBlend), 0.025);
    bed.coastWeight = (1.0 - smoothstep(2.4, 10.0, bed.depth)) * saturate(_Civ6WaterShelfStrength);
    float2 rippleUV = HexWaterBedCoordinates(worldXZ, 0.24);
    float ripplePhase = (rippleUV.x + rippleUV.y * 0.42 + banks * 1.2) * (2.0 * PI);
    float rippleAA = 1.0 - smoothstep(0.6, 2.8, fwidth(ripplePhase));
    bed.ripple = sin(ripplePhase) * rippleAA;
    bed.sand = saturate(0.55 + (grain - 0.5) * 0.45 + (banks - 0.5) * 0.25);
    // HF's intersection correction controls water coverage/optics, never the
    // seabed's normal. Only continuous shoals, channels and ripples cast bottom
    // shading, so no stamp boundary is amplified by screen-space derivatives.
    float bedHeight = -relief + bed.ripple * 0.045 * _Civ6WaterBedDetail;
    float2 gradient = HexWaterWorldGradient(worldXZ, bedHeight);
    bed.normalWS = normalize(float3(-gradient.x, 1.0, -gradient.y));
    return bed;
}

float EvaluateHexShoreFoam(float3 positionWS, float depth, float coverage,
    HexWaterSurface surface)
{
    // Estimate horizontal distance to the actual HF water/terrain contour from
    // its depth gradient. A metric band avoids disappearing foam on steep banks
    // and extremely wide foam on gently sloped seabeds; no extra HF samples.
    float2 px = ddx(positionWS.xz);
    float2 py = ddy(positionWS.xz);
    float determinant = px.x * py.y - px.y * py.x;
    float safeDeterminant = (determinant < 0.0 ? -1.0 : 1.0) * max(abs(determinant), 0.000001);
    float2 depthGradient = float2(
        ddx(depth) * py.y - ddy(depth) * px.y,
        ddy(depth) * px.x - ddx(depth) * py.x) / safeDeterminant;
    float shoreDistance = depth / max(length(depthGradient), 0.12);
    float width = max(_Civ6WaterFoamWidth, 0.1);
    float distance01 = shoreDistance / width;
    float band = (1.0 - smoothstep(0.55, 1.0, distance01)) *
        (1.0 - smoothstep(0.6, 1.5, depth));

    // Wave.artdef uses white crests moving from offshore toward the bank with
    // fade-in/out. This Unity contour-wave implementation uses our existing
    // imported wave height for breakup, rather than claiming engine source.
    float phase = distance01 * 1.6 + _Time.y * _Civ6WaterFoamSpeed + surface.height * 0.22;
    float crest = 0.5 + 0.5 * cos(phase * (2.0 * PI));
    float aa = min(max(fwidth(phase) * 1.5, 0.015), 0.22);
    float breaker = smoothstep(0.79 - aa, 0.94 + aa, crest);
    // Fade unresolved crests rather than turning distant shores solid white.
    breaker *= 1.0 - smoothstep(0.35, 1.1, fwidth(phase));
    float2 foamUV = HexWaterUV(positionWS.xz, float2(0.70710678, 0.70710678), 0.08);
    float patches = 0.5 + 0.5 * sin(foamUV.x * (12.0 * PI) +
        sin(foamUV.y * (10.0 * PI)) + surface.height * 5.0);
    float breakup = lerp(0.18, 1.0, smoothstep(0.22, 0.72, patches));
    float wash = (1.0 - smoothstep(0.015, 0.19, distance01)) * 0.20;
    return saturate((breaker * breakup + wash * breakup) * band * coverage *
        _Civ6WaterFoamStrength);
}

float3 ApplyHexShoreFoam(float3 water, float foam)
{
    // White air/water scattering should replace the dark surface, not just add
    // a faint blue contour to it. Retain a little of the authored foam tint.
    float3 foamColor = lerp(float3(0.94, 0.98, 1.0), _HexShoreFoamColor.rgb, 0.18);
    return lerp(water, foamColor, foam);
}

float3 ShadeHexWater(float3 positionWS, HexWaterSurface surface, HexWaterBed bed)
{
    float3 authoredDeep = _HexDeepOceanColor.rgb;
    float3 authoredShallow = _HexShallowWaterColor.rgb;
    if (_HexNearTerrainEnabled > 0.5)
    {
        authoredDeep = _HexNearDeepWater.rgb;
        authoredShallow = _HexNearShallowWater.rgb;
    }
    float hasStyledColor = step(0.001, dot(authoredDeep, authoredDeep));
    float3 deepColor = lerp(float3(0.010, 0.037, 0.095),
        authoredDeep * float3(0.86, 0.88, 0.98), hasStyledColor * 0.90) * _Civ6WaterDeepDarkening;
    float3 viewDirection = SafeNormalize(GetWorldSpaceViewDir(positionWS));
    float ndv = saturate(dot(surface.normalWS, viewDirection));
    float3 shallowScatter = lerp(float3(0.025, 0.18, 0.25),
        authoredShallow * float3(0.36, 0.55, 0.60), hasStyledColor * 0.50) * _Civ6WaterShallowDarkening;
    float3 scattering = lerp(shallowScatter, deepColor, smoothstep(1.5, 12.0, bed.depth));

    // SDK Coast/Deep materials provide distinct absorption density maps over
    // depth (DensityDepthRange=10), rather than a final painted blue color.
    // Here Beer-Lambert transmission is evaluated over our continuous bed.
    // Compositing it in both paths also avoids exposing the old coarse HF sand
    // mesh through a low-alpha near-water surface.
    float opticalDepth = bed.depth * (0.38 + rcp(max(ndv, 0.35))) / max(_Civ6WaterClarity, 0.25);
    float3 transmission = exp(-float3(0.75, 0.19, 0.11) * opticalDepth);
    transmission *= saturate(_Civ6WaterShelfStrength);
    Light mainLight = GetMainLight();
    float bedLighting = 0.62 + 0.38 * saturate(dot(bed.normalWS, mainLight.direction));
    // Pale submerged sand retains blue light for the clear aqua band; red is
    // absorbed sooner. At the .03-deep beach intersection it remains warm.
    float3 sand = lerp(float3(0.29, 0.245, 0.23), float3(0.43, 0.35, 0.295), bed.sand);
    sand *= bedLighting * (1.0 + bed.ripple * 0.055 * _Civ6WaterBedDetail);
    // Refracted sunlight moves over the stationary sand relief; the bed does
    // not scroll with the waves. Keep caustics subordinate to depth structure.
    float caustic = smoothstep(0.59, 0.73, surface.height) * 0.10 * bed.coastWeight;
    float3 water = sand * transmission * (1.0 + caustic) + scattering * (1.0 - transmission);
    water *= 1.0 + (surface.height - 0.5) * _Civ6WaterHeightTone;
    float f0 = clamp(_Civ6WaterF0, 0.001, 0.04);
    float viewFresnel = f0 + (1.0 - f0) * pow(1.0 - ndv, 5.0);
    // Restrained blue sky reflection, without a constant gray wash or cloud map.
    water = lerp(water, float3(0.065, 0.105, 0.175), saturate(viewFresnel * _Civ6WaterSkyStrength));

    float3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
    float ndh = saturate(dot(surface.normalWS, halfDirection));
    float ndl = saturate(dot(surface.normalWS, mainLight.direction));
    float vdh = saturate(dot(viewDirection, halfDirection));
    float sunFresnel = f0 + (1.0 - f0) * pow(1.0 - vdh, 5.0);
    float baseExponent = max(_Civ6WaterSpecularExponent, 32.0);
    // A narrow 2D lobe has total slope variance approximately 2 / exponent.
    // Filtering adds unresolved slope variance. Broadening lowers the peak by
    // the corresponding normalized-lobe ratio instead of creating broad white patches.
    float effectiveExponent = baseExponent / (1.0 + 0.5 * baseExponent * surface.slopeVariance);
    float energyRatio = (effectiveExponent + 2.0) / (baseExponent + 2.0);
    float sunLobe = pow(ndh, effectiveExponent) * energyRatio;
    float specular = sunLobe * ((baseExponent + 2.0) / (8.0 * PI)) * sunFresnel * ndl;
    water += mainLight.color * (specular * _Civ6WaterSunStrength) * lerp(1.0, 0.65, bed.coastWeight);
    return water;
}

float3 ApplyHexWaterOverlay(float3 water, HexGridData grid)
{
    // Dark water needs a quieter gameplay lattice. Keep the full authoring grid,
    // selection, cell overlay, political borders and occupation treatment.
    water = ApplyHexMapSurfaceOverlay(water, grid,
        max(_HexEditorShowGrid, _HexMapShowGrid * 0.18), 1.0);
    return ApplyOccupationStripes(water, grid);
}

#endif
