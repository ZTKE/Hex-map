#include "../HexCellData.hlsl"

TEXTURE2D(_HexTerrainStyleAtlas);
SAMPLER(sampler_HexTerrainStyleAtlas);
SAMPLER(sampler_point_clamp);
#define HF_TERRAIN_LINEAR_SAMPLER sampler_HexTerrainStyleAtlas
#define HF_TERRAIN_POINT_SAMPLER sampler_point_clamp
#include "../HFTerrainBlend.hlsl"
#include "../Hex Civilization Style.hlsl"

float _HexTerrainAtlasBlend;
float _HexTerrainAtlasTiling;
float _HexTerrainMacroVariation;
float4 _HexDeepOceanColor;
float4 _HexShallowWaterColor;
float4 _HexWetSandColor;

void GetVertexCellData_float(
	float3 Indices,
	float3 Weights,
	bool EditMode,
	out float4 Terrain,
	out float4 Visibility)
{
	float4 cell0 = GetCellData(Indices, 0, EditMode);
	float4 cell1 = GetCellData(Indices, 1, EditMode);
	float4 cell2 = GetCellData(Indices, 2, EditMode);

	Terrain.x = cell0.w;
	Terrain.y = cell1.w;
	Terrain.z = cell2.w;
	Terrain.w = max(max(cell0.b, cell1.b), cell2.b) * 30.0;

	Visibility.x = cell0.x;
	Visibility.y = cell1.x;
	Visibility.z = cell2.x;
	Visibility.xyz = lerp(0.25, 1, Visibility.xyz);
	Visibility.w =
		cell0.y * Weights.x + cell1.y * Weights.y + cell2.y * Weights.z;
}

