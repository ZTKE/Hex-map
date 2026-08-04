Shader "Hex Map/Coast Cliff"
{
	Properties
	{
		[NoScaleOffset] _Relief_Rock ("Rock", 2D) = "gray" {}
		[NoScaleOffset] _Relief_Strata ("Strata", 2D) = "gray" {}
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Opaque"
			"Queue" = "Geometry+5"
			"RenderPipeline" = "UniversalPipeline"
		}

		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }
			Cull Off
			ZWrite On

			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
			#pragma multi_compile_fragment _ _SHADOWS_SOFT
			#pragma multi_compile_fog
			#pragma multi_compile _ _HEX_MAP_EDIT_MODE

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "HexCellData.hlsl"
			#include "Hex Civilization Style.hlsl"

			TEXTURE2D(_Relief_Rock);
			SAMPLER(sampler_Relief_Rock);
			TEXTURE2D(_Relief_Strata);
			SAMPLER(sampler_Relief_Strata);
			float4 _HexCoastCliffLow;
			float4 _HexCoastCliffHigh;
			float4 _HexSnowCoastCliff;
			float4 _HexWetSandColor;
			float4 _HexDrySandColor;
			float _HexCoastCliffStrata;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				float4 style : TEXCOORD0;
				float3 cellIndices : TEXCOORD1;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				half3 normalWS : TEXCOORD1;
				float4 style : TEXCOORD2;
				float3 cellIndices : TEXCOORD3;
				half fogFactor : TEXCOORD4;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				VertexPositionInputs positions =
					GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = positions.positionCS;
				output.positionWS = positions.positionWS;
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				output.style = input.style;
				output.cellIndices = input.cellIndices;
				output.fogFactor = ComputeFogFactor(positions.positionCS.z);
				return output;
			}

			float Hash21(float2 p)
			{
				p = frac(p * float2(123.34, 456.21));
				p += dot(p, p + 45.32);
				return frac(p.x * p.y);
			}

			half4 Frag(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
			{
				half3 normalWS = normalize(input.normalWS) * (frontFace ? 1.0 : -1.0);
				float terrain = floor(input.style.x + 0.5);
				float rocky = input.style.y;
				float seed = input.style.z;
				float height01 = input.style.w;
				float2 rockUV = float2(
					input.positionWS.x * 0.045 + input.positionWS.z * 0.061,
					input.positionWS.y * 0.13 + seed * 3.7);
				half3 rock = SAMPLE_TEXTURE2D(
					_Relief_Rock, sampler_Relief_Rock, rockUV * 1.7).rgb;
				half3 strata = SAMPLE_TEXTURE2D(
					_Relief_Strata, sampler_Relief_Strata, rockUV).rgb;
				float detail = dot(
					lerp(rock, strata, _HexCoastCliffStrata),
					half3(0.299, 0.587, 0.114));
				float band = 0.5 + 0.5 * sin(
					input.positionWS.y * 7.5 + seed * 8.0 +
					Hash21(input.positionWS.xz * 0.13) * 2.2);
				half3 baseRock = lerp(
					_HexCoastCliffLow.rgb, _HexCoastCliffHigh.rgb,
					saturate(height01 * 0.66 + detail * 0.34));
				baseRock *= lerp(0.78, 1.16, detail);
				baseRock *= lerp(0.9, 1.04, band * rocky);
				if (terrain < 0.5)
				{
					baseRock *= half3(1.14, 0.88, 0.58);
				}
				else if (terrain > 3.5)
				{
					baseRock = lerp(baseRock, _HexSnowCoastCliff.rgb,
						smoothstep(0.48, 0.9, height01));
				}
				baseRock = lerp(
					lerp(_HexWetSandColor.rgb, _HexDrySandColor.rgb, height01),
					baseRock, rocky);
				baseRock *= lerp(0.84, 1.0, smoothstep(0.03, 0.24, height01));

				bool editMode = false;
				#ifdef _HEX_MAP_EDIT_MODE
					editMode = true;
				#endif
				float4 cellData = GetCellData(input.cellIndices, 0, editMode);
				baseRock *= lerp(0.25, 1.0, cellData.r);

				float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
				Light mainLight = GetMainLight(shadowCoord);
				half rawDiffuse = saturate(dot(normalWS, mainLight.direction));
				half paintedDiffuse = floor(rawDiffuse * 4.0 + 0.5) * 0.25;
				rawDiffuse = lerp(
					rawDiffuse, paintedDiffuse, _HexCivStyleStrength * 0.44);
				half diffuse = 0.34 + 0.66 * rawDiffuse;
				half3 lighting = SampleSH(normalWS) +
					mainLight.color * diffuse * mainLight.shadowAttenuation;
				baseRock *= min(lighting, 1.2);
				baseRock = HexCivGrade(
					baseRock, input.positionWS, rawDiffuse, 1.0);
				return half4(MixFog(baseRock, input.fogFactor), 1);
			}
			ENDHLSL
		}
	}
}
