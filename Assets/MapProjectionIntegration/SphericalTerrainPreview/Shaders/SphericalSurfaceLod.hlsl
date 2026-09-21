#ifndef WW2_SPHERICAL_SURFACE_LOD_INCLUDED
#define WW2_SPHERICAL_SURFACE_LOD_INCLUDED
TEXTURE2D(_DetailCoverage);
SAMPLER(sampler_DetailCoverage);

float SphericalChunkCoverage(float3 n)
{
    float3 delta = n - _CoverageFocus.xyz;
    float2 uv = float2(dot(delta, _CoverageEast.xyz), dot(delta, _CoverageNorth.xyz))
        * (_SphereRadius / max(_CoverageWorldSize, 1)) + .5;
    if (any(uv < 0) || any(uv > 1) || dot(n, _CoverageFocus.xyz) <= 0) return 0;
    float ready = SAMPLE_TEXTURE2D_LOD(_DetailCoverage, sampler_DetailCoverage, uv, 0).r;
    float distance = length((n - _DetailFocus.xyz) * _SphereRadius);
    float width = max(_DetailBlendWidth, 1);
    // Compute the resident region's shoulder per fragment. Encoding a single
    // edge opacity for each cell leaves hexagonal color steps on open water.
    return ready * (1 - smoothstep(max(0, _DetailRadius - width), _DetailRadius + width, distance));
}

float SphericalStreamingSatelliteBlend(float3 radial, float satelliteBlend)
{
    if (_SurfaceLod > .5 || _UseDetailCoverage < .5 || satelliteBlend >= .999) return satelliteBlend;
    // Both newly published tiles and the rim of the resident region retain
    // the satellite appearance until their detailed presentation fades in.
    return 1 - (1 - satelliteBlend) * smoothstep(0, 1, SphericalChunkCoverage(radial));
}

void ClipSurfaceLod(float3 positionWS, float2 pixelPosition, bool continuousAppearance)
{
    [branch] if (_SurfaceLod > 1.5) return;
    float3 radial = normalize(positionWS - _SphereCenter.xyz);
    float coverage = 0;
    [branch] if (_UseDetailCoverage > .5)
        coverage = SphericalChunkCoverage(radial);
    else
    {
        float distanceToFocus = length((radial - _DetailFocus.xyz) * _SphereRadius);
        coverage = _DetailRadius > 0 ? 1 - smoothstep(max(0, _DetailRadius - max(_DetailBlendWidth, .01)), _DetailRadius, distanceToFocus) : 0;
    }
    // World-domain fallback and shadow coverage. Camera terrain uses actual
    // per-sample geometry availability below; unequal LOD heights do not share
    // the same radial coordinate at one screen pixel.
    // Ocean appearance can interpolate continuously over the satellite bed;
    // it need not dissolve into visible black dots while a tile arrives.
    if (continuousAppearance) coverage = step(.001, coverage);
    float threshold = frac(52.9829189 * frac(dot(floor(pixelPosition), float2(.06711056, .00583715))));
    threshold = clamp(threshold, .0001, .9999);
    clip(_SurfaceLod < .5 ? coverage - threshold : threshold - coverage);
}
void ClipSurfaceLod(float3 positionWS, float2 pixelPosition)
{
    ClipSurfaceLod(positionWS, pixelPosition, false);
}

#if defined(WW2_SPHERICAL_TERRAIN_SCREEN_LOD)
// Bound only while the isolated preview's runtime renderer renders its own
// camera. An MSAA mask is never resolved: each bit must prove geometry exists
// at that exact rasterizer sample, especially along unmatched silhouettes.
Texture2D<float2> _SphericalLodAvailability;
Texture2DMS<float2> _SphericalLodAvailabilityMS;
float4 _SphericalLodAvailabilitySize;
float4x4 _SphericalLodInverseViewProjection;
float _SphericalLodAvailabilityEnabled;
int _SphericalLodAvailabilitySamples;
int _SphericalLodDepthSamples;

float3 SphericalScreenReferenceDirection(float2 pixelPosition)
{
    float2 uv = (floor(pixelPosition) + .5) * _SphericalLodAvailabilitySize.zw;
    #if UNITY_REVERSED_Z
        float nearDepth = 1, farDepth = 0;
    #else
        float nearDepth = UNITY_NEAR_CLIP_VALUE, farDepth = 1;
    #endif
    float3 nearPoint = ComputeWorldSpacePosition(uv, nearDepth, _SphericalLodInverseViewProjection);
    float3 farPoint = ComputeWorldSpacePosition(uv, farDepth, _SphericalLodInverseViewProjection);
    float3 ray = normalize(farPoint - nearPoint);
    float3 origin = nearPoint - _SphereCenter.xyz;
    float b = dot(origin, ray);
    float discriminant = b * b - (dot(origin, origin) - _SphereRadius * _SphereRadius);
    // A mountain can protrude beyond the reference sphere. Its closest ray
    // direction still supplies a common transition weight; actual availability,
    // rather than this analytical intersection, decides whether it may clip.
    float distance = discriminant >= 0 ? max(0, -b - sqrt(max(0, discriminant))) : max(0, -b);
    return normalize(origin + ray * distance);
}

