Shader "Hex Map/World Overview"
{
	Properties
	{
		[NoScaleOffset] _MainTex ("Political tint", 2D) = "white" {}
		_PoliticalBlend ("Political tint", Range(0, 1)) = 0.48
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
			Name "ExactWorldSurface"
			Tags { "LightMode" = "UniversalForward" }
			Cull Back
			ZWrite On

			HLSLPROGRAM
			#pragma target 4.6
			#pragma require 2darray
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "HexCellData.hlsl"

			// Use the same samplers and logical reconstruction as Hex Relief.shader.
			SAMPLER(sampler_linear_clamp);
			SAMPLER(sampler_point_clamp);
			#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
			#define HF_TERRAIN_POINT_SAMPLER sampler_point_clamp
			#include "HexTerrainShape.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);

			half4 _HexDeepOceanColor;
			half4 _HexShallowWaterColor;
			half4 _HexShoreFoamColor;
			half4 _HexWetSandColor;
			half4 _HexDrySandColor;
			half4 _HexRiverWaterColor;
			half4 _HexRiverBankColor;
			half4 _HexRiverWaterMotion;
			half _PoliticalBlend;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float2 uv : TEXCOORD1;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				VertexPositionInputs positionInputs =
					GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = positionInputs.positionCS;
				output.positionWS = positionInputs.positionWS;
				output.uv = input.uv;
				return output;
			}

			half OverviewWave(float2 mapUV)
			{
				// Integer U frequencies make all three world-wrap copies meet
				// seamlessly. Adding directional swells instead of multiplying an X
				// and Z wave avoids the regular checkerboard pattern.
				const float tau = 6.28318530718;
				half a = sin(tau *
					(mapUV.x * 47.0 + mapUV.y * 19.0) + _Time.y * 0.17);
				half b = sin(tau *
					(mapUV.x * -29.0 + mapUV.y * 37.0) - _Time.y * 0.11);
				half c = sin(tau *
					(mapUV.x * 113.0 - mapUV.y * 61.0) + _Time.y * 0.23);
				return saturate(0.5h + a * 0.22h + b * 0.14h + c * 0.055h);
			}

			half3 EvaluateOpenOcean(float2 mapUV)
			{
				half wave = OverviewWave(mapUV);
				const float tau = 6.28318530718;
				half swell = sin(tau *
					(mapUV.x * 11.0 + mapUV.y * 7.0) + _Time.y * 0.045) *
					0.5h + 0.5h;
				half crest = pow(saturate((wave - 0.58h) * 2.15h), 3.0h);
				half3 water = _HexDeepOceanColor.rgb *
					(0.84h + wave * 0.15h + swell * 0.08h);
				water = lerp(
					water, _HexShallowWaterColor.rgb,
					crest * 0.075h);
				water += _HexShoreFoamColor.rgb * crest * 0.026h;
				return saturate(water);
			}

			half3 EvaluateWater(
				HFReliefSurface surface, float signedWaterDepth,
				float2 mapUV, out half waterCoverage)
			{
				float coverageWidth = max(fwidth(signedWaterDepth) * 1.5, 0.015);
				half heightCoverage = smoothstep(
					-coverageWidth, coverageWidth, signedWaterDepth);
				half seaCoverage = smoothstep(
					0.025, 0.18, saturate(surface.seaInfluence));
				// Once HF's sea ownership is unambiguous this is one continuous
				// water body. Per-cell sea height may shape the seabed, but it must
				// never punch a visible checkerboard into the water surface.
				half openWater = smoothstep(
					0.58h, 0.9h, saturate(surface.seaInfluence));
				waterCoverage = max(heightCoverage * seaCoverage, openWater);

				float depth = max(signedWaterDepth, 0.0);
				half coastOwnership = 1.0h - smoothstep(
					0.32h, 0.86h, saturate(surface.seaInfluence));
				half shore = coastOwnership *
					(1.0h - smoothstep(0.04h, 0.58h, depth));
				half wave = OverviewWave(mapUV);
				half crest = pow(saturate((wave - 0.57h) * 2.2h), 3.0h);
				half3 ocean = lerp(
					EvaluateOpenOcean(mapUV), _HexShallowWaterColor.rgb,
					saturate(shore * 0.68h));
				half beach = smoothstep(0.72h, 0.96h, shore);
				half3 sand = lerp(
					_HexWetSandColor.rgb, _HexDrySandColor.rgb,
					smoothstep(0.84h, 1.0h, shore));
				half3 water = lerp(ocean, sand, beach);
				half foamBand = crest * shore *
					(1.0h - smoothstep(0.78h, 0.96h, shore));
				water += _HexShoreFoamColor.rgb * foamBand * 0.34h;
				return saturate(water);
			}

			half3 ApplyRiver(
				half3 ground, HFReliefSurface surface,
				half submerged)
			{
				half visibleRiver = (1.0h - submerged) *
					(1.0h - smoothstep(0.42h, 0.78h, surface.seaInfluence));
				half riverBank = 1.0h - smoothstep(
					_HexReliefRiverCarve.x + 0.015,
					_HexReliefRiverCarve.x + 0.12,
					surface.riverDistance);
				half riverCore = 1.0h - smoothstep(
					_HexReliefRiverCarve.x * 0.72,
					_HexReliefRiverCarve.x + 0.065,
					surface.riverDistance);
				ground = lerp(
					ground, _HexRiverBankColor.rgb,
					riverBank * visibleRiver * 0.56h);

				float phase = surface.riverUV.y *
					max(_HexRiverWaterMotion.y, 0.25h) * (2.0 * HF_PI) -
					_Time.y * max(_HexRiverWaterMotion.x, 0.01h) * (2.0 * HF_PI);
				half crest = saturate(
					sin(phase + surface.riverUV.x * 3.2) * 0.28h + 0.5h);
				half crossRiver = abs((half)surface.riverUV.x * 2.0h - 1.0h);
				half3 river = lerp(
					_HexRiverWaterColor.rgb,
					_HexRiverWaterColor.rgb * 0.72h,
					1.0h - crossRiver);
				river = lerp(
					river, _HexShoreFoamColor.rgb, crest * crest * 0.08h);
				return lerp(
					ground, river, riverCore * visibleRiver * 0.96h);
			}

			half4 Frag(Varyings input) : SV_Target
			{
				HexGridData gridData = GetHexGridData(input.positionWS.xz);
				float2 resolvedOffset;
				if (!HFResolveOffset(
					gridData.cellOffsetCoordinates, resolvedOffset))
				{
					return half4(EvaluateOpenOcean(input.uv), 1.0h);
				}

				// Most of the world is deep water. Interior water cells do not need
				// the expensive 13-neighbour HF relief/diffuse reconstruction used
				// by land and the coast band. Besides being faster, this guarantees
				// that the ocean reads as one continuous surface.
				uint waterFlags = (uint)round(
					HFSampleShapeTexel(resolvedOffset).b * 255.0);
				bool underwaterCell =
					(waterFlags & HF_SHAPE_UNDERWATER_BIT) != 0u;
				bool coastCell = (waterFlags & HF_SHAPE_COAST_BIT) != 0u;
				if (underwaterCell && !coastCell)
				{
					return half4(EvaluateOpenOcean(input.uv), 1.0h);
				}

				float cellIndex = resolvedOffset.y *
					_HexCellData_TexelSize.z + resolvedOffset.x;
				float2 hexPosition = WoldToHexSpace(input.positionWS.xz);
				float2 localPosition = (hexPosition - gridData.cellCenter) *
					(1.5 / HF_MIXER_SQRT3_OVER_2);

				// These are the exact functions used by the detailed HF terrain.
				HFReliefSurface surface = HF_EvaluateOriginalRelief(
					cellIndex, localPosition);
				half3 ground = HF_EvaluateOriginalDiffuse(
					cellIndex, localPosition);
				float signedWaterDepth = _HexHFOriginalDatumY -
					HFStabilizeOceanSurfaceY(surface);
				half submerged = step(0.001, signedWaterDepth);

				// Match the detailed terrain's wet/dry coast floor before the water
				// surface is composited over it.
				half3 floorSand = lerp(
					_HexDrySandColor.rgb, _HexWetSandColor.rgb,
					smoothstep(0.0, 0.55, signedWaterDepth));
				half3 deepFloor = lerp(
					_HexShallowWaterColor.rgb, _HexDeepOceanColor.rgb,
					smoothstep(1.2, 4.2, signedWaterDepth)) * 0.78h;
				half3 coastFloor = lerp(
					floorSand, deepFloor,
					smoothstep(0.35, 1.25, signedWaterDepth));
				half hfDetail = dot(
					ground, half3(0.299h, 0.587h, 0.114h));
				coastFloor *= lerp(0.84h, 1.14h, hfDetail);
				ground = lerp(
					ground, coastFloor,
					smoothstep(0.06h, 0.78h, surface.seaInfluence) * submerged);
				ground = ApplyRiver(ground, surface, submerged);

				half waterCoverage;
				half3 water = EvaluateWater(
					surface, signedWaterDepth, input.uv,
					waterCoverage);
				// The overview reads as one water surface instead of exposing each
				// individual sea-floor stamp through a very transparent ocean.
				half waterAlpha = waterCoverage * 0.91h;
				half3 exactSurface = lerp(ground, water, waterAlpha);

				// Politics is an independent transparent overlay. It never supplies
				// terrain or ocean color.
				half4 politics = SAMPLE_TEXTURE2D(
					_MainTex, sampler_MainTex, input.uv);
				half luminance = dot(
					exactSurface, half3(0.299h, 0.587h, 0.114h));
				half3 tinted = politics.rgb * lerp(0.58h, 1.24h, luminance);
				half tintStrength = _PoliticalBlend * politics.a *
					(1.0h - waterCoverage);
				return half4(lerp(exactSurface, tinted, tintStrength), 1.0h);
			}
			ENDHLSL
		}
	}
}