// Terrain-style anti-tiling. A second rotated scale breaks the large repeated
// squares of the source array, while low-frequency value noise gives each
// region a coherent macro tint. Relief uses the same sampling layout so its
// skirts merge back into this base surface.
float TerrainStyleHash21(float2 p)
{
	p = frac(p * float2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return frac(p.x * p.y);
}

float TerrainStyleValueNoise(float2 p)
{
	float2 i = floor(p);
	float2 f = frac(p);
	f = f * f * (3.0 - 2.0 * f);
	return lerp(
		lerp(
			TerrainStyleHash21(i),
			TerrainStyleHash21(i + float2(1, 0)), f.x),
		lerp(
			TerrainStyleHash21(i + float2(0, 1)),
			TerrainStyleHash21(i + 1), f.x),
		f.y);
}

float TerrainStyleFbm(float2 p)
{
	float value = 0.0;
	float amplitude = 0.55;
	[unroll]
	for (int i = 0; i < 4; i++)
	{
		value += TerrainStyleValueNoise(p) * amplitude;
		p = float2(
			p.x * 1.62 - p.y * 1.18,
			p.x * 1.18 + p.y * 1.62) + 7.13;
		amplitude *= 0.48;
	}
	return value;
}

float2 TerrainStyleRotateUV(float2 p)
{
	return float2(
		p.x * 0.8 - p.y * 0.6,
		p.x * 0.6 + p.y * 0.8);
}

float3 SampleStrategyTerrainAtlas(float3 worldPosition, float terrainIndex)
{
	float panel = clamp(floor(terrainIndex + 0.5), 0.0, 4.0);
	float2 localA = frac(worldPosition.xz * _HexTerrainAtlasTiling);
	float2 localB = frac(
		TerrainStyleRotateUV(worldPosition.xz) *
		(_HexTerrainAtlasTiling * 1.71) + float2(0.37, 0.61));
	// Keep filtering inside one fifth of the atlas, away from biome seams.
	localA.x = (panel + lerp(0.025, 0.975, localA.x)) * 0.2;
	localB.x = (panel + lerp(0.025, 0.975, localB.x)) * 0.2;
	float3 a = SAMPLE_TEXTURE2D(
		_HexTerrainStyleAtlas, sampler_HexTerrainStyleAtlas, localA).rgb;
	float3 b = SAMPLE_TEXTURE2D(
		_HexTerrainStyleAtlas, sampler_HexTerrainStyleAtlas, localB).rgb;
	float blend = TerrainStyleValueNoise(worldPosition.xz * 0.041 + panel * 7.3);
	return lerp(a, b, 0.16 + blend * 0.28);
}

// Sample one biome independently of the CPU mesh's vertex ownership. This is
// also used by the HF logical mixer, which chooses ownership from soft stamps.
float4 SampleTerrainSurface(
	UnityTexture2DArray TerrainTextures,
	float3 WorldPosition,
	float terrainIndex)
{
	float2 uvA = WorldPosition.xz * (2 * TILING_SCALE);
	float2 uvB = TerrainStyleRotateUV(WorldPosition.xz) *
		(3.73 * TILING_SCALE) + float2(4.37, 1.91);
	float4 a = TerrainTextures.Sample(
		TerrainTextures.samplerstate, float3(uvA, terrainIndex));
	float4 b = TerrainTextures.Sample(
		TerrainTextures.samplerstate, float3(uvB, terrainIndex));
	float macro = TerrainStyleValueNoise(WorldPosition.xz * 0.035 + 11.7);
	float4 c = lerp(a, b, 0.2 + macro * 0.28);
	float3 authored = SampleStrategyTerrainAtlas(WorldPosition, terrainIndex);
	c.rgb = lerp(c.rgb, authored, saturate(_HexTerrainAtlasBlend));
	c.rgb *= lerp(
		1.0 - _HexTerrainMacroVariation,
		1.0 + _HexTerrainMacroVariation * 0.55,
		TerrainStyleFbm(WorldPosition.xz * 0.018));
	return c;
}

// Retain the original vertex-weight path as a fallback for underwater cells
// and for styles that do not provide the shared HF mixer atlas.
float4 GetTerrainColor(
	UnityTexture2DArray TerrainTextures,
	float3 WorldPosition,
	float4 Terrain,
	float3 Weights,
	float4 Visibility,
	int index)
{
	return SampleTerrainSurface(
		TerrainTextures, WorldPosition, Terrain[index]) *
		(Weights[index] * Visibility[index]);
}

float4 GetHFMixedTerrainColor(
	UnityTexture2DArray TerrainTextures,
	float3 WorldPosition,
	float3 MeshWeights,
	float4 Visibility,
	out float blendStrength)
{
	HexGridData grid = GetHexGridData(WorldPosition.xz);
	float2 hexPosition = WoldToHexSpace(WorldPosition.xz);
	float2 local = hexPosition - grid.cellCenter;
	HFTerrainMixWeights mix = HFMixEvaluateNeighborhood(
		grid.cellOffsetCoordinates, local);

	float4 mixed = 0.0;
	if (mix.terrain0123.x > 0.0001)
		mixed += SampleTerrainSurface(
			TerrainTextures, WorldPosition, 0.0) * mix.terrain0123.x;
	if (mix.terrain0123.y > 0.0001)
		mixed += SampleTerrainSurface(
			TerrainTextures, WorldPosition, 1.0) * mix.terrain0123.y;
	if (mix.terrain0123.z > 0.0001)
		mixed += SampleTerrainSurface(
			TerrainTextures, WorldPosition, 2.0) * mix.terrain0123.z;
	if (mix.terrain0123.w > 0.0001)
		mixed += SampleTerrainSurface(
			TerrainTextures, WorldPosition, 3.0) * mix.terrain0123.w;
	if (mix.terrain4 > 0.0001)
		mixed += SampleTerrainSurface(
			TerrainTextures, WorldPosition, 4.0) * mix.terrain4;
	// In faithful mode the colour comes from the same rotated HF diffuse stamps
	// that produced the ownership field, instead of merely recolouring the
	// current world-tiled biome textures.
	mixed = lerp(
		mixed, float4(mix.diffuse, 1.0), saturate(_HexHFOriginalBlend));

	float meshVisibility = dot(MeshWeights, Visibility.xyz);
	mixed *= meshVisibility;
	blendStrength = HFMixNeighborhoodBlendStrength(mix);
	return mixed;
}

// Apply an 80% darkening grid outline at hex center distance 0.965-1.
float3 ApplyGrid(float3 baseColor, HexGridData h)
{
	return baseColor * (0.2 + 0.8 * h.Smoothstep10(0.965));
}

// Apply a white outline at hex center distance 0.68-0.8.
float3 ApplyHighlight(float3 baseColor, HexGridData h)
{
	return saturate(h.SmoothstepRange(0.68, 0.8) + baseColor.rgb);
}

// Underwater terrain is deliberately reduced to two readable material bands:
// a warm/turquoise coastal shelf and a cool deep-ocean floor.
float3 ColorizeSubmergence(float3 baseColor, float surfaceY, float waterY)
{
	float submergence = waterY - surfaceY;
	float underwater = step(0.05, submergence);
	float deep = smoothstep(2.8, 3.35, submergence);
	float3 shallowFloor = lerp(
		_HexWetSandColor.rgb, _HexShallowWaterColor.rgb, 0.42);
	float3 deepFloor = lerp(
		_HexDeepOceanColor.rgb, _HexShallowWaterColor.rgb, 0.13) * 0.72;
	float3 twoTierFloor = lerp(shallowFloor, deepFloor, deep);
	// Retain only a trace of authored texture so biome indices cannot create
	// extra apparent water-depth layers.
	twoTierFloor = lerp(twoTierFloor, baseColor, 0.12);
	return lerp(baseColor, twoTierFloor, underwater);
}

void GetFragmentData_float(
	UnityTexture2DArray TerrainTextures,
	float3 WorldPosition,
	float4 Terrain,
	float4 Visibility,
	float3 Weights,
	bool ShowGrid,
	out float3 BaseColor,
	out float Exploration)
{
	float4 c =
		GetTerrainColor(
			TerrainTextures, WorldPosition, Terrain, Weights, Visibility, 0) +
		GetTerrainColor(
			TerrainTextures, WorldPosition, Terrain, Weights, Visibility, 1) +
		GetTerrainColor(
			TerrainTextures, WorldPosition, Terrain, Weights, Visibility, 2);
	// The opaque HF relief surface covers every dry cell in faithful mode.
	// Avoid evaluating the same seven source stamps again underneath it; this
	// base graph remains visible for the unchanged ocean and coast pipeline.
	if (_HexHFOriginalBlend < 0.999)
	{
		float hfBlend;
		float4 hfMixed = GetHFMixedTerrainColor(
			TerrainTextures, WorldPosition, Weights, Visibility, hfBlend);
		c = lerp(c, hfMixed, hfBlend);
	}

	BaseColor = ColorizeSubmergence(c.rgb, WorldPosition.y, Terrain.w);
	float paintedLight = 0.52 +
		TerrainStyleFbm(WorldPosition.xz * 0.026 + 5.7) * 0.24;
	BaseColor = HexCivGrade(BaseColor, WorldPosition, paintedLight, 1.0);

	HexGridData hgd = GetHexGridData(WorldPosition.xz);
	if (_HexHFOriginalBlend > 0.999)
	{
		// HF has one displaced terrain surface for both land and seabed. Remove
		// the complete Catlike base surface; keeping its underwater half would
		// put a hex-stepped floor beneath the continuous Water_h coastline.
		clip(-1.0);
	}

	if (ShowGrid)
	{
		BaseColor = ApplyGrid(BaseColor, hgd);
	}

	if (hgd.IsHighlighted())
	{
		BaseColor = ApplyHighlight(BaseColor, hgd);
	}
	
	Exploration = Visibility.w;
}
