#ifndef HF_TERRAIN_BLEND_INCLUDED
#define HF_TERRAIN_BLEND_INCLUDED

// Shared HoneyFramework-style mixer support. The original plugin baked these
// overlapping masks into render textures. Here the map-wide logical textures
// identify the cells and the shared atlas is sampled directly at render time.

TEXTURE2D(_HexTerrainShapeData);
TEXTURE2D(_HexHFTerrainMixer);

#ifndef HF_TERRAIN_LINEAR_SAMPLER
	#define HF_TERRAIN_LINEAR_SAMPLER sampler_HexCellData
#endif

#include "HFOriginalTerrain.hlsl"

float _HexTerrainShapeWrap;
float _HexHFStampScale;
float _HexHFBlendStrength;
float _HexHFReliefFootprint;

#define HF_MIXER_PANEL_COUNT 8.0
#define HF_MIXER_PI 3.14159265359
#define HF_MIXER_SQRT3_OVER_2 0.86602540378

struct HFTerrainMixStamp
{
	float2 offset;
	float2 localUV;
	float terrain;
	float landform;
	float angle;
	float underwater;
	float mixer;
	float centralization;
	float3 diffuse;
	float valid;
};

struct HFTerrainMixWeights
{
	float4 terrain0123;
	float terrain4;
	float total;
	float rootValid;
	float rootUnderwater;
	float3 diffuse;
};

float2 HFMixDirection(int direction)
{
	if (direction == 0) return float2(0.5, HF_MIXER_SQRT3_OVER_2);
	if (direction == 1) return float2(1.0, 0.0);
	if (direction == 2) return float2(0.5, -HF_MIXER_SQRT3_OVER_2);
	if (direction == 3) return float2(-0.5, -HF_MIXER_SQRT3_OVER_2);
	if (direction == 4) return float2(-1.0, 0.0);
	return float2(-0.5, HF_MIXER_SQRT3_OVER_2);
}

