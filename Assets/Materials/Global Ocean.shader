Shader "Hex Map/Global Ocean"
{
	Properties
	{
		[NoScaleOffset] _OceanNoise ("Ocean noise", 2D) = "gray" {}
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "TransparentCutout"
			"Queue" = "AlphaTest+30"
			"RenderPipeline" = "UniversalPipeline"
		}

		Pass
		{
			Name "ContinuousOcean"
			Tags { "LightMode" = "UniversalForward" }
			Cull Back
			ZWrite On
			ZTest LEqual
			AlphaToMask On

			HLSLPROGRAM
			#pragma target 4.6
			#pragma require 2darray
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile_fog

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "HexCellData.hlsl"

			SAMPLER(sampler_linear_clamp);
			SAMPLER(sampler_point_clamp);
			#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
			#define HF_TERRAIN_POINT_SAMPLER sampler_point_clamp
			#include "HexTerrainShape.hlsl"

			TEXTURE2D(_OceanNoise);
			SAMPLER(sampler_OceanNoise);

			half4 _HexDeepOceanColor;
			half4 _HexShallowWaterColor;
			half4 _HexShoreFoamColor;
			half4 _HexRiverWaterColor;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float2 mapUV : TEXCOORD1;
				half fogFactor : TEXCOORD2;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				VertexPositionInputs positionInputs =
					GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = positionInputs.positionCS;
				output.positionWS = positionInputs.positionWS;
				output.mapUV = input.uv;
				output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
				return output;
			}

			struct OceanPattern
			{
				half basinTone;
				half macroTone;
				half fineTone;
				half current;
				half3 normalWS;
			};

			OceanPattern EvaluateOceanPattern(float2 mapUV)
			{
				// Two unrelated, slowly advected noise fields produce broad broken
				// currents. Unlike directional sine waves, they have no visible rows or
				// repeating sun-glint lattice at strategic-map altitude.
				float time = _Time.y;
				// Integer coefficients on mapUV.x make all three east-west wrap
				// copies sample the exact same noise at their shared seam.
				float2 uvA = float2(
					mapUV.x * 7.0 + mapUV.y * 3.0,
					mapUV.x * 2.0 - mapUV.y * 5.0) +
					float2(time * 0.0045, time * -0.0022);
				float2 uvB = float2(
					-mapUV.x * 11.0 + mapUV.y * 4.0,
					mapUV.x * 3.0 + mapUV.y * 8.0) +
					float2(time * -0.0032, time * 0.0018);
				float2 uvC = float2(
					mapUV.x * 2.0 + mapUV.y,
					-mapUV.x + mapUV.y * 3.0) +
					float2(time * 0.0008, time * 0.00035);
				half4 noiseA = SAMPLE_TEXTURE2D(
					_OceanNoise, sampler_OceanNoise, uvA);
				half4 noiseB = SAMPLE_TEXTURE2D(
					_OceanNoise, sampler_OceanNoise, uvB);
				half4 noiseC = SAMPLE_TEXTURE2D(
					_OceanNoise, sampler_OceanNoise, uvC);

				OceanPattern pattern;
				pattern.basinTone = noiseC.b;
				pattern.macroTone = saturate(
					noiseA.b * 0.56h + noiseB.r * 0.44h);
				pattern.fineTone = saturate(
					noiseA.g * 0.48h + noiseB.b * 0.52h);
				// A very sparse, soft current accent. It changes the ocean's tone but
				// never becomes literal white foam in open water.
				half difference = 1.0h - abs(noiseA.r - noiseB.g);
				pattern.current = smoothstep(0.76h, 0.95h, difference) *
					smoothstep(0.34h, 0.78h, noiseC.r);
				half2 slope =
					(noiseA.rg - 0.5h) * 0.055h +
					(noiseB.gb - 0.5h) * 0.032h;
				pattern.normalWS = normalize(half3(slope.x, 1.0h, slope.y));
				return pattern;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				HexGridData gridData = GetHexGridData(input.positionWS.xz);
				float2 resolvedOffset;
				if (!HFResolveOffset(
					gridData.cellOffsetCoordinates, resolvedOffset))
				{
					discard;
				}

				uint waterFlags = (uint)round(
					HFSampleShapeTexel(resolvedOffset).b * 255.0);
				bool underwaterCell =
					(waterFlags & HF_SHAPE_UNDERWATER_BIT) != 0u;
				bool coastCell = (waterFlags & HF_SHAPE_COAST_BIT) != 0u;
				half coverage = underwaterCell ? 1.0h : 0.0h;
				float depth = underwaterCell ? 4.0 : 0.0;
				half riverMouth = 0.0h;

				if (coastCell)
				{
					float cellIndex = resolvedOffset.y *
						_HexCellData_TexelSize.z + resolvedOffset.x;
					float2 hexPosition = WoldToHexSpace(input.positionWS.xz);
					float2 localPosition =
						(hexPosition - gridData.cellCenter) *
						(1.5 / HF_MIXER_SQRT3_OVER_2);
					HFReliefSurface surface = HF_EvaluateOriginalRelief(
						cellIndex, localPosition);
					float signedWaterDepth = _HexHFOriginalDatumY -
						HFStabilizeOceanSurfaceY(surface);
					float coverageWidth = max(
						fwidth(signedWaterDepth) * 1.5, 0.015);
					half heightCoverage = smoothstep(
						-coverageWidth, coverageWidth, signedWaterDepth);
					half seaCoverage = smoothstep(
						0.025h, 0.18h, saturate(surface.seaInfluence));
					half openWater = smoothstep(
						0.58h, 0.9h, saturate(surface.seaInfluence));
					coverage = max(heightCoverage * seaCoverage, openWater);
					depth = max(signedWaterDepth, 0.0);

					half riverCore = 1.0h - smoothstep(
						_HexReliefRiverCarve.x + 0.015h,
						_HexReliefRiverCarve.x + 0.105h,
						surface.riverDistance);
					riverMouth = riverCore *
						(1.0h - smoothstep(0.42h, 2.2h, depth));
				}

				clip(coverage - 0.035h);

				OceanPattern pattern = EvaluateOceanPattern(input.mapUV);
				half3 normalWS = pattern.normalWS;
				half shallow = 1.0h - smoothstep(0.035h, 1.15h, depth);
				// The far ocean has its own restrained cartographic palette. Nearby
				// chunk water may stay brighter and more transparent; the world view
				// needs a quiet field that does not compete with borders and terrain.
				half3 authoredDeep = _HexDeepOceanColor.rgb;
				half hasStyledColor = step(
					0.001h, dot(authoredDeep, authoredDeep));
				half3 deepColor = lerp(
					half3(0.014h, 0.070h, 0.125h),
					authoredDeep * half3(0.60h, 0.68h, 0.76h),
					hasStyledColor * 0.16h);
				half3 shallowColor = lerp(
					half3(0.032h, 0.185h, 0.225h),
					_HexShallowWaterColor.rgb * half3(0.62h, 0.76h, 0.78h),
					hasStyledColor * 0.18h);
				half shelf = shallow * shallow;
				half3 water = lerp(deepColor, shallowColor, shelf * 0.66h);
				half tone =
					(pattern.basinTone - 0.5h) * 0.20h +
					(pattern.macroTone - 0.5h) * 0.09h +
					(pattern.fineTone - 0.5h) * 0.025h;
				water *= 1.0h + tone;
				water = lerp(
					water, shallowColor * 0.72h,
					pattern.current * 0.075h);

				half3 viewDirection = SafeNormalize(
					GetWorldSpaceViewDir(input.positionWS));
				half fresnel = pow(
					1.0h - saturate(dot(normalWS, viewDirection)), 4.0h);
				water = lerp(water, shallowColor * 0.84h, fresnel * 0.12h);

				Light mainLight = GetMainLight();
				half3 halfDirection = SafeNormalize(
					mainLight.direction + viewDirection);
				half specular = pow(
					saturate(dot(normalWS, halfDirection)), 128.0h) *
					mainLight.shadowAttenuation *
					smoothstep(0.70h, 0.93h, pattern.fineTone);
				water += mainLight.color * specular * 0.025h;

				half shore = 1.0h - smoothstep(0.025h, 0.19h, depth);
				half brokenFoam = smoothstep(
					0.52h, 0.76h,
					pattern.fineTone * 0.58h + pattern.macroTone * 0.42h);
				half foam = shore * brokenFoam * coverage;
				water += _HexShoreFoamColor.rgb * foam * 0.14h;
				water = lerp(
					water, _HexRiverWaterColor.rgb,
					saturate(riverMouth * 0.72h));
				water = MixFog(water, input.fogFactor);
				return half4(saturate(water), coverage);
			}
			ENDHLSL
		}
	}
}
