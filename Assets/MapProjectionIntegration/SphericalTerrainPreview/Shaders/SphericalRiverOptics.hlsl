#ifndef WW2_SPHERICAL_RIVER_OPTICS_INCLUDED
#define WW2_SPHERICAL_RIVER_OPTICS_INCLUDED

// The mesh supplies signed cross-channel metres, cumulative source-to-mouth
// distance, and a downstream tangent in its local radial east/north frame.
// Source-to-mouth UVs avoid rotating arbitrary world UVs at each bend, which
// would tear a directional pattern where the flow direction changes.
//
// Technique references (adapted to this sphere and its existing wave moments):
// https://catlikecoding.com/unity/tutorials/flow/texture-distortion/
// https://catlikecoding.com/unity/tutorials/flow/directional-flow/
// https://docs.unity3d.com/Packages/com.unity.shadergraph@17.4/manual/Shader-Graph-Sample-Production-Ready-Water.html

struct SphericalRiverMotionData
{
    float3 normalWS;
    float variance;
    float height;
    float foam;
};

float SphericalRiverStreaks(float2 channel, float2 footprint)
{
    // Elongated, broken filaments: advected along the river, never horizontal
    // edge-wide pulses. Analytic footprint keeps subpixel filaments quiet.
    float curl = sin(channel.y * .83) * .29 + sin(channel.y * 1.71) * .12;
    float strandPhase = channel.x * 5.1 + curl;
    float aa = clamp(footprint.x * 5.1 + footprint.y * .45, .018, .32);
    float strand = 1 - smoothstep(max(.015, .13 - aa), .35 + aa, abs(sin(strandPhase)));
    float breakup = .5 + sin(channel.y * 1.27 + channel.x * 2.13) * .3
        + sin(channel.y * 2.37 - channel.x * 1.31) * .2;
    return strand * smoothstep(.33, .73, breakup);
}

void SphericalRiverDecode(float4 value, out float2 slope, out float variance)
{
    slope = value.rg * 2 - 1;
    slope.y = -slope.y; // Existing height-moment export uses image-row derivatives.
    variance = max(2 * value.b - dot(slope, slope), 0);
}