float HFMixHash21(float2 p)
{
	p = frac(p * float2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return frac(p.x * p.y);
}

float HFMixValueNoise(float2 p)
{
	float2 i = floor(p);
	float2 f = frac(p);
	f = f * f * (3.0 - 2.0 * f);
	return lerp(
		lerp(HFMixHash21(i), HFMixHash21(i + float2(1.0, 0.0)), f.x),
		lerp(HFMixHash21(i + float2(0.0, 1.0)),
			HFMixHash21(i + float2(1.0, 1.0)), f.x), f.y);
}

float2 HFMixRootCenter(float2 offset)
{
	float odd = fmod(floor(offset.y + 0.5), 2.0);
	return float2(
		offset.x + odd * 0.5,
		offset.y * HF_MIXER_SQRT3_OVER_2);
}

float2 HFMixNeighborOffset(float2 offset, int direction)
{
	float odd = fmod(offset.y, 2.0);
	if (direction == 0) return offset + float2(odd, 1.0);
	if (direction == 1) return offset + float2(1.0, 0.0);
	if (direction == 2) return offset + float2(odd, -1.0);
	if (direction == 3) return offset + float2(odd - 1.0, -1.0);
	if (direction == 4) return offset + float2(-1.0, 0.0);
	return offset + float2(odd - 1.0, 1.0);
}

float2 HFMixRotate(float2 p, float angle)
{
	float s = sin(angle);
	float c = cos(angle);
	return float2(p.x * c + p.y * s, -p.x * s + p.y * c);
}

bool HFMixResolveOffset(float2 requested, out float2 resolved)
{
	float width = _HexCellData_TexelSize.z;
	float height = _HexCellData_TexelSize.w;
	resolved = floor(requested + 0.5);
	if (resolved.y < 0.0 || resolved.y >= height)
	{
		return false;
	}
	if (_HexTerrainShapeWrap > 0.5)
	{
		resolved.x = fmod(fmod(resolved.x, width) + width, width);
		return true;
	}
	return resolved.x >= 0.0 && resolved.x < width;
}

float4 HFMixSampleShape(float2 offset)
{
	float2 uv = (offset + 0.5) * _HexCellData_TexelSize.xy;
	return SAMPLE_TEXTURE2D_LOD(
		_HexTerrainShapeData, sampler_HexCellData, uv, 0);
}

float HFMixPanelFor(float terrain, float landform)
{
	if (landform > 1.5) return 6.0;
	if (landform > 0.5) return 5.0;
	return clamp(floor(terrain + 0.5), 0.0, 4.0);
}

float2 HFMixAtlasUV(float panel, float2 localUV)
{
	float2 safeUV = clamp(localUV, 0.001, 0.999);
	return float2(
		(panel + safeUV.x) / HF_MIXER_PANEL_COUNT,
		safeUV.y);
}

float HFMixSamplePanel(float panel, float2 localUV)
{
	float edge = min(
		min(localUV.x, 1.0 - localUV.x),
		min(localUV.y, 1.0 - localUV.y));
	float inside = step(0.0, edge);
	float centralization = smoothstep(0.0, 0.075, max(edge, 0.0));
	float mask = SAMPLE_TEXTURE2D_LOD(
		_HexHFTerrainMixer, HF_TERRAIN_LINEAR_SAMPLER,
		HFMixAtlasUV(panel, localUV), 0).r;
	return mask * centralization * inside;
}

HFTerrainMixStamp HFMixLoadStamp(
	float2 requestedOffset, float2 localFromCandidateCenter)
{
	HFTerrainMixStamp stamp;
	stamp.offset = requestedOffset;
	stamp.localUV = 0.5;
	stamp.terrain = 0.0;
	stamp.landform = 0.0;
	stamp.angle = 0.0;
	stamp.underwater = 0.0;
	stamp.mixer = 0.0;
	stamp.centralization = 0.0;
	stamp.diffuse = 0.0;
	stamp.valid = 0.0;

	float2 resolved;
	if (!HFMixResolveOffset(requestedOffset, resolved))
	{
		return stamp;
	}
	stamp.offset = resolved;
	stamp.valid = 1.0;

	float4 encoded = HFMixSampleShape(resolved);
	float packed = floor(encoded.r * 255.0 + 0.5);
	stamp.landform = floor(packed / 64.0);
	float angle01 = fmod(packed, 64.0) / 63.0;
	stamp.angle = angle01 * (2.0 * HF_MIXER_PI) - HF_MIXER_PI;

	float4 mapData = GetCellData(resolved, false);
	stamp.terrain = floor(mapData.a * 255.0 + 0.5);
	stamp.underwater = step(0.0001, mapData.b);

	float2 rotated = HFMixRotate(localFromCandidateCenter, stamp.angle);
	stamp.localUV = rotated / (2.0 * max(_HexHFStampScale, 0.1)) + 0.5;
	float panel = HFMixPanelFor(stamp.terrain, stamp.landform);
	float authoredMask = HFMixSamplePanel(panel, stamp.localUV);
	// A continuous radial partition owns the blend. The authored HF mask only
	// perturbs it, otherwise each mask's square/round silhouette remains visible
	// as a repeated tile stamp. A two-ring Gaussian removes the final cell-scale
	// scalloping while the compact support keeps ownership changes continuous.
	float distanceToCenter = length(localFromCandidateCenter);
	float radialWeight = exp(-distanceToCenter * distanceToCenter * 0.28) *
		(1.0 - smoothstep(1.78, 2.04, distanceToCenter));
	float authoredVariation = lerp(0.92, 1.08, authoredMask);
	float legacyMixer = radialWeight * authoredVariation;

	// Faithful HF path: each cell stamps the original mixer over a square that
	// is 1.6 neighbour spacings wide. The mixer is the ownership field itself,
	// not a small perturbation of a Gaussian field.
	// Hex-space uses one east/west neighbour spacing as one unit. HF authored
	// its 1.6 scale in outer-radius units, so convert by sqrt(3) here.
	float2 originalUV = HFOriginalUV(
		localFromCandidateCenter / HF_MIXER_SQRT3_OVER_2 * 1.5,
		stamp.angle);
	float originalCentralization = HFOriginalCentralization(originalUV);
	float originalPanel = HFOriginalPanelFor(stamp.terrain, stamp.landform);
	float originalMixer = HFOriginalSampleMixer(originalPanel, originalUV) *
		originalCentralization;
	float originalBlend = saturate(_HexHFOriginalBlend);
	stamp.localUV = lerp(stamp.localUV, originalUV, originalBlend);
	stamp.centralization = lerp(
		legacyMixer, originalCentralization, originalBlend);
	stamp.mixer = lerp(legacyMixer, originalMixer, originalBlend) *
		(1.0 - stamp.underwater);
	stamp.centralization *= 1.0 - stamp.underwater;
	stamp.diffuse = HFOriginalSampleDiffuse(originalPanel, originalUV);
	return stamp;
}

void HFMixSelectNeighborDirections(
	float2 localFromCenter, out int firstDirection, out int secondDirection)
{
	firstDirection = 0;
	secondDirection = 1;
	float firstScore = -1000.0;
	float secondScore = -1000.0;
	[unroll]
	for (int direction = 0; direction < 6; direction++)
	{
		float score = dot(localFromCenter, HFMixDirection(direction));
		if (score > firstScore)
		{
			secondScore = firstScore;
			secondDirection = firstDirection;
			firstScore = score;
			firstDirection = direction;
		}
		else if (score > secondScore)
		{
			secondScore = score;
			secondDirection = direction;
		}
	}
}

void HFMixAccumulateTerrainWeight(
	inout float4 terrain0123, inout float terrain4,
	float terrain, float weight)
{
	if (terrain < 0.5) terrain0123.x += weight;
	else if (terrain < 1.5) terrain0123.y += weight;
	else if (terrain < 2.5) terrain0123.z += weight;
	else if (terrain < 3.5) terrain0123.w += weight;
	else terrain4 += weight;
}

void HFMixAccumulateStamp(
	inout HFTerrainMixWeights mix,
	inout HFTerrainMixWeights fillMix,
	inout float globalMaximum,
	inout float3 mixerDiffuse,
	inout float3 fillDiffuse,
	HFTerrainMixStamp stamp)
{
	float originalBlend = saturate(_HexHFOriginalBlend);
	float mixerWeight = pow(
		saturate(stamp.mixer), lerp(0.72, 1.0, originalBlend));
	float fillWeight = stamp.centralization;
	HFMixAccumulateTerrainWeight(
		mix.terrain0123, mix.terrain4, stamp.terrain, mixerWeight);
	HFMixAccumulateTerrainWeight(
		fillMix.terrain0123, fillMix.terrain4, stamp.terrain, fillWeight);
	mix.total += mixerWeight;
	fillMix.total += fillWeight;
	mixerDiffuse += stamp.diffuse * mixerWeight;
	fillDiffuse += stamp.diffuse * fillWeight;
	globalMaximum = max(globalMaximum, stamp.mixer);
}

// HF's rotated 1.6-radius square reaches 1.6*sqrt(2) radii at its corners.
// Besides the root and direct ring, the six diagonal second-ring cells can
// therefore touch a tiny region near a root-hex vertex. The six axial cells in
// that ring remain outside the maximum reach and are deliberately omitted.
HFTerrainMixWeights HFMixEvaluateNeighborhood(
	float2 rootOffset, float2 localFromRootCenter)
{
	HFTerrainMixWeights mix;
	mix.terrain0123 = 0.0;
	mix.terrain4 = 0.0;
	mix.total = 0.0;
	mix.rootValid = 0.0;
	mix.rootUnderwater = 0.0;
	mix.diffuse = 0.0;
	HFTerrainMixWeights fillMix = mix;
	float3 mixerDiffuse = 0.0;
	float3 fillDiffuse = 0.0;
	float globalMaximum = 0.0;
	float originalBlend = saturate(_HexHFOriginalBlend);
	// Domain-warp the continuous weight field with shared world-space noise.
	// It belongs only to the legacy mode; HF's original rotated masks already
	// provide the irregular ownership boundary in faithful mode.
	float2 globalHexPoint =
		HFMixRootCenter(rootOffset) + localFromRootCenter;
	float2 warp = float2(
		HFMixValueNoise(globalHexPoint * 0.58 + float2(13.7, 2.9)),
		HFMixValueNoise(globalHexPoint * 0.58 + float2(4.1, 19.3))) - 0.5;
	float2 samplePoint = localFromRootCenter +
		warp * (0.48 * (1.0 - originalBlend));

	HFTerrainMixStamp root = HFMixLoadStamp(
		rootOffset, samplePoint);
	mix.rootValid = root.valid;
	mix.rootUnderwater = root.underwater;
	HFMixAccumulateStamp(
		mix, fillMix, globalMaximum,
		mixerDiffuse, fillDiffuse, root);

	[unroll]
	for (int direction = 0; direction < 6; direction++)
	{
		float2 directionVector = HFMixDirection(direction);
		float2 firstOffset = HFMixNeighborOffset(rootOffset, direction);
		HFTerrainMixStamp first = HFMixLoadStamp(
			firstOffset, samplePoint - directionVector);
		HFMixAccumulateStamp(
			mix, fillMix, globalMaximum,
			mixerDiffuse, fillDiffuse, first);

		int nextDirection = (direction + 1) % 6;
		float2 cornerOffset = HFMixNeighborOffset(
			firstOffset, nextDirection);
		float2 cornerCenter = directionVector +
			HFMixDirection(nextDirection);
		HFTerrainMixStamp corner = HFMixLoadStamp(
			cornerOffset, samplePoint - cornerCenter);
		HFMixAccumulateStamp(
			mix, fillMix, globalMaximum,
			mixerDiffuse, fillDiffuse, corner);
	}

	// HF first stores the maximum ownership in a chunk-wide mixer. The second
	// pass fills any uncovered strength so no cell-shaped hole can remain.
	float missingStrength =
		(1.0 - saturate(globalMaximum)) * originalBlend;
	mix.terrain0123 += fillMix.terrain0123 * missingStrength;
	mix.terrain4 += fillMix.terrain4 * missingStrength;
	mix.total += fillMix.total * missingStrength;
	mixerDiffuse += fillDiffuse * missingStrength;

	// Terrain-specific continuous modulation dissolves the remaining regular
	// iso-weight rhythm of a discrete hex field. In a single-biome interior it
	// cancels during normalization; it only reshapes transition bands.
	float2 noisePoint = globalHexPoint * 0.64;
	float4 terrainNoise0123 = float4(
		HFMixValueNoise(noisePoint + float2(2.7, 11.9)),
		HFMixValueNoise(noisePoint + float2(17.3, 4.6)),
		HFMixValueNoise(noisePoint + float2(7.1, 23.5)),
		HFMixValueNoise(noisePoint + float2(29.2, 13.4)));
	float terrainNoise4 = HFMixValueNoise(
		noisePoint + float2(37.8, 31.1));
	mix.terrain0123 *= lerp(
		lerp(0.58, 1.42, terrainNoise0123), 1.0, originalBlend);
	mix.terrain4 *= lerp(
		lerp(0.58, 1.42, terrainNoise4), 1.0, originalBlend);
	mix.total = dot(
		mix.terrain0123, float4(1.0, 1.0, 1.0, 1.0)) + mix.terrain4;
	float inverseTotal = mix.total > 0.0001 ? 1.0 / mix.total : 0.0;
	mix.terrain0123 *= inverseTotal;
	mix.terrain4 *= inverseTotal;
	mix.diffuse = mixerDiffuse * inverseTotal;
	return mix;
}

float HFMixNeighborhoodBlendStrength(HFTerrainMixWeights mix)
{
	return saturate(_HexHFBlendStrength) * mix.rootValid *
		(1.0 - mix.rootUnderwater) * step(0.001, mix.total);
}

#endif
