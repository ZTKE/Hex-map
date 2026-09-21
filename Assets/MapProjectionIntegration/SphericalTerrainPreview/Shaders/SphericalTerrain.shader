Shader "WW2/Spherical Terrain Preview/Terrain"
{
    Properties
    {
        [NoScaleOffset] _Albedos ("Existing near albedo atlas", 2DArray) = "white" {}
        [NoScaleOffset] _Shapes ("Existing near shape atlas", 2DArray) = "black" {}
        [NoScaleOffset] _LandResponse ("Existing material responses", 2DArray) = "black" {}
        [NoScaleOffset] _LandMasks ("Existing authored material IDs", 2DArray) = "black" {}
        _SphereCenter ("Sphere center in world space", Vector) = (0,0,0,0)
        _SphereRadius ("Sphere radius", Float) = 3300
        _DetailFocus ("Ready detail focus, unit direction", Vector) = (0,1,0,0)
        _DetailRadius ("Ready detail chord radius", Float) = 0
        _DetailBlendWidth ("Detail blend width", Float) = 20
        _SurfaceLod ("Surface LOD: detail 0, shell 1, bypass 2", Float) = 2
        [NoScaleOffset] _DetailCoverage ("Published chunk coverage", 2D) = "black" {}
        _UseDetailCoverage ("Use published chunk coverage", Float) = 0
        _CoverageFocus ("Coverage region radial focus", Vector) = (0,1,0,0)
        _CoverageEast ("Coverage region tangent east", Vector) = (1,0,0,0)
        _CoverageNorth ("Coverage region tangent north", Vector) = (0,0,1,0)
        _CoverageWorldSize ("Coverage region full width", Float) = 900
        [NoScaleOffset] _SatelliteColor ("Existing offline satellite color", 2D) = "white" {}
        [NoScaleOffset] _SatelliteRelief ("Existing offline ETOPO normals and elevation", 2D) = "gray" {}
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
        _MaterialTiling ("Existing near material scale", Float) = .065
        _UseResponse ("Material response available", Float) = 0
        _UseMasks ("Material masks available", Float) = 0
        _LandParameters ("Normal, blend, macro, macro scale", Vector) = (.3,.14,.18,.19)
        _LandShading ("Roughness min/max, specular", Vector) = (.38,.96,.12,0)
        _MountainHeight ("Mountain height", Float) = 7.5
        _DesertMountainHeight ("Desert mountain height", Float) = 7.2
        _MountainGeometryScale ("Authored to displayed mountain height", Float) = .94
        _PlateauHeight ("Single regional platform height", Float) = 4.32
        _PlateauWeathering ("Highland weathering", Range(0,1)) = .65
        // These originate from raw Shader.SetGlobalColor values in the flat
        // map. Color properties would apply a second conversion on GPU upload.
        _DrySand ("Existing dry sand (raw global)", Vector) = (.78,.63,.36,1)
        [NoScaleOffset] _CoastAlbedo ("Source coast soil and sand", 2D) = "white" {}
        [NoScaleOffset] _CoastHeight ("Source coast microrelief", 2D) = "gray" {}
        [NoScaleOffset] _CliffAlbedo ("Source exposed coast rock", 2D) = "white" {}
        [NoScaleOffset] _CliffHeight ("Source exposed coast rock microrelief", 2D) = "gray" {}
        _CoastMaterialScale ("Coast material scale", Float) = .18
        _WetSand ("Existing wet sand (raw global)", Vector) = (.45,.34,.2,1)
        _ShallowWater ("Existing shallow floor grading", Vector) = (.24,.62,.66,1)
        _DeepWater ("Existing deep floor grading", Vector) = (.035,.12,.23,1)
        _RiverBank ("Existing river bank (raw global)", Vector) = (.43,.32,.18,1)
        [NoScaleOffset] _Civ6RiverBankAlbedo ("Existing authored river bank", 2D) = "white" {}
        _Civ6RiverBankTextureStrength ("Existing river bank grain", Range(0,1)) = .32
        _Civ6RiverWorldScale ("Existing river texture scale", Float) = .18
        _RiverCarve ("Existing river core / shoulder in map units", Vector) = (.7,3.1,0,0)
        _BiomeTintStrength ("Optional regional tint", Range(0,1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Cull [_Cull]
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "SphericalLighting.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _SphereCenter;
            float4 _DetailFocus;
            float4 _LandParameters, _LandShading;
            float4 _RiverCarve;
            float _Civ6RiverBankTextureStrength, _Civ6RiverWorldScale;
            float _CoastMaterialScale;
            half4 _DrySand, _WetSand, _RiverBank, _ShallowWater, _DeepWater;
            float _SphereRadius, _MaterialTiling, _UseResponse, _UseMasks;
            float _MountainHeight, _DesertMountainHeight, _PlateauWeathering, _BiomeTintStrength, _Cull;
            float _MountainGeometryScale, _PlateauHeight;
            float _DetailRadius, _DetailBlendWidth, _SurfaceLod;
            float _UseDetailCoverage, _UseSatellite, _SatelliteBlend;
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
        CBUFFER_END
        #define WW2_SPHERICAL_TERRAIN_SCREEN_LOD 1
        #include "SphericalSurfaceLod.hlsl"
        #include "SphericalSatellite.hlsl"
        #include "SphericalGameplayPolitics.hlsl"
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 stamp : TEXCOORD0;
            float4 terrain : TEXCOORD1;
            float4 masks : TEXCOORD2;
            float4 biomeWeights : TEXCOORD3;
            float4 upperBiomeWeights : TEXCOORD4;
            float4 desertMaterial : TEXCOORD5;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float4 stamp : TEXCOORD2;
            float4 terrain : TEXCOORD3;
            float4 masks : TEXCOORD4;
            float4 biomeWeights : TEXCOORD5;
            float4 upperBiomeWeights : TEXCOORD6;
            float4 desertMaterial : TEXCOORD7;
            half4 color : COLOR;
            UNITY_VERTEX_OUTPUT_STEREO
        };
        Varyings TerrainVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.stamp = input.stamp; output.terrain = input.terrain;
            output.masks = input.masks; output.color = input.color;
            output.biomeWeights = input.biomeWeights;
            output.upperBiomeWeights = input.upperBiomeWeights;
            output.desertMaterial = input.desertMaterial;
            return output;
        }
        float4 SphericalShadowCoord(float3 positionWS)
        {
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                return ComputeScreenPos(TransformWorldToHClip(positionWS));
            #else
                return TransformWorldToShadowCoord(positionWS);
            #endif
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainVertex
            #pragma fragment TerrainFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "SphericalTerrainMaterial.hlsl"
            half4 TerrainFragment(Varyings input, out uint sampleCoverage : SV_Coverage) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                ClipSphericalTerrainLod(input.positionWS, input.positionCS.xy, false, sampleCoverage);
                float3 radialUp = normalize(input.positionWS - _SphereCenter.xyz);
                // The flat renderer obtains face normals after dense GPU
                // tessellation. Our streamed CPU mesh has larger triangles;
                // using their screen derivatives makes a visible checkerboard.
                // Shared-topology normals carry the continuous large-scale
                // shape; authored micro/macro gradients supply rock detail.
                float3 geometryNormal = normalize(input.normalWS);
                Light sun = GetMainLight(SphericalShadowCoord(input.positionWS));
                float satelliteWeight = saturate(_UseSatellite * SphericalStreamingSatelliteBlend(radialUp, _SatelliteBlend));
                half3 satelliteColor = 0;
                float satelliteLand = 0, satelliteCoast = 0;
                [branch] if (satelliteWeight > .001)
                    satelliteColor = SatelliteSurfaceColor(input.positionWS, radialUp, sun, satelliteLand, satelliteCoast);
                // Mid/far terrain skips the many source-atlas texture samples;
                // the natural satellite surface needs only two geographic reads.
                [branch] if (satelliteWeight >= .999)
                    return half4(ApplySphericalGameplayPolitics(satelliteColor, radialUp, satelliteLand, satelliteLand, satelliteCoast, satelliteWeight), 1);
                LandSample land = EvaluateLand(input.positionWS, geometryNormal, radialUp, input.stamp, input.terrain, input.masks,
                    input.biomeWeights, input.upperBiomeWeights, input.desertMaterial, saturate(input.color.a));
                float3 gradient = land.gradient - geometryNormal * dot(geometryNormal, land.gradient);
                gradient *= _LandParameters.x;
                gradient *= rsqrt(max(dot(gradient, gradient) / 4, 1));
                float gentle = smoothstep(.55, .94, dot(geometryNormal, radialUp)) *
                    smoothstep(.20, .38, input.terrain.w * .24) * (1 - smoothstep(.03, .22, 1 - input.color.a));
                // Keep source grain restrained on gentle ground; steep shoulders
                // retain their continuous geometric normal and triplanar detail.
                float3 detailNormal = normalize(geometryNormal - gradient);
                float3 normal = normalize(lerp(geometryNormal, detailNormal, lerp(1, .55, gentle)));
                normal = normalize(lerp(normal,radialUp,.36*gentle));
                float diffuse = saturate(dot(normal, sun.direction));
                // Match Game_2: the water pass owns submerged shadows and
                // reflections. A second land shadow produces a dark coast rim.
                float submerged = 1-smoothstep(-.018,.004,length(input.positionWS-_SphereCenter.xyz)-_SphereRadius);
                sun.shadowAttenuation = lerp(sun.shadowAttenuation,1,submerged);
                half3 ambient = SphericalAmbient(normal,radialUp) * .62h + half3(.065h,.078h,.095h);
                half3 lighting = ambient + sun.color * (.06h + .94h * diffuse) * sun.shadowAttenuation;
                half3 albedo = lerp(land.albedo, land.albedo * input.color.rgb, _BiomeTintStrength);
                half3 color = albedo * min(lighting, 1.2h);
                color += LandSpecular(land, normal, SafeNormalize(_WorldSpaceCameraPos.xyz - input.positionWS), sun) * (1-submerged);
                color = lerp(color, satelliteColor, satelliteWeight);
                return half4(ApplySphericalGameplayPolitics(color, radialUp, input.color.a, satelliteLand, satelliteCoast, satelliteWeight), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 _LightDirection;
            float3 _LightPosition;
            Varyings ShadowVertex(Attributes input)
            {
                Varyings output = TerrainVertex(input);
                float3 p = output.positionWS;
                float3 normal = output.normalWS;
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 direction = normalize(_LightPosition - p);
                #else
                    float3 direction = _LightDirection;
                #endif
                float4 clip = TransformWorldToHClip(ApplyShadowBias(p, normal, direction));
                #if UNITY_REVERSED_Z
                    clip.z = min(clip.z, UNITY_NEAR_CLIP_VALUE * clip.w);
                #else
                    clip.z = max(clip.z, UNITY_NEAR_CLIP_VALUE * clip.w);
                #endif
                output.positionCS = clip;
                return output;
            }
            half4 DepthFragment(Varyings input) : SV_Target { ClipSurfaceLod(input.positionWS, input.positionCS.xy); return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            half4 DepthFragment(Varyings input, out uint sampleCoverage : SV_Coverage) : SV_Target
            { ClipSphericalTerrainLod(input.positionWS, input.positionCS.xy, true, sampleCoverage); return input.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex TerrainVertex
            #pragma fragment NormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            half4 NormalsFragment(Varyings input, out uint sampleCoverage : SV_Coverage) : SV_Target
            {
                ClipSphericalTerrainLod(input.positionWS, input.positionCS.xy, true, sampleCoverage);
                float3 normal = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct = PackNormalOctQuadEncode(normal);
                    return half4(PackFloat2To888(saturate(oct * .5 + .5)), 0);
                #else
                    return half4(normal, 0);
                #endif
            }
            ENDHLSL
        }
        Pass
        {
            Name "LodAvailability"
            Tags { "LightMode"="SphericalLodAvailability" }
            ZWrite Off ZTest Always Cull Back
            Blend One One
            BlendOp Max
            ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex AvailabilityVertex
            #pragma fragment AvailabilityFragment
            #pragma multi_compile_instancing
            float4 AvailabilityVertex(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return TransformWorldToHClip(TransformObjectToWorld(input.positionOS.xyz));
            }
            half4 AvailabilityFragment() : SV_Target
            {
                // Deliberately no LOD clip and no depth ordering: R/G indicate
                // actual fine/coarse raster coverage, not which surface is nearer.
                return _SurfaceLod < .5 ? half4(1,0,0,0) : half4(0,1,0,0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
