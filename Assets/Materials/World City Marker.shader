Shader "Hex Map/World City Marker"
{
	Properties
	{
		_MarkerSize ("Marker radius in pixels", Range(2, 10)) = 4.5
		_FillColor ("City fill", Color) = (0.94, 0.78, 0.38, 0.98)
		_BorderColor ("City outline", Color) = (0.08, 0.065, 0.05, 1)
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent+25"
			"RenderPipeline" = "UniversalPipeline"
		}
		Pass
		{
			Name "City Markers"
			Tags { "LightMode" = "UniversalForward" }
			Blend SrcAlpha OneMinusSrcAlpha
			Cull Off
			ZWrite Off
			ZTest LEqual

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			struct Attributes
			{
				float3 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			CBUFFER_START(UnityPerMaterial)
				float _MarkerSize;
				half4 _FillColor;
				half4 _BorderColor;
			CBUFFER_END

			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS);
				float2 clipOffset = input.uv * _MarkerSize * 2.0 /
					_ScreenParams.xy * output.positionCS.w;
				output.positionCS.xy += clipOffset;
				output.uv = input.uv;
				return output;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				half radius = length(input.uv);
				half edgeWidth = max(fwidth(radius), 0.025h);
				half coverage = 1.0h - smoothstep(
					1.0h - edgeWidth, 1.0h + edgeWidth, radius);
				half border = smoothstep(0.58h, 0.74h, radius);
				half4 color = lerp(_FillColor, _BorderColor, border);
				color.a *= coverage;
				return color;
			}
			ENDHLSL
		}
	}
}
