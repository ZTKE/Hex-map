#ifndef HEX_RIVER_SURFACE_INCLUDED
#define HEX_RIVER_SURFACE_INCLUDED

// Unity adaptation of the local Civ6 SDK Water/River material resources.
// River_Bump is a linear R8 Terrain_Heightmap, exported to linear HALF moments:
// RG=.5+.5*slope, B=.5*dot(slope,slope), A=source height. No recovered Civ6 HLSL.
// Density uses its documented one-dimensional top row and RGB*alpha. For Scatter,
// this adapter applies the analogous convention; the original slot formula is unknown.
// The optical model, world scale and specular response below are adaptations.
TEXTURE2D(_Civ6RiverWaveMoments);
TEXTURE2D(_Civ6RiverDensity);
TEXTURE2D(_Civ6RiverScatter);
TEXTURE2D(_Civ6RiverBankAlbedo);
// ForwardLit is at the D3D11 16-sampler limit: reuse existing states. The bound
// near albedo array is Repeat/Trilinear. Legacy profiles fall back to the existing
// Repeat/Bilinear terrain array; neither route allocates another sampler.

float _Civ6RiverWorldScale;
float _Civ6RiverScrollSpeed;
float _Civ6RiverBumpStrength;
float _Civ6RiverOpticalDepth;
float _Civ6RiverDensityRange;
float _Civ6RiverDensityStrength;
float _Civ6RiverScatterTint;
float _Civ6RiverBankTextureStrength;
float _Civ6RiverWaterDarkening;
float _Civ6RiverF0;
float _Civ6RiverSpecularExponent;
float _Civ6RiverSunStrength;
float _Civ6RiverSkyStrength;

struct HexRiverSurface
{
    float3 normalWS;
    float3 baseColor;
    float variance;
};

float2 HexRiverWorldScale(float target)
{
    target = max(target, .0001);
    float mapWidth = max(_HexCellData_TexelSize.z * (2.0 * OUTER_RADIUS * OUTER_TO_INNER), 1.0);
    return float2(max(round(mapWidth * target), 1.0) / mapWidth, target);
}

float4 HexRiverWaveSample(float2 worldXZ, float2 dx, float2 dy, float target,
    float2 scrollDirection, float scrollRate, float2 phaseOffset)
{
    float2 scale = HexRiverWorldScale(target);
    // Global ripple animation, not a recovered network downstream flow. The HF
    // V coordinate resets per hashed edge and its canonical direction can flip.
    // Neither edge identity nor selected branch affects this phase or its LOD.
    float motion = max(_HexRiverWaterMotion.x, 0.0) / .72;
    float2 scroll = frac(scrollDirection * (_Time.y * max(_Civ6RiverScrollSpeed, 0.0) * motion * scrollRate));
    float2 uv = worldXZ * scale + scroll + phaseOffset;
    float4 wave;
    [branch] if (_HexNearTerrainEnabled > .5)
        wave = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverWaveMoments, sampler_HexNearAlbedos,
            uv, dx * scale, dy * scale);
    else
        wave = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverWaveMoments, sampler_Terrain_Textures,
            uv, dx * scale, dy * scale);
    return wave;
}

void HexRiverDecodeMoments(float4 sampleValue, out float2 meanSlope, out float variance)
{
    meanSlope = sampleValue.rg * 2.0 - 1.0;
    // The exported height derivative follows image rows, opposite to Unity UV V.
    meanSlope.y = -meanSlope.y;
    variance = max(2.0 * sampleValue.b - dot(meanSlope, meanSlope), 0.0);
}

float3 EvaluateHexRiverBankColor(float2 worldXZ, float2 dx, float2 dy, float bankCoverage)
{
    float3 bank = _HexRiverBankColor.rgb;
    [branch] if (bankCoverage > .001 && _Civ6RiverBankTextureStrength > 0.0)
    {
        float2 scale = HexRiverWorldScale(max(_Civ6RiverWorldScale, .0001) * .45);
        float3 source;
        [branch] if (_HexNearTerrainEnabled > .5)
            source = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverBankAlbedo, sampler_HexNearAlbedos,
                worldXZ * scale, dx * scale, dy * scale).rgb;
        else
            source = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverBankAlbedo, sampler_Terrain_Textures,
                worldXZ * scale, dx * scale, dy * scale).rgb;
        // Keep the approved bank palette. Source River_B supplies restrained chroma
        // and grain without inventing a new river width, height or terrain layer.
        float luminance = max(dot(source, float3(.2126, .7152, .0722)), .04);
        float3 variation = clamp(source / luminance, .75, 1.25) * lerp(.82, 1.18, saturate(luminance));
        bank *= lerp(1.0, variation, saturate(_Civ6RiverBankTextureStrength));
    }
    return bank;
}

