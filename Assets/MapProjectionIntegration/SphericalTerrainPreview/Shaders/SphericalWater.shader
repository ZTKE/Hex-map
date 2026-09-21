Shader "WW2/Spherical Terrain Preview/Water"
{
    Properties
    {
        _SphereCenter ("Sphere center in world space", Vector) = (0,0,0,0)
        _SphereRadius ("Sphere radius", Float) = 3300
        _DetailFocus ("Ready detail focus, unit direction", Vector) = (0,1,0,0)
        _DetailRadius ("Ready detail chord radius", Float) = 0
        _DetailBlendWidth ("Detail blend width", Float) = 20
        _SurfaceLod ("Surface LOD: detail 0, shell 1, bypass 2", Float) = 2
        [NoScaleOffset] _DetailCoverage ("Published chunk coverage", 2D) = "black" {}
        _UseDetailCoverage ("Use published chunk coverage", Float) = 0
        [NoScaleOffset] _DetailOwner ("Published ocean owner and coverage", 2D) = "black" {}
        _UseDetailOwner ("Use unique published ocean ownership", Float) = 0
        _OceanTileOwner ("This fine ocean tile key plus one", Float) = 0
        _CoverageFocus ("Coverage region radial focus", Vector) = (0,1,0,0)
        _CoverageEast ("Coverage region tangent east", Vector) = (1,0,0,0)
        _CoverageNorth ("Coverage region tangent north", Vector) = (0,0,1,0)
        _CoverageWorldSize ("Coverage region full width", Float) = 900
        [NoScaleOffset] _SatelliteColor ("Existing offline satellite color", 2D) = "white" {}
        [NoScaleOffset] _SatelliteRelief ("Existing offline ETOPO relief", 2D) = "gray" {}
        _UseSatellite ("Satellite inputs available", Float) = 0
        _SatelliteBlend ("Continuous near to satellite art", Range(0,1)) = 0
        _FarEarthGrade ("Far saturation: land, ocean, atmosphere, blend", Vector) = (1.24,1.18,1.20,0)
        _FarEarthLight ("Far-only light: blend, land, ocean", Vector) = (0,1.22,1.65,0)
        _UseNaturalSatellite ("Use shared natural satellite art profile", Float) = 0
        _NaturalSurfaceTint ("Natural satellite surface tint", Color) = (.96,.96,.89,1)
        _NaturalOceanTint ("Natural satellite ocean tint", Color) = (.045,.12,.17,1)
        [NoScaleOffset] _OceanRelief ("Saved ocean depth and relief", 2D) = "gray" {}
        _NaturalBorderTint ("Natural satellite country border", Color) = (.91,.87,.72,1)
        _NaturalAtmosphereTint ("Natural satellite atmosphere tint", Color) = (.4,.62,.74,1)
        _NaturalSurfaceParameters ("Saturation, contrast, normal, light", Vector) = (.7,1.06,.65,.55)
        [NoScaleOffset] _GameplayCountries ("Live native natural country field", 2D) = "black" {}
        [NoScaleOffset] _GameplayNativeLookup ("Native sphere cell lookup seeds", 2D) = "black" {}
        [NoScaleOffset] _GameplayPalette ("Live ushort country palette", 2D) = "black" {}
        _UseGameplayPolitics ("Bind native sphere gameplay", Float) = 0
        _GameplayGrid ("Field resolution, native cell count, sphere radius", Vector) = (4096,2048,590492,3300)
        _GameplaySelection ("Selected country, hover country, native tile, wash", Vector) = (0,0,-1,0)
        _GameplayPoliticalStrength ("Border strength, reserved, reserved, satellite weight", Vector) = (0,0,0,0)
        _GameplayPoliticalStyle ("Inward band opacity, width, outline pixels, middle emphasis", Vector) = (0,1,1,0)
        // Match the flat map's raw global float4 palette. A Color property
        // converts on GPU upload even when its stored GetVector value matches.
        _ShallowWater ("Existing shallow water (raw global)", Vector) = (.06,.36,.44,1)
        _DeepWater ("Existing deep water (raw global)", Vector) = (.018,.1,.2,1)
        _NearShallowWater ("Existing near terrain shallow water (raw global)", Vector) = (.12,.4,.46,1)
        _NearDeepWater ("Existing near terrain deep water (raw global)", Vector) = (.018,.09,.17,1)
        _RiverWater ("Existing river water (raw global)", Vector) = (.035,.25,.29,1)
        _FoamColor ("Existing shore foam (raw global)", Vector) = (.9,.96,.94,1)
        _WaterKind ("Ocean 0, river 1", Float) = 0
        _RiverCarve ("Existing river core / shoulder in map units", Vector) = (.7,3.1,0,0)
        _RiverMotion ("Existing flow, wave frequency, strength, smoothness", Vector) = (.72,2.4,.32,.82)
        _UseWaterWaves ("Source ocean waves available", Float) = 0
        _UseRiverWaves ("Source river waves available", Float) = 0
        [NoScaleOffset] _Civ6WaterDeep0 ("Existing deep wave moments 1", 2D) = "gray" {}
        [NoScaleOffset] _Civ6WaterDeep1 ("Existing deep wave moments 2", 2D) = "gray" {}
        [NoScaleOffset] _Civ6WaterCoast0 ("Existing coastal wave moments 1", 2D) = "gray" {}
        [NoScaleOffset] _Civ6WaterCoast1 ("Existing coastal wave moments 2", 2D) = "gray" {}
        [NoScaleOffset] _Civ6RiverWaveMoments ("Existing river wave moments", 2D) = "gray" {}
        [NoScaleOffset] _Civ6RiverDensity ("Existing river density", 2D) = "white" {}
        [NoScaleOffset] _Civ6RiverScatter ("Existing river scatter", 2D) = "gray" {}
        [NoScaleOffset] _Civ6RiverBankAlbedo ("Existing river bank", 2D) = "white" {}
        _Civ6WaterWorldScale ("Ocean wave scale", Float) = .0275
        _Civ6WaterScrollSpeed ("Ocean wave scroll", Float) = .012
        _Civ6WaterDeepStrength ("Deep wave strength", Float) = 2
        _Civ6WaterCoastStrength ("Coastal wave strength", Float) = .8
        _Civ6WaterSpecularExponent ("Ocean sun sharpness", Float) = 850
        _Civ6WaterF0 ("Ocean reflectance", Float) = .004
        _Civ6WaterSunStrength ("Ocean sun strength", Float) = 3
        _Civ6WaterSkyStrength ("Ocean sky strength", Float) = .35
        _Civ6WaterDeepDarkening ("Deep water color scale", Float) = .22
        _Civ6WaterShallowDarkening ("Shallow water color scale", Float) = .55
        _Civ6WaterHeightTone ("Ocean wave color variation", Float) = .08
        _Civ6WaterShelfWidth ("Continental bed slope softness", Float) = .22
        _Civ6WaterShelfStrength ("Coastal seabed visibility", Float) = 1
        _Civ6WaterFoamStrength ("Porous shore breaker opacity", Float) = .86
        _Civ6WaterFoamWidth ("Shore breaker width", Float) = 2
        _Civ6WaterFoamSpeed ("Shore breaker speed", Float) = .18
        _Civ6WaterClarity ("Shallow optical clarity", Float) = 1
        _Civ6WaterBedRelief ("Seabed shoals and channels", Float) = 1
        _Civ6WaterBedDetail ("Seabed sand ripples", Float) = 1
        _Civ6RiverWorldScale ("River wave scale", Float) = .18
        _Civ6RiverScrollSpeed ("River wave scroll", Float) = .03
        _Civ6RiverBumpStrength ("River bump strength", Float) = .075
        _Civ6RiverOpticalDepth ("River optical depth", Float) = .6
        _Civ6RiverDensityRange ("River density range", Float) = 2
        _Civ6RiverDensityStrength ("River density strength", Float) = 1
        _Civ6RiverScatterTint ("River scatter tint", Float) = .18
        _Civ6RiverWaterDarkening ("River darkening", Float) = .48
        _Civ6RiverF0 ("River reflectance", Float) = .004
        _Civ6RiverSpecularExponent ("River sun sharpness", Float) = 1600
        _Civ6RiverSunStrength ("River sun strength", Float) = .6
        _Civ6RiverSkyStrength ("River sky strength", Float) = .12
        _FoamStrength ("Subtle shallow foam", Range(0,1)) = .28
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        // The continuous bed is optically opaque; alpha only composites the
        // narrow real shoreline and river feather. Back faces remain culled.
        // A prepass may write only fully opaque water: otherwise it would hide
        // the terrain that this forward pass needs beneath its feathered edge.
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry+10" }
        Cull [_Cull] ZWrite On
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "SphericalLighting.hlsl"
        TEXTURE2D(_Civ6WaterDeep0); SAMPLER(sampler_Civ6WaterDeep0);
        TEXTURE2D(_Civ6WaterDeep1);
        TEXTURE2D(_Civ6WaterCoast0);
        TEXTURE2D(_Civ6WaterCoast1);
        TEXTURE2D(_Civ6RiverWaveMoments);
        TEXTURE2D(_Civ6RiverDensity);
        TEXTURE2D(_Civ6RiverScatter);
        TEXTURE2D(_Civ6RiverBankAlbedo);
        SAMPLER(sampler_linear_clamp);
        CBUFFER_START(UnityPerMaterial)
            float4 _SphereCenter;
            float4 _DetailFocus;
            float _DetailRadius, _DetailBlendWidth, _SurfaceLod, _UseDetailCoverage, _UseSatellite, _SatelliteBlend;
            float4 _FarEarthGrade;
            float4 _FarEarthLight;
            float4 _GameplayGrid, _GameplaySelection, _GameplayPoliticalStrength, _GameplayPoliticalStyle;
            float4 _SatelliteRelief_TexelSize;
            float _UseGameplayPolitics, _UseNaturalSatellite;
            float4 _NaturalSurfaceTint, _NaturalOceanTint, _NaturalBorderTint, _NaturalAtmosphereTint;
            float4 _NaturalSurfaceParameters;
            float4 _NativeSelectionPlanes[6];
            float4 _CoverageFocus, _CoverageEast, _CoverageNorth;
            float _CoverageWorldSize;
            float4 _DetailOwner_TexelSize;
            float _UseDetailOwner, _OceanTileOwner;
            half4 _ShallowWater, _DeepWater, _RiverWater, _FoamColor;
            half4 _NearShallowWater, _NearDeepWater;
            float4 _RiverCarve, _RiverMotion;
            float _SphereRadius, _WaterKind, _UseWaterWaves, _UseRiverWaves, _FoamStrength, _Cull;
            float _Civ6WaterWorldScale, _Civ6WaterScrollSpeed, _Civ6WaterDeepStrength, _Civ6WaterCoastStrength;
            float _Civ6WaterSpecularExponent, _Civ6WaterF0, _Civ6WaterSunStrength, _Civ6WaterSkyStrength;
            float _Civ6WaterDeepDarkening, _Civ6WaterShallowDarkening, _Civ6WaterHeightTone;
            float _Civ6WaterShelfWidth, _Civ6WaterShelfStrength, _Civ6WaterFoamStrength, _Civ6WaterFoamWidth;
            float _Civ6WaterFoamSpeed, _Civ6WaterClarity, _Civ6WaterBedRelief, _Civ6WaterBedDetail;
            float _Civ6RiverWorldScale, _Civ6RiverScrollSpeed, _Civ6RiverBumpStrength, _Civ6RiverOpticalDepth;
            float _Civ6RiverDensityRange, _Civ6RiverDensityStrength, _Civ6RiverScatterTint, _Civ6RiverWaterDarkening;
            float _Civ6RiverF0, _Civ6RiverSpecularExponent, _Civ6RiverSunStrength, _Civ6RiverSkyStrength;
        CBUFFER_END
        #define WW2_SPHERICAL_WATER_OWNER 1
        #include "SphericalSurfaceLod.hlsl"
        #include "SphericalSatellite.hlsl"
        #include "SphericalGameplayPolitics.hlsl"
        #include "SphericalWaterOptics.hlsl"
        #include "SphericalRiverOptics.hlsl"
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 uv : TEXCOORD0;
            float4 waterData : TEXCOORD1;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float4 uv : TEXCOORD2;
            float4 waterData : TEXCOORD3;
            half4 color : COLOR;
            UNITY_VERTEX_OUTPUT_STEREO
        };
        Varyings WaterVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.color = input.color; output.uv = input.uv;
            output.waterData = input.waterData;
            return output;
        }
        // Adapted from the current HexWaterSurface / HexRiverSurface. Exported
        // height moments and gloss widths are reused; triplanar world gradients
        // replace the flat world-XZ normal. All phases are global, not a claim to
        // simulate a recovered source-engine downstream flow algorithm.
        void DecodeWave(float4 value, out float2 slope, out float variance)
        {
            slope = value.rg * 2 - 1; slope.y = -slope.y;
            variance = max(2 * value.b - dot(slope, slope), 0);
        }
        float4 WaveProjection(float2 world, float2 dx, float2 dy, float coast, out float height)
        {
            float2 slope0 = 0, slope1 = 0, slope2 = 0, slope3 = 0;
            float variance0 = 0, variance1 = 0, variance2 = 0, variance3 = 0;
            height = .5;
            [branch] if (_WaterKind > .5)
            {
                if (_UseRiverWaves < .5) return 0;
                float scale = max(_Civ6RiverWorldScale, .0001) * max(_RiverMotion.y,.1) / 2.4;
                float motion = max(_RiverMotion.x,0) / .72;
                float2 uv0 = world * scale + frac(float2(.91914503,.39391930) * (_Time.y * _Civ6RiverScrollSpeed * motion));
                float2 uv1 = world * scale * 1.31 + frac(float2(-.6,.8) * (_Time.y * _Civ6RiverScrollSpeed * motion * .73)) + float2(.37,.61);
                float4 a = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverWaveMoments, sampler_Civ6WaterDeep0, uv0, dx * scale, dy * scale);
                float4 b = SAMPLE_TEXTURE2D_GRAD(_Civ6RiverWaveMoments, sampler_Civ6WaterDeep0, uv1, dx * scale * 1.31, dy * scale * 1.31);
                DecodeWave(a, slope0, variance0); DecodeWave(b, slope1, variance1);
                float amplitude = max(_Civ6RiverBumpStrength, 0) * saturate(_RiverMotion.z) / .32;
                height = a.a * .6 + b.a * .4;
                return float4((slope0 * .6 + slope1 * .4) * amplitude,
                    (variance0 * .36 + variance1 * .16) * amplitude * amplitude, 0);
            }
            if (_UseWaterWaves < .5) return 0;
            float oceanScale = max(_Civ6WaterWorldScale, .0001);
            const float2 direction0 = float2(-.1391731,.9902681), direction1 = float2(.70710678,.70710678);
            float2 phase0 = frac(direction0 * (_Time.y * _Civ6WaterScrollSpeed));
            float2 phase1 = frac(direction1 * (_Time.y * _Civ6WaterScrollSpeed * 1.3));
            float2 phase2 = frac(direction1 * (_Time.y * _Civ6WaterScrollSpeed * .5));
            float2 uv = world * oceanScale, gx = dx * oceanScale, gy = dy * oceanScale;
            float4 d0 = SAMPLE_TEXTURE2D_GRAD(_Civ6WaterDeep0, sampler_Civ6WaterDeep0, uv + phase0, gx, gy);
            float4 d1 = SAMPLE_TEXTURE2D_GRAD(_Civ6WaterDeep1, sampler_Civ6WaterDeep0, uv + phase1, gx, gy);
            float4 c0 = SAMPLE_TEXTURE2D_GRAD(_Civ6WaterCoast0, sampler_Civ6WaterDeep0, uv + phase0, gx, gy);
            float4 c1 = SAMPLE_TEXTURE2D_GRAD(_Civ6WaterCoast1, sampler_Civ6WaterDeep0, uv + phase2, gx, gy);
            DecodeWave(d0, slope0, variance0); DecodeWave(d1, slope1, variance1);
            DecodeWave(c0, slope2, variance2); DecodeWave(c1, slope3, variance3);
            float deepAmplitude = (1 - coast) * max(_Civ6WaterDeepStrength, 0) * .5;
            float coastAmplitude = coast * max(_Civ6WaterCoastStrength, 0) * .5;
            float2 slope = (slope0 + slope1) * deepAmplitude + (slope2 + slope3) * coastAmplitude;
            float variance = (variance0 + variance1) * deepAmplitude * deepAmplitude + (variance2 + variance3) * coastAmplitude * coastAmplitude;
            height = lerp((d0.a + d1.a) * .5, (c0.a + c1.a) * .5, coast);
            return float4(slope, variance, 0);
        }
        void SphericalWaves(float3 position, float3 radial, float coast, out float3 normal, out float variance, out float height)
        {
            float3 p = position - _SphereCenter.xyz, dx = ddx(position), dy = ddy(position);
            float3 weights = pow(abs(radial), 4); weights /= max(dot(weights, 1), .00001);
            float3 gradient = 0; variance = 0; height = 0;
            float h;
            [branch] if (weights.x > .001)
            {
                float4 wave = WaveProjection(p.zy, dx.zy, dy.zy, coast, h);
                gradient += float3(0,wave.y,wave.x) * weights.x;
                variance += wave.z * weights.x * weights.x; height += h * weights.x;
            }
            [branch] if (weights.y > .001)
            {
                float4 wave = WaveProjection(p.xz, dx.xz, dy.xz, coast, h);
                gradient += float3(wave.x,0,wave.y) * weights.y;
                variance += wave.z * weights.y * weights.y; height += h * weights.y;
            }
            [branch] if (weights.z > .001)
            {
                float4 wave = WaveProjection(p.xy, dx.xy, dy.xy, coast, h);
                gradient += float3(wave.x,wave.y,0) * weights.z;
                variance += wave.z * weights.z * weights.z; height += h * weights.z;
            }
            gradient -= radial * dot(gradient, radial);
            normal = normalize(radial - gradient);
        }
        float WaterCoverage(Varyings input)
        {
            if (_WaterKind > .5)
            {
                float core = 1-smoothstep(_RiverCarve.x*.72,_RiverCarve.x+.65,abs(input.uv.z));
                float sea = smoothstep(.08,.32,input.waterData.x)*smoothstep(-.06,.06,input.uv.w);
                return core * (1-sea) * .96;
            }
            float feather = max(fwidth(input.uv.w)*3.2,.06);
            return smoothstep(-feather,feather,input.uv.w)*smoothstep(.02,.24,input.waterData.x);
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            half4 WaterFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                ClipSurfaceLod(input.positionWS, input.positionCS.xy, _WaterKind < .5 && _UseSatellite > .5);
                ClipSphericalOceanOwner(input.positionWS);
                ClipSatelliteOcean(_WaterKind);
                float3 radial = normalize(input.positionWS - _SphereCenter.xyz);
                float coast = saturate(input.color.a);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
                #else
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                Light sun = GetMainLight(shadowCoord);
                float satelliteWeight = _WaterKind < .5 ? saturate(_UseSatellite * SphericalStreamingSatelliteBlend(radial, _SatelliteBlend)) : 0;
                half3 satelliteColor = 0;
                float satelliteLand = 0, satelliteCoast = 0;
                [branch] if (satelliteWeight > .001)
                {
                    satelliteColor = SatelliteSurfaceColor(input.positionWS, radial, sun, satelliteLand, satelliteCoast);
                    // During the near/satellite crossfade, native ocean meshes
                    // can cover geographic satellite LAND. Match the terrain's
                    // complete political presentation there; an uncolored water
                    // pass otherwise repaints a polygon-shaped coastal fringe.
                    [branch] if (satelliteLand > .001)
                        satelliteColor = ApplySphericalGameplayPolitics(satelliteColor, radial,
                            satelliteLand, satelliteLand, satelliteCoast, 1);
                    else
                        satelliteColor = ApplySphericalGameWaterSelection(satelliteColor, radial);
                }
                [branch] if (satelliteWeight >= .999)
                    return half4(satelliteColor, 1);
                float coverage = WaterCoverage(input);
                clip(coverage - .001);
                SphericalWaterBed bed = (SphericalWaterBed)0;
                [branch] if (_WaterKind < .5)
                {
                    bed = EvaluateSphericalBed(input.positionWS,radial,input.waterData.y,max(input.uv.w,0),input.waterData.x);
                    coast = bed.coastWeight;
                }
                float3 normal; float variance, height;
                SphericalRiverMotionData riverMotion = (SphericalRiverMotionData)0;
                if (_WaterKind > .5)
                {
                    riverMotion = EvaluateSphericalRiverMotion(radial,input.uv.xy,input.waterData.zw,input.uv.z);
                    normal = riverMotion.normalWS; variance = riverMotion.variance; height = riverMotion.height;
                }
                else SphericalWaves(input.positionWS, radial, coast, normal, variance, height);
                float3 view = SafeNormalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                float3 halfDirection = SafeNormalize(view + sun.direction);
                float f0 = _WaterKind > .5 ? _Civ6RiverF0 : _Civ6WaterF0;
                float exponentBase = max(_WaterKind > .5 ? _Civ6RiverSpecularExponent : _Civ6WaterSpecularExponent, 32);
                if (_WaterKind > .5) exponentBase *= lerp(.2,1,saturate(_RiverMotion.w));
                float sunStrength = _WaterKind > .5 ? _Civ6RiverSunStrength : _Civ6WaterSunStrength;
                float skyStrength = _WaterKind > .5 ? _Civ6RiverSkyStrength : _Civ6WaterSkyStrength;
                float exponent = exponentBase / (1 + .5 * exponentBase * variance);
                float energyRatio = (exponent + 2) / (exponentBase + 2);
                float noL = saturate(dot(normal, sun.direction));
                float noV = saturate(dot(normal, view)), voH = saturate(dot(view, halfDirection));
                float fresnelSun = f0 + (1 - f0) * pow(1 - voH, 5);
                float fresnelSky = f0 + (1 - f0) * pow(1 - noV, 5);
                float glint = pow(saturate(dot(normal, halfDirection)), exponent) * energyRatio * ((exponentBase + 2) / (8 * PI)) * fresnelSun * noL;
                half3 albedo;
                [branch] if (_WaterKind > .5)
                {
                    float center = saturate(1-abs(input.uv.z)/max(_RiverCarve.x+.65,.1));
                    albedo = lerp(lerp(_RiverWater.rgb,_ShallowWater.rgb,.18),
                        _RiverWater.rgb*_Civ6RiverWaterDarkening,smoothstep(0,.9,center));
                    if (_UseRiverWaves > .5)
                    {
                        float depth = max(_Civ6RiverOpticalDepth, .01) * lerp(.1,1,center*center);
                        float lookup = saturate(depth / max(_Civ6RiverDensityRange, .01));
                        float4 density = SAMPLE_TEXTURE2D_LOD(_Civ6RiverDensity, sampler_linear_clamp, float2(lookup, 1), 0);
                        float4 scatter = SAMPLE_TEXTURE2D_LOD(_Civ6RiverScatter, sampler_linear_clamp, float2(lookup, 1), 0);
                        albedo *= lerp(1, exp(-max(density.rgb * density.a, 0) * depth * _Civ6RiverDensityStrength), .38);
                        float3 scatterColor = max(scatter.rgb * scatter.a, 0);
                        float luminance = dot(scatterColor, float3(.2126,.7152,.0722));
                        albedo *= lerp(1, luminance > .0001 ? clamp(scatterColor / luminance, .7, 1.3) : float3(1,1,1), _Civ6RiverScatterTint);
                    }
                    albedo *= 1 + (height - .5) * .15;
                }
                else
                {
                    albedo = ShadeSphericalBed(bed,view,normal,height,sun,input.color.rgb);
                }
                half3 illumination = SphericalAmbient(normal,radial) * .62h + half3(.065,.078,.095) + sun.color * (.06 + .94 * noL) * sun.shadowAttenuation;
                half3 color;
                [branch] if (_WaterKind > .5)
                {
                    color = albedo * min(illumination,1.2);
                    color += sun.color * glint * sunStrength * sun.shadowAttenuation;
                    color += half3(.045,.09,.14) * fresnelSky * skyStrength;
                    half3 flowLight = clamp(SphericalAmbient(radial,radial)*.55h +
                        sun.color*(.30h+.60h*saturate(dot(radial,sun.direction)))*sun.shadowAttenuation,.48h,1.08h);
                    color = lerp(color,half3(.74,.87,.87)*flowLight,riverMotion.foam);
                }
                else
                {
                    // The shared flat-water optical model already lights its
                    // bottom and scattering. A second land diffuse multiply
                    // would muddy clear shallows and hide the authored bed.
                    color = lerp(albedo,half3(.065,.105,.175),saturate(fresnelSky*skyStrength));
                    color += sun.color*glint*sunStrength*lerp(1,.65,bed.coastWeight);
                    float foam = SphericalShoreFoam(input.positionWS,radial,input.uv.w,input.waterData.zw,coverage,height);
                    half3 foamLight = clamp(SphericalAmbient(radial,radial)*.55h +
                        sun.color*(.30h+.60h*saturate(dot(radial,sun.direction)))*sun.shadowAttenuation,.48h,1.08h);
                    half3 foamColor = lerp(half3(.90,.96,.97),_FoamColor.rgb,.24)*foamLight;
                    color = lerp(color,foamColor,foam);
                }
                // Each endpoint includes interaction once, before interpolation.
                // The satellite land endpoint already has its country wash.
                color = lerp(ApplySphericalGameWaterSelection(color, radial), satelliteColor, satelliteWeight);
                return half4(color,lerp(coverage,1,satelliteWeight));
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            half4 DepthFragment(Varyings input) : SV_Target { ClipSurfaceLod(input.positionWS, input.positionCS.xy, _WaterKind < .5 && _UseSatellite > .5); ClipSphericalOceanOwner(input.positionWS); ClipSatelliteOcean(_WaterKind); clip(WaterCoverage(input)-.999); return input.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment NormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            half4 NormalsFragment(Varyings input) : SV_Target
            {
                ClipSurfaceLod(input.positionWS, input.positionCS.xy, _WaterKind < .5 && _UseSatellite > .5);
                ClipSphericalOceanOwner(input.positionWS);
                ClipSatelliteOcean(_WaterKind);
                clip(WaterCoverage(input)-.999);
                float3 normal = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    return half4(PackFloat2To888(saturate(PackNormalOctQuadEncode(normal) * .5 + .5)), 0);
                #else
                    return half4(normal, 0);
                #endif
            }
            ENDHLSL
        }
    }
    FallBack Off
}
