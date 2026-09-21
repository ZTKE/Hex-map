Shader "Hex Map/World Political Border"
{
	Properties
	{
		_CoreColor ("Border core", Color) = (0.02, 0.015, 0.01, 0.96)
		_HaloColor ("Border soft edge", Color) = (0.04, 0.03, 0.02, 0.42)
		_BorderWidthPixels ("Border width (pixels)", Range(3.0, 10.0)) = 4.0
		_CoreWidthPixels ("Core width (pixels)", Range(1.5, 8.0)) = 2.0
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent-5"
			"RenderPipeline" = "UniversalPipeline"
		}

		// 2px solid black core + soft falloff on each side (4px total strip).
		Pass
		{
			Name "WorldPoliticalBorder"
			Tags { "LightMode" = "UniversalForward" }
			Blend SrcAlpha OneMinusSrcAlpha
			Cull Off
			ZWrite Off
			ZTest LEqual

			HLSLPROGRAM
			#pragma target 2.0
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			half4 _CoreColor;
			half4 _HaloColor;
			float _BorderWidthPixels;
			float _CoreWidthPixels;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 directionOS : NORMAL;
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
				float3 centerWS = TransformObjectToWorld(input.positionOS.xyz);
				float3 directionWS = TransformObjectToWorldDir(input.directionOS);
				float4 centerCS = TransformWorldToHClip(centerWS);
				float4 directionCS = TransformWorldToHClip(centerWS + directionWS);
				float2 centerNDC = centerCS.xy / max(abs(centerCS.w), 0.0001);
				float2 directionNDC =
					directionCS.xy / max(abs(directionCS.w), 0.0001) - centerNDC;
				float2 directionPixels = directionNDC * _ScreenParams.xy;
				float directionLength = max(length(directionPixels), 0.0001);
				directionPixels /= directionLength;
				float2 sidePixels = float2(-directionPixels.y, directionPixels.x);
				float sideSign = input.uv.x * 2.0 - 1.0;
				float capSign = input.uv.y * 2.0 - 1.0;
				float halfStrip = max(_BorderWidthPixels, 4.0) * 0.5;
				float2 offsetPixels =
					sidePixels * sideSign * halfStrip +
					directionPixels * capSign * (halfStrip * 0.55);
				centerCS.xy += offsetPixels *
					(2.0 / _ScreenParams.xy) * centerCS.w;
				output.positionCS = centerCS;
				output.uv = input.uv;
				return output;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				float halfStrip = max(_BorderWidthPixels, 4.0) * 0.5;
				float distanceFromCorePixels =
					abs(input.uv.x * 2.0 - 1.0) * halfStrip;
				float pixelFootprint = max(fwidth(distanceFromCorePixels), 0.45);
				float coreHalfWidth = max(_CoreWidthPixels, 2.0) * 0.5;

				half core = 1.0h - smoothstep(
					coreHalfWidth - pixelFootprint * 0.35,
					coreHalfWidth + pixelFootprint * 0.35,
					distanceFromCorePixels);
				half softEdge = 1.0h - smoothstep(
					coreHalfWidth,
					halfStrip,
					distanceFromCorePixels);

				half3 color = lerp(_HaloColor.rgb, _CoreColor.rgb, core);
				half alpha = saturate(
					_HaloColor.a * softEdge + _CoreColor.a * core);
				return half4(color, alpha);
			}
			ENDHLSL
		}
	}
}