SphericalRiverMotionData EvaluateSphericalRiverMotion(float3 radial, float2 channel,
    float2 downstreamFrame, float riverDistance)
{
    SphericalRiverMotionData result = (SphericalRiverMotionData)0;
    float3 framePole = abs(radial.y) < .98 ? float3(0,1,0) : float3(0,0,1);
    float3 east = normalize(cross(framePole, radial));
    float3 north = cross(radial, east);
    float3 downstream = east * downstreamFrame.x + north * downstreamFrame.y;
    downstream = dot(downstream, downstream) > .01 ? normalize(downstream) : north;
    float3 across = normalize(cross(radial, downstream));

    float halfWidth = max(_RiverCarve.x + .65, .1);
    float center = saturate(1 - abs(riverDistance) / halfWidth);
    // Keep the existing material speed controls, expressed in map units/sec.
    // At the default .03 scroll and .72 motion this is 1.35 units/sec midstream.
    float speed = max(_Civ6RiverScrollSpeed, 0) * 45 * max(_RiverMotion.x, 0) / .72;
    float streamSpeed = speed * lerp(.32, 1, smoothstep(.05, .85, center));
    const float cycle = 4.2;
    float time = _Time.y;
    float spatialPhase = sin(channel.y * .063 + channel.x * .9) * .31
        + sin(channel.y * .117 - channel.x * .7) * .19;
    float phaseA = frac(time * (speed > .0001 ? 1 / cycle : 0) + spatialPhase);
    float phaseB = frac(phaseA + .5);
    // Complementary smooth weights have no brightness gap, and the resetting
    // phase has zero weight and zero weight derivative. Spatial phase offsets
    // spread the transition out instead of making the entire river pulse.
    float weightA = .5 - .5 * cos(phaseA * 6.28318530718);
    float weightB = 1 - weightA;
    float2 flowA = channel, flowB = channel;
    // A uniform slow drift avoids a conspicuous short repeating loop. Only
    // the bounded phase displacement shears across the channel, preventing
    // arbitrarily stretched UVs after a long play session.
    flowA.y -= speed * time * .25 + streamSpeed * (phaseA * cycle * .75);
    flowB.y -= speed * time * .25 + streamSpeed * (phaseB * cycle * .75);
    flowB += float2(.37, 1.73);

    float2 dx = ddx(channel), dy = ddy(channel);
    float2 footprint = abs(dx) + abs(dy);
    float phaseDx = ddx(spatialPhase), phaseDy = ddy(spatialPhase);
    float speedDx = ddx(streamSpeed), speedDy = ddy(streamSpeed);
    float2 dxA = dx, dxB = dx, dyA = dy, dyB = dy;
    dxA.y -= .75 * cycle * (speedDx * phaseA + streamSpeed * phaseDx);
    dxB.y -= .75 * cycle * (speedDx * phaseB + streamSpeed * phaseDx);
    dyA.y -= .75 * cycle * (speedDy * phaseA + streamSpeed * phaseDy);
    dyB.y -= .75 * cycle * (speedDy * phaseB + streamSpeed * phaseDy);
    float scale = max(_Civ6RiverWorldScale, .0001) / .18 * max(_RiverMotion.y, .1) / 2.4;
    float2 tiling = float2(.70, .24) * scale;
    float2 slopeA = 0, slopeB = 0;
    float varianceA = 0, varianceB = 0;
    float heightA = .5, heightB = .5;
    [branch] if (_UseRiverWaves > .5)
    {
        // Analytic phase gradients include bounded shear but exclude the frac
        // reset jump. Existing repeat/trilinear sampler; no new texture asset.
        float4 waveA = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverWaveMoments, sampler_Civ6WaterDeep0,
            flowA * tiling, dxA * tiling, dyA * tiling);
        float4 waveB = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverWaveMoments, sampler_Civ6WaterDeep0,
            flowB * tiling, dxB * tiling, dyB * tiling);
        SphericalRiverDecode(waveA, slopeA, varianceA);
        SphericalRiverDecode(waveB, slopeB, varianceB);
        heightA = waveA.a; heightB = waveB.a;
    }
    float strength = max(_Civ6RiverBumpStrength, 0) * saturate(_RiverMotion.z) / .32;
    strength *= lerp(.4, 1, center);
    float2 meanSlope = slopeA * weightA + slopeB * weightB;
    float secondMoment = (varianceA + dot(slopeA, slopeA)) * weightA
        + (varianceB + dot(slopeB, slopeB)) * weightB;
    result.variance = max(secondMoment - dot(meanSlope, meanSlope), 0) * strength * strength;
    meanSlope *= strength;
    // Rotate the derivatives as well as the UVs so moving specular glints
    // track the actual downstream direction around every bend.
    result.normalWS = normalize(radial - across * meanSlope.x - downstream * meanSlope.y);
    result.height = heightA * weightA + heightB * weightB;

    float foamA = SphericalRiverStreaks(flowA, abs(dxA) + abs(dyA));
    float foamB = SphericalRiverStreaks(flowB, abs(dxB) + abs(dyB));
    float edge = saturate(abs(channel.x) / halfWidth);
    // Broken patches cross the channel; fixed symmetric side bands read as
    // two road markings, especially in a wide view of the full river.
    float patches = smoothstep(.20,.80,.5+.3*sin(flowA.y*.47+flowA.x*1.73)+.2*sin(flowA.y*.91-flowA.x*2.31));
    float resolved = 1 - smoothstep(.24, .9, max(footprint.x, footprint.y * .25));
    result.foam = (foamA * weightA + foamB * weightB) * lerp(.06,.34,patches) * (1-smoothstep(.82,1,edge))
        * saturate(_FoamStrength * 2.2) * resolved;
    return result;
}
#endif
