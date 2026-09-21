Shader "WW2/Spherical Terrain Preview/Earth Atmosphere"
{
    Properties
    {
        [NoScaleOffset] _CloudMap ("Earth cloud coverage", 2D) = "black" {}
        _SphereCenter ("Earth center", Vector) = (0,0,0,0)
        _PlanetRadius ("Earth radius", Float) = 3300
        _LayerKind ("Clouds 0, atmospheric shell 1", Float) = 0
        [HideInInspector] _DstBlend ("Destination blend", Float) = 10
        _Visibility ("Far presentation visibility", Range(0,1)) = 0
        _CloudDetail ("Cloud detail clarity", Range(0,.4)) = .20
        _CirrusStrength ("Observed thin cloud detail", Range(0,.2)) = .08
        _CloudMotion ("Real-time angular wind / slow evolution", Vector) = (0,0,0,0)
        _FarEarthGrade ("Far saturation: land, ocean, atmosphere, blend", Vector) = (1.24,1.18,1.20,0)
        _UseNaturalSatellite ("Natural satellite atmospheric palette", Float) = 0
        _NaturalAtmosphereTint ("Shared natural satellite atmosphere", Color) = (.4,.62,.74,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+10" "RenderType"="Transparent" }
        Pass
        {
            Name "GeographicAtmosphere"
            Tags { "LightMode"="UniversalForward" }
            Blend One [_DstBlend]
            ZWrite Off ZTest LEqual Cull Back
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_CloudMap); SAMPLER(sampler_CloudMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _SphereCenter, _CloudMotion, _FarEarthGrade;
                float _PlanetRadius, _LayerKind, _Visibility, _UseNaturalSatellite;
                float _CloudDetail, _CirrusStrength;
                float4 _NaturalAtmosphereTint;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                if (_LayerKind < .5)
                {
                    // Clearance is needed over exaggerated near relief only.
                    // Satellite clouds sit close to the actual planet limb.
                    float3 fromCenter = output.positionWS - _SphereCenter.xyz;
                    float height = 10;
                    output.positionWS = _SphereCenter.xyz + normalize(fromCenter) *
                        lerp(length(fromCenter), _PlanetRadius + height, _CloudMotion.z);
                }
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }
            float2 CloudUV(float3 radial)
            {
                float lon = atan2(radial.z, radial.x);
                float lat = asin(clamp(radial.y, -1, 1));
                // Preserve the satellite weather silhouettes. Small latitude-
                // dependent shear animates fronts without stretching the poles.
                lon += _CloudMotion.x;
                lon += sin(lat * 5 + _CloudMotion.y) * .003 * cos(lat);
                return float2(lon / (2 * PI) + .5, lat / PI + .5);
            }
            float Coverage(float2 uv, float2 dx, float2 dy)
            {
                return SAMPLE_TEXTURE2D_GRAD(_CloudMap, sampler_CloudMap, uv, dx, dy).r;
            }
            half4 Frag(Varyings input):SV_Target
            {
                float3 radial = normalize(input.positionWS - _SphereCenter.xyz);
                float3 view = SafeNormalize(_WorldSpaceCameraPos.xyz - input.positionWS);
                Light sun = GetMainLight();
                float daylight = smoothstep(-.18, .28, dot(radial, sun.direction));
                float facing = saturate(dot(radial, view));
                [branch] if (_LayerKind > .5 && _LayerKind < 1.5)
                {
                    // Density is centred on the planet silhouette, NOT the
                    // larger proxy shell silhouette (which made a second rim).
                    float impact = length(cross(input.positionWS - _SphereCenter.xyz, view));
                    float outside = max(0, impact - _PlanetRadius) / 22;
                    float inside = max(0, _PlanetRadius - impact) / 55;
                    float rim = exp2(-outside * outside - inside * inside);
                    float feather = smoothstep(0, .12, facing);
                    half3 tint = half3(.055h, .24h, .59h);
                    tint = lerp(tint, _NaturalAtmosphereTint.rgb, step(.5, _UseNaturalSatellite));
                    half luminance = dot(tint, half3(.2126h, .7152h, .0722h));
                    tint = lerp(tint, max(0, lerp(luminance.xxx, tint, _FarEarthGrade.z)), _FarEarthGrade.w);
                    float alpha = rim * feather * _Visibility * .42 * lerp(.18, 1, daylight);
                    return half4(tint * alpha, alpha);
                }
                // One weather layer: duplicated cirrus and displaced shadows
                // from the same map read as ghost copies over the ocean.
                float2 uv = CloudUV(radial);
                float2 dx = ddx(uv), dy = ddy(uv);
                dx.x -= round(dx.x); dy.x -= round(dy.x);
                // Detail enhancement uses concentric filter footprints at the
                // SAME geographic point, not offset silhouettes or bump edges.
                dx *= 1.25; dy *= 1.25;
                float fine = Coverage(uv, dx, dy);
                float body = Coverage(uv, dx*3, dy*3);
                float coverage = saturate(fine + (fine-body) * _CloudDetail);
                float density = pow(smoothstep(.11,.98,coverage), 1.30);
                // Restore photographed translucent fibres in place. Thin and
                // thick optical depths are composited once, so even evolving
                // weather cannot produce a displaced second silhouette.
                float cirrus = smoothstep(.035,.18,coverage) *
                    (1-smoothstep(.20,.48,coverage)) * _CirrusStrength;
                float opticalDepth = density * 2.5 + cirrus;
                float alpha = (1-exp(-opticalDepth)) * _Visibility * .82 * smoothstep(.01,.12,facing);
                // Satellite coverage is not a height map. Differentiating every
                // bright fleck made false embossed edges and a doubled texture.
                float diffuse = saturate(dot(radial, sun.direction));
                half3 illumination = half3(.14h,.16h,.20h) + sun.color * (.30 + diffuse * .64) * daylight;
                // Broad thickness shading gives storm interiors depth without
                // embossing each tiny bright fleck or projecting a ghost copy.
                float thickness = smoothstep(.28,.86,body);
                half3 cloud = illumination * lerp(1.03h,.88h,thickness);
                return half4(cloud * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
