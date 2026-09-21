Shader "Hidden/Hex Map/River Continuity Validation"
{
	SubShader
	{
		Tags { "RenderType" = "Opaque" }
		Pass
		{
			Cull Off ZWrite Off ZTest Always
			HLSLPROGRAM
			#pragma target 4.5
			// Editor-only correctness probe, never a performance measurement. The
			// combined river and coastal-height optimizer exceeded D3D's task timeout.
			// Keep every production calculation and readback, without that optimizer.
			#pragma skip_optimizations d3d11
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
				// Cell, edge (plus 6/12 for either mouth endpoint), t, unique ID.
				float4 probe = SAMPLE_TEXTURE2D_LOD(_SamplePoints, sampler_point_clamp, input.uv, 0);
				if (probe.a < 0) return float4(0, -1, 0, 0);
				int code = (int)round(probe.g), direction = code % 6;
				float2 offset = HFCellIndexToOffset(probe.r);
				float2 center = float2((offset.x + fmod(offset.y, 2.0) * .5) * HF_SQRT3_OVER_2 * 2.0, offset.y * 1.5);
				float2 probePosition, tangent;
				if (code < 6)
				{
					float2 a, b, tangentA, tangentB;
					HFRiverCurveParameters(offset, direction, a, b, tangentA, tangentB);
					HFRiverCurveSample(a, b, tangentA, tangentB, probe.b, probePosition, tangent);
				}
				else
				{
					float2 a, b;
					if (!HFRiverMouthSegment(offset, direction, code / 6 - 1, a, b)) return float4(1000, probe.a, 1000, 0);
					probePosition = lerp(a, b, probe.b);
				}
				float first, second; float2 uv;
				HF_EvaluateRiverCoordinates(probe.r, probePosition - center, first, uv);
				float2 next = HFNeighborOffset(offset, direction), resolved;
				if (!HFResolveOffset(next, resolved)) return float4(1000, probe.a, 1000, 0);
				float nextIndex = resolved.x + resolved.y * _HexCellData_TexelSize.z;
				HF_EvaluateRiverCoordinates(nextIndex, probePosition - center - HFNeighborCenter(direction), second, uv);
				float greatest = max(first, second), least = min(first, second);
				if (code >= 6)
				{
					int thirdDirection = (direction + (code / 6 == 1 ? 5 : 1)) % 6;
					float2 third = HFNeighborOffset(offset, thirdDirection);
					if (!HFResolveOffset(third, resolved)) return float4(1000, probe.a, 1000, 0);
					float thirdIndex = resolved.x + resolved.y * _HexCellData_TexelSize.z, waterDistance;
					HF_EvaluateRiverCoordinates(thirdIndex, probePosition - center - HFNeighborCenter(thirdDirection), waterDistance, uv);
					greatest = max(greatest, waterDistance); least = min(least, waterDistance);
				}
				float coverage = HFRiverCoreCoverage(greatest);
				if (code >= 6)
				{
					// Also detect a gap between terrain river tint and the actual
					// sea-layer height/sea-mask clipping, not just line geometry.
					HFReliefSurface surface = HF_EvaluateRelief(probe.r, probePosition - center);
					float signedDepth = _HexHFOriginalDatumY - HFStabilizeOceanSurfaceY(surface);
					float seaAlpha = smoothstep(-.06, .06, signedDepth) * smoothstep(.02, .24, surface.seaInfluence);
					float riverAlpha = coverage * HFRiverSurfaceVisibility(surface.seaInfluence, signedDepth);
					coverage = 1.0 - (1.0 - riverAlpha) * (1.0 - seaAlpha);
				}
				return float4(greatest, probe.a, greatest - least, coverage);
			}
			ENDHLSL
		}
	}
}
