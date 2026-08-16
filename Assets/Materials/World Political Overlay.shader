Shader "Hex Map/World Political Border"
{
	Properties
	{
		_CoreColor ("Border core", Color) = (0.28, 0.22, 0.18, 0.78)
		_HaloColor ("Border halo", Color) = (0.78, 0.70, 0.58, 0.26)
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent-5"
			"RenderPipeline" = "UniversalPipeline"
		}

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

			half4 Frag(Varyings input) : SV_Target
			{
				half distanceFromCore = abs(input.uv.x * 2.0h - 1.0h);
				half outer = 1.0h - smoothstep(0.48h, 1.0h, distanceFromCore);
				half core = 1.0h - smoothstep(0.10h, 0.52h, distanceFromCore);
				half4 color = lerp(_HaloColor, _CoreColor, core);
				color.a *= outer;
				return color;
			}
			ENDHLSL
		}
	}
}
