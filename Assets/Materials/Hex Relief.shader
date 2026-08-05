Shader "Hex Map/Relief"
{
	Properties
	{
		[NoScaleOffset] _Terrain_Textures ("Terrain Textures", 2DArray) = "white" {}
		[NoScaleOffset] _HexTerrainStyleAtlas ("Strategy Terrain Atlas", 2D) = "white" {}
		[NoScaleOffset] _Relief_Rock ("Relief Rock", 2D) = "gray" {}
		[NoScaleOffset] _Relief_Strata ("Relief Strata", 2D) = "gray" {}
		[NoScaleOffset][Normal] _Relief_Rock_Normal ("Relief Rock Normal", 2D) = "bump" {}
		[NoScaleOffset] _Mountain_Color_Decal ("Mountain Color Decal", 2D) = "white" {}
		[HideInInspector] _Use_Mountain_Color_Decal ("Use Mountain Color Decal", Float) = 0
		[NoScaleOffset] _HexMountainMasks ("Logical Mountain Height Masks", 2DArray) = "white" {}
		_HexReliefTessellation ("Relief Tessellation", Range(1, 16)) = 8
		_HexReliefTessellationStart ("Tessellation Start Distance", Float) = 80
		_HexReliefTessellationEnd ("Tessellation End Distance", Float) = 280
		_HexReliefStampScale ("HF Stamp Overlap", Range(1.01, 1.5)) = 1.22
		_HexReliefRiverCarve ("River Cut Core / Shoulder", Vector) = (0.07, 0.31, 0, 0)
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Opaque"
			"Queue" = "Geometry+1"
			"RenderPipeline" = "UniversalPipeline"
		}

		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }
			Cull Back
			ZWrite On
			Blend One Zero

			HLSLPROGRAM
			#pragma target 4.6
			#pragma require tessellation tessHW 2darray
			#pragma vertex Vert
			#pragma hull Hull
			#pragma domain Domain
			#pragma fragment Frag
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
			#pragma multi_compile_fragment _ _SHADOWS_SOFT
			#pragma multi_compile_fog
			#pragma multi_compile _ _HEX_MAP_EDIT_MODE

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "HexCellData.hlsl"
			TEXTURE2D_ARRAY(_Terrain_Textures);
			SAMPLER(sampler_Terrain_Textures);
			SAMPLER(sampler_linear_clamp);
			#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
			#include "HexTerrainShape.hlsl"
			#include "Hex Civilization Style.hlsl"

			TEXTURE2D(_HexTerrainStyleAtlas);
			SAMPLER(sampler_HexTerrainStyleAtlas);
			TEXTURE2D(_Relief_Rock);
			SAMPLER(sampler_Relief_Rock);
			TEXTURE2D(_Relief_Strata);
			SAMPLER(sampler_Relief_Strata);
			TEXTURE2D(_Relief_Rock_Normal);
			SAMPLER(sampler_Relief_Rock_Normal);
			TEXTURE2D(_Mountain_Color_Decal);
			SAMPLER(sampler_Mountain_Color_Decal);
			float _Use_Mountain_Color_Decal;
			float _HexReliefTessellation;
			float _HexReliefTessellationStart;
			float _HexReliefTessellationEnd;
			float _HexTerrainAtlasBlend;
			float _HexTerrainAtlasTiling;
			float _HexTerrainMacroVariation;
			float4 _HexSnowBand;
			float4 _HexDesertStripeCenters;
			float4 _HexDesertStripeWidths;
			float4 _HexBiomeScree[5];
			float4 _HexBiomeLowRock[5];
			float4 _HexBiomeHighRock[5];
			float4 _HexBiomeStripe[5];
			float4 _HexBiomeSnow[5];
			float _HexBiomeHillLine[5];
			float _HexBiomeSnowLine[5];

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				float3 cellIndices : TEXCOORD1;
				float2 localPosition : TEXCOORD2;
			};

			struct TessControlPoint
			{
				float4 positionOS : INTERNALTESSPOS;
				float3 normalOS : NORMAL;
				float3 cellIndices : TEXCOORD1;
				float2 localPosition : TEXCOORD2;
			};

			struct TessellationFactors
			{
				float edge[3] : SV_TessFactor;
				float inside : SV_InsideTessFactor;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				half3 normalWS : TEXCOORD1;
				float4 relief : TEXCOORD2;
				float3 cellIndices : TEXCOORD3;
				float4 style : TEXCOORD4;
				float4 localData : TEXCOORD5;
				half fogFactor : TEXCOORD6;
				float2 localPosition : TEXCOORD7;
			};

			TessControlPoint Vert(Attributes input)
			{
				TessControlPoint output;
				output.positionOS = input.positionOS;
				output.normalOS = input.normalOS;
				output.cellIndices = input.cellIndices;
				output.localPosition = input.localPosition;
				return output;
			}

			float ReliefTessellationFactor(float3 positionOS)
			{
				float3 positionWS = TransformObjectToWorld(positionOS);
				float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
				float distanceFade = saturate(
					(distanceToCamera - _HexReliefTessellationStart) /
					max(_HexReliefTessellationEnd - _HexReliefTessellationStart, 1.0));
				return max(1.0, lerp(_HexReliefTessellation, 1.0, distanceFade));
			}

			TessellationFactors PatchConstants(
				InputPatch<TessControlPoint, 3> patch)
			{
				TessellationFactors factors;
				factors.edge[0] = ReliefTessellationFactor(
					(patch[1].positionOS.xyz + patch[2].positionOS.xyz) * 0.5);
				factors.edge[1] = ReliefTessellationFactor(
					(patch[2].positionOS.xyz + patch[0].positionOS.xyz) * 0.5);
				factors.edge[2] = ReliefTessellationFactor(
					(patch[0].positionOS.xyz + patch[1].positionOS.xyz) * 0.5);
				factors.inside = ReliefTessellationFactor(
					(patch[0].positionOS.xyz + patch[1].positionOS.xyz +
					patch[2].positionOS.xyz) / 3.0);
				return factors;
			}

			[domain("tri")]
			[outputcontrolpoints(3)]
			[outputtopology("triangle_cw")]
			[partitioning("fractional_even")]
			[patchconstantfunc("PatchConstants")]
			TessControlPoint Hull(
				InputPatch<TessControlPoint, 3> patch,
				uint controlPointId : SV_OutputControlPointID)
			{
				return patch[controlPointId];
			}

			[domain("tri")]
			Varyings Domain(
				TessellationFactors factors,
				const OutputPatch<TessControlPoint, 3> patch,
				float3 barycentricCoordinates : SV_DomainLocation)
			{
				Varyings output;
				float4 positionOS =
					patch[0].positionOS * barycentricCoordinates.x +
					patch[1].positionOS * barycentricCoordinates.y +
					patch[2].positionOS * barycentricCoordinates.z;
				float3 normalOS = normalize(
					patch[0].normalOS * barycentricCoordinates.x +
					patch[1].normalOS * barycentricCoordinates.y +
					patch[2].normalOS * barycentricCoordinates.z);
				float3 cellIndices =
					patch[0].cellIndices * barycentricCoordinates.x +
					patch[1].cellIndices * barycentricCoordinates.y +
					patch[2].cellIndices * barycentricCoordinates.z;
				float2 localPosition =
					patch[0].localPosition * barycentricCoordinates.x +
					patch[1].localPosition * barycentricCoordinates.y +
					patch[2].localPosition * barycentricCoordinates.z;

				HFReliefSurface surface = HF_EvaluateRelief(
					cellIndices.x, localPosition);
				positionOS.y = surface.y;
				VertexPositionInputs positionInputs =
					GetVertexPositionInputs(positionOS.xyz);
				VertexNormalInputs normalInputs = GetVertexNormalInputs(normalOS);
				output.positionCS = positionInputs.positionCS;
				output.positionWS = positionInputs.positionWS;
				output.normalWS = normalInputs.normalWS;
				output.relief = float4(
					surface.height01, surface.landform,
					surface.terrain, surface.coverage);
				output.cellIndices = cellIndices;
				output.style = surface.style;
				output.localData = float4(
					surface.moduleUV, surface.moduleIndex, 0.0);
				output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
				output.localPosition = localPosition;
				return output;
			}

			float Hash21(float2 p)
			{
				p = frac(p * float2(123.34, 456.21));
				p += dot(p, p + 45.32);
				return frac(p.x * p.y);
			}

			float ValueNoise(float2 p)
			{
				float2 i = floor(p);
				float2 f = frac(p);
				f = f * f * (3.0 - 2.0 * f);
				return lerp(
					lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x),
					lerp(Hash21(i + float2(0, 1)), Hash21(i + 1), f.x),
					f.y);
			}

			float Fbm(float2 p)
			{
				float value = 0.0;
				float amplitude = 0.55;
				[unroll]
				for (int i = 0; i < 4; i++)
				{
					value += ValueNoise(p) * amplitude;
					p = float2(
						p.x * 1.62 - p.y * 1.18,
						p.x * 1.18 + p.y * 1.62) + 7.13;
					amplitude *= 0.48;
				}
				return value;
			}

			float2 RotateUV(float2 p)
			{
				return float2(
					p.x * 0.8 - p.y * 0.6,
					p.x * 0.6 + p.y * 0.8);
			}

			half3 SampleTerrainSurface(float3 positionWS, float terrainIndex)
			{
				float2 uvA = positionWS.xz * (2.0 * TILING_SCALE);
				float2 uvB = RotateUV(positionWS.xz) * (3.73 * TILING_SCALE) +
					float2(4.37, 1.91);
				half3 a = SAMPLE_TEXTURE2D_ARRAY(
					_Terrain_Textures, sampler_Terrain_Textures,
					uvA, terrainIndex).rgb;
				half3 b = SAMPLE_TEXTURE2D_ARRAY(
					_Terrain_Textures, sampler_Terrain_Textures,
					uvB, terrainIndex).rgb;
				float macro = ValueNoise(positionWS.xz * 0.035 + 11.7);
				half3 surface = lerp(a, b, 0.2 + macro * 0.28);

				float panel = clamp(floor(terrainIndex + 0.5), 0.0, 4.0);
				float2 atlasA = frac(positionWS.xz * _HexTerrainAtlasTiling);
				float2 atlasB = frac(
					RotateUV(positionWS.xz) * (_HexTerrainAtlasTiling * 1.71) +
					float2(0.37, 0.61));
				atlasA.x = (panel + lerp(0.025, 0.975, atlasA.x)) * 0.2;
				atlasB.x = (panel + lerp(0.025, 0.975, atlasB.x)) * 0.2;
				half3 authoredA = SAMPLE_TEXTURE2D(
					_HexTerrainStyleAtlas, sampler_HexTerrainStyleAtlas, atlasA).rgb;
				half3 authoredB = SAMPLE_TEXTURE2D(
					_HexTerrainStyleAtlas, sampler_HexTerrainStyleAtlas, atlasB).rgb;
				half3 authored = lerp(authoredA, authoredB, 0.16 + macro * 0.28);
				surface = lerp(surface, authored, saturate(_HexTerrainAtlasBlend));
				surface *= lerp(
					1.0 - _HexTerrainMacroVariation,
					1.0 + _HexTerrainMacroVariation * 0.55,
					Fbm(positionWS.xz * 0.018));
				return surface;
			}

			// Relief used to sample only the terrain type of the strongest height
			// stamp. The flat surface below it already uses HF's neighbourhood mixer,
			// so the two passes could disagree at biome borders and make a hill look
			// like a separately coloured object. Reconstruct the same compact one-ring
			// mix for the relief surface as well.
			half3 SampleHFMixedReliefGround(
				float3 positionWS, float fallbackTerrain)
			{
				half3 fallback = SampleTerrainSurface(
					positionWS, fallbackTerrain);
				HexGridData grid = GetHexGridData(positionWS.xz);
				float2 hexPosition = WoldToHexSpace(positionWS.xz);
				float2 local = hexPosition - grid.cellCenter;
				HFTerrainMixWeights mix = HFMixEvaluateNeighborhood(
					grid.cellOffsetCoordinates, local);

				half3 mixed = 0.0;
				if (mix.terrain0123.x > 0.0001)
					mixed += SampleTerrainSurface(positionWS, 0.0) *
						mix.terrain0123.x;
				if (mix.terrain0123.y > 0.0001)
					mixed += SampleTerrainSurface(positionWS, 1.0) *
						mix.terrain0123.y;
				if (mix.terrain0123.z > 0.0001)
					mixed += SampleTerrainSurface(positionWS, 2.0) *
						mix.terrain0123.z;
				if (mix.terrain0123.w > 0.0001)
					mixed += SampleTerrainSurface(positionWS, 3.0) *
						mix.terrain0123.w;
				if (mix.terrain4 > 0.0001)
					mixed += SampleTerrainSurface(positionWS, 4.0) * mix.terrain4;
				return lerp(
					fallback, mixed, HFMixNeighborhoodBlendStrength(mix));
			}

			half3 GetTriplanarWeights(half3 normalWS)
			{
				half3 weights = pow(abs(normalWS), 5.0);
				return weights / max(weights.x + weights.y + weights.z, 0.001);
			}

			half3 SampleRockSurface(
				float3 positionWS, half3 normalWS, float2 seed)
			{
				half3 weights = GetTriplanarWeights(normalWS);
				float scale = 0.082;
				half3 sampleX = SAMPLE_TEXTURE2D(
					_Relief_Rock, sampler_Relief_Rock,
					positionWS.zy * scale + seed * 3.1).rgb;
				half3 sampleY = SAMPLE_TEXTURE2D(
					_Relief_Rock, sampler_Relief_Rock,
					positionWS.xz * scale + seed * 2.3).rgb;
				half3 sampleZ = SAMPLE_TEXTURE2D(
					_Relief_Rock, sampler_Relief_Rock,
					positionWS.xy * scale + seed * 4.7).rgb;
				half3 detail = sampleX * weights.x +
					sampleY * weights.y + sampleZ * weights.z;

				float strataScale = 0.105;
				half3 strataX = SAMPLE_TEXTURE2D(
					_Relief_Strata, sampler_Relief_Strata,
					positionWS.zy * strataScale + seed * 1.7).rgb;
				half3 strataY = SAMPLE_TEXTURE2D(
					_Relief_Strata, sampler_Relief_Strata,
					positionWS.xz * strataScale + seed * 2.9).rgb;
				half3 strataZ = SAMPLE_TEXTURE2D(
					_Relief_Strata, sampler_Relief_Strata,
					positionWS.xy * strataScale + seed * 4.1).rgb;
				half3 strata = strataX * weights.x +
					strataY * weights.y + strataZ * weights.z;
				return lerp(detail, strata, 0.68);
			}

			half3 SampleRockNormal(
				float3 positionWS, half3 geometryNormal, float2 seed)
			{
				half3 weights = GetTriplanarWeights(geometryNormal);
				float scale = 0.082;
				half3 normalX = UnpackNormalScale(SAMPLE_TEXTURE2D(
					_Relief_Rock_Normal, sampler_Relief_Rock_Normal,
					positionWS.zy * scale + seed * 3.1), 0.62);
				half3 normalY = UnpackNormalScale(SAMPLE_TEXTURE2D(
					_Relief_Rock_Normal, sampler_Relief_Rock_Normal,
					positionWS.xz * scale + seed * 2.3), 0.62);
				half3 normalZ = UnpackNormalScale(SAMPLE_TEXTURE2D(
					_Relief_Rock_Normal, sampler_Relief_Rock_Normal,
					positionWS.xy * scale + seed * 4.7), 0.62);
				normalX = half3(
					normalX.z * sign(geometryNormal.x), normalX.y, normalX.x);
				normalY = half3(
					normalY.x, normalY.z * sign(geometryNormal.y), normalY.y);
				normalZ = half3(
					normalZ.x, normalZ.y, normalZ.z * sign(geometryNormal.z));
				return normalize(
					normalX * weights.x + normalY * weights.y + normalZ * weights.z);
			}

			void GetMountainPalette(
				float terrainIndex,
				out half3 scree,
				out half3 lowRock,
				out half3 highRock,
				out half3 stripe)
			{
				int index = clamp((int)floor(terrainIndex + 0.5), 0, 4);
				scree = (half3)_HexBiomeScree[index].rgb;
				lowRock = (half3)_HexBiomeLowRock[index].rgb;
				highRock = (half3)_HexBiomeHighRock[index].rgb;
				stripe = (half3)_HexBiomeStripe[index].rgb;
			}

			float StripeBand(float height01, float center, float halfWidth, float noise)
			{
				return 1.0 - smoothstep(
					halfWidth * 0.52, halfWidth,
					abs(height01 + (noise - 0.5) * 0.035 - center));
			}

			half4 Frag(Varyings input) : SV_Target
			{
				half3 vertexNormal = normalize(input.normalWS);
				float height01 = saturate(input.relief.x);
				float landform = input.relief.y;
				float terrainIndex = floor(input.relief.z + 0.5);
				int biomeIndex = clamp((int)terrainIndex, 0, 4);
				float isMountain = step(1.5, landform);
				// Coverage alone describes the broad logical stamp. Multiplying it by
				// actual relief height prevents a nearly-flat transparent sheet from
				// revealing the underlying hex patch through different lighting.
				float hillPresence = smoothstep(0.08, 0.24, height01);
				float mountainPresence = smoothstep(0.025, 0.10, height01);
				float reliefPresence = lerp(
					hillPresence, mountainPresence, isMountain);
				float originalBlend = saturate(_HexHFOriginalBlend);
				// HF renders a single opaque chunk surface. In faithful mode coverage,
				// not relief height, owns the footprint so flat cells and mountain cells
				// are guaranteed to meet on the same continuous surface.
				float edgeFade = lerp(
					reliefPresence, input.relief.w, originalBlend);
				clip(edgeFade - 0.025);

				// HF's final diffuse was baked per pixel and already contained the
				// Oven height-offset light/shadow pass. Keep this as a separate path:
				// none of the newer rock, facet, posterization, or biome recolouring
				// belongs to HoneyFramework's final terrain material.
				if (originalBlend > 0.999)
				{
					half3 color = HF_EvaluateOriginalDiffuse(
						input.cellIndices.x, input.localPosition);

					bool editMode = false;
					#ifdef _HEX_MAP_EDIT_MODE
						editMode = true;
					#endif
					float4 cellData = GetCellData(
						input.cellIndices, 0, editMode);
					color *= lerp(0.25, 1.0, cellData.r);

					float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
					Light mainLight = GetMainLight(shadowCoord);
					half rawDiffuse = saturate(dot(vertexNormal, mainLight.direction));
					half diffuse = 0.3h + 0.7h * rawDiffuse;
					half3 lighting = SampleSH(vertexNormal) +
						mainLight.color * diffuse * mainLight.shadowAttenuation;
					color *= min(lighting, 1.2h);
					color = MixFog(color, input.fogFactor);
					return half4(color, 1.0h);
				}

				half3 faceNormal = normalize(cross(
					ddy(input.positionWS), ddx(input.positionWS)));
				faceNormal *= dot(faceNormal, vertexNormal) < 0.0 ? -1.0 : 1.0;
				float facetStrength = lerp(0.11, 0.62, isMountain) *
					smoothstep(0.06, 0.34, edgeFade);
				half3 normalWS = normalize(lerp(
					vertexNormal, faceNormal, facetStrength));
				float slope = 1.0 - saturate(normalWS.y);

				half3 legacyGround = SampleHFMixedReliefGround(
					input.positionWS, terrainIndex);
				half3 originalGround = originalBlend > 0.001 ?
					HF_EvaluateOriginalDiffuse(
						input.cellIndices.x, input.localPosition) : legacyGround;
				half3 ground = lerp(
					legacyGround, originalGround, originalBlend);
				float legacyArt = 1.0 - originalBlend;
				float macroNoise = Fbm(
					input.positionWS.xz * 0.055 + input.style.xy * 19.0);
				float fineNoise = Fbm(
					input.positionWS.xz * 0.23 + input.style.yz * 31.0);
				half3 color = ground;

				// Hills retain their biome material. Only the high, exposed side gets
				// a dry/high variant, which mirrors a separate per-biome hill material.
				float hillThreshold = _HexBiomeHillLine[biomeIndex];
				float hillHigh = (1.0 - isMountain) *
					smoothstep(hillThreshold - 0.07, hillThreshold + 0.15, height01 +
						(macroNoise - 0.5) * 0.09);
				half3 hillEarth = terrainIndex < 0.5 ? half3(0.62, 0.46, 0.24) :
					terrainIndex < 1.5 ? half3(0.38, 0.37, 0.2) :
					terrainIndex < 2.5 ? half3(0.55, 0.48, 0.29) :
					terrainIndex < 3.5 ? half3(0.43, 0.36, 0.27) :
					half3(0.84, 0.87, 0.87);
				// The biome remains visible on the crest, but it should emerge from
				// the shared ground colour instead of recolouring the entire mound.
				half hillEarthBlend = terrainIndex > 3.5 ? 0.25 : 0.15;
				half3 hillTop = lerp(ground, hillEarth, hillEarthBlend);
				float hillMaterialPresence = hillHigh *
					smoothstep(0.24, 0.68, edgeFade) * legacyArt;
				color = lerp(color, hillTop, hillMaterialPresence);

				half3 scree;
				half3 lowRock;
				half3 highRock;
				half3 stripeColor;
				GetMountainPalette(
					terrainIndex, scree, lowRock, highRock, stripeColor);

				float screeMask = isMountain *
					smoothstep(0.035, 0.17, height01) *
					(1.0 - smoothstep(0.3, 0.46, height01)) *
					smoothstep(0.04, 0.32, edgeFade) *
					smoothstep(0.28, 0.68, macroNoise) * legacyArt;
				half3 screeMaterial = scree * lerp(0.86, 1.13, fineNoise);
				color = lerp(color, screeMaterial, screeMask * 0.82);

				float rockFace = isMountain * saturate(
					smoothstep(0.12, 0.31, height01) * 0.78 +
					smoothstep(0.08, 0.48, slope) * 0.62);
				rockFace *= smoothstep(0.055, 0.3, edgeFade) * legacyArt;
				float highBand = smoothstep(
					0.54, 0.79, height01 + (macroNoise - 0.5) * 0.08);
				half3 rockSurface = SampleRockSurface(
					input.positionWS, normalWS, input.style.xy);
				float rockValue = dot(rockSurface, half3(0.299, 0.587, 0.114));
				half3 rockMaterial = lerp(lowRock, highRock, highBand) *
					lerp(0.62, 1.28, rockValue) * lerp(0.92, 1.06, fineNoise);
				half3 rockChromatic = rockSurface /
					max(rockValue, 0.16);
				rockMaterial *= lerp(half3(1.0, 1.0, 1.0),
					rockChromatic, 0.46);
				half3 mountainDecal = SAMPLE_TEXTURE2D(
					_Mountain_Color_Decal, sampler_Mountain_Color_Decal,
					saturate(input.localData.xy)).rgb;
				rockMaterial *= lerp(half3(1.0, 1.0, 1.0),
					mountainDecal * 2.0,
					saturate(_Use_Mountain_Color_Decal) * 0.38);
				half3 texturedNormal = SampleRockNormal(
					input.positionWS, normalWS, input.style.xy);
				normalWS = normalize(lerp(normalWS, texturedNormal, rockFace * 0.58));
				slope = 1.0 - saturate(normalWS.y);

				float bedding = 0.5 + 0.5 * sin(
					input.positionWS.y * 5.7 +
					input.positionWS.x * 0.31 - input.positionWS.z * 0.19 +
					input.style.x * 8.0);
				float fissure = smoothstep(0.03, 0.2, slope) *
					smoothstep(0.84, 0.97, bedding + (fineNoise - 0.5) * 0.24);
				rockMaterial *= lerp(1.0, 0.68, fissure);
				color = lerp(color, rockMaterial, rockFace);

				// The desert style uses several narrow height-controlled stripe
				// materials instead of a snow cap.
				float desert = 1.0 - step(0.5, terrainIndex);
				float desertStripes = max(
					StripeBand(height01, _HexDesertStripeCenters.x,
						_HexDesertStripeWidths.x, macroNoise),
					max(
						StripeBand(height01, _HexDesertStripeCenters.y,
							_HexDesertStripeWidths.y, macroNoise),
						max(
							StripeBand(height01, _HexDesertStripeCenters.z,
								_HexDesertStripeWidths.z, macroNoise),
							StripeBand(height01, _HexDesertStripeCenters.w,
								_HexDesertStripeWidths.w, macroNoise))));
				float stripeBreakup = smoothstep(
					0.24, 0.72, Fbm(input.positionWS.xz * 0.14 + input.style.xy * 9.0));
				color = lerp(
					color, stripeColor * lerp(0.9, 1.1, fineNoise),
					desert * isMountain * desertStripes * rockFace *
					lerp(0.16, 0.38, stripeBreakup));

				// Default mountains use a low/high top transition. The snow line is
				// lowered for cold biomes and disabled for the desert mountain set.
				float snowLine = _HexBiomeSnowLine[biomeIndex];
				float snowTransition = max(
					0.045, _HexSnowBand.y - _HexSnowBand.x);
				float snow = isMountain *
					smoothstep(snowLine, snowLine + snowTransition,
						height01 + (macroNoise - 0.5) * 0.075) *
					smoothstep(0.4, 0.76, normalWS.y) * legacyArt;
				half3 snowMaterial = (half3)_HexBiomeSnow[biomeIndex].rgb *
					lerp(0.88, 1.0, rockValue);
				color = lerp(color, snowMaterial, snow);

				// Keep contact darkening in a narrow low-height band. The previous
				// inverted mask darkened the complete low relief skirt and exposed its
				// stamp/hex silhouette even when geometry was almost flat.
				float contactBand = smoothstep(0.025, 0.07, height01) *
					(1.0 - smoothstep(0.14, 0.28, height01));
				float contactAO = contactBand * smoothstep(0.16, 0.5, edgeFade);
				contactAO *= legacyArt;
				color *= 1.0 - contactAO * lerp(0.055, 0.085, isMountain);

				bool editMode = false;
				#ifdef _HEX_MAP_EDIT_MODE
					editMode = true;
				#endif
				float4 cellData = GetCellData(input.cellIndices, 0, editMode);
				color *= lerp(0.25, 1.0, cellData.r);

				float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
				Light mainLight = GetMainLight(shadowCoord);
				half rawDiffuse = saturate(dot(normalWS, mainLight.direction));
				half paintedDiffuse = floor(rawDiffuse * 4.0 + 0.5) * 0.25;
				rawDiffuse = lerp(
					rawDiffuse, paintedDiffuse, _HexCivStyleStrength * 0.42);
				half diffuse = 0.3 + 0.7 * rawDiffuse;
				half3 lighting = SampleSH(normalWS) +
					mainLight.color * diffuse * mainLight.shadowAttenuation;
				color *= min(lighting, 1.2);
				color *= lerp(1.0, 0.89, rockFace * slope *
					smoothstep(0.25, 0.72, 1.0 - macroNoise));
				color = HexCivGrade(
					color, input.positionWS, rawDiffuse,
					lerp(0.86, 1.0, isMountain));
				color = MixFog(color, input.fogFactor);
				return half4(color, 1.0);
			}
			ENDHLSL
		}

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On
			ZTest LEqual
			ColorMask 0
			Cull Back

			HLSLPROGRAM
			#pragma target 4.6
			#pragma require tessellation tessHW 2darray
			#pragma vertex ShadowVert
			#pragma hull ShadowHull
			#pragma domain ShadowDomain
			#pragma fragment ShadowFrag
			#pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "HexCellData.hlsl"
			SAMPLER(sampler_linear_clamp);
			#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
			#include "HexTerrainShape.hlsl"

			float3 _LightDirection;
			float3 _LightPosition;
			float _HexReliefTessellation;
			float _HexReliefTessellationStart;
			float _HexReliefTessellationEnd;

			struct ShadowAttributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				float3 cellIndices : TEXCOORD1;
				float2 localPosition : TEXCOORD2;
			};

			struct ShadowControlPoint
			{
				float4 positionOS : INTERNALTESSPOS;
				float3 normalOS : NORMAL;
				float3 cellIndices : TEXCOORD1;
				float2 localPosition : TEXCOORD2;
			};

			struct ShadowTessellationFactors
			{
				float edge[3] : SV_TessFactor;
				float inside : SV_InsideTessFactor;
			};

			struct ShadowVaryings
			{
				float4 positionCS : SV_POSITION;
				float edgeFade : TEXCOORD0;
			};

			ShadowControlPoint ShadowVert(ShadowAttributes input)
			{
				ShadowControlPoint output;
				output.positionOS = input.positionOS;
				output.normalOS = input.normalOS;
				output.cellIndices = input.cellIndices;
				output.localPosition = input.localPosition;
				return output;
			}

			float ShadowTessellationFactor(float3 positionOS)
			{
				float3 positionWS = TransformObjectToWorld(positionOS);
				float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
				float distanceFade = saturate(
					(distanceToCamera - _HexReliefTessellationStart) /
					max(_HexReliefTessellationEnd - _HexReliefTessellationStart, 1.0));
				return max(1.0, lerp(_HexReliefTessellation, 1.0, distanceFade));
			}

			ShadowTessellationFactors ShadowPatchConstants(
				InputPatch<ShadowControlPoint, 3> patch)
			{
				ShadowTessellationFactors factors;
				factors.edge[0] = ShadowTessellationFactor(
					(patch[1].positionOS.xyz + patch[2].positionOS.xyz) * 0.5);
				factors.edge[1] = ShadowTessellationFactor(
					(patch[2].positionOS.xyz + patch[0].positionOS.xyz) * 0.5);
				factors.edge[2] = ShadowTessellationFactor(
					(patch[0].positionOS.xyz + patch[1].positionOS.xyz) * 0.5);
				factors.inside = ShadowTessellationFactor(
					(patch[0].positionOS.xyz + patch[1].positionOS.xyz +
					patch[2].positionOS.xyz) / 3.0);
				return factors;
			}

			[domain("tri")]
			[outputcontrolpoints(3)]
			[outputtopology("triangle_cw")]
			[partitioning("fractional_even")]
			[patchconstantfunc("ShadowPatchConstants")]
			ShadowControlPoint ShadowHull(
				InputPatch<ShadowControlPoint, 3> patch,
				uint controlPointId : SV_OutputControlPointID)
			{
				return patch[controlPointId];
			}

			[domain("tri")]
			ShadowVaryings ShadowDomain(
				ShadowTessellationFactors factors,
				const OutputPatch<ShadowControlPoint, 3> patch,
				float3 barycentricCoordinates : SV_DomainLocation)
			{
				ShadowVaryings output;
				float4 positionOS =
					patch[0].positionOS * barycentricCoordinates.x +
					patch[1].positionOS * barycentricCoordinates.y +
					patch[2].positionOS * barycentricCoordinates.z;
				float3 cellIndices =
					patch[0].cellIndices * barycentricCoordinates.x +
					patch[1].cellIndices * barycentricCoordinates.y +
					patch[2].cellIndices * barycentricCoordinates.z;
				float2 localPosition =
					patch[0].localPosition * barycentricCoordinates.x +
					patch[1].localPosition * barycentricCoordinates.y +
					patch[2].localPosition * barycentricCoordinates.z;
				HFReliefSurface surface = HF_EvaluateRelief(
					cellIndices.x, localPosition);
				positionOS.y = surface.y;
				float3 positionWS = TransformObjectToWorld(positionOS.xyz);
				float3 normalWS = TransformObjectToWorldNormal(float3(0.0, 1.0, 0.0));
				#if _CASTING_PUNCTUAL_LIGHT_SHADOW
					float3 lightDirectionWS = normalize(_LightPosition - positionWS);
				#else
					float3 lightDirectionWS = _LightDirection;
				#endif
				float4 positionCS = TransformWorldToHClip(ApplyShadowBias(
					positionWS, normalWS, lightDirectionWS));
				#if UNITY_REVERSED_Z
					positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
				#else
					positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
				#endif
				output.positionCS = positionCS;
				float isMountain = step(1.5, surface.landform);
				float hillPresence = smoothstep(0.08, 0.24, surface.height01);
				float mountainPresence = smoothstep(0.025, 0.10, surface.height01);
				output.edgeFade = lerp(
					lerp(hillPresence, mountainPresence, isMountain),
					surface.coverage,
					saturate(_HexHFOriginalBlend));
				return output;
			}

			half4 ShadowFrag(ShadowVaryings input) : SV_Target
			{
				clip(smoothstep(0.12, 0.48, input.edgeFade) - 0.35);
				return 0;
			}
			ENDHLSL
		}
	}
}
