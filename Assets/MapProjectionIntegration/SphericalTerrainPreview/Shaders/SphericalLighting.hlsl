#ifndef SPHERICAL_LIGHTING_INCLUDED
#define SPHERICAL_LIGHTING_INCLUDED
// This preview uses the flat map's trilight environment (sky/equator/ground),
// whose spherical harmonics are axially symmetric around world Y. Preserve
// that authored lighting relative to local radial up at every latitude.
// Direct sunlight and shadow coordinates remain in real world space.
half3 SphericalAmbient(float3 normalWS, float3 radialUp)
{
    float up = clamp(dot(normalWS, radialUp), -1, 1);
    return SampleSH(float3(sqrt(saturate(1-up*up)), up, 0));
}
#endif