HexRiverSurface EvaluateHexRiverSurface(float2 worldXZ, float2 dx, float2 dy, float riverDistance)
{
    HexRiverSurface river;
    float scale = max(_Civ6RiverWorldScale, .0001) * max(_HexRiverWaterMotion.y, .1) / 2.4;
    // Two globally phased views of the actual river height resource. These are
    // visually adapted ripples; SDK RiverWater only specifies one Bumps slot.
    float4 wave0 = HexRiverWaveSample(worldXZ, dx, dy, scale,
        float2(.91914503, .39391930), 1.0, float2(0, 0));
    float4 wave1 = HexRiverWaveSample(worldXZ, dx, dy, scale * 1.31,
        float2(-.6, .8), .73, float2(.37, .61));
    float2 slope0, slope1;
    float variance0, variance1;
    HexRiverDecodeMoments(wave0, slope0, variance0);
    HexRiverDecodeMoments(wave1, slope1, variance1);
    float amplitude = max(_Civ6RiverBumpStrength, 0.0) * saturate(_HexRiverWaterMotion.z) / .32;
    float2 slope = (slope0 * .60 + slope1 * .40) * amplitude;
    river.variance = (variance0 * .36 + variance1 * .16) * amplitude * amplitude;
    river.normalWS = normalize(float3(-slope.x, 1.0, -slope.y));

    // Symmetric distance is continuous at branch changes; signed U and hashed V
    // are deliberately absent. This is an optical cross-section proxy, not a
    // measured bathymetric depth and never feeds the geometry or coverage.
    float center = saturate(1.0 - riverDistance / max(_HexReliefRiverCarve.x + .065, .01));
    float depth = max(_Civ6RiverOpticalDepth, .01) * lerp(.10, 1.0, center * center);
    float lookupDepth = saturate(depth / max(_Civ6RiverDensityRange, .01));
    // Native PNG top row becomes Unity's top V edge. Explicit LOD0 plus clamp
    // avoids mixing undefined lower rows of these one-dimensional source maps.
    float4 densitySample = SAMPLE_TEXTURE2D_LOD(_Civ6RiverDensity, sampler_linear_clamp, float2(lookupDepth, 1.0), 0);
    float4 scatterSample = SAMPLE_TEXTURE2D_LOD(_Civ6RiverScatter, sampler_linear_clamp, float2(lookupDepth, 1.0), 0);
    float3 density = max(densitySample.rgb * densitySample.a, 0.0);
    float3 transmittance = exp(-density * (depth * max(_Civ6RiverDensityStrength, 0.0)));
    float3 scatter = max(scatterSample.rgb * scatterSample.a, 0.0);
    float scatterLuminance = dot(scatter, float3(.2126, .7152, .0722));
    float3 scatterTint = scatterLuminance > .0001 ? clamp(scatter / scatterLuminance, .70, 1.30) : float3(1, 1, 1);
    // A restrained source-derived tint, not a claim to reconstruct Civ6's
    // undocumented scattering BRDF or to render refracted geometry underneath.
    float3 shallow = lerp(_HexRiverWaterColor.rgb, _HexShallowWaterColor.rgb, .18);
    float3 deep = _HexRiverWaterColor.rgb * max(_Civ6RiverWaterDarkening, 0.0);
    river.baseColor = lerp(shallow, deep, smoothstep(.0, .9, center));
    river.baseColor *= lerp(1.0, transmittance, .38);
    river.baseColor *= lerp(1.0, scatterTint, saturate(_Civ6RiverScatterTint));
    river.baseColor *= 1.0 + ((wave0.a * .60 + wave1.a * .40) - .5) * .04;
    return river;
}

float3 EvaluateHexRiverReflection(HexRiverSurface river, float3 normalWS,
    float3 viewDirection, float3 lightDirection, float3 lightColor, float shadowAttenuation)
{
    float f0 = clamp(_Civ6RiverF0, .0001, .04);
    float noV = saturate(dot(normalWS, viewDirection));
    float viewFresnel = f0 + (1.0 - f0) * pow(1.0 - noV, 5.0);
    float3 halfDirection = SafeNormalize(viewDirection + lightDirection);
    float noH = saturate(dot(normalWS, halfDirection));
    float noL = saturate(dot(normalWS, lightDirection));
    float voH = saturate(dot(viewDirection, halfDirection));
    float sunFresnel = f0 + (1.0 - f0) * pow(1.0 - voH, 5.0);
    // Retain the style's smoothness control, with a finite narrow-lobe floor.
    // Unresolved moments widen the lobe while reducing its peak energy.
    float baseExponent = max(_Civ6RiverSpecularExponent, 32.0) * lerp(.2, 1.0, saturate(_HexRiverWaterMotion.w));
    float exponent = baseExponent / (1.0 + .5 * baseExponent * river.variance);
    float energyRatio = (exponent + 2.0) / (baseExponent + 2.0);
    float specular = pow(noH, exponent) * energyRatio * ((baseExponent + 2.0) / (8.0 * PI)) * sunFresnel * noL;
    float3 sky = float3(.045, .090, .140) * (viewFresnel * max(_Civ6RiverSkyStrength, 0.0));
    return sky + lightColor * (specular * max(_Civ6RiverSunStrength, 0.0) * saturate(shadowAttenuation));
}
#endif
