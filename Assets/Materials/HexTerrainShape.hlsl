#ifndef HEX_TERRAIN_SHAPE_INCLUDED
#define HEX_TERRAIN_SHAPE_INCLUDED

// HF-style logical terrain stamps.
//
// HoneyFramework blended several overlapping height / mixer textures into a
// chunk render target. This version keeps the overlap and topology rules, but
// reconstructs the surface directly from one RGBA32 texel per map cell plus a
// tiny shared array of mountain masks. There are no per-chunk height textures.

#include "HFTerrainBlend.hlsl"

TEXTURE2D_ARRAY(_HexMountainMasks);
SAMPLER(sampler_HexMountainMasks);

float4 _HexReliefHeights;
float4 _HexReliefWidths;
float _HexReliefStampScale;
float4 _HexReliefRiverCarve;

#define HF_PI 3.14159265359
#define HF_SQRT3_OVER_2 0.86602540378
#define HF_SHAPE_RIVER_MASK 63u
#define HF_SHAPE_UNDERWATER_BIT 64u
#define HF_SHAPE_COAST_BIT 128u

struct HFCellShape
{
	float2 offset;
	float landform;
	float angle;
	float baseY;
	float terrain;
	float plantLevel;
	float underwater;
	float waterSurfaceY;
	uint neighborMask;
	uint riverMask;
	float4 hash;
	float valid;
};

struct HFReliefSurface
{
	float y;
	float height;
	float height01;
	// HF's baked Alpha8 height before displacement. Foreground placement and
	// the Oven light/shadow pass use this exact 0..1 value.
	float bakedHeight;
	float landform;
	float terrain;
	float coverage;
	float4 style;
	float3 diffuse;
	float2 moduleUV;
	float moduleIndex;
	// Continuous share of HF's sea triplet at this point and the nearby water
	// plane height. Together they drive beach / shallow-water art without using
	// a straight hex-edge shore strip.
	float seaInfluence;
	float waterSurfaceY;
};

float HFHash21(float2 p, float salt)
{
	return frac(sin(dot(p, float2(127.1, 311.7)) + salt) * 43758.5453123);
}

float4 HFHash42(float2 p)
{
	return float4(
		HFHash21(p, 0.17),
		HFHash21(p, 19.19),
		HFHash21(p, 47.47),
		HFHash21(p, 83.83));
}

float2 HFDirection(int direction)
{
	if (direction == 0) return float2(0.5, HF_SQRT3_OVER_2);
	if (direction == 1) return float2(1.0, 0.0);
	if (direction == 2) return float2(0.5, -HF_SQRT3_OVER_2);
	if (direction == 3) return float2(-0.5, -HF_SQRT3_OVER_2);
	if (direction == 4) return float2(-1.0, 0.0);
	return float2(-0.5, HF_SQRT3_OVER_2);
}

// Cell-center offsets in the local relief coordinate system, where a hex
// corner is (sin(angle), cos(angle)).
float2 HFNeighborCenter(int direction)
{
	// localPosition is normalized by the outer radius. Horizontal cell spacing
	// is therefore sqrt(3), not 2. The old offsets evaluated the same world point
	// at different coordinates from adjacent patches, exposing every hex edge.
	if (direction == 0) return float2(HF_SQRT3_OVER_2, 1.5);
	if (direction == 1) return float2(2.0 * HF_SQRT3_OVER_2, 0.0);
	if (direction == 2) return float2(HF_SQRT3_OVER_2, -1.5);
	if (direction == 3) return float2(-HF_SQRT3_OVER_2, -1.5);
	if (direction == 4) return float2(-2.0 * HF_SQRT3_OVER_2, 0.0);
	return float2(-HF_SQRT3_OVER_2, 1.5);
}

float2 HFNeighborOffset(float2 offset, int direction)
{
	float odd = fmod(offset.y, 2.0);
	if (direction == 0) return offset + float2(odd, 1.0);
	if (direction == 1) return offset + float2(1.0, 0.0);
	if (direction == 2) return offset + float2(odd, -1.0);
	if (direction == 3) return offset + float2(odd - 1.0, -1.0);
	if (direction == 4) return offset + float2(-1.0, 0.0);
	return offset + float2(odd - 1.0, 1.0);
}

