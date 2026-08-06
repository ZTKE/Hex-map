#include "HexCellData.hlsl"
#include "Water.hlsl"

#include "Hex Civilization Style.hlsl"

float4 _HexDeepOceanColor;
float4 _HexShallowWaterColor;
float4 _HexShoreFoamColor;
float4 _HexWetSandColor;
float4 _HexDrySandColor;
float4 _HexRiverWaterColor;
float4 _HexRiverBankColor;
float _HexWaterStyleBlend;

TEXTURE2D(_HexHFRiverMixer);
SAMPLER(sampler_HexHFRiverMixer);
// Shader Graph cannot legally reuse a named texture sampler for HF's other
// texture objects. Use Unity's recognized inline state instead; every HF
// lookup still shares this single linear-clamp sampler state.
SAMPLER(sampler_linear_clamp);
SAMPLER(sampler_point_clamp);
#define HF_TERRAIN_LINEAR_SAMPLER sampler_linear_clamp
#define HF_TERRAIN_POINT_SAMPLER sampler_point_clamp
#include "HexTerrainShape.hlsl"
float _HexHFRiverMixerStrength;

float2 HexHFRiverUV(float2 riverUV)
{
	return float2(saturate(riverUV.x), frac(riverUV.y));
}

float HexHFRiverMask(float2 riverUV)
{
	float2 hfUV = HexHFRiverUV(riverUV);
	float compactMask = SAMPLE_TEXTURE2D(
		_HexHFRiverMixer, sampler_HexHFRiverMixer, hfUV).r;
	float originalMask = SAMPLE_TEXTURE2D(
		_HFRiverMixer, sampler_HexHFRiverMixer, hfUV).r;
	float mask = lerp(
		compactMask, originalMask, saturate(_HexHFOriginalBlend));
	return lerp(1.0, mask, saturate(_HexHFRiverMixerStrength));
}

float3 HexHFOriginalRiverDiffuse(float2 riverUV)
{
	return SAMPLE_TEXTURE2D(
		_HFRiverDiffuse, sampler_HexHFRiverMixer,
		HexHFRiverUV(riverUV)).rgb;
}

float3 HexStyledColor(float3 fallback, float3 styled)
{
	return lerp(fallback, styled, saturate(_HexWaterStyleBlend));
}

float3 HexShoreColor(
	float shore,
	float foam,
	float waves,
	float2 worldXZ,
	float3 fallback)
{
	float waterDepth = smoothstep(0.02, 0.68, shore);
	float3 ocean = lerp(
		_HexDeepOceanColor.rgb, _HexShallowWaterColor.rgb, waterDepth);
	float beach = smoothstep(0.58, 0.78, shore);
	float3 sand = lerp(
		_HexWetSandColor.rgb, _HexDrySandColor.rgb,
		smoothstep(0.7, 0.98, shore));
	float3 styled = lerp(ocean, sand, beach);
	float foamBand = foam * (1.0 - smoothstep(0.68, 0.84, shore));
	styled += _HexShoreFoamColor.rgb * foamBand * 0.62;
	styled += _HexShoreFoamColor.rgb * waves * (1.0 - beach) * 0.14;
	return HexCivGrade(
		HexStyledColor(fallback, styled),
		float3(worldXZ.x, 0.0, worldXZ.y),
		0.68 + waves * 0.22,
		0.58);
}

// Used by Water and Water Shore shader graphs.
void GetVertexCellData_float(
	float3 Indices,
	float3 Weights,
	bool EditMode,
	out float2 Visibility)
{
	float4 cell0 = GetCellData(Indices, 0, EditMode);
	float4 cell1 = GetCellData(Indices, 1, EditMode);
	float4 cell2 = GetCellData(Indices, 2, EditMode);
	
	Visibility = 0;
	Visibility.x =
		cell0.x * Weights.x + cell1.x * Weights.y + cell2.x * Weights.z;
	Visibility.x = lerp(0.25, 1, Visibility.x);
	Visibility.y =
		cell0.y * Weights.x + cell1.y * Weights.y + cell2.y * Weights.z;
}

