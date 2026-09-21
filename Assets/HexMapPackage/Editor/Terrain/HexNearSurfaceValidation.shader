Shader "Hidden/Hex Map/Near Surface Validation"
{
	SubShader
	{
		Tags { "RenderType" = "Opaque" }
		Pass
		{
			Cull Off ZWrite Off ZTest Always
			HLSLPROGRAM
			#pragma target 4.5
			#pragma require 2darray
			#pragma vertex Vert
			#pragma fragment Frag
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "../../Materials/HexCellData.hlsl"
			TEXTURE2D_ARRAY(_Terrain_Textures);
			SAMPLER(sampler_Terrain_Textures);
			SAMPLER(sampler_linear_clamp);
			SAMPLER(sampler_point_clamp);
			#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
			#define HF_TERRAIN_POINT_SAMPLER sampler_point_clamp
			#include "../../Materials/HexTerrainShape.hlsl"
			TEXTURE2D(_SamplePoints);
			struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
			struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				output.uv = input.uv;
				return output;
			}
			float4 Frag(Varyings input) : SV_Target
			{
				// R = cell index, GB = local XZ / outer radius, A = unique sample ID.
				float4 samplePoint = SAMPLE_TEXTURE2D_LOD(_SamplePoints, sampler_point_clamp, input.uv, 0);
				if (samplePoint.a < 0) return float4(0, -1, 0, 1);
				HFReliefSurface surface = HF_EvaluateRelief(samplePoint.r, samplePoint.gb);
				// Echo ID prevents readback row orientation from faking discrepancies.
				return float4(surface.y, samplePoint.a, HFStabilizeOceanSurfaceY(surface), 1);
			}
			ENDHLSL
		}
	}
}
