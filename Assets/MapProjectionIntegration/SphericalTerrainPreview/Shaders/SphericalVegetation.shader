Shader "WW2/Spherical Terrain Preview/Vegetation"
{
	Properties
	{
		[MainTexture] _BaseMap("Base Color / Opacity", 2D) = "white" {}
		[MainColor] _BaseColor("Color", Color) = (1,1,1,1)
		[Normal] _BumpMap("Normal", 2D) = "bump" {}
		_BumpScale("Normal Strength", Range(0,2)) = 1
		_Cutoff("Leaf Cutout", Range(0,1)) = 0.35
		_Smoothness("Smoothness", Range(0,1)) = 0.22
		_Translucency("Leaf Light Transmission", Range(0,1)) = 0.12
		_SphereCenter("Sphere center", Vector) = (0,0,0,0)
	}
	SubShader
	{
		Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
		Cull Off
		ZWrite On
		HLSLINCLUDE
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
		#include "SphericalLighting.hlsl"
		// Sphere preview uses its own cell graph and has no flat fog-of-war globals.
		TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
		TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
		CBUFFER_START(UnityPerMaterial)
			float4 _BaseMap_ST;
			half4 _BaseColor;
			half _BumpScale, _Cutoff, _Smoothness, _Translucency;
			float4 _SphereCenter;
		CBUFFER_END
		UNITY_INSTANCING_BUFFER_START(HexNearVegetation)
			UNITY_DEFINE_INSTANCED_PROP(float, _HexNearInstanceCell)
			UNITY_DEFINE_INSTANCED_PROP(float4, _HexNearInstanceTint)
		UNITY_INSTANCING_BUFFER_END(HexNearVegetation)
		struct Attributes
		{
			float4 positionOS : POSITION;
			float3 normalOS : NORMAL;
			float4 tangentOS : TANGENT;
			float2 uv : TEXCOORD0;
			UNITY_VERTEX_INPUT_INSTANCE_ID
		};
		struct Varyings
		{
			float4 positionCS : SV_POSITION;
			float3 positionWS : TEXCOORD0;
			half3 normalWS : TEXCOORD1;
			half4 tangentWS : TEXCOORD2;
			float2 uv : TEXCOORD3;
			half2 visibility : TEXCOORD4;
			half4 tint : TEXCOORD5;
			half fog : TEXCOORD6;
			UNITY_VERTEX_INPUT_INSTANCE_ID
		};
		half2 CellVisibility() { return half2(1, 1); }
		Varyings Vert(Attributes input)
		{
			UNITY_SETUP_INSTANCE_ID(input);
			Varyings output = (Varyings)0;
			UNITY_TRANSFER_INSTANCE_ID(input, output);
			VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
			VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
			output.positionCS = position.positionCS;
			output.positionWS = position.positionWS;
			output.normalWS = normal.normalWS;
			output.tangentWS = half4(normal.tangentWS, input.tangentOS.w * GetOddNegativeScale());
			output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
			output.visibility = CellVisibility();
			output.tint = UNITY_ACCESS_INSTANCED_PROP(HexNearVegetation, _HexNearInstanceTint);
			output.fog = ComputeFogFactor(output.positionCS.z);
			return output;
		}
		half4 SampleFoliage(Varyings input)
		{
			// The CPU submits complete trees using stable per-root visibility.
			// Every pass preserves the same authored leaf/trunk alpha silhouette;
			// view and shadow-map pixels never punch dither holes into that shape.
			clip(max(input.visibility.x, input.visibility.y) - 0.001h);
			half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
			clip(albedo.a - _Cutoff);
			return albedo;
		}
		ENDHLSL
		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode"="UniversalForward" }
			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile_instancing
			#pragma instancing_options forcemaxcount:256
			// Visibility is fully enabled in this independent art preview.
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile_fragment _ _SHADOWS_SOFT
			#pragma multi_compile_fog
			half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
			{
				UNITY_SETUP_INSTANCE_ID(input);
				half4 albedo = SampleFoliage(input);
				half3 normal = normalize(input.normalWS);
				half faceSign = IS_FRONT_VFACE(frontFace, 1.0h, -1.0h);
				half3 tangent = normalize(input.tangentWS.xyz + half3(0.00001, 0, 0));
				half3 bitangent = cross(normal, tangent) * input.tangentWS.w;
				half3 tangentNormal = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
				normal = normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + normal * tangentNormal.z) * faceSign;
				#if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
					float4 shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
				#else
					float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
				#endif
				Light light = GetMainLight(shadowCoord);
				half ndotl = saturate(dot(normal, light.direction));
				half backlight = saturate(dot(-normal, light.direction)) * _Translucency;
				float3 radialUp = normalize(input.positionWS-_SphereCenter.xyz);
				half3 illumination = max(SphericalAmbient(normal,radialUp), 0.08h) + light.color *
					(ndotl + backlight) * light.distanceAttenuation * light.shadowAttenuation;
				half3 color = albedo.rgb * input.tint.rgb * illumination;
				half3 viewDirection = GetWorldSpaceNormalizeViewDir(input.positionWS);
				half3 halfDirection = SafeNormalize(light.direction + viewDirection);
				half specular = pow(saturate(dot(normal, halfDirection)), lerp(16.0h, 64.0h, _Smoothness));
				color += light.color * (specular * _Smoothness * 0.06h * light.shadowAttenuation);
				color *= lerp(0.25h, 1.0h, input.visibility.x);
				return half4(MixFog(color, input.fog), 1);
			}
			ENDHLSL
		}
		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode"="ShadowCaster" }
			ColorMask 0
			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex ShadowVert
			#pragma fragment ShadowFrag
			#pragma multi_compile_instancing
			#pragma instancing_options forcemaxcount:256
			// Visibility is fully enabled in this independent art preview.
			#pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
			float3 _LightDirection;
			float3 _LightPosition;
			Varyings ShadowVert(Attributes input)
			{
				Varyings output = Vert(input);
				float3 lightDirection = _LightDirection;
				#if _CASTING_PUNCTUAL_LIGHT_SHADOW
					lightDirection = normalize(_LightPosition - output.positionWS);
				#endif
				output.positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, lightDirection));
				#if UNITY_REVERSED_Z
					output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
				#else
					output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
				#endif
				return output;
			}
			half4 ShadowFrag(Varyings input) : SV_Target { SampleFoliage(input); return 0; }
			ENDHLSL
		}
		Pass
		{
			Name "DepthOnly"
			Tags { "LightMode"="DepthOnly" }
			ColorMask R
			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment DepthFrag
			#pragma multi_compile_instancing
			#pragma instancing_options forcemaxcount:256
			// Visibility is fully enabled in this independent art preview.
			half4 DepthFrag(Varyings input) : SV_Target { SampleFoliage(input); return input.positionCS.z; }
			ENDHLSL
		}
	}
}
