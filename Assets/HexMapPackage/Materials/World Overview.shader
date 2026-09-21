Shader "Hex Map/World Overview"
{
	Properties
	{
		[NoScaleOffset] _MainTex ("Baked political base (RGB color, A land)", 2D) = "white" {}
		[NoScaleOffset] _ReliefTex ("Relief + masks (R height/ocean, G hatch country, B hatch, A fill)", 2D) = "black" {}
		_OceanTint ("Strategic ocean tint", Color) = (0.08, 0.18, 0.38, 1)
		_Brightness ("Strategic brightness", Range(0.5, 1.2)) = 0.94
		_CountryBreathStrength ("Country breath", Range(0, 0.12)) = 0.040
		_CountryBreathTranslucency ("Country translucency", Range(0, 0.15)) = 0.040
		_CountryRimStrength ("Country border depth", Range(0, 1)) = 0.80
		_CountryRimStep ("Country rim step lift", Range(0, 1)) = 0.72
		_CountryInnerGlow ("Country inner glow curve", Range(0.2, 1.2)) = 0.50
		_CountryRimPlateau ("Country border plateau", Range(0, 0.4)) = 0.02
		_CountryInteriorLift ("Country interior lift", Range(0, 1)) = 0.92
		_TerrainReliefStrength ("Terrain relief", Range(0, 0.25)) = 0.07
		_PaperGrain ("Paper grain", Range(0, 0.12)) = 0.035
		_RimEmbossStrength ("Dark-rim plate emboss", Range(0, 1)) = 1.0
		_OceanRimStrength ("Ocean shallow rim", Range(0, 1)) = 0.58
		_OceanInteriorDepth ("Ocean interior depth", Range(0, 1)) = 0.72
		_BorderHatchColor ("Border hatch ink", Color) = (0.01, 0.008, 0.005, 1)
		_BorderHatchSpacing ("Border hatch spacing", Range(2.5, 12)) = 4.5
		_BorderHatchSlope ("Border hatch slope", Range(-1.4, 1.4)) = 0.72
		_BaseBorderHatch ("Idle border hatch", Range(0, 1)) = 0
		_HoveredBorderHatch ("Hovered country hatch", Range(0, 1)) = 0.78
		_SelectedStripeSpacing ("Selected wide stripe spacing", Range(2, 48)) = 3.5
		_SelectedStripeSlope ("Selected wide stripe slope", Range(-1.4, 1.4)) = 0.92
		_SelectedStripeGray ("Selected stripe darken (×0.8 = 20% gray)", Range(0.5, 1)) = 0.80
		_SelectedStripeSoft ("Selected stripe edge soft", Range(0.01, 0.12)) = 0.022
		_SelectedStripeDuty ("Selected dark band width", Range(0.35, 0.65)) = 0.50
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Opaque"
			"Queue" = "Geometry-10"
			"RenderPipeline" = "UniversalPipeline"
		}

		Pass
		{
			Name "StrategicPoliticalSurface"
			Tags { "LightMode" = "UniversalForward" }
			Cull Back
			ZWrite On

			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "HexCellData.hlsl"

			TEXTURE2D(_MainTex);
			SAMPLER(sampler_MainTex);
			TEXTURE2D(_ReliefTex);
			SAMPLER(sampler_ReliefTex);
			float4 _MainTex_TexelSize;
			float4 _ReliefTex_TexelSize;
			float _HexHoveredCountryId;
			float _HexSelectedCountryId;

			CBUFFER_START(UnityPerMaterial)
				half4 _OceanTint;
				half _Brightness;
				half _CountryBreathStrength;
				half _CountryBreathTranslucency;
				half _CountryRimStrength;
				half _CountryRimStep;
				half _CountryInnerGlow;
				half _CountryRimPlateau;
				half _CountryInteriorLift;
				half _TerrainReliefStrength;
				half _PaperGrain;
				half _RimEmbossStrength;
				half _OceanRimStrength;
				half _OceanInteriorDepth;
				half4 _BorderHatchColor;
				half _BorderHatchSpacing;
				half _BorderHatchSlope;
				half _BaseBorderHatch;
				half _HoveredBorderHatch;
				half _SelectedStripeSpacing;
				half _SelectedStripeSlope;
				half _SelectedStripeGray;
				half _SelectedStripeSoft;
				half _SelectedStripeDuty;
			CBUFFER_END

			struct Attributes
			{
				float4 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 positionWS : TEXCOORD1;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				output.uv = input.uv;
				output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
				return output;
			}

			float Hash21(float2 p)
			{
				float3 p3 = frac(float3(p.xyx) * 0.1031);
				p3 += dot(p3, p3.yzx + 33.33);
				return frac((p3.x + p3.y) * p3.z);
			}

			float SoftNoise(float2 p)
			{
				float2 i = floor(p);
				float2 f = frac(p);
				f = f * f * (3.0 - 2.0 * f);
				return lerp(
					lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x),
					lerp(Hash21(i + float2(0, 1)), Hash21(i + 1), f.x),
					f.y);
			}

			half CountryLuma(half3 c)
			{
				return dot(c, half3(0.299h, 0.587h, 0.114h));
			}

			// Scale chroma (1 = keep, >1 = more vivid, <1 = muted).
			half3 ScaleCountrySaturation(half3 political, half satScale)
			{
				half luma = CountryLuma(political);
				return saturate(luma + (political - luma) * satScale);
			}

			// Deepen by same-hue watercolor pigment — craft depth, never charcoal.
			half3 DeepenCountryTint(half3 political, half amount)
			{
				half luma = CountryLuma(political);
				half lightProtect = saturate((luma - 0.40h) / 0.50h);
				half3 pigment = political * half3(0.92h, 0.96h, 0.90h);
				pigment = lerp(pigment, political, 0.35h);
				half darkMul = lerp(
					lerp(0.82h, 0.68h, amount),
					lerp(0.93h, 0.86h, amount),
					lightProtect);
				half3 deepened = lerp(political * darkMul, pigment * darkMul, 0.55h);
				deepened = lerp(deepened, political, lightProtect * 0.28h);
				return saturate(deepened);
			}

			// Border rim deepen (uniform — no pale-country special cases).
			half3 DeepenCountryTintForRim(half3 political, half amount)
			{
				amount = saturate(amount);
				half3 pigment = political * half3(0.88h, 0.92h, 0.86h);
				pigment = lerp(pigment, political, 0.20h);
				half darkMul = lerp(0.68h, 0.48h, amount);
				half3 deepened = lerp(political * darkMul, pigment * darkMul, 0.55h);
				half3 chroma = ScaleCountrySaturation(deepened, 1.08h);
				return saturate(lerp(deepened, chroma, 0.28h));
			}

			half3 EvaluateCountryFill(
				half3 political, half inland, half borderBand)
			{
				// Watercolor shelf: dark lip → flat plateau → luminous core.
				half plateauWidth = max(_CountryRimPlateau, 0.02h);
				half3 lipWash = DeepenCountryTint(
					political, 0.40h * _CountryRimStrength);
				half3 midWash = lerp(
					lipWash,
					political,
					0.28h + 0.30h * _CountryRimStep);
				midWash = saturate(midWash);

				// Flat pigment shelf just inland of the rim (museum print step).
				half plateauMask = smoothstep(0.02h, 0.10h, inland) *
					(1.0h - smoothstep(0.10h, 0.10h + plateauWidth * 1.8h, inland));
				half3 plateauTint = DeepenCountryTint(
					political, 0.28h * _CountryRimStrength);
				midWash = lerp(midWash, plateauTint, plateauMask * 0.72h);

				half3 coreColor = lerp(
					political,
					political * half3(1.14h, 1.11h, 1.06h) + half3(0.07h, 0.060h, 0.045h),
					_CountryInteriorLift);
				coreColor = lerp(
					coreColor,
					half3(0.96h, 0.93h, 0.86h),
					0.30h * _CountryInteriorLift);
				coreColor = saturate(coreColor);

				half glow = saturate(inland);
				glow = glow * glow * (3.0h - 2.0h * glow);
				glow = pow(max(glow, 0.0h), max(_CountryInnerGlow, 0.45h));
				half3 fill = lerp(midWash, coreColor, glow);

				// Soft border ambient weight (broader than emboss lip).
				half borderAO = lerp(0.90h, 1.0h, saturate(inland * 1.15h));
				fill *= lerp(1.0h, borderAO, 0.65h);
				return fill;
			}

			// Fixed deepen + simple artistic emboss on the border lip only.
			half3 ApplyHexBorderRim(
				half3 land, half3 political, HexGridData grid)
			{
				float2 borderNormal;
				half edgeDist = (half)EvaluatePoliticalBorderEdge(
					grid, borderNormal);
				if (edgeDist > 0.999h)
				{
					return land;
				}

				half aa = max((half)grid.distanceSmoothing, 0.016h);

				half rimBand = 1.0h - smoothstep(0.0h, 0.22h + aa, edgeDist);
				rimBand *= saturate(_CountryRimStrength + 0.15h);

				half outerLip = 1.0h - smoothstep(0.0h, 0.10h + aa, edgeDist);
				half3 deepenSoft = DeepenCountryTintForRim(
					political, 0.72h * _CountryRimStrength);
				half3 deepenRim = DeepenCountryTintForRim(
					political, 0.88h * _CountryRimStrength);
				half3 rimFill = lerp(deepenSoft, deepenRim, outerLip);
				land = lerp(land, rimFill, rimBand);

				UNITY_BRANCH
				if (_RimEmbossStrength < 0.001h)
				{
					return land;
				}

				// Narrow emboss band: clear lit / shade 色阶 along the edge.
				half embossMask = 1.0h - smoothstep(0.0h, 0.14h + aa, edgeDist);
				embossMask *= saturate(_RimEmbossStrength);

				float2 lightXZ = normalize(float2(-0.62, 0.45));
				half facing = (half)dot(normalize(borderNormal), lightXZ);
				half lit = saturate(facing * 0.5h + 0.5h);
				lit = smoothstep(0.28h, 0.72h, lit);

				half3 shadeTone = DeepenCountryTintForRim(political, 1.0h);
				half3 lightTone = ScaleCountrySaturation(political, 1.30h);
				lightTone = saturate(
					lightTone * half3(1.22h, 1.16h, 1.08h) +
					half3(0.08h, 0.06h, 0.04h));
				half3 embossed = lerp(shadeTone, lightTone, lit);
				land = lerp(land, embossed, embossMask);

				return land;
			}

			half ComputeTerrainLuminance(float2 uv, half reliefHeight)
			{
				// Prefer baked height shade; keep a tiny derivative term only.
				// Full ddx/ddy normals shimmer hard while the camera pans.
				half dx = ddx(reliefHeight);
				half dy = ddy(reliefHeight);
				float3 normal = normalize(float3(-dx * 18.0, 1.0, -dy * 18.0));
				float3 lightDir = normalize(float3(-0.40, 0.80, 0.45));
				half sun = lerp(0.96h, 1.03h, saturate(dot(normal, lightDir)));
				half height = lerp(0.92h, 1.04h, reliefHeight);
				half shade = height * sun;
				return lerp(1.0h, shade, _TerrainReliefStrength * 3.0h);
			}

			half MicroPaperGrain(float2 mapPosition)
			{
				// Pulp body + anisotropic fiber — tactile print stock, not TV noise.
				float pulp = SoftNoise(mapPosition * 0.17 + 2.3);
				float fiber = SoftNoise(mapPosition * float2(0.62, 0.09) + 7.1);
				float tooth = SoftNoise(mapPosition * 0.48 + 13.7);
				float g = pulp * 0.55 + fiber * 0.28 + tooth * 0.17;
				half amp = _PaperGrain;
				return lerp(1.0h - amp, 1.0h + amp, g);
			}

			// Soft gallery light across the sheet (large-scale, pan-stable).
			half EvaluateStudioSheetLight(float2 uv)
			{
				float2 d = uv - float2(0.46, 0.58);
				half key = saturate(1.0h - dot(d, d) * 2.35h);
				half slant = saturate(dot(uv - 0.5h, float2(-0.40h, 0.55h)) * 0.85h + 0.55h);
				return lerp(0.96h, 1.045h, key * 0.72h + slant * 0.28h);
			}

			// Living paper wash — slow, tiny; never reads as camera jitter.
			half3 ApplyCountryBreath(half3 land, float2 uv)
			{
				half phase = sin(_Time.y * 0.09 + uv.x * 1.7 + uv.y * 1.1) * 0.5h + 0.5h;
				half breath = phase * _CountryBreathStrength;
				half3 lifted = land * half3(1.035h, 1.025h, 1.015h);
				land = lerp(land, lifted, breath);
				half luma = CountryLuma(land);
				half3 airy = lerp(luma.xxx, land, 0.88h) * 1.02h;
				land = lerp(land, airy, _CountryBreathTranslucency * phase);
				return land;
			}

			// Coast contact shade: land lip darkens, ocean gets under-continent shadow.
			void ApplyCoastContactShade(
				inout half3 land, inout half3 ocean, half landCoverage, half oceanProximity)
			{
				half coastBand = saturate(1.0h - abs(landCoverage - 0.5h) * 3.6h);
				coastBand = coastBand * coastBand;
				half landLip = coastBand * landCoverage;
				half oceanLip = coastBand * (1.0h - landCoverage);
				land = lerp(land, land * half3(0.86h, 0.84h, 0.80h), landLip * 0.55h);
				half underMass = saturate(oceanProximity * 1.15h) * oceanLip;
				ocean = lerp(
					ocean,
					ocean * half3(0.72h, 0.78h, 0.88h),
					underMass * 0.70h);
			}

			half DrawChartLine(float coord, half width)
			{
				half dist = abs(frac(coord) - 0.5h);
				half fw = fwidth(coord);
				return 1.0h - smoothstep(width * fw, (width + 1.0h) * fw, dist);
			}

			half3 EvaluateOceanBase(float mapLat)
			{
				// Match globe navy / midnight slate (EvaluateGlobeOcean).
				half3 deepSea = half3(0.06h, 0.14h, 0.32h);
				half3 midSea = half3(0.09h, 0.20h, 0.40h);
				half3 shallowSea = half3(0.14h, 0.30h, 0.48h);
				half3 polar = half3(0.14h, 0.24h, 0.40h);

				float latDist = abs(mapLat - 0.5) * 2.0;
				float equatorBand = 1.0 - smoothstep(0.0, 0.28, latDist);
				float polarBand = smoothstep(0.70, 0.95, latDist);

				half3 color = lerp(midSea, deepSea, 0.42h);
				color = lerp(color, shallowSea, equatorBand * 0.22h);
				color = lerp(color, polar, polarBand * 0.28h);

				half chartBand = DrawChartLine(mapLat * 11.0, 0.35h);
				color = lerp(color, deepSea, chartBand * 0.04h);

				float depthBand = smoothstep(0.18, 0.82, mapLat);
				color = lerp(color, deepSea, depthBand * 0.10h);

				color = lerp(color, _OceanTint.rgb, 0.35h);
				return saturate(color);
			}

			half3 EvaluateOceanFill(half3 ocean, half oceanFillDepth)
			{
				half rim = smoothstep(0.08h, 0.92h, oceanFillDepth);
				rim = rim * rim * (3.0h - 2.0h * rim);
				half inward = 1.0h - oceanFillDepth;
				half pool = smoothstep(0.02h, 0.98h, inward);
				pool = pool * pool * (3.0h - 2.0h * pool);

				half3 shallow = ocean * half3(1.18h, 1.12h, 0.90h);
				half3 abyss = ocean * half3(0.60h, 0.70h, 1.24h);
				half3 color = ocean;
				color = lerp(color, shallow, rim * _OceanRimStrength);
				color = lerp(color, abyss, pool * _OceanInteriorDepth);
				return color;
			}

			// Static soft water sheen — same language as globe, no animated flow.
			half3 EvaluateOceanSpecular(half3 ocean, float3 positionWS)
			{
				float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
				float3 normal = float3(0.0, 1.0, 0.0);
				half facing = saturate(dot(normal, viewDir));
				half fresnel = pow(1.0h - facing, 3.0h);
				float3 sunDir = normalize(float3(0.32, 0.78, 0.42));
				float3 halfVec = normalize(sunDir + viewDir);
				half spec = pow(saturate(dot(normal, halfVec)), 64.0h);
				ocean += half3(0.45h, 0.70h, 0.95h) *
					(spec * 0.18h + fresnel * 0.04h) * facing;
				return saturate(ocean);
			}

			half MatchCountryId(float cellCountryId, float targetId)
			{
				return step(0.5h, targetId) *
					(1.0h - step(0.1h, abs(cellCountryId - targetId)));
			}

			// Same lift used for sticky select and transient hover.
			half3 ApplyCountryBrighten(half3 land, half mask)
			{
				if (mask < 0.001h)
				{
					return land;
				}

				half3 bright = land * half3(1.28h, 1.22h, 1.14h) +
					half3(0.07h, 0.058h, 0.04h);
				bright = ScaleCountrySaturation(bright, 1.18h);
				bright = lerp(bright, half3(1.0h, 0.97h, 0.90h), 0.10h);
				return lerp(land, saturate(bright), mask * 0.78h);
			}

			// 20% gray wide diagonal stripes: 50/50 band/gap, land × 0.8 on dark bands.
			half3 ApplySelectedWideStripes(
				half3 land, half mask, float2 mapPosition)
			{
				if (mask < 0.001h)
				{
					return land;
				}

				float2 stripeDir = normalize(float2(
					-_SelectedStripeSlope, 1.0));
				float spacing = max(_SelectedStripeSpacing, 2.0h);
				float along = dot(mapPosition, stripeDir) / spacing;
				float phase = frac(along);
				half aa = max(
					(half)fwidth(along),
					max(_SelectedStripeSoft, 0.01h));
				half duty = clamp(_SelectedStripeDuty, 0.35h, 0.65h);

				// Equal-width stripe and gap when duty ≈ 0.5.
				half band = 1.0h - smoothstep(duty - aa, duty + aa, phase);

				half darken = saturate(_SelectedStripeGray);
				half3 inked = land * darken;
				return lerp(land, inked, band * mask);
			}

			half EvaluateBorderHatch(
				float2 positionSS, half borderProximity)
			{
				// Continuous clear diagonals — no along-stroke breaks.
				float2 hatchNormal = normalize(float2(-_BorderHatchSlope, 1.0));
				float spacing = max(_BorderHatchSpacing, 2.5h);
				float hatchCoordinate = dot(positionSS, hatchNormal) / spacing;
				float hatchDistance =
					abs(frac(hatchCoordinate) - 0.5) * spacing;

				// Thin, even strokes (slight width variance only).
				float hatchIndex = floor(hatchCoordinate);
				float halfWidthPixels = lerp(
					0.55, 0.85, Hash21(float2(hatchIndex, 19.7)));
				float aa = max(fwidth(hatchDistance), 0.35);
				half hatchLine = 1.0h - smoothstep(
					halfWidthPixels - aa * 0.25,
					halfWidthPixels + aa * 0.55,
					hatchDistance);

				half outwardFade = smoothstep(0.02h, 0.98h, borderProximity);
				outwardFade = outwardFade * outwardFade;
				return hatchLine * outwardFade;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				half4 atlas = SAMPLE_TEXTURE2D(
					_MainTex, sampler_MainTex, input.uv);
				half4 surface = SAMPLE_TEXTURE2D(
					_ReliefTex, sampler_ReliefTex, input.uv);

				half edgeWidth = max(fwidth(atlas.a) * 0.72h, 0.012h);
				half landCoverage = smoothstep(
					0.5h - edgeWidth, 0.5h + edgeWidth, atlas.a);

				HexGridData countryGrid = GetHexGridData(input.positionWS.xz);
				float2 mapPosition = input.uv *
					max(_MainTex_TexelSize.zw * 0.5, float2(1.0, 1.0));

				// 建造选格：沿用 overview 纸张/海洋框架，陆地不用政治填色，只走选格美术。
				UNITY_BRANCH
				if (_HexLandBuildSelectionActive > 0.5h)
				{
					half grain = MicroPaperGrain(mapPosition);
					half3 ocean = EvaluateOceanBase(input.uv.y);
					ocean = EvaluateOceanFill(ocean, surface.r);
					ocean *= half3(0.88h, 0.92h, 0.98h);
					ocean *= lerp(0.98h, 1.01h, SoftNoise(mapPosition * 0.11 + 5.2));

					half3 sheetLand = half3(0.68h, 0.70h, 0.73h) * grain;
					half3 land = ApplyLandBuildSelectionOverlay(
						sheetLand, countryGrid);
					half3 oceanSel = ApplyLandBuildSelectionOverlay(
						ocean, countryGrid);

					half3 color = lerp(oceanSel, land, landCoverage);
					color *= EvaluateStudioSheetLight(input.uv);
					return half4(saturate(color * _Brightness), 1.0h);
				}

				// Inland from the already-fetched relief alpha (no extra tap).
				half inland = surface.a;
				half terrain = ComputeTerrainLuminance(input.uv, surface.r);
				half3 land = EvaluateCountryFill(
					atlas.rgb, inland, 0.0h);
				land *= terrain;
				land = ApplyCountryBreath(land, input.uv);

				// Fixed deepen + artistic emboss on hex border edges.
				land = ApplyHexBorderRim(land, atlas.rgb, countryGrid);

				float2 grainBorderN;
				half grainEdgeDist = (half)EvaluatePoliticalBorderEdge(
					countryGrid, grainBorderN);
				half hexLip = 1.0h - smoothstep(0.0h, 0.18h, grainEdgeDist);
				half grain = MicroPaperGrain(mapPosition);
				land *= lerp(grain, 1.0h, hexLip * 0.9h);

				// Soft paper sheen — gallery specular catching fiber (very subtle).
				half sheen = SoftNoise(mapPosition * 0.07 + 0.4);
				half sheenMask = saturate(inland * 0.85h) * (1.0h - hexLip);
				land = lerp(
					land,
					saturate(land * half3(1.06h, 1.04h, 1.02h) + half3(0.02h, 0.016h, 0.01h)),
					sheen * sheenMask * 0.12h);

				half3 ocean = EvaluateOceanBase(input.uv.y);
				ocean = EvaluateOceanFill(ocean, surface.r);
				ocean = EvaluateOceanSpecular(ocean, input.positionWS);
				ocean *= lerp(0.988h, 1.008h, SoftNoise(mapPosition * 0.11 + 5.2));

				ApplyCoastContactShade(land, ocean, landCoverage, surface.r);

				float cellCountryId = GetHexCountryId(
					countryGrid.cellOffsetCoordinates);
				half selectedMask = MatchCountryId(
					cellCountryId, _HexSelectedCountryId);
				half hoveredMask = MatchCountryId(
					cellCountryId, _HexHoveredCountryId);
				// Hover gets the same brighten as select; select stays sticky.
				half brightenMask = max(selectedMask, hoveredMask);
				land = ApplyCountryBrighten(land, brightenMask);
				land = ApplySelectedWideStripes(
					land, selectedMask, mapPosition);

				half3 color = lerp(ocean, land, landCoverage);
				color *= EvaluateStudioSheetLight(input.uv);

				// Occupation stripes + optional cell overlay (detail views).
				{
					HexGridData grid = countryGrid;
					color = ApplyOccupationStripes(color, grid);
					UNITY_BRANCH
					if (_HexCellOverlayStrength > 0.001)
					{
						color = ApplyHexMapSurfaceOverlay(
							color, grid, 0.0, 0.0);
					}
				}

				// Idle frontier hatch — faint permanent inked border craft.
				half hatchProximity = surface.b;
				UNITY_BRANCH
				if (_BaseBorderHatch > 0.001h && hatchProximity > 0.04h)
				{
					half idle = EvaluateBorderHatch(
						input.positionCS.xy, hatchProximity) *
						_BaseBorderHatch *
						smoothstep(0.04h, 0.55h, hatchProximity);
					idle = smoothstep(0.18h, 0.78h, idle);
					color = lerp(color, _BorderHatchColor.rgb, idle * 0.55h);
				}

				// Outward hatch for hover and/or selected country (selected is sticky).
				half hasHoveredCountry = step(0.5h, _HexHoveredCountryId);
				half hasSelectedCountry = step(0.5h, _HexSelectedCountryId);
				UNITY_BRANCH
				if ((hasHoveredCountry > 0.5h || hasSelectedCountry > 0.5h) &&
					_HoveredBorderHatch > 0.001h)
				{
					float2 reliefCoord =
						input.uv * _ReliefTex_TexelSize.zw;
					float2 reliefUV =
						(floor(reliefCoord) + 0.5) * _ReliefTex_TexelSize.xy;
					half4 hatchSample = SAMPLE_TEXTURE2D(
						_ReliefTex, sampler_ReliefTex, reliefUV);
					half hatchSourceId = round(hatchSample.g * 255.0h);
					half proximity = hatchSample.b;
					half matchHover = hasHoveredCountry * (1.0h - step(
						0.1h, abs(hatchSourceId - _HexHoveredCountryId)));
					half matchSelected = hasSelectedCountry * (1.0h - step(
						0.1h, abs(hatchSourceId - _HexSelectedCountryId)));
					half hatchStrength = _HoveredBorderHatch *
						max(matchHover, matchSelected) *
						step(0.04h, proximity);
					if (hatchStrength > 0.001h)
					{
						half hatch = EvaluateBorderHatch(
							input.positionCS.xy, proximity) * hatchStrength;
						half ink = saturate(hatch);
						ink = smoothstep(0.12h, 0.72h, ink);
						color = lerp(color, _BorderHatchColor.rgb, ink);
					}
				}

				return half4(saturate(color * _Brightness), 1.0h);
			}
			ENDHLSL
		}
	}
}
