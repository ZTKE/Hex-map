Shader "Hex Map/Globe Atmosphere"
{
	Properties
	{
		_Strength ("Overall strength", Range(0, 1)) = 1.0
		_SpaceStrength ("Cosmos strength", Range(0, 1)) = 1.0
		_NebulaStrength ("Nebula strength", Range(0, 1)) = 0.72
		_StarStrength ("Star field", Range(0, 1)) = 0.85
		_LimbStrength ("Atmosphere limb", Range(0, 1)) = 1.0
		_LimbWidth ("Outer soft halo", Range(0.04, 0.35)) = 0.14
		_InnerVelvet ("Inner atmosphere", Range(0, 1)) = 0.72
		_GoldGlow ("Rim glow intensity", Range(0, 1.5)) = 1.35
		_MistStrength ("Stylized mist", Range(0, 1)) = 0.35
		_VoidWarmth ("Void warmth", Range(0, 1)) = 0.18
		_DriftSpeed ("Cosmos drift", Range(0, 0.08)) = 0.018
	}

	SubShader
	{
		Tags { "RenderPipeline" = "UniversalPipeline" }

		Pass
		{
			Name "GlobeAtmosphere"
			ZWrite Off
			ZTest Always
			Cull Off

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

			CBUFFER_START(UnityPerMaterial)
				half _BackgroundOnly;
				half _Strength;
				half _SpaceStrength;
				half _NebulaStrength;
				half _StarStrength;
				half _LimbStrength;
				half _LimbWidth;
				half _InnerVelvet;
				half _GoldGlow;
				half _MistStrength;
				half _VoidWarmth;
				half _DriftSpeed;
			CBUFFER_END

			float4 _GlobeScreenDisk;
			float4 _GlobeCamRight;
			float4 _GlobeCamUp;
			float4 _GlobeCamForward;

			float Hash12(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * 0.1031);
				p3 += dot(p3, p3.yzx + 33.33);
				return frac((p3.x + p3.y) * p3.z);
			}

			float Hash13(float3 p)
			{
				p = frac(p * 0.1031);
				p += dot(p, p.yzx + 33.33);
				return frac((p.x + p.y) * p.z);
			}

			float ValueNoise(float2 p)
			{
				float2 i = floor(p);
				float2 f = frac(p);
				float2 u = f * f * (3.0 - 2.0 * f);
				float a = Hash12(i);
				float b = Hash12(i + float2(1.0, 0.0));
				float c = Hash12(i + float2(0.0, 1.0));
				float d = Hash12(i + float2(1.0, 1.0));
				return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
			}

			float Fbm(float2 p)
			{
				float v = 0.0;
				float a = 0.5;
				UNITY_UNROLL
				for (int i = 0; i < 4; i++)
				{
					v += a * ValueNoise(p);
					p = p * 2.07 + float2(17.1, 9.3);
					a *= 0.5;
				}
				return v;
			}

			half3 EvaluateCosmos(float2 uv, float2 pixel, float time)
			{
				float2 drift = float2(time * _DriftSpeed, time * _DriftSpeed * -0.62);
				float2 q = uv * float2(1.65, 1.0) + drift;

				// Deep ink void — charcoal navy, slight warm breath, not neon space.
				half3 voidCool = half3(0.008h, 0.011h, 0.022h);
				half3 voidWarm = half3(0.018h, 0.014h, 0.010h);
				half warmthWave = Fbm(q * 1.4 + 3.7);
				half3 voidCol = lerp(voidCool, voidWarm, warmthWave * _VoidWarmth);

				// Slow nebula veils: amber gold dust + desaturated teal ink.
				float n1 = Fbm(q * 2.4 + float2(0.0, time * 0.011));
				float n2 = Fbm(q * 3.8 + float2(4.2, -time * 0.008) + n1 * 1.3);
				float veil = saturate(n1 * 0.55 + n2 * 0.45);
				veil = smoothstep(0.28, 0.82, veil);

				half3 inkTeal = half3(0.04h, 0.09h, 0.12h);
				half3 amberDust = half3(0.22h, 0.14h, 0.06h);
				half3 sepiaBloom = half3(0.16h, 0.10h, 0.05h);
				half3 nebula = lerp(inkTeal, amberDust, saturate(n2));
				nebula = lerp(nebula, sepiaBloom, saturate(n1 * 0.65));
				voidCol = lerp(voidCol, voidCol + nebula, veil * _NebulaStrength * 0.85h);

				// Soft diagonal milky band — archival brass, not sci-fi purple.
				float2 bandUv = uv - 0.5;
				float band = abs(bandUv.x * 0.72 + bandUv.y * 0.92 +
					sin(time * 0.07) * 0.04);
				band = 1.0 - smoothstep(0.02, 0.28, band);
				band *= lerp(0.55, 1.0, Fbm(uv * 5.0 + drift * 2.0));
				half3 brass = half3(0.18h, 0.13h, 0.06h);
				voidCol += brass * band * 0.22h * _NebulaStrength;

				// Sparse star field with gentle twinkle.
				float2 starCell = floor(pixel * 0.42);
				float2 starLocal = frac(pixel * 0.42) - 0.5;
				float starSeed = Hash12(starCell);
				float starChance = step(0.965, starSeed);
				float starDist = length(starLocal);
				float starCore = 1.0 - smoothstep(0.0, 0.045 + starSeed * 0.04, starDist);
				float twinkle = 0.65 + 0.35 * sin(
					time * (1.4 + starSeed * 2.8) + starSeed * 40.0);
				half starLuma = starCore * starChance * twinkle;
				half3 starTint = lerp(
					half3(0.75h, 0.78h, 0.85h),
					half3(0.95h, 0.82h, 0.55h),
					Hash12(starCell + 19.7));
				voidCol += starTint * starLuma * _StarStrength * 0.95h;

				// Occasional distant gold mote clusters.
				float mote = pow(saturate(Fbm(q * 9.0 + 11.0) - 0.72) * 3.2, 2.0);
				voidCol += half3(0.55h, 0.40h, 0.18h) * mote * 0.18h * _StarStrength;

				// Soft corner falloff so the stage reads as a vignette chamber.
				float2 vig = uv - 0.5;
				half vignette = saturate(1.0h - dot(vig, vig) * 1.55h);
				voidCol *= lerp(0.55h, 1.0h, vignette);

				return saturate(voidCol);
			}


			// Multi-layer soft atmosphere: pale cyan → cyan → deep blue → void.
			// No opaque white stroke — color is driven by radial falloff only.
            half3 ApplyAtmosphereLimb(
				half3 color, half r, float2 uv, float2 centerUV, float2 radiusUV, half strength)
            {
				half glow = saturate(_GoldGlow);
				half limb = max(_LimbWidth, 0.04h);
				half inner = saturate(_InnerVelvet);

				// Layer palette (inside→edge→outer fringe).
				half3 layerPale = half3(0.55h, 0.82h, 0.98h);
				half3 layerCyan = half3(0.22h, 0.58h, 0.92h);
				half3 layerMid = half3(0.10h, 0.32h, 0.72h);
				half3 layerDeep = half3(0.04h, 0.12h, 0.38h);

				float2 fromCenter = (uv - centerUV) / radiusUV;
				half lit = saturate(fromCenter.y * 0.35h + fromCenter.x * -0.15h + 0.55h);
				half amp = strength * glow * lerp(0.90h, 1.12h, lit);

				// Normalized distance from silhouette: 0 at edge, 1 at outer end.
				half outerDist = saturate((r - 1.0h) / limb);
				half insideDist = saturate((1.0h - r) / 0.075h);

				// Outer halo density — soft exponential falloff into space.
				half outerMask = (1.0h - smoothstep(0.0h, 1.0h, outerDist));
				outerMask *= smoothstep(0.992h, 1.0h, r); // start just at the limb
				outerMask = pow(saturate(outerMask), 0.85h);

				// Color ramp along outer distance: pale → cyan → mid → deep.
				half3 outerCol = layerPale;
				outerCol = lerp(outerCol, layerCyan, smoothstep(0.00h, 0.22h, outerDist));
				outerCol = lerp(outerCol, layerMid, smoothstep(0.18h, 0.55h, outerDist));
				outerCol = lerp(outerCol, layerDeep, smoothstep(0.45h, 0.95h, outerDist));

				// Density also thins as color deepens (soft bloom, not a hard band).
				half outerDensity = outerMask * lerp(0.95h, 0.18h, outerDist);

				// Inner atmosphere: soft cyan wash fading into the map.
				half innerMask = pow(1.0h - insideDist, 2.4h) * smoothstep(0.88h, 0.998h, r);
				innerMask *= (1.0h - smoothstep(0.998h, 1.01h, r));
				half3 innerCol = lerp(layerCyan, layerPale, saturate(1.0h - insideDist * 1.4h));
				half innerDensity = innerMask * 0.28h * inner;

				half3 fused = color;
				// Soft additive layers — never replace with a solid rim color.
				fused += outerCol * outerDensity * 0.78h * amp;
				fused += innerCol * innerDensity * amp;

				// Very faint near-limb lift (still cyan, not chalk white).
				half kiss = pow(saturate(1.0h - abs(r - 1.0h) / 0.008h), 2.5h);
				fused += layerPale * kiss * 0.18h * amp;

				return saturate(fused);
            }

			// Sparse parchment mist locked to sphere lon/lat so it orbits with
			// the political map. Tiny world-space drift keeps it alive at rest.
			half3 ApplyStylizedMist(
				half3 color, half r, float2 fromCenter, float time, half strength)
			{
				half mistAmt = saturate(_MistStrength) * strength;
				if (mistAmt < 0.001h || r >= 0.998h)
				{
					return color;
				}

				float z2 = max(0.0, 1.0 - dot(fromCenter, fromCenter));
				float z = sqrt(z2);
				// Reconstruct world direction on the unit sphere (camera looks
				// toward origin; visible point = right*x + up*y - forward*z).
				float3 worldDir = normalize(
					_GlobeCamRight.xyz * fromCenter.x +
					_GlobeCamUp.xyz * fromCenter.y -
					_GlobeCamForward.xyz * z);

				float lon = atan2(worldDir.z, worldDir.x);
				float lat = asin(clamp(worldDir.y, -1.0, 1.0));
				// Noticeable slow crawl in geographic space — still locked to the
				// sphere so orbiting keeps mist glued to the map.
				float2 drift = float2(time * 0.045, time * -0.028);
				float2 q = float2(lon, lat) * float2(1.35, 1.85) + drift;
				float n1 = Fbm(q * 2.1);
				float n2 = Fbm(q * 4.4 + float2(3.1, -1.7) + n1 * 0.8 + time * 0.02);
				float veil = saturate(n1 * 0.55 + n2 * 0.45);

				half wisps = smoothstep(0.52h, 0.70h, (half)veil);
				wisps *= 1.0h - smoothstep(0.78h, 0.94h, (half)veil);
				wisps = pow(saturate(wisps), 1.35h);

				half limbBias = smoothstep(0.28h, 0.88h, r);
				limbBias *= 1.0h - smoothstep(0.96h, 0.998h, r);
				half face = (half)saturate(z);
				limbBias *= lerp(0.35h, 1.0h, face);

				half alpha = wisps * limbBias * mistAmt * 0.14h;
				half3 mistCol = half3(0.78h, 0.88h, 0.96h);
				half3 lifted = color + mistCol * alpha;
				return saturate(lerp(color, lifted, 0.85h));
			}

			half4 Frag(Varyings input) : SV_Target
			{
				float2 uv = input.texcoord;
				UNITY_BRANCH
				if (_BackgroundOnly > 0.5h)
					return half4(saturate(EvaluateCosmos(uv, uv * _ScreenParams.xy, _Time.y)), 1.0h);
				half3 source = SAMPLE_TEXTURE2D_X(
					_BlitTexture, sampler_LinearClamp, uv).rgb;

				float2 pixel = uv * _ScreenParams.xy;
				float2 centerUV = _GlobeScreenDisk.xy;
				float2 radiusUV = max(_GlobeScreenDisk.zw, float2(1e-4, 1e-4));
				float2 fromCenter = (uv - centerUV) / radiusUV;
				half r = (half)length(fromCenter);

				half strength = saturate(_Strength);

				// Replace clear color only outside the sphere — do not eat the
				// political disk (that caused the thick pale silhouette band).
				half spaceMask = smoothstep(1.0h, 1.012h, r);
				half3 color = source;
				UNITY_BRANCH
				if (_SpaceStrength > 0.001h)
				{
					half3 cosmos = EvaluateCosmos(uv, pixel, _Time.y);
					color = lerp(source, cosmos, spaceMask * _SpaceStrength * strength);
				}

				UNITY_BRANCH
				if (_MistStrength > 0.001h)
					color = ApplyStylizedMist(color, r, fromCenter, _Time.y, strength);
				color = ApplyAtmosphereLimb(
					color, r, uv, centerUV, radiusUV, _LimbStrength * strength);

				half grain = Hash13(float3(pixel, floor(_Time.y * 12.0)));
				color *= lerp(0.985h, 1.015h, grain);

				return half4(saturate(color), 1.0h);
			}
			ENDHLSL
		}
	}
}
