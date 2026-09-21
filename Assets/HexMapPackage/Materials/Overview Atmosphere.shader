Shader "Hex Map/Overview Atmosphere"
{
	Properties
	{
		_Strength ("Overall strength", Range(0, 1)) = 0.85
		_BreathExposure ("Breath exposure", Range(0, 0.2)) = 0.11
		_Translucency ("Translucency", Range(0, 0.25)) = 0.12
		_Warmth ("Fine paper warmth", Range(0, 1)) = 0.36
		_SoftBloom ("Soft bloom", Range(0, 0.5)) = 0.20
		_Vignette ("Map vignette", Range(0, 0.35)) = 0.14
		_Grain ("Paper grain", Range(0, 0.04)) = 0.022
		_PaperFrameStrength ("Briefing paper frame", Range(0, 0.4)) = 0.22
		_PeriodGradeStrength ("Period ink grade", Range(0, 1)) = 0.36
	}

	SubShader
	{
		Tags { "RenderPipeline" = "UniversalPipeline" }

		Pass
		{
			Name "OverviewAtmosphere"
			ZWrite Off
			ZTest Always
			Cull Off

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

			CBUFFER_START(UnityPerMaterial)
				half _Strength;
				half _BreathExposure;
				half _Translucency;
				half _Warmth;
				half _SoftBloom;
				half _Vignette;
				half _Grain;
				half _PaperFrameStrength;
				half _PeriodGradeStrength;
			CBUFFER_END

			half ComputeBreathPhase()
			{
				// Slow, low-amplitude — fast screen breath reads as pan jitter.
				half slow = sin(_Time.y * 0.08 + 1.9) * 0.5h + 0.5h;
				return lerp(0.48h, 0.52h, slow);
			}

			float Hash12(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * 0.1031);
				p3 += dot(p3, p3.yzx + 33.33);
				return frac((p3.x + p3.y) * p3.z);
			}

			half EvaluatePaperFrame(float2 uv)
			{
				float2 edge = abs(uv - 0.5) * 2.0;
				float2 corner = pow(edge, float2(2.4, 2.1));
				half frame = saturate(max(corner.x, corner.y));
				frame = smoothstep(0.55h, 1.02h, frame);
				half inner = smoothstep(0.0h, 0.18h, min(edge.x, edge.y));
				return frame * (1.0h - inner * 0.35h);
			}

			half3 ApplyPeriodInkGrade(half3 color)
			{
				half luma = dot(color, half3(0.299h, 0.587h, 0.114h));
				// Soft print grade — warm polish without heavy sepia mud.
				half3 printTone = half3(
					luma * 1.05h + 0.025h,
					luma * 1.00h + 0.018h,
					luma * 0.92h + 0.012h);
				half3 softShadow = color * half3(0.96h, 0.97h, 0.94h);
				half3 graded = lerp(softShadow, printTone, 0.36h);
				return lerp(color, graded, _PeriodGradeStrength * _Strength);
			}

			half4 Frag(Varyings input) : SV_Target
			{
				float2 uv = input.texcoord;
				half3 source = SAMPLE_TEXTURE2D_X(
					_BlitTexture, sampler_LinearClamp, uv).rgb;
				half3 color = source;

				half phase = ComputeBreathPhase();
				color = ApplyPeriodInkGrade(color);

				half3 warm = color * half3(1.035h, 1.018h, 0.975h);
				color = lerp(color, warm, _Warmth * _Strength);

				half luma = dot(color, half3(0.299h, 0.587h, 0.114h));
				half3 airy = lerp(luma.xxx, color, 0.82h) * half3(1.04h, 1.025h, 1.01h);
				half3 dense = lerp(luma.xxx, color, 0.94h) * half3(0.97h, 0.96h, 0.95h);
				color = lerp(dense, airy, phase);
				color = lerp(source, color, _Translucency * _Strength);

				half exposure = lerp(
					1.0h - _BreathExposure, 1.0h + _BreathExposure, phase);
				color *= lerp(1.0h, exposure, _Strength);

				half3 bloom = max(color - 0.52h, 0.0h) * _SoftBloom;
				color += bloom * lerp(0.82h, 1.18h, phase) * _Strength;

				half paperFrame = EvaluatePaperFrame(uv);
				half3 frameTint = half3(0.74h, 0.70h, 0.62h);
				color = lerp(color, color * frameTint, paperFrame * _PaperFrameStrength * _Strength);

				// Studio key: soft center lift — photographed print, not crushed vignette.
				float2 keyCoord = uv - float2(0.48, 0.55);
				half studioKey = saturate(1.0h - dot(keyCoord, keyCoord) * 2.4h);
				color *= lerp(1.0h, 1.05h, studioKey * 0.55h * _Strength);

				float2 vignetteCoord = uv - 0.5;
				half vignette = 1.0h - dot(vignetteCoord, vignetteCoord) * 1.25h;
				vignette = saturate(vignette);
				color *= lerp(1.0h - _Vignette, 1.0h, vignette);

				// Dual grain: pulp + faint fiber for expensive stock.
				half pulp = Hash12(uv * float2(920.0, 680.0));
				half fiber = Hash12(uv * float2(210.0, 1480.0) + 17.0);
				half grain = pulp * 0.65h + fiber * 0.35h;
				color *= lerp(1.0h - _Grain, 1.0h + _Grain, grain);

				return half4(saturate(color), 1.0h);
			}
			ENDHLSL
		}
	}
}
