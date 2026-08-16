Shader "Hex Map/World Overview"
{
	Properties
	{
		[NoScaleOffset] _MainTex ("Political atlas (RGB country, A land)", 2D) = "white" {}
		[NoScaleOffset] _ReliefTex ("Logical relief", 2D) = "black" {}
		_OceanTint ("Strategic ocean tint", Color) = (0.78, 0.84, 0.86, 1)
		_CountrySaturation ("Country saturation", Range(0, 1)) = 0.62
		_CountryStrength ("Country paper wash", Range(0, 1)) = 0.78
		_PaperGrain ("Paper grain", Range(0, 0.25)) = 0.10
		_ReliefStrength ("Logical relief", Range(0, 0.4)) = 0.20
		_Brightness ("Strategic brightness", Range(0.5, 1.2)) = 1.05
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Opaque"
			"Queue" = "Geometry-10"
			"RenderPipeline" = "UniversalPipeline"
		}

		Pass
		{
			Name "StrategicPoliticalSurface"
			Tags { "LightMode" = "UniversalForward" }
			Cull Back
			ZWrite On

			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
			TEXTURE2D(_ReliefTex);
			SAMPLER(sampler_ReliefTex);
			float4 _MainTex_TexelSize;
			float4 _ReliefTex_TexelSize;

			CBUFFER_START(UnityPerMaterial)
				half4 _OceanTint;
				half _CountrySaturation;
				half _CountryStrength;
				half _PaperGrain;
				half _ReliefStrength;
				half _Brightness;
			CBUFFER_END

			struct Attributes
			{
				float4 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				output.uv = input.uv;
				return output;
			}

			float Hash21(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * 0.1031);
				p3 += dot(p3, p3.yzx + 33.33);
				return frac((p3.x + p3.y) * p3.z);
			}

			float SoftNoise(float2 p)
			{
				float2 i = floor(p);
				float2 f = frac(p);
				f = f * f * (3.0 - 2.0 * f);
				return lerp(
					lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x),
					lerp(Hash21(i + float2(0, 1)), Hash21(i + 1), f.x),
					f.y);
			}

			float FarPaperGrain(float2 mapPosition)
			{
				float g0 = SoftNoise(mapPosition * 0.025);
				float g1 = SoftNoise(mapPosition * 0.075 + 2.1);
				float g2 = SoftNoise(mapPosition * 0.19 + 5.7);
				float g3 = SoftNoise(mapPosition * 0.48 + 11.3);
				return g0 * 0.38 + g1 * 0.32 + g2 * 0.20 + g3 * 0.10;
			}

			half3 EvaluateOcean(float2 mapPosition)
			{
				half3 vellum = half3(0.92h, 0.88h, 0.78h);
				half3 seaWash = half3(0.72h, 0.80h, 0.84h);
				half3 baseColor = lerp(
					vellum,
					saturate(_OceanTint.rgb * 0.55h + vellum * 0.55h),
					0.35h);
				float grain = FarPaperGrain(mapPosition);
				float wash = SoftNoise(mapPosition * 0.031 + 1.3);
				float fiber = SoftNoise(
					mapPosition * float2(0.32, 0.09) + 8.0);
				half3 color = lerp(baseColor, seaWash, wash * 0.28h);
				color *= 1.0h + (grain - 0.5h) * _PaperGrain;
				color = lerp(
					color, color * half3(1.04h, 1.02h, 0.98h),
					fiber * 0.12h);
				return saturate(color);
			}

			half3 EvaluateLand(
				half3 countryColor, float2 mapPosition, half vegetation)
			{
				half3 paper = half3(0.94h, 0.90h, 0.80h);
				half luminance = dot(
					countryColor, half3(0.299h, 0.587h, 0.114h));
				half3 muted = lerp(
					luminance.xxx, countryColor, _CountrySaturation);
				muted = lerp(muted, muted * paper, 0.28h);
				half3 color = lerp(
					paper * 0.96h, muted, _CountryStrength);

				float grain = FarPaperGrain(mapPosition);
				float fiber = SoftNoise(
					mapPosition * float2(0.32, 0.09) + 3.3);
				color *= 1.0h + (grain - 0.5h) * _PaperGrain;
				color = lerp(
					color, color * half3(1.03h, 1.01h, 0.97h),
					fiber * 0.10h);
				color *= lerp(1.0h, 0.975h, vegetation * 0.45h);
				return saturate(color);
			}

			half EvaluateRelief(
				float2 uv, float2 mapPosition, half reliefHeight)
			{
				float2 texel = _ReliefTex_TexelSize.xy * 1.35;
				half left = SAMPLE_TEXTURE2D_LOD(
					_ReliefTex, sampler_ReliefTex,
					uv - float2(texel.x, 0), 0).r;
				half right = SAMPLE_TEXTURE2D_LOD(
					_ReliefTex, sampler_ReliefTex,
					uv + float2(texel.x, 0), 0).r;
				half down = SAMPLE_TEXTURE2D_LOD(
					_ReliefTex, sampler_ReliefTex,
					uv - float2(0, texel.y), 0).r;
				half up = SAMPLE_TEXTURE2D_LOD(
					_ReliefTex, sampler_ReliefTex,
					uv + float2(0, texel.y), 0).r;

				float3 normal = normalize(float3(
					(left - right) * 3.2,
					0.72,
					(down - up) * 3.2));
				float3 lightDirection = normalize(float3(-0.42, 0.78, 0.46));
				half directional = saturate(dot(normal, lightDirection));
				half reliefMask = smoothstep(0.22h, 0.86h, reliefHeight);
				half broad = SoftNoise(mapPosition * 0.16 + 19.7) - 0.5h;
				half shade = lerp(0.89h, 1.10h, directional);
				shade *= 1.0h + broad * reliefMask * 0.075h;
				return lerp(1.0h, shade, reliefMask * _ReliefStrength * 3.2h);
			}

			half4 Frag(Varyings input) : SV_Target
			{
				half4 atlas = SAMPLE_TEXTURE2D(
					_MainTex, sampler_MainTex, input.uv);
				half4 surface = SAMPLE_TEXTURE2D(
					_ReliefTex, sampler_ReliefTex, input.uv);
				float2 mapPosition = input.uv *
					max(_MainTex_TexelSize.zw * 0.5, float2(1.0, 1.0));

				half edgeWidth = max(fwidth(atlas.a) * 0.72h, 0.012h);
				half landCoverage = smoothstep(
					0.5h - edgeWidth, 0.5h + edgeWidth, atlas.a);
				half3 ocean = EvaluateOcean(mapPosition);
				half3 land = EvaluateLand(
					atlas.rgb, mapPosition, surface.g);
				land *= EvaluateRelief(
					input.uv, mapPosition, surface.r);
				half3 color = lerp(ocean, land, landCoverage);
				return half4(saturate(color * _Brightness), 1.0h);
			}
			ENDHLSL
		}
	}
}