// Used by shader graphs that cross a cell edge: Estuary, River, and Road.
void GetVertexCellDataEdge_float(
	float3 Indices,
	float2 Weights,
	bool EditMode,
	out float2 Visibility)
{
	float4 cell0 = GetCellData(Indices, 0, EditMode);
	float4 cell1 = GetCellData(Indices, 1, EditMode);
	
	Visibility = 0;
	Visibility.x = cell0.x * Weights.x + cell1.x * Weights.y;
	Visibility.x = lerp(0.25, 1, Visibility.x);
	Visibility.y = cell0.y * Weights.x + cell1.y * Weights.y;
}

void GetFragmentDataEstuary_float(
	UnityTexture2D NoiseTexture,
	float2 RiverUV,
	float2 ShoreUV,
	float3 WorldPosition,
	float4 Color,
	float2 Visibility,
	float Time,
	out float3 BaseColor,
	out float Alpha,
	out float Exploration)
{
	float shore = ShoreUV.y;
	float foam = Foam(shore, WorldPosition.xz, Time, NoiseTexture);
	float waves = Waves(WorldPosition.xz, Time, NoiseTexture);
	waves *= 1 - shore;

	float river = River(RiverUV, Time, NoiseTexture);

	float3 coast = HexShoreColor(
		shore, foam, waves, WorldPosition.xz, Color.rgb);
	float hfRiverMask = HexHFRiverMask(RiverUV);
	float hfBank = 1.0 - smoothstep(0.12, 0.82, hfRiverMask);
	float3 hfRiverColor = lerp(
		_HexRiverWaterColor.rgb, _HexRiverBankColor.rgb, hfBank * 0.72);
	float3 riverColor = HexStyledColor(Color.rgb, hfRiverColor);
	riverColor = lerp(
		riverColor, HexHFOriginalRiverDiffuse(RiverUV),
		saturate(_HexHFOriginalBlend));
	float3 c = saturate(lerp(coast, riverColor + river * 0.16, ShoreUV.x));
	BaseColor = c * Visibility.x;
	Alpha = lerp(Color.a, lerp(0.7, 0.5, shore),
		saturate(_HexWaterStyleBlend));
	Exploration = Visibility.y;
}

void GetFragmentDataRoad_float(
	UnityTexture2D NoiseTexture,
	float2 BlendUV,
	float3 WorldPosition,
	float4 Color,
	float2 Visibility,
	out float3 BaseColor,
	out float Alpha,
	out float Exploration)
{
	float4 noise = NoiseTexture.Sample(
		NoiseTexture.samplerstate, WorldPosition.xz * (3 * TILING_SCALE));
	float3 roadColor = Color.rgb * (noise.y * 0.75 + 0.25);
	BaseColor = HexCivGrade(
		roadColor, WorldPosition, 0.58, 0.72) * Visibility.x;
	Alpha = BlendUV.x;
	Alpha *= noise.x + 0.5;
	Alpha = smoothstep(0.4, 0.7, Alpha);
	Exploration = Visibility.y;
}

void GetFragmentDataRiver_float(
	UnityTexture2D NoiseTexture,
	float2 RiverUV,
	float4 Color,
	float2 Visibility,
	float Time,
	out float3 BaseColor,
	out float Alpha,
	out float Exploration)
{
	float river = River(RiverUV, Time, NoiseTexture);
	float edgeSilt = smoothstep(0.66, 0.98, abs(RiverUV.x * 2.0 - 1.0));
	float hfRiverMask = HexHFRiverMask(RiverUV);
	float hfBank = 1.0 - smoothstep(0.12, 0.82, hfRiverMask);
	float bankBlend = max(edgeSilt * 0.34, hfBank * 0.72);
	float3 styled = lerp(
		_HexRiverWaterColor.rgb, _HexRiverBankColor.rgb, bankBlend);
	styled = lerp(
		styled, HexHFOriginalRiverDiffuse(RiverUV),
		saturate(_HexHFOriginalBlend));
	float3 c = saturate(HexStyledColor(Color.rgb, styled) +
		_HexShoreFoamColor.rgb * river * 0.12);
	c = HexCivGrade(
		c, float3(RiverUV.x * 19.0, 0.0, RiverUV.y * 19.0),
		0.66 + river * 0.16, 0.46);
	BaseColor = c * Visibility.x;
	Alpha = lerp(Color.a, 0.68, saturate(_HexWaterStyleBlend));
	// Reveal the terrain beneath the dark sides of HF's meandering mask. This
	// makes the water channel bend inside the strip instead of reading as a
	// uniformly colored straight ribbon.
	Alpha *= smoothstep(0.08, 0.72, hfRiverMask);
	Exploration = Visibility.y;
}

