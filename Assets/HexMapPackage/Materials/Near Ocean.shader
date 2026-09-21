Shader "Hex Map/Near Ocean"
{
	Properties
	{
		[NoScaleOffset] _Noise ("Ocean noise", 2D) = "gray" {}
		[NoScaleOffset] [NonModifiableTextureData] _Civ6WaterDeep0 ("Deep waves 1: slope moments", 2D) = "gray" {}
		[NoScaleOffset] [NonModifiableTextureData] _Civ6WaterDeep1 ("Deep waves 2: slope moments", 2D) = "gray" {}
		[NoScaleOffset] [NonModifiableTextureData] _Civ6WaterCoast0 ("Coastal waves 1: slope moments", 2D) = "gray" {}
		[NoScaleOffset] [NonModifiableTextureData] _Civ6WaterCoast1 ("Coastal waves 2: slope moments", 2D) = "gray" {}
		_Civ6WaterWorldScale ("Wave repeats per world unit", Float) = 0.0275
		_Civ6WaterScrollSpeed ("Wave scroll rate", Float) = 0.012
		_Civ6WaterDeepStrength ("Deep wave strength", Range(0, 4)) = 2
		_Civ6WaterCoastStrength ("Coastal wave strength", Range(0, 4)) = 0.8
		_Civ6WaterSpecularExponent ("Sun reflection sharpness", Range(32, 6000)) = 850
		_Civ6WaterF0 ("Water reflectance", Range(0.001, 0.04)) = 0.004
		_Civ6WaterSunStrength ("Sun reflection intensity", Range(0, 6)) = 3
		_Civ6WaterSkyStrength ("Sky reflection intensity", Range(0, 1)) = 0.35
		_Civ6WaterDeepDarkening ("Deep water color scale", Range(0, 1)) = 0.22
		_Civ6WaterShallowDarkening ("Shallow water color scale", Range(0, 1)) = 0.55
		_Civ6WaterHeightTone ("Wave color variation", Range(0, 0.2)) = 0.08
		_Civ6WaterShelfWidth ("Continental bed slope softness", Range(0.12, 0.45)) = 0.22
		_Civ6WaterShelfStrength ("Coastal seabed visibility", Range(0, 1)) = 1.0
		_Civ6WaterFoamStrength ("White shore breaker opacity", Range(0, 1)) = 0.86
		_Civ6WaterFoamWidth ("Shore breaker width in world units", Range(0.3, 4)) = 2.0
		_Civ6WaterFoamSpeed ("Shore breaker cycles per second", Range(0.03, 0.6)) = 0.18
		_Civ6WaterClarity ("Shallow water optical clarity", Range(0.25, 3)) = 1.0
		_Civ6WaterBedRelief ("Seabed shoals and channels", Range(0, 2)) = 1.0
		_Civ6WaterBedDetail ("Visible seabed sand ripples", Range(0, 2)) = 1.0
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent-10"
			"RenderPipeline" = "UniversalPipeline"
		}

		Pass
		{
			Name "NearOcean"
			Tags { "LightMode" = "UniversalForward" }
			Cull Off
			ZWrite Off
			ZTest LEqual
			Blend SrcAlpha OneMinusSrcAlpha

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

			half4 _HexDeepOceanColor;
			half4 _HexShallowWaterColor;
			half4 _HexShoreFoamColor;
			half4 _HexRiverWaterColor;
			half4 _HexNearShallowWater;
			half4 _HexNearDeepWater;

			#include "HexWaterSurface.hlsl"

			struct Attributes
			{
				float4 positionOS : POSITION;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				half fogFactor : TEXCOORD1;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				VertexPositionInputs positionInputs =
					GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = positionInputs.positionCS;
				output.positionWS = positionInputs.positionWS;
				output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
				return output;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				HexGridData gridData = GetHexGridData(input.positionWS.xz);
				float2 resolvedOffset;
				if (!HFResolveOffset(
					gridData.cellOffsetCoordinates, resolvedOffset))
				{
					return half4(0, 0, 0, 0);
				}

				float cellIndex = resolvedOffset.y *
					_HexCellData_TexelSize.z + resolvedOffset.x;
				float2 hexPosition = WoldToHexSpace(input.positionWS.xz);
				float2 localPosition =
					(hexPosition - gridData.cellCenter) *
					(1.5 / HF_MIXER_SQRT3_OVER_2);
				HFReliefSurface surface = HF_EvaluateRelief(
					cellIndex, localPosition);

				// Match the historical HF shoreline fix: soft Water_h contour,
				// never the hex mesh edge.
				float signedWaterDepth = input.positionWS.y -
					HFStabilizeOceanSurfaceY(surface);
				float coverageWidth = max(fwidth(signedWaterDepth) * 3.2, 0.06);
				half heightCoverage = smoothstep(
					-coverageWidth, coverageWidth, signedWaterDepth);
				half seaInfluence = saturate(surface.seaInfluence);
				half seaCoverage = smoothstep(0.02h, 0.24h, seaInfluence);
				half coverage = heightCoverage * seaCoverage;
				if (coverage < 0.01h)
				{
					return half4(0, 0, 0, 0);
				}

				float depth = max(signedWaterDepth, 0.0);
				half coastShelf = 1.0h - smoothstep(0.22h, 0.78h, seaInfluence);
				HexWaterBed bed = EvaluateHexWaterBed(input.positionWS.xz, gridData,
					resolvedOffset, depth, seaInfluence);
				HexWaterSurface pattern = EvaluateHexWaterSurface(input.positionWS.xz, bed.coastWeight);
				half3 water = ShadeHexWater(input.positionWS, pattern, bed);
				float foam = EvaluateHexShoreFoam(input.positionWS, depth, coverage, pattern);

				half riverCore = 1.0h - smoothstep(
					_HexReliefRiverCarve.x + 0.015h,
					_HexReliefRiverCarve.x + 0.105h,
					surface.riverDistance);
				half riverMouth = riverCore *
					(1.0h - smoothstep(0.42h, 2.2h, depth)) * coastShelf;
				water = lerp(
					water, _HexRiverWaterColor.rgb,
					saturate(riverMouth * 0.72h));
				water = ApplyHexShoreFoam(water, foam * (1.0 - riverMouth * 0.8));

				water = ApplyHexWaterOverlay(water, gridData);
				water = MixFog(water, input.fogFactor);

				// The common optical model already transmits the continuous bed.
				// Alpha only softens the real shoreline, not coarse hex sand meshes.
				return half4(max(water, 0.0h), coverage);
			}
			ENDHLSL
		}
	}
}
