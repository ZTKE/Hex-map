Shader "Hex Map/Near Road"
{
	Properties
	{
		_BaseColor("Dry Earth", Color) = (0.64, 0.55, 0.38, 0.86)
		_TrackColor("Wheel Ruts", Color) = (0.43, 0.35, 0.23, 1)
	}
	SubShader
	{
		Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent-10" }
		Blend SrcAlpha OneMinusSrcAlpha
		ZWrite Off
		Offset -1, -1
		Pass
		{
			Name "NearRoad"
			Tags { "LightMode"="UniversalForward" }
			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile _ _HEX_MAP_EDIT_MODE
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile_fragment _ _SHADOWS_SOFT
			#pragma multi_compile_fog
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "../Materials/HexCellData.hlsl"
			CBUFFER_START(UnityPerMaterial)
				half4 _BaseColor, _TrackColor;
			CBUFFER_END
			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				float2 uv : TEXCOORD0;
				float3 cells : TEXCOORD2;
				float4 weights : COLOR;
			};
			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				half3 normalWS : TEXCOORD1;
				float coverage : TEXCOORD2;
				half2 visibility : TEXCOORD3;
				half fog : TEXCOORD4;
			};
			Varyings Vert(Attributes input)
			{
				Varyings output = (Varyings)0;
				VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = position.positionCS;
				output.positionWS = position.positionWS;
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				// Existing road UVs mirror each half: shoulder=0, center=1.
				// V is always zero, so longitudinal grain uses world coordinates.
				output.coverage = input.uv.x;
				bool editMode = false;
				#ifdef _HEX_MAP_EDIT_MODE
					editMode = true;
				#endif
				output.visibility = GetCellData(input.cells, 0, editMode).xy * input.weights.r +
					GetCellData(input.cells, 1, editMode).xy * input.weights.g +
					GetCellData(input.cells, 2, editMode).xy * input.weights.b;
				output.fog = ComputeFogFactor(output.positionCS.z);
				return output;
			}
			float Hash(float2 p)
			{
				float3 h = frac(float3(p.xyx) * .1031);
				h += dot(h, h.yzx + 33.33);
				return frac((h.x + h.y) * h.z);
			}
			float Grain(float2 p)
			{
				float2 i = floor(p), f = frac(p);
				f = f * f * (3.0 - 2.0 * f);
				return lerp(lerp(Hash(i), Hash(i + float2(1,0)), f.x),
					lerp(Hash(i + float2(0,1)), Hash(i + 1.0), f.x), f.y);
			}
			half4 Frag(Varyings input) : SV_Target
			{
				// With fog disabled, visible can be 1 while explored remains 0.
				half known = max(input.visibility.x, input.visibility.y);
				clip(known - .001h);
				float grain = Grain(input.positionWS.xz * 1.8);
				float coverage = saturate(input.coverage);
				float edge = smoothstep(.34, .65, coverage + (grain - .5) * .11);
				float aa = max(fwidth(coverage), .015);
				float tracks = 1.0 - smoothstep(.045, .045 + aa, abs(coverage - .68));
				tracks *= lerp(.7, 1.0, grain);
				half3 earth = _BaseColor.rgb * lerp(.9h, 1.08h, grain);
				half3 color = lerp(earth, _TrackColor.rgb, tracks * .65);
				half3 normal = normalize(input.normalWS);
				Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
				half3 illumination = max(SampleSH(normal), .08h) + light.color *
					saturate(dot(normal, light.direction)) * light.distanceAttenuation * light.shadowAttenuation;
				color *= illumination * lerp(.25h, 1.0h, input.visibility.x);
				return half4(MixFog(color, input.fog), edge * _BaseColor.a * known);
			}
			ENDHLSL
		}
	}
}
