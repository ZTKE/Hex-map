#ifndef HF_ORIGINAL_TERRAIN_INCLUDED
#define HF_ORIGINAL_TERRAIN_INCLUDED

// Original HoneyFramework terrain triplets. These textures are shared by the
// whole map; only the logical per-cell data grows with map size.
TEXTURE2D(_HFDirtDiffuse);
// All original HF stamps use the same bilinear sampler. Sharing it avoids the
// 16-sampler ceiling of the terrain Shader Graph while retaining every source
// texture as an independently compressed Unity asset.
TEXTURE2D(_HFDirtHeight);
TEXTURE2D(_HFDirtMixer);

TEXTURE2D(_HFPlainsDiffuse);
TEXTURE2D(_HFCommonHeight);
TEXTURE2D(_HFPlainsMixer);

TEXTURE2D(_HFMarshDiffuse);
TEXTURE2D(_HFMarshMixer);

TEXTURE2D(_HFHillDiffuse);
TEXTURE2D(_HFHillHeight);
TEXTURE2D(_HFHillMixer);

TEXTURE2D(_HFMountainDiffuse);
TEXTURE2D(_HFMountainHeight);
TEXTURE2D(_HFMountainMixer);

TEXTURE2D(_HFRiverDiffuse);
TEXTURE2D(_HFRiverHeight);
TEXTURE2D(_HFRiverMixer);

float _HexHFOriginalBlend;
float _HexHFOriginalStampScale;
float _HexHFOriginalHeightScale;
float _HexHFOriginalHeightLod;
float _HexHFOriginalShadowStrength;
float4 _HexHFOriginalShadowOffsets;

#define HF_ORIGINAL_DIRT 0.0
#define HF_ORIGINAL_PLAINS 1.0
#define HF_ORIGINAL_MARSH 2.0
#define HF_ORIGINAL_HILL 3.0
#define HF_ORIGINAL_MOUNTAIN 4.0

float HFOriginalPanelFor(float terrain, float landform)
{
	if (landform > 1.5) return HF_ORIGINAL_MOUNTAIN;
	if (landform > 0.5) return HF_ORIGINAL_HILL;
	if (terrain < 0.5) return HF_ORIGINAL_DIRT;
	if (terrain < 1.5) return HF_ORIGINAL_PLAINS;
	if (terrain < 2.5) return HF_ORIGINAL_MARSH;
	if (terrain < 3.5) return HF_ORIGINAL_DIRT;
	return HF_ORIGINAL_PLAINS;
}

float HFOriginalPanelForCell(
	float terrain, float landform, float plantLevel)
{
	// HF's forest terrain definitions (OID 5 and OID 9) both use the
	// Plains1 triplet; foreground density is a terrain-definition choice, not a
	// recolour layered over Dirt or Marsh.
	if (landform < 0.5 && plantLevel > 0.5)
	{
		return HF_ORIGINAL_PLAINS;
	}
	return HFOriginalPanelFor(terrain, landform);
}

float2 HFOriginalRotate(float2 p, float angle)
{
	float s = sin(angle);
	float c = cos(angle);
	return float2(p.x * c + p.y * s, -p.x * s + p.y * c);
}

float2 HFOriginalUV(float2 pointInNeighborUnits, float angle)
{
	float2 oriented = HFOriginalRotate(pointInNeighborUnits, angle);
	return oriented / (2.0 * max(_HexHFOriginalStampScale, 0.1)) + 0.5;
}

float HFOriginalCentralization(float2 uv)
{
	float2 edge = abs(uv - 0.5);
	float inside = step(max(edge.x, edge.y), 0.5);
	// This is the exact square centralization curve used by HF's Oven shaders.
	return saturate(3.0 * (1.0 - max(edge.x, edge.y) * 2.0)) * inside;
}

float3 HFOriginalSampleDiffuse(float panel, float2 uv)
{
	float2 safeUV = saturate(uv);
	float3 sampleValue = 0.0;
	if (panel < 0.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFDirtDiffuse, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).rgb;
	else if (panel < 1.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFPlainsDiffuse, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).rgb;
	else if (panel < 2.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFMarshDiffuse, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).rgb;
	else if (panel < 3.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFHillDiffuse, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).rgb;
	else
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFMountainDiffuse, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).rgb;
	return sampleValue;
}

float HFOriginalSampleHeight(float panel, float2 uv)
{
	float2 safeUV = saturate(uv);
	float sampleValue = 0.0;
	if (panel < 0.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFDirtHeight, HF_TERRAIN_LINEAR_SAMPLER, safeUV,
			_HexHFOriginalHeightLod).r;
	else if (panel < 2.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFCommonHeight, HF_TERRAIN_LINEAR_SAMPLER, safeUV,
			_HexHFOriginalHeightLod).r;
	else if (panel < 3.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFHillHeight, HF_TERRAIN_LINEAR_SAMPLER, safeUV,
			_HexHFOriginalHeightLod).r;
	else
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFMountainHeight, HF_TERRAIN_LINEAR_SAMPLER, safeUV,
			_HexHFOriginalHeightLod).r;
	return sampleValue;
}

float HFOriginalSampleMixer(float panel, float2 uv)
{
	float2 safeUV = saturate(uv);
	float sampleValue = 0.0;
	if (panel < 0.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFDirtMixer, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).r;
	else if (panel < 1.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFPlainsMixer, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).r;
	else if (panel < 2.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFMarshMixer, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).r;
	else if (panel < 3.5)
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFHillMixer, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).r;
	else
		sampleValue = SAMPLE_TEXTURE2D_LOD(
			_HFMountainMixer, HF_TERRAIN_LINEAR_SAMPLER, safeUV, 0).r;
	return sampleValue;
}

float3 HFOriginalSampleRiverDiffuse(float2 uv)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HFRiverDiffuse, HF_TERRAIN_LINEAR_SAMPLER, saturate(uv), 0).rgb;
}

float HFOriginalSampleRiverHeight(float2 uv)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HFRiverHeight, HF_TERRAIN_LINEAR_SAMPLER, saturate(uv), 0).r;
}

float HFOriginalSampleRiverMixer(float2 uv)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HFRiverMixer, HF_TERRAIN_LINEAR_SAMPLER, saturate(uv), 0).r;
}

#endif
