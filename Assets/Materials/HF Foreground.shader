Shader "Hex Map/HF Foreground"
{
	Properties
	{
		_MainTex("HF Foreground Atlas", 2D) = "white" {}
		_Cutoff("Alpha Cutoff", Range(0, 1)) = 0.08
	}

	SubShader
	{
		Tags
		{
			"RenderPipeline" = "UniversalPipeline"
			"RenderType" = "TransparentCutout"
			"Queue" = "AlphaTest+20"
		}
		Cull Off
		ZWrite On

		Pass
		{
			Name "UniversalForward"
			Tags { "LightMode" = "UniversalForward" }

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile_fog

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "HexCellData.hlsl"

			// HF terrain stamps intentionally share this sampler to remain below the
			// hardware sampler limit. The foreground vertex uses the same logical
			// height reconstruction as the terrain surface beneath it.
			SAMPLER(sampler_linear_clamp);
			#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
			#include "HexTerrainShape.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);

			CBUFFER_START(UnityPerMaterial)
				float4 _MainTex_ST;
				float _Cutoff;
			CBUFFER_END

			struct Attributes
			{
				float3 positionOS : POSITION;
				float2 uv : TEXCOORD0;
				float2 billboardOffset : TEXCOORD1;
				float3 cellIndices : TEXCOORD2;
				float2 localPosition : TEXCOORD3;
				float4 color : COLOR;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float2 uv : TEXCOORD0;
				float4 color : COLOR;
				float fogFactor : TEXCOORD1;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				HFReliefSurface relief = HF_EvaluateRelief(
					input.cellIndices.x, input.localPosition);
				float3 centerOS = input.positionOS;
				centerOS.y = relief.y + 0.035;
				float3 centerWS = TransformObjectToWorld(centerOS);
				float3 cameraRightWS = normalize(float3(
					UNITY_MATRIX_I_V._m00,
					UNITY_MATRIX_I_V._m10,
					UNITY_MATRIX_I_V._m20));
				float3 cameraUpWS = normalize(float3(
					UNITY_MATRIX_I_V._m01,
					UNITY_MATRIX_I_V._m11,
					UNITY_MATRIX_I_V._m21));
				float3 positionWS = centerWS +
					cameraRightWS * input.billboardOffset.x +
					cameraUpWS * input.billboardOffset.y;
				output.positionCS = TransformWorldToHClip(positionWS);
				output.uv = TRANSFORM_TEX(input.uv, _MainTex);
				output.color = input.color;
				output.fogFactor = ComputeFogFactor(output.positionCS.z);
				return output;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				half4 atlas = SAMPLE_TEXTURE2D(
					_MainTex, sampler_MainTex, input.uv);
				clip(atlas.a - _Cutoff);
				half3 color = atlas.rgb * input.color.rgb;
				color = MixFog(color, input.fogFactor);
				return half4(color, 1.0h);
			}
			ENDHLSL
		}
	}
}
