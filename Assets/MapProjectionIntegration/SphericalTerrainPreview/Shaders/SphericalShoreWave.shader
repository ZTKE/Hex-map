Shader "WW2/Spherical Terrain Preview/Shore Wave"
{
    Properties
    {
        [NoScaleOffset] _CrestAtlas ("Original Civ6 wave crest atlas, 8 by 2", 2D) = "black" {}
        [NoScaleOffset] _WaveAux ("Original Civ6 auxiliary wave texture", 2D) = "gray" {}
        _SphereCenter ("Sphere center", Vector) = (0,0,0,0)
        _SphereRadius ("Sphere radius", Float) = 3300
        _DetailFocus ("Ready detail focus", Vector) = (0,1,0,0)
        _DetailRadius ("Ready detail radius", Float) = 0
        _DetailBlendWidth ("Detail blend width", Float) = 20
        _SurfaceLod ("Detail surface", Float) = 0
        [NoScaleOffset] _DetailCoverage ("Published chunk coverage", 2D) = "black" {}
        _UseDetailCoverage ("Use published coverage", Float) = 0
        _CoverageFocus ("Coverage radial focus", Vector) = (0,1,0,0)
        _CoverageEast ("Coverage tangent east", Vector) = (1,0,0,0)
        _CoverageNorth ("Coverage tangent north", Vector) = (0,0,1,0)
        _CoverageWorldSize ("Coverage region width", Float) = 900
        _SatelliteBlend ("Satellite art blend", Range(0,1)) = 0
        _WaveOpacity ("Linear source crest and restrained wash opacity", Range(0,1)) = .90
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent+10" }
        Pass
        {
            Name "ShoreCrests"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off ZTest LEqual Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SphericalLighting.hlsl"
            TEXTURE2D(_CrestAtlas); SAMPLER(sampler_CrestAtlas);
            TEXTURE2D(_WaveAux); SAMPLER(sampler_WaveAux);
            CBUFFER_START(UnityPerMaterial)
                float4 _SphereCenter, _DetailFocus;
                float _SphereRadius, _DetailRadius, _DetailBlendWidth, _SurfaceLod, _UseDetailCoverage;
                float4 _CoverageFocus, _CoverageEast, _CoverageNorth;
                float _CoverageWorldSize, _SatelliteBlend, _WaveOpacity;
            CBUFFER_END
            #include "SphericalSurfaceLod.hlsl"
            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 uv : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv;
                return output;
            }
            float CrestMask(float page, float2 uv, float2 gx, float2 gy)
            {
                // Never sample an adjacent atlas page at a clamped endpoint.
                float2 halfTexel = .5 / float2(128,512);
                float2 localUV = clamp(uv,halfTexel,1-halfTexel);
                float2 atlasUV = (float2(fmod(page,8),floor(page/8)) + localUV)/float2(8,2);
                return SAMPLE_TEXTURE2D_GRAD(_CrestAtlas,sampler_CrestAtlas,atlasUV,gx,gy).r;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                ClipSurfaceLod(input.positionWS,input.positionCS.xy);
                float seed = input.uv.w;
                float duration = lerp(15,24,frac(seed*17.31));
                float delay = frac(seed*31.73)*2.5;
                float cycle = frac((_Time.y+seed*47.17)/(duration+delay))*(duration+delay)/duration;
                float life = smoothstep(0,.13,cycle)*(1-smoothstep(.78,1,cycle));
                float scale = lerp(.75,1,frac(seed*53.19));
                float breaking = smoothstep(.18,.68,cycle);
                float travel = 1-pow(1-saturate(cycle),1.2);
                float crestDistance = lerp(3.4,.12,travel)*scale;
                float width = lerp(2.6,4,breaking)*scale;
                float2 auxUV = float2(input.uv.x*.34+_Time.y*.024,input.uv.y*.23+seed*11);
                float auxiliary = SAMPLE_TEXTURE2D(_WaveAux,sampler_WaveAux,auxUV).r;
                float bend = SAMPLE_TEXTURE2D(_WaveAux,sampler_WaveAux,
                    float2(input.uv.y*.11+seed*7,_Time.y*.015+seed*3)).r-.5;
                float u = (input.uv.x-crestDistance-bend*.15)/width+.16;
                float v = input.uv.y/max(input.uv.z,.01);
                float endFade = smoothstep(0,.22,v)*(1-smoothstep(.78,1,v));
                // Fade the authored streaks before their far UV boundary. The
                // existing source-noise bend staggers the soft tail along V,
                // so neighboring streaks do not end at one straight edge.
                float tailEnd = .82 + bend*.16;
                float border = smoothstep(0,.05,u)*(1-smoothstep(.43,tailEnd,u));
                float page = min(floor(seed*16),15);
                // The source is 16 independent crest silhouettes, not an animation
                // flipbook. Alongshore is V; its fine streaks trail offshore in +U.
                float2 gx = ddx(float2(u,v))/float2(8,2), gy = ddy(float2(u,v))/float2(8,2);
                float intensity = CrestMask(page,float2(u,v),gx,gy);
                // A narrow, three-tap reconstruction gives the original bright
                // crest a soft physical width instead of a single white pixel.
                float spread = lerp(.008,.018,breaking);
                float shoulder = max(CrestMask(page,float2(u-spread,v),gx,gy),
                    CrestMask(page,float2(u+spread,v),gx,gy));
                float crest = max(intensity,shoulder*.72);
                // Keep only a faint residual wash from the offset source page.
                // Its bright ridge is masked out, so this cannot become a
                // second parallel crest line.
                float washU = .16 + (u-.16)*.78;
                float wash = CrestMask(fmod(page+7,16),float2(washU,1-v),gx*float2(.78,-1),gy*float2(.78,-1));
                // Across all 16 pages the strongest authored ridge stays below
                // U=.422. Residual wash begins beyond that measured range.
                wash *= smoothstep(.44,.58,washU)*(1-smoothstep(.82,1,washU));
                // Both inputs use the local data-linear import contract. Avoid a
                // sub-unity exponent that lifts low RGB streaks into a broad
                // white brush footprint; retain the original brighter ridge.
                float whitewater = pow(saturate(crest),1.12) + pow(saturate(wash),1.25)*breaking*.12;
                // Atlas alpha is almost opaque even on black: RGB alone is the
                // authored crest mask. Auxiliary detail breaks its brightness,
                // without introducing a mathematical bubble/porous mask.
                float breakup = lerp(.38,1,smoothstep(.16,.78,auxiliary));
                float alpha = saturate(whitewater*1.22)*breakup*life*endFade*border*_WaveOpacity;
                alpha *= 1-smoothstep(.05,.7,_SatelliteBlend);
                clip(alpha-.002);
                float3 radial = normalize(input.positionWS-_SphereCenter.xyz);
                Light sun = GetMainLight();
                half3 light = clamp(SphericalAmbient(radial,radial)*.55h +
                    sun.color*(.3h+.6h*saturate(dot(radial,sun.direction))),.48h,1.08h);
                return half4(half3(.90,.96,.97)*light,alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