void GetFragmentDataWater_float(
	UnityTexture2D NoiseTexture,
	float3 WorldPosition,
	float4 Color,
	float2 Visibility,
	float Time,
	out float3 BaseColor,
	out float Alpha,
	out float Exploration)
{
	float waves = Waves(WorldPosition.xz, Time, NoiseTexture);
	float shore = 0.0;
	float waterCoverage = 1.0;
	if (_HexHFOriginalBlend > 0.999)
	{
		HexGridData grid = GetHexGridData(WorldPosition.xz);
		float2 hexPosition = WoldToHexSpace(WorldPosition.xz);
		float2 local = hexPosition - grid.cellCenter;
		// Seamless wrapping moves complete chunk columns by exactly one map
		// width. GetHexGridData therefore returns an X offset outside the logical
		// texture range for those visual copies. Wrapping the texture UV alone is
		// not enough: linearizing the unwrapped X first carries +/-width into the
		// row and makes the water mask sample the previous / next Z cell.
		float2 resolvedCellOffset;
		HFResolveOffset(grid.cellOffsetCoordinates, resolvedCellOffset);
		float cellIndex = resolvedCellOffset.y *
			_HexCellData_TexelSize.z + resolvedCellOffset.x;
		// Use the same reconstructed height that displaces the opaque HF
		// surface. Foam therefore follows the actual water / terrain
		// intersection instead of the much wider Sea mixer ownership band.
		HFReliefSurface coastSurface = HF_EvaluateOriginalRelief(
			cellIndex, local * (1.5 / HF_MIXER_SQRT3_OVER_2));
		float signedWaterDepth =
			WorldPosition.y - HFStabilizeOceanSurfaceY(coastSurface);
		// The relief mesh is distance-tessellated, so its rasterized depth is an
		// approximation of the HF height field. Resolve the water mask from the
		// exact logical height again per pixel; this keeps the shoreline invariant
		// when the camera moves and prevents low-LOD sand triangles from occluding
		// the water plane.
		float coverageWidth = max(fwidth(signedWaterDepth) * 1.5, 0.015);
		waterCoverage = smoothstep(
			-coverageWidth, coverageWidth, signedWaterDepth);
		float waterDepth = max(signedWaterDepth, 0.0);
		// The old ShoreUV covered only a narrow edge strip. HF's beach slope is
		// much wider, so remap world-space depth to an equally narrow contour or
		// the complete shallow stamp turns into a large white / sand blob.
		shore = 1.0 - smoothstep(0.04, 0.52, waterDepth);
	}
	float foam = Foam(shore, WorldPosition.xz, Time, NoiseTexture);
	float3 water = HexStyledColor(Color.rgb, _HexDeepOceanColor.rgb);
	float3 coast = HexShoreColor(
		shore, foam, waves * (1.0 - shore), WorldPosition.xz, water);
	float3 c = saturate(lerp(
		water + _HexShoreFoamColor.rgb * waves * 0.12,
		coast, saturate(_HexHFOriginalBlend)));
	c = HexCivGrade(c, WorldPosition, 0.66 + waves * 0.2, 0.48);

	BaseColor = c * Visibility.x;
	float coastAlpha = lerp(0.82, 0.68, smoothstep(0.38, 0.94, shore));
	Alpha = lerp(
		Color.a, coastAlpha, saturate(_HexWaterStyleBlend)) * waterCoverage;
	Exploration = Visibility.y;
}

void GetFragmentDataShore_float(
	UnityTexture2D NoiseTexture,
	float2 ShoreUV,
	float3 WorldPosition,
	float4 Color,
	float2 Visibility,
	float Time,
	out float3 BaseColor,
	out float Alpha,
	out float Exploration)
{
	float shore = ShoreUV.y;
	float foam = Foam(shore, WorldPosition.xz, Time, NoiseTexture);
	float waves = Waves(WorldPosition.xz, Time, NoiseTexture);
	waves *= 1 - shore;
	float3 c = saturate(HexShoreColor(
		shore, foam, waves, WorldPosition.xz, Color.rgb));
	
	BaseColor = c * Visibility.x;
	Alpha = lerp(Color.a, lerp(0.74, 0.38, smoothstep(0.2, 1.0, shore)),
		saturate(_HexWaterStyleBlend));
	Exploration = Visibility.y;
}