bool HFResolveOffset(float2 offset, out float2 resolved)
{
	float width = _HexCellData_TexelSize.z;
	float height = _HexCellData_TexelSize.w;
	resolved = floor(offset + 0.5);
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

float4 HFSampleShapeTexel(float2 offset)
{
	float2 uv = (offset + 0.5) * _HexCellData_TexelSize.xy;
	return SAMPLE_TEXTURE2D_LOD(
		_HexTerrainShapeData, HF_TERRAIN_POINT_SAMPLER, uv, 0);
}

float2 HFCellIndexToOffset(float cellIndex)
{
	float width = _HexCellData_TexelSize.z;
	float index = floor(cellIndex + 0.5);
	return float2(fmod(index, width), floor(index / width));
}

uint HFShapeWaterFlags(float2 requestedOffset)
{
	float2 offset;
	if (!HFResolveOffset(requestedOffset, offset))
	{
		return 0u;
	}
	return (uint)round(HFSampleShapeTexel(offset).b * 255.0);
}

float HFShapeCoastFlag(float2 requestedOffset)
{
	return (HFShapeWaterFlags(requestedOffset) & HF_SHAPE_COAST_BIT) != 0u ?
		1.0 : 0.0;
}

int HFClosestNeighborDirection(float2 outwardLocalPosition)
{
	int bestDirection = 0;
	float bestScore = -1000.0;
	[unroll]
	for (int direction = 0; direction < 6; direction++)
	{
		float score = dot(
			outwardLocalPosition, HFNeighborCenter(direction));
		if (score > bestScore)
		{
			bestScore = score;
			bestDirection = direction;
		}
	}
	return bestDirection;
}

HFCellShape HFLoadCell(float2 requestedOffset)
{
	HFCellShape cell;
	float2 offset;
	cell.valid = HFResolveOffset(requestedOffset, offset) ? 1.0 : 0.0;
	cell.offset = offset;
	cell.landform = 0.0;
	cell.angle = 0.0;
	cell.baseY = 0.0;
	cell.terrain = 0.0;
	cell.plantLevel = 0.0;
	cell.underwater = 0.0;
	cell.waterSurfaceY = 0.0;
	cell.neighborMask = 0u;
	cell.riverMask = 0u;
	cell.hash = 0.0;
	if (cell.valid < 0.5)
	{
		return cell;
	}

	float4 encoded = HFSampleShapeTexel(offset);
	uint packed = (uint)round(encoded.r * 255.0);
	cell.landform = (float)(packed >> 6u);
	float angle01 = (float)(packed & 63u) / 63.0;
	cell.angle = angle01 * (2.0 * HF_PI) - HF_PI;
	uint packedNeighborsAndPlants = (uint)round(encoded.g * 255.0);
	cell.neighborMask = packedNeighborsAndPlants & 63u;
	cell.plantLevel = (float)(packedNeighborsAndPlants >> 6u);
	cell.riverMask =
		(uint)round(encoded.b * 255.0) & HF_SHAPE_RIVER_MASK;
	cell.baseY = encoded.a * 30.0;
	float4 mapData = GetCellData(offset, false);
	cell.terrain = round(mapData.a * 255.0);
	cell.underwater = step(0.0001, mapData.b);
	cell.waterSurfaceY = mapData.b * 30.0;
	cell.hash = HFHash42(offset);
	return cell;
}

float HFStabilizeOceanSurfaceY(HFReliefSurface surface)
{
	float hasWater = step(0.0001, surface.waterSurfaceY);
	float strongSea = smoothstep(0.82, 0.96, surface.seaInfluence) *
		hasWater * step(0.999, _HexHFOriginalBlend);
	float safeSeabedY = min(
		surface.y, surface.waterSurfaceY - 0.08);
	return lerp(surface.y, safeSeabedY, strongSea);
}

int HFCountBits(uint value)
{
	int count = 0;
	[unroll]
	for (int i = 0; i < 6; i++)
	{
		count += (value & (1u << i)) != 0u ? 1 : 0;
	}
	return count;
}

bool HFHasOppositeNeighbors(uint mask)
{
	return ((mask & 1u) != 0u && (mask & 8u) != 0u) ||
		((mask & 2u) != 0u && (mask & 16u) != 0u) ||
		((mask & 4u) != 0u && (mask & 32u) != 0u);
}

int HFSelectMountainModule(uint neighborMask, float selector)
{
	int count = HFCountBits(neighborMask);
	if (count == 0) return min((int)floor(selector * 5.0), 4);
	if (count == 1) return 0;
	if (count == 2) return HFHasOppositeNeighbors(neighborMask) ? 2 : 1;
	return count == 3 ? 3 : 4;
}

float2 HFRotateIntoModule(float2 p, float angle)
{
	float s = sin(angle);
	float c = cos(angle);
	return float2(p.x * c + p.y * s, -p.x * s + p.y * c);
}

float HFEllipticalMound(float2 p, float widthX, float widthY, float height)
{
	float q = p.x * p.x / (widthX * widthX) +
		p.y * p.y / (widthY * widthY);
	return exp(-q * 1.7) * height;
}

float HFEvaluateConnections(float2 samplePoint, uint neighborMask, bool mountain)
{
	float connection = 0.0;
	[unroll]
	for (int directionIndex = 0; directionIndex < 6; directionIndex++)
	{
		if ((neighborMask & (1u << directionIndex)) == 0u)
		{
			continue;
		}
		float2 direction = HFDirection(directionIndex);
		float along = dot(samplePoint, direction);
		float lateral = abs(
			samplePoint.x * direction.y - samplePoint.y * direction.x);
		float start = mountain ? 0.12 : 0.28;
		// A connection is only the half-ridge between this cell and its
		// neighbour. It used to have a start gate but no end gate, so it stayed
		// non-zero beyond the neighbour and was finally clipped by the outer edge
		// of the finite relief patch set. That exposed a row of full hex edges.
		float startGate = smoothstep(start, start + 0.26, along);
		float end = mountain ? 1.06 : 1.10;
		float endGate = 1.0 - smoothstep(end, end + 0.26, along);
		float gate = startGate * endGate;
		float width = mountain ?
			lerp(0.30, 0.17, saturate(along)) :
			lerp(0.42, 0.25, saturate(along));
		float crossSection = pow(
			saturate(1.0 - lateral / width), mountain ? 1.35 : 1.8);
		float ridgeHeight = mountain ?
			lerp(2.05, 0.56, smoothstep(0.1, 1.0, along)) :
			lerp(0.42, 0.12, smoothstep(0.2, 1.0, along));
		connection = max(connection, gate * crossSection * ridgeHeight);
	}
	return connection;
}

float HFEvaluateConnectionCoverage(
	float2 samplePoint, uint neighborMask, bool mountain)
{
	float coverage = 0.0;
	[unroll]
	for (int directionIndex = 0; directionIndex < 6; directionIndex++)
	{
		if ((neighborMask & (1u << directionIndex)) == 0u)
		{
			continue;
		}
		float2 direction = HFDirection(directionIndex);
		float along = dot(samplePoint, direction);
		float lateral = abs(
			samplePoint.x * direction.y - samplePoint.y * direction.x);
		float width = mountain ?
			lerp(0.29, 0.14, saturate(along)) :
			lerp(0.40, 0.22, saturate(along));
		float end = mountain ? 1.06 : 1.10;
		float longitudinal = smoothstep(0.18, 0.5, along) *
			(1.0 - smoothstep(end, end + 0.26, along));
		float ridge = longitudinal *
			pow(saturate(1.0 - lateral / width), 1.6);
		coverage = max(coverage, ridge);
	}
	return coverage;
}

float HFEvaluateHill(
	HFCellShape cell, float2 p, float radius, float edgeFade,
	float connectionHeight)
{
	float height;
	if (cell.terrain < 0.5)
	{
		float dune = 0.5 + 0.5 * sin(
			p.x * 8.2 + p.y * 2.1 + cell.hash.z * (2.0 * HF_PI));
		dune = pow(dune, 1.8);
		float swell = exp(-(p.x * p.x * 1.45 + p.y * p.y * 2.2));
		height = swell * (0.58 + dune * 0.52);
	}
	else
	{
		float2 offset = float2(
			(cell.hash.y - 0.5) * 0.28,
			(cell.hash.z - 0.5) * 0.20);
		float mainMound = HFEllipticalMound(p - offset, 0.92, 0.70, 1.38);
		float shoulder = HFEllipticalMound(
			p + float2(0.34 + offset.y, -0.12), 0.60, 0.48, 0.68);
		float back = HFEllipticalMound(
			p - float2(0.38, 0.15 - offset.x), 0.52, 0.44, 0.48);
		height = max(mainMound, max(shoulder, back));
	}
	float undulation = 0.045 * sin(
		(p.x * 3.1 - p.y * 2.4) * HF_PI + cell.hash.x * 6.0);
	float heightScale = _HexReliefHeights.x / 1.9;
	// A hill must return to the shared ground datum at the edge. The previous
	// sub-linear exponent kept a large fraction of the height alive even where
	// the mixer was almost black, which made each stamp read as a raised pad.
	float bodyFade = pow(saturate(edgeFade), 1.55);
	float body = max(height + undulation, 0.0) * bodyFade;
	float connectedRidge = connectionHeight * pow(saturate(edgeFade), 0.82);
	return min(
		(body + connectedRidge) * heightScale,
		_HexReliefHeights.x);
}

float HFEvaluateMountain(
	HFCellShape cell, float2 modulePoint, float radius, float edgeFade,
	int moduleIndex, float connectionHeight)
{
	float maximum = cell.terrain < 0.5 ?
		_HexReliefHeights.z : _HexReliefHeights.y;
	float peak01 = SAMPLE_TEXTURE2D_ARRAY_LOD(
		_HexMountainMasks, sampler_HexMountainMasks,
		saturate(modulePoint * 0.5 + 0.5), moduleIndex, 0).r;
	// Explicitly zero samples outside the authored module square. Clamp mode is
	// useful at the edge, but must not smear the last texel into another cell.
	peak01 *= step(max(abs(modulePoint.x), abs(modulePoint.y)), 1.0);
	float peak = peak01 * maximum;
	float apron = pow(max(edgeFade, 0.0001), 1.12) *
		(0.12 + 0.25 * exp(-radius * radius * 2.2));
	float erosion = 0.11 *
		sin(modulePoint.x * 17.0 + modulePoint.y * 7.0 + cell.hash.y * 8.0) *
		sin(modulePoint.y * 13.0 - modulePoint.x * 5.0 + cell.hash.z * 7.0) *
		smoothstep(0.08, 0.78, peak / max(maximum, 0.001));
	float height = (peak + apron + erosion) *
		pow(max(edgeFade, 0.0001), 0.22);
	// Keep the directional ridge inside a compact stamp even if future mask or
	// width tuning makes its longitudinal gate wider. The one-ring CPU patch
	// allocation can then never become the visible end of the ridge.
	float connectionSupport = 1.0 - smoothstep(1.06, 1.34, radius);
	float connectedRidge = connectionHeight * connectionSupport;
	return min(max(height, connectedRidge + apron * 0.42), maximum);
}

float HFStampWeight(float2 samplePoint)
{
	float radius = length(samplePoint) / max(_HexReliefStampScale, 1.001);
	return 1.0 - smoothstep(0.58, 1.0, radius);
}

float HFDistanceToSegment(float2 samplePoint, float2 a, float2 b)
{
	float2 ab = b - a;
	float t = saturate(dot(samplePoint - a, ab) / max(dot(ab, ab), 0.0001));
	return length(samplePoint - (a + ab * t));
}

float HFRiverDistance(float2 samplePoint, uint riverMask)
{
	float distanceToRiver = 1000.0;
	[unroll]
	for (int directionIndex = 0; directionIndex < 6; directionIndex++)
	{
		if ((riverMask & (1u << directionIndex)) == 0u)
		{
			continue;
		}
		// The river mesh runs from the cell center to each crossed edge. A
		// slightly extended segment makes cuts from adjacent cells meet cleanly.
		float2 endpoint = HFDirection(directionIndex) * 1.08;
		distanceToRiver = min(
			distanceToRiver,
			HFDistanceToSegment(samplePoint, float2(0.0, 0.0), endpoint));
	}
	return distanceToRiver;
}

float HFSmoothMaximum(float a, float b, float softness)
{
	float h = saturate(0.5 + 0.5 * (a - b) / softness);
	return lerp(b, a, h) + softness * h * (1.0 - h);
}

void HFEvaluateStamp(
	HFCellShape cell, float2 samplePoint,
	out float height, out float coverage,
	out float2 modulePoint, out float moduleIndex)
{
	height = 0.0;
	coverage = 0.0;
	modulePoint = samplePoint;
	moduleIndex = -1.0;
	if (cell.valid < 0.5 || cell.landform < 0.5)
	{
		return;
	}

	float2 oriented = HFRotateIntoModule(samplePoint, cell.angle);
	float radius = length(samplePoint);
	float stampRadius = radius / max(_HexReliefStampScale, 1.001);
	float geometricEdge = 1.0 - smoothstep(0.56, 1.0, stampRadius);
	float panel = HFMixPanelFor(cell.terrain, cell.landform);
	float2 hfMixerUV =
		oriented / (2.0 * max(_HexReliefStampScale, 1.001)) + 0.5;
	float hfFootprint = HFMixSamplePanel(panel, hfMixerUV);
	// Keep the already-continuous procedural apron authoritative. HF's mask
	// modulates that apron instead of replacing it, so neighboring mountains
	// remain one mass even where the authored mixer has a dark notch.
	float modulatedEdge = saturate(
		geometricEdge * lerp(0.68, 1.16, hfFootprint));
	float edgeFade = lerp(
		geometricEdge, modulatedEdge, saturate(_HexHFReliefFootprint));
	bool mountain = cell.landform > 1.5;
	float connection = HFEvaluateConnections(
		samplePoint, cell.neighborMask, mountain);
	float connectionCoverage = HFEvaluateConnectionCoverage(
		samplePoint, cell.neighborMask, mountain);
	coverage = max(edgeFade, connectionCoverage);

	if (!mountain)
	{
		height = HFEvaluateHill(
			cell, oriented, radius, edgeFade, connection);
		// Do not render a nearly flat relief sheet. Its different lighting and
		// transparent Z-write exposed the underlying patch as a hexagonal plate.
		float visibleHeight = smoothstep(0.025, 0.2, height);
		coverage = max(
			edgeFade * visibleHeight,
			connectionCoverage * smoothstep(0.015, 0.11, height));
		modulePoint = oriented;
		return;
	}

	float width = cell.terrain < 0.5 ?
		_HexReliefWidths.y : _HexReliefWidths.x;
	modulePoint = oriented / max(width, 0.25);
	int selectedModule = HFSelectMountainModule(
		cell.neighborMask, cell.hash.w);
	moduleIndex = (float)selectedModule;
	height = HFEvaluateMountain(
		cell, modulePoint, radius, edgeFade, selectedModule, connection);
}

struct HFOriginalReliefAccumulator
{
	float globalMaximum;
	float mixerWeight;
	float fillWeight;
	float mixerHeight;
	float fillHeight;
	float weightedBaseY;
	float baseWeight;
	float strongestScore;
	float landform;
	float terrain;
	float4 style;
	float2 strongestUV;
	float strongestPanel;
	float riverDistance;
	float mixerSea;
	float fillSea;
	float weightedWaterSurfaceY;
	float waterSurfaceWeight;
};

void HFAccumulateOriginalRelief(
	inout HFOriginalReliefAccumulator accumulator,
	float2 requestedOffset,
	float2 samplePoint)
{
	HFCellShape cell = HFLoadCell(requestedOffset);
	if (cell.valid < 0.5)
	{
		return;
	}

	// Relief-local coordinates already use the hex outer radius, which is the
	// unit used by HF's original 1.6-scale baking quads.
	float2 uv = HFOriginalUV(samplePoint, cell.angle);
	float centralization = HFOriginalCentralization(uv);
	if (centralization <= 0.0001)
	{
		return;
	}

	float panel = HFOriginalPanelForCell(
		cell.terrain, cell.landform, cell.plantLevel, cell.underwater);
	float mixer = HFOriginalSampleMixer(panel, uv) * centralization;
	float heightSample = HFOriginalSampleHeight(panel, uv);

	accumulator.globalMaximum = max(accumulator.globalMaximum, mixer);
	accumulator.mixerWeight += mixer;
	accumulator.fillWeight += centralization;
	accumulator.mixerHeight += heightSample * mixer;
	accumulator.fillHeight += heightSample * centralization;
	accumulator.mixerSea += mixer * cell.underwater;
	accumulator.fillSea += centralization * cell.underwater;
	accumulator.weightedBaseY += cell.baseY * centralization;
	accumulator.baseWeight += centralization;
	accumulator.weightedWaterSurfaceY +=
		cell.waterSurfaceY * centralization * cell.underwater;
	accumulator.waterSurfaceWeight += centralization * cell.underwater;
	accumulator.riverDistance = min(
		accumulator.riverDistance,
		HFRiverDistance(samplePoint, cell.riverMask));

	float score = mixer + centralization * 0.001;
	if (score > accumulator.strongestScore)
	{
		accumulator.strongestScore = score;
		accumulator.landform = cell.landform;
		accumulator.terrain = cell.terrain;
		accumulator.style = cell.hash;
		accumulator.strongestUV = uv;
		accumulator.strongestPanel = panel;
	}
}

HFReliefSurface HF_EvaluateOriginalRelief(
	float cellIndex, float2 localPosition)
{
	HFReliefSurface result;
	result.y = 0.0;
	result.height = 0.0;
	result.height01 = 0.0;
	result.bakedHeight = 0.5;
	result.landform = 0.0;
	result.terrain = 0.0;
	result.coverage = 0.0;
	result.style = 0.0;
	result.diffuse = 0.0;
	result.moduleUV = localPosition * 0.5 + 0.5;
	result.moduleIndex = -1.0;
	result.seaInfluence = 0.0;
	result.waterSurfaceY = 0.0;

	float width = _HexCellData_TexelSize.z;
	float2 rootOffset = float2(
		fmod(floor(cellIndex + 0.5), width),
		floor((cellIndex + 0.5) / width));
	HFCellShape rootCell = HFLoadCell(rootOffset);
	if (rootCell.valid < 0.5)
	{
		return result;
	}

	HFOriginalReliefAccumulator accumulator;
	accumulator.globalMaximum = 0.0;
	accumulator.mixerWeight = 0.0;
	accumulator.fillWeight = 0.0;
	accumulator.mixerHeight = 0.0;
	accumulator.fillHeight = 0.0;
	accumulator.weightedBaseY = 0.0;
	accumulator.baseWeight = 0.0;
	accumulator.strongestScore = -1.0;
	accumulator.landform = rootCell.landform;
	accumulator.terrain = rootCell.terrain;
	accumulator.style = rootCell.hash;
	accumulator.strongestUV = 0.5;
	accumulator.strongestPanel = HFOriginalPanelForCell(
		rootCell.terrain, rootCell.landform, rootCell.plantLevel,
		rootCell.underwater);
	accumulator.riverDistance = 1000.0;
	accumulator.mixerSea = 0.0;
	accumulator.fillSea = 0.0;
	accumulator.weightedWaterSurfaceY = 0.0;
	accumulator.waterSurfaceWeight = 0.0;

	HFAccumulateOriginalRelief(
		accumulator, rootOffset, localPosition);
	[unroll]
	for (int direction = 0; direction < 6; direction++)
	{
		float2 firstOffset = HFNeighborOffset(rootOffset, direction);
		float2 firstCenter = HFNeighborCenter(direction);
		HFAccumulateOriginalRelief(
			accumulator, firstOffset, localPosition - firstCenter);

		// A rotated 1.6-radius square can just reach the root hex from the
		// diagonal half of ring two. Sampling these six candidates makes the
		// result root-invariant at shared hex vertices; the axial half of the
		// ring is farther than HF's 1.6*sqrt(2) potential reach.
		int nextDirection = (direction + 1) % 6;
		float2 cornerOffset = HFNeighborOffset(
			firstOffset, nextDirection);
		float2 cornerCenter = firstCenter +
			HFNeighborCenter(nextDirection);
		HFAccumulateOriginalRelief(
			accumulator, cornerOffset, localPosition - cornerCenter);
	}

	float missingStrength = 1.0 - saturate(accumulator.globalMaximum);
	float totalWeight = accumulator.mixerWeight +
		accumulator.fillWeight * missingStrength;
	if (totalWeight <= 0.0001)
	{
		return result;
	}
	float inverseWeight = 1.0 / totalWeight;
	float heightSample = (accumulator.mixerHeight +
		accumulator.fillHeight * missingStrength) * inverseWeight;
	// Diffuse is reconstructed per fragment by HF_EvaluateOriginalDiffuse.
	// Sampling it here would reduce HF's full-resolution colour to one sample per
	// tessellated vertex and would also repeat the work for the shadow offsets.
	result.diffuse = 0.0;

	// HF's baked height is centered around 0.5 and both its terrain mesh and
	// water plane share one Y datum. Blending Catlike's per-cell dry / shallow /
	// deep base elevations here shifts the zero crossing and creates false sand
	// islands whose coarse triangulation changes with camera distance.
	float baseY = _HexHFOriginalDatumY;
	float displacement =
		(heightSample - 0.5) * _HexHFOriginalHeightScale;
	// HF attenuates downward displacement to avoid deep pits.
	if (displacement < 0.0)
	{
		displacement *= 0.6;
	}
	float riverFade = smoothstep(
		_HexReliefRiverCarve.x,
		_HexReliefRiverCarve.y,
		accumulator.riverDistance);
	// River water is 1.5 units below the logical elevation in this project.
	// Sink the stamped terrain slightly farther at the channel core.
	displacement = lerp(-1.65, displacement, riverFade);

	result.height = displacement;
	result.height01 = saturate(
		displacement / max(_HexHFOriginalHeightScale * 0.5, 0.001));
	result.bakedHeight = heightSample;
	result.landform = accumulator.landform;
	result.terrain = accumulator.terrain;
	result.coverage = 1.0;
	result.style = accumulator.style;
	result.moduleUV = accumulator.strongestUV;
	result.moduleIndex = accumulator.strongestPanel;
	result.seaInfluence = saturate(
		(accumulator.mixerSea + accumulator.fillSea * missingStrength) *
		inverseWeight);
	result.waterSurfaceY = accumulator.waterSurfaceWeight > 0.0001 ?
		_HexHFOriginalDatumY : 0.0;
	result.y = baseY + displacement + 0.018;
	return result;
}

HFReliefSurface HF_EvaluateLegacyRelief(float cellIndex, float2 localPosition)
{
	HFReliefSurface result;
	result.y = 0.0;
	result.height = 0.0;
	result.height01 = 0.0;
	result.bakedHeight = 0.5;
	result.landform = 0.0;
	result.terrain = 0.0;
	result.coverage = 0.0;
	result.style = 0.0;
	result.diffuse = 0.0;
	result.moduleUV = localPosition * 0.5 + 0.5;
	result.moduleIndex = -1.0;
	result.seaInfluence = 0.0;
	result.waterSurfaceY = 0.0;

	float width = _HexCellData_TexelSize.z;
	float2 rootOffset = float2(
		fmod(floor(cellIndex + 0.5), width),
		floor((cellIndex + 0.5) / width));

	float weightedBaseY = 0.0;
	float totalBaseWeight = 0.0;
	float strongestScore = -1.0;
	float hasHeight = 0.0;
	float riverDistance = 1000.0;
	HFCellShape rootCell = HFLoadCell(rootOffset);

	[unroll]
	for (int candidate = 0; candidate < 7; candidate++)
	{
		int directionIndex = candidate - 1;
		float2 requestedOffset = rootOffset;
		float2 centerOffset = float2(0.0, 0.0);
		if (candidate > 0)
		{
			requestedOffset = HFNeighborOffset(rootOffset, directionIndex);
			centerOffset = HFNeighborCenter(directionIndex);
		}
		float2 candidatePoint = localPosition - centerOffset;
		HFCellShape cell = HFLoadCell(requestedOffset);
		if (cell.valid < 0.5)
		{
			continue;
		}

		float stampWeight = HFStampWeight(candidatePoint);
		if (stampWeight > 0.0001)
		{
			weightedBaseY += cell.baseY * stampWeight;
			totalBaseWeight += stampWeight;
		}
		riverDistance = min(
			riverDistance,
			HFRiverDistance(candidatePoint, cell.riverMask));

		float stampHeight;
		float stampCoverage;
		float2 modulePoint;
		float moduleIndex;
		HFEvaluateStamp(
			cell, candidatePoint,
			stampHeight, stampCoverage, modulePoint, moduleIndex);
		if (stampHeight <= 0.0001)
		{
			continue;
		}

		if (hasHeight < 0.5)
		{
			result.height = stampHeight;
			hasHeight = 1.0;
		}
		else
		{
			// HF's Max mixer is associative, so every patch gets exactly the same
			// result regardless of which cell is considered the root. Iterative
			// soft-max was order-dependent and produced triangular/hexagonal seams
			// where several mountains overlapped.
			result.height = max(result.height, stampHeight);
		}
		result.coverage = max(result.coverage, stampCoverage);
		float score = stampHeight + stampCoverage * 0.18;
		if (score > strongestScore)
		{
			strongestScore = score;
			result.landform = cell.landform;
			result.terrain = cell.terrain;
			result.style = cell.hash;
			result.moduleUV = modulePoint * 0.5 + 0.5;
			result.moduleIndex = moduleIndex;
		}
	}

	float rootBaseY = rootCell.valid > 0.5 ? rootCell.baseY : 0.0;
	float baseY = totalBaseWeight > 0.0001 ?
		weightedBaseY / totalBaseWeight : rootBaseY;
	float riverFade = smoothstep(
		_HexReliefRiverCarve.x, _HexReliefRiverCarve.y, riverDistance);
	result.height *= riverFade;
	result.coverage *= riverFade;
	float maximum = result.landform < 1.5 ?
		_HexReliefHeights.x :
		(result.terrain < 0.5 ? _HexReliefHeights.z : _HexReliefHeights.y);
	result.height01 = saturate(result.height / max(maximum, 0.001));
	result.bakedHeight = saturate(
		0.5 + result.height / max(_HexHFOriginalHeightScale, 0.001));
	result.y = baseY + result.height + 0.018;
	return result;
}

HFReliefSurface HF_EvaluateRelief(float cellIndex, float2 localPosition)
{
	float originalBlend = saturate(_HexHFOriginalBlend);
	if (originalBlend > 0.999)
	{
		return HF_EvaluateOriginalRelief(cellIndex, localPosition);
	}
	HFReliefSurface legacy = HF_EvaluateLegacyRelief(
		cellIndex, localPosition);
	if (originalBlend < 0.001)
	{
		return legacy;
	}
	HFReliefSurface original = HF_EvaluateOriginalRelief(
		cellIndex, localPosition);
	legacy.y = lerp(legacy.y, original.y, originalBlend);
	legacy.height = lerp(legacy.height, original.height, originalBlend);
	legacy.height01 = lerp(
		legacy.height01, original.height01, originalBlend);
	legacy.bakedHeight = lerp(
		legacy.bakedHeight, original.bakedHeight, originalBlend);
	legacy.coverage = lerp(legacy.coverage, original.coverage, originalBlend);
	legacy.diffuse = original.diffuse;
	legacy.seaInfluence = lerp(
		legacy.seaInfluence, original.seaInfluence, originalBlend);
	legacy.waterSurfaceY = lerp(
		legacy.waterSurfaceY, original.waterSurfaceY, originalBlend);
	if (originalBlend > 0.5)
	{
		legacy.landform = original.landform;
		legacy.terrain = original.terrain;
		legacy.style = original.style;
		legacy.moduleUV = original.moduleUV;
		legacy.moduleIndex = original.moduleIndex;
	}
	return legacy;
}

struct HFOriginalDiffuseAccumulator
{
	float globalMaximum;
	float mixerWeight;
	float fillWeight;
	float3 mixerDiffuse;
	float3 fillDiffuse;
};

void HFAccumulateOriginalDiffuse(
	inout HFOriginalDiffuseAccumulator accumulator,
	float2 requestedOffset,
	float2 samplePoint)
{
	HFCellShape cell = HFLoadCell(requestedOffset);
	if (cell.valid < 0.5)
	{
		return;
	}

	float2 uv = HFOriginalUV(samplePoint, cell.angle);
	float centralization = HFOriginalCentralization(uv);
	if (centralization <= 0.0001)
	{
		return;
	}

	float panel = HFOriginalPanelForCell(
		cell.terrain, cell.landform, cell.plantLevel, cell.underwater);
	float mixer = HFOriginalSampleMixer(panel, uv) * centralization;
	// HF does not blend Sand1_d as an ordinary terrain diffuse. Its Oven draws
	// sea colour in a separate border-only pass below the 0.55 height contour.
	// Excluding it here prevents the complete Water_m ownership lobes from
	// becoming opaque beige terrain; the relief fragment adds the submerged
	// sand/deep-floor art from the final reconstructed height instead.
	if (cell.underwater > 0.5)
	{
		return;
	}
	float3 diffuseSample = HFOriginalSampleDiffuse(panel, uv);
	accumulator.globalMaximum = max(accumulator.globalMaximum, mixer);
	accumulator.mixerWeight += mixer;
	accumulator.fillWeight += centralization;
	accumulator.mixerDiffuse += diffuseSample * mixer;
	accumulator.fillDiffuse += diffuseSample * centralization;
}

// Reconstruct diffuse in the fragment stage. HF baked its colour at full
// texture resolution; interpolating one colour per tessellated vertex exposes
// every micro-triangle as a large triangular colour patch.
float3 HF_EvaluateOriginalDiffuse(float cellIndex, float2 localPosition)
{
	float width = _HexCellData_TexelSize.z;
	float2 rootOffset = float2(
		fmod(floor(cellIndex + 0.5), width),
		floor((cellIndex + 0.5) / width));
	HFCellShape rootCell = HFLoadCell(rootOffset);
	if (rootCell.valid < 0.5)
	{
		return 0.0;
	}

	HFOriginalDiffuseAccumulator accumulator;
	accumulator.globalMaximum = 0.0;
	accumulator.mixerWeight = 0.0;
	accumulator.fillWeight = 0.0;
	accumulator.mixerDiffuse = 0.0;
	accumulator.fillDiffuse = 0.0;

	HFAccumulateOriginalDiffuse(
		accumulator, rootOffset, localPosition);
	[unroll]
	for (int direction = 0; direction < 6; direction++)
	{
		float2 firstOffset = HFNeighborOffset(rootOffset, direction);
		float2 firstCenter = HFNeighborCenter(direction);
		HFAccumulateOriginalDiffuse(
			accumulator, firstOffset, localPosition - firstCenter);

		int nextDirection = (direction + 1) % 6;
		float2 cornerOffset = HFNeighborOffset(
			firstOffset, nextDirection);
		float2 cornerCenter = firstCenter + HFNeighborCenter(nextDirection);
		HFAccumulateOriginalDiffuse(
			accumulator, cornerOffset, localPosition - cornerCenter);
	}

	float missingStrength = 1.0 - saturate(accumulator.globalMaximum);
	float totalWeight = accumulator.mixerWeight +
		accumulator.fillWeight * missingStrength;
	return totalWeight > 0.0001 ?
		(accumulator.mixerDiffuse +
			accumulator.fillDiffuse * missingStrength) / totalWeight :
		float3(0.5, 0.5, 0.5);
}

#endif