uint SphericalTerrainSampleCoverage(float3 positionWS, float2 pixelPosition, bool depthPass)
{
    if (_SphericalLodAvailabilityEnabled < .5 || _SurfaceLod > 1.5)
    {
        return 0xffffffffu;
    }
    int2 pixel = clamp((int2)floor(pixelPosition), int2(0,0), (int2)_SphericalLodAvailabilitySize.xy - 1);
    float3 radial = SphericalScreenReferenceDirection(pixelPosition);
    float coverage = 0;
    if (_UseDetailCoverage > .5) coverage = SphericalChunkCoverage(radial);
    else
    {
        float distanceToFocus = length((radial - _DetailFocus.xyz) * _SphereRadius);
        coverage = _DetailRadius > 0 ? 1 - smoothstep(max(0, _DetailRadius - max(_DetailBlendWidth, .01)), _DetailRadius, distanceToFocus) : 0;
    }
    float threshold = clamp(frac(52.9829189 * frac(dot(floor(pixelPosition), float2(.06711056, .00583715)))), .0001, .9999);
    // With satellite fallback, complete fine geometry can appear in the same
    // satellite colors, then reveal its near materials continuously. Keep the
    // actual per-sample availability guard: unequal silhouettes still require
    // coarse coverage wherever a fine raster sample does not exist.
    bool preferFine = _UseSatellite > .5 ? coverage > .001 : coverage >= threshold;
    uint selectedSamples = 0;
    int samples = depthPass ? _SphericalLodDepthSamples : _SphericalLodAvailabilitySamples;
    [loop] for (int sample = 0; sample < samples; sample++)
    {
        float2 available = 0;
        if (samples > 1) available = _SphericalLodAvailabilityMS.Load(pixel, sample);
        else available = _SphericalLodAvailability.Load(int3(pixel, 0));
        bool fine = available.x > .5, coarse = available.y > .5;
        bool selectFine = fine && (!coarse || preferFine);
        bool selectCoarse = coarse && (!fine || !preferFine);
        if (_SurfaceLod < .5 ? selectFine : selectCoarse) selectedSamples |= 1u << sample;
    }
    return selectedSamples;
}

void ClipSphericalTerrainLod(float3 positionWS, float2 pixelPosition, bool depthPass, out uint sampleCoverage)
{
    // Assign the output before any discard. This also keeps FXC's definite
    // assignment analysis correct when compiling coverage-output fragments.
    sampleCoverage = SphericalTerrainSampleCoverage(positionWS, pixelPosition, depthPass);
    if (_SphericalLodAvailabilityEnabled < .5) ClipSurfaceLod(positionWS, pixelPosition);
    clip((float)sampleCoverage - .5);
}
#endif

// Guard water remains necessary outside a core cell at the bilinear coverage
// rim. Only the water shader opts into this additional ownership test; the
// common terrain / shoreline / river coverage function above is unchanged.
#if defined(WW2_SPHERICAL_WATER_OWNER)
TEXTURE2D(_DetailOwner);

float SphericalOwnerWeight(float owner, float4 owners, float4 contributions)
{
    float4 sameOwner = 1 - step(.5, abs(owners - owner));
    return owner > .5 ? dot(sameOwner, contributions) : 0;
}

void ClipSphericalOceanOwner(float3 positionWS)
{
    [branch] if (_WaterKind > .5 || _SurfaceLod >= .5 || _UseDetailCoverage < .5 || _UseDetailOwner < .5) return;
    float3 radial = normalize(positionWS - _SphereCenter.xyz);
    float3 delta = radial - _CoverageFocus.xyz;
    float2 uv = float2(dot(delta, _CoverageEast.xyz), dot(delta, _CoverageNorth.xyz))
        * (_SphereRadius / max(_CoverageWorldSize, 1)) + .5;
    if (any(uv < 0) || any(uv > 1) || dot(radial, _CoverageFocus.xyz) <= 0) { clip(-1); return; }
    int2 dimensions = (int2)max(_DetailOwner_TexelSize.zw, 1);
    float2 texel = uv * dimensions - .5;
    int2 baseTexel = (int2)floor(texel);
    float2 f = frac(texel);
    float2 a = LOAD_TEXTURE2D(_DetailOwner, clamp(baseTexel, int2(0,0), dimensions - 1)).rg;
    float2 b = LOAD_TEXTURE2D(_DetailOwner, clamp(baseTexel + int2(1,0), int2(0,0), dimensions - 1)).rg;
    float2 c = LOAD_TEXTURE2D(_DetailOwner, clamp(baseTexel + int2(0,1), int2(0,0), dimensions - 1)).rg;
    float2 d = LOAD_TEXTURE2D(_DetailOwner, clamp(baseTexel + int2(1,1), int2(0,0), dimensions - 1)).rg;
    float4 owners = float4(a.x,b.x,c.x,d.x);
    float4 contributions = float4((1-f.x)*(1-f.y),f.x*(1-f.y),(1-f.x)*f.y,f.x*f.y)
        * float4(a.y,b.y,c.y,d.y);
    // Sum every contributing tap belonging to each candidate. Never interpolate
    // the IDs or choose an empty nearest tap: a partly loaded rim still belongs
    // to one published core, whose one-ring guard covers this sampling footprint.
    float selected = 0, best = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float candidate = owners[i];
        float score = SphericalOwnerWeight(candidate, owners, contributions);
        if (score > best || (score > 0 && score == best && candidate < selected))
        { selected = candidate; best = score; }
    }
    clip(selected - .5);
    clip(.25 - abs(selected - _OceanTileOwner));
}
#endif
#endif
