#include "HexMetrics.hlsl"

TEXTURE2D(_HexCellData);
SAMPLER(sampler_HexCellData);
TEXTURE2D(_HexPoliticalData);
SAMPLER(sampler_HexPoliticalData);
TEXTURE2D(_HexPoliticalColorData);
SAMPLER(sampler_HexPoliticalColorData);
TEXTURE2D(_HexCellOverlayData);
SAMPLER(sampler_HexCellOverlayData);
TEXTURE2D(_HexOccupationData);
SAMPLER(sampler_HexOccupationData);
TEXTURE2D(_HexLandBuildSelectionData);
// 与 overlay / 逻辑格数据相同：Point + Clamp，不单独占 sampler 槽（D3D11 ps_5_0 上限 16）。
float _HexLandBuildSelectionActive;
float _HexLandBuildSelectionStrength;
float4 _HexCellData_TexelSize;

float4 FilterCellData(float4 data, bool editMode)
{
	if (editMode)
	{
		data.xy = 1;
	}
	return data;
}

float4 GetCellData(float3 uv2, int index, bool editMode)
{
	float2 uv;
	uv.x = (uv2[index] + 0.5) * _HexCellData_TexelSize.x;
	float row = floor(uv.x);
	uv.x -= row;
	uv.y = (row + 0.5) * _HexCellData_TexelSize.y;
	float4 data = SAMPLE_TEXTURE2D_LOD(
		_HexCellData, sampler_HexCellData, uv, 0);
	data.w *= 255;
	return FilterCellData(data, editMode);
}

float4 GetCellData(float2 cellDataCoordinates, bool editMode)
{
	float2 uv = cellDataCoordinates + 0.5;
	uv.x *= _HexCellData_TexelSize.x;
	uv.y *= _HexCellData_TexelSize.y;
	return FilterCellData(
		SAMPLE_TEXTURE2D_LOD(_HexCellData, sampler_HexCellData, uv, 0),
		editMode);
}

// Cell highlighting data, in hex space.
// x: Highlight center X position.
// y: Highlight center Z position.
// z: Highlight radius, squared with bias. Is negative if there is no highlight.
// w: Hex grid wrap size, for X wrapping. Is zero if there is no wrapping.
float4 _CellHighlighting;

// Shared editor overlay control. Catlike's terrain keeps its material keyword;
// HF uses this global value because its relief and water are separate materials.
float _HexEditorShowGrid;
float _HexMapShowGrid;
float4 _HexGridColor;
float4 _HexSelectionColor;
float4 _HexPoliticalBorderCoreColor;
float4 _HexPoliticalBorderGlowColor;
float4 _HexPoliticalBorderWidths;
float _HexCellOverlayStrength;

// Hex grid data derived from world-space XZ position.
struct HexGridData
{
	// Cell center in hex space.
	float2 cellCenter;

	// Approximate cell offset coordinates.
	// Good enough for point-filtered sampling.
	float2 cellOffsetCoordinates;

	// For potential future use. U covers entire cell, V wraps a bit.
	float2 cellUV;

	// Position relative to the nearest cell center, in hex space.
	float2 localPosition;

	// Hexagonal distance to cell center, 0 at center, 1 at edges.
	float distanceToCenter;

	// Smoothstep smoothing for cell center distance transitions.
	// Based on screen-space derivatives.
	float distanceSmoothing;

	// Is highlighed if square distance from cell to highlight center
	// is below threshold. Works up to brush size 6.
	bool IsHighlighted()
	{
		float2 cellToHighlight = abs(_CellHighlighting.xy - cellCenter);

		// Adjust for world X wrapping if needed.
		if (cellToHighlight.x > _CellHighlighting.w * 0.5)
		{
			cellToHighlight.x -= _CellHighlighting.w;
		}

		return dot(cellToHighlight, cellToHighlight) < _CellHighlighting.z;
	}

	// Smoothstep from 0 to 1 at cell center distance threshold.
	float Smoothstep01(float threshold)
	{
		return smoothstep(
			threshold - distanceSmoothing,
			threshold + distanceSmoothing,
			distanceToCenter);
	}

	// Smoothstep from 1 to 0 at cell center distance threshold.
	float Smoothstep10(float threshold){
		return smoothstep(
			threshold + distanceSmoothing,
			threshold - distanceSmoothing,
			distanceToCenter);
	}

	// Smoothstep from 0 to 1 inside cell center distance range.
	float SmoothstepRange (float innerThreshold, float outerThreshold)
	{
		return Smoothstep01(innerThreshold) * Smoothstep10(outerThreshold);
	}
};

#define HEX_ANGLED_EDGE_VECTOR float2(1, sqrt(3))

// Calculate hexagonal center-edge distance for point relative to
// the center in hex space. 0 at cell center and 1 at edges.
float HexagonalCenterToEdgeDistance(float2 p)
{
	// Reduce problem to one quadrant.
	p = abs(p);
	// Calculate distance to angled edge.
	float d = dot(p, normalize(HEX_ANGLED_EDGE_VECTOR));
	// Incorporate distance to vertical edge.
	d = max(d, p.x);
	// Double to increase range from center to edge to 0-1.
	return 2 * d;
}

// Calculate hex-based modulo to find position vector.
float2 HexModulo(float2 p)
{
	return p - HEX_ANGLED_EDGE_VECTOR * floor(p / HEX_ANGLED_EDGE_VECTOR);
}

HexGridData GetHexGridDataFromHexSpace(float2 p)
{
	// Vectors from nearest two cell centers to position.
	float2 gridOffset = HEX_ANGLED_EDGE_VECTOR * 0.5;
	float2 a = HexModulo(p) - gridOffset;
	float2 b = HexModulo(p - gridOffset) - gridOffset;
	bool aIsNearest = dot(a, a) < dot(b, b);

	float2 vectorFromCenterToPosition = aIsNearest ? a : b;

	HexGridData d;
	d.cellCenter = p - vectorFromCenterToPosition;
	d.cellOffsetCoordinates.x = d.cellCenter.x - (aIsNearest ? 0.5 : 0.0);
	d.cellOffsetCoordinates.y = d.cellCenter.y / OUTER_TO_INNER;
	d.cellUV = vectorFromCenterToPosition + 0.5;
	d.localPosition = vectorFromCenterToPosition;
	d.distanceToCenter = HexagonalCenterToEdgeDistance(
		vectorFromCenterToPosition);
	d.distanceSmoothing = fwidth(d.distanceToCenter);
	return d;
}

// Get hex grid data analytically derived from world-space XZ position.
HexGridData GetHexGridData(float2 worldPositionXZ)
{
	return GetHexGridDataFromHexSpace(WoldToHexSpace(worldPositionXZ));
}

float2 GetHexLogicalTextureUV(float2 cellOffsetCoordinates)
{
	return (cellOffsetCoordinates + 0.5) * _HexCellData_TexelSize.xy;
}

float4 GetHexCellOverlay(float2 cellOffsetCoordinates)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HexCellOverlayData, sampler_HexCellOverlayData,
		GetHexLogicalTextureUV(cellOffsetCoordinates), 0);
}

float4 GetHexOccupation(float2 cellOffsetCoordinates)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HexOccupationData, sampler_HexOccupationData,
		GetHexLogicalTextureUV(cellOffsetCoordinates), 0);
}

float GetLandBuildSelectionState(float2 cellOffsetCoordinates)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HexLandBuildSelectionData, sampler_HexCellOverlayData,
		GetHexLogicalTextureUV(cellOffsetCoordinates), 0).r * 255.0;
}

float3 ApplyLandBuildSelectionOverlay(float3 baseColor, HexGridData grid)
{
	if (_HexLandBuildSelectionActive < 0.5)
	{
		return baseColor;
	}

	float state = GetLandBuildSelectionState(grid.cellOffsetCoordinates);
	if (state < 0.5)
	{
		return baseColor;
	}

	float aa = max(grid.distanceSmoothing, 0.004);
	// 格内强度一致，仅在六边形外缘做抗锯齿，避免不可选格出现明显 hex 边。
	float edgeFade = 1.0 - smoothstep(0.93 - aa, 1.02 + aa, grid.distanceToCenter);
	float strength = _HexLandBuildSelectionStrength * edgeFade;

	// 选格模式以 blocked / allowed 为主色，不是政治图或 relief 上的薄涂。
	float3 blockedColor = float3(0.58, 0.62, 0.68);
	float3 allowedColor = float3(0.48, 0.84, 0.42);
	float3 allowedHighlight = float3(0.72, 0.98, 0.52);
	float3 selectionColor;

	if (state < 1.5)
	{
		selectionColor = blockedColor;
	}
	else if (state < 2.5)
	{
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		selectionColor = lerp(
			allowedColor, allowedHighlight,
			smoothstep(0.0, 0.65, inside) * 0.42);
		float edgeLine = smoothstep(0.93 - aa, 0.97, grid.distanceToCenter) *
			(1.0 - smoothstep(0.98, 1.02 + aa, grid.distanceToCenter));
		selectionColor = lerp(
			selectionColor, float3(0.72, 0.98, 0.52), edgeLine * 0.18 * strength);
	}
	else if (state < 3.5)
	{
		float3 embarkColor = float3(0.92, 0.34, 0.28);
		float3 embarkHighlight = float3(1.0, 0.48, 0.36);
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		selectionColor = lerp(embarkColor, embarkHighlight, smoothstep(0.0, 0.7, inside) * 0.55);
		float edgeLine = smoothstep(0.90 - aa, 0.96, grid.distanceToCenter) *
			(1.0 - smoothstep(0.98, 1.02 + aa, grid.distanceToCenter));
		selectionColor = lerp(selectionColor, float3(1.0, 0.62, 0.48), edgeLine * 0.35 * strength);
	}
	else if (state < 4.5)
	{
		float3 hoverColor = float3(0.78, 1.0, 0.58);
		float3 hoverHighlight = float3(0.92, 1.0, 0.72);
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		selectionColor = lerp(hoverColor, hoverHighlight, smoothstep(0.0, 0.55, inside) * 0.65);
		float edgeLine = smoothstep(0.88 - aa, 0.95, grid.distanceToCenter) *
			(1.0 - smoothstep(0.97, 1.02 + aa, grid.distanceToCenter));
		selectionColor = lerp(selectionColor, float3(1.0, 1.0, 0.82), edgeLine * 0.28 * strength);
	}
	else if (state < 5.5)
	{
		float3 seaColor = float3(0.38, 0.72, 0.96);
		float3 seaHighlight = float3(0.52, 0.86, 1.0);
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		selectionColor = lerp(seaColor, seaHighlight, smoothstep(0.0, 0.62, inside) * 0.48);
		float edgeLine = smoothstep(0.91 - aa, 0.97, grid.distanceToCenter) *
			(1.0 - smoothstep(0.98, 1.02 + aa, grid.distanceToCenter));
		selectionColor = lerp(selectionColor, float3(0.72, 0.94, 1.0), edgeLine * 0.22 * strength);
	}
	else if (state < 6.5)
	{
		float3 seaHover = float3(0.55, 0.88, 1.0);
		float3 seaHoverHi = float3(0.78, 0.96, 1.0);
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		selectionColor = lerp(seaHover, seaHoverHi, smoothstep(0.0, 0.55, inside) * 0.62);
		float edgeLine = smoothstep(0.88 - aa, 0.95, grid.distanceToCenter) *
			(1.0 - smoothstep(0.97, 1.02 + aa, grid.distanceToCenter));
		selectionColor = lerp(selectionColor, float3(0.92, 1.0, 1.0), edgeLine * 0.26 * strength);
	}
	else
	{
		float3 blockedSea = float3(0.18, 0.32, 0.50);
		float3 blockedSeaHi = float3(0.26, 0.42, 0.60);
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		selectionColor = lerp(blockedSea, blockedSeaHi, smoothstep(0.0, 0.55, inside) * 0.35);
		float edgeLine = smoothstep(0.90 - aa, 0.96, grid.distanceToCenter) *
			(1.0 - smoothstep(0.98, 1.02 + aa, grid.distanceToCenter));
		selectionColor = lerp(selectionColor, float3(0.34, 0.50, 0.66), edgeLine * 0.14 * strength);
	}

	// 极少量保留底色只为纸张微起伏；主导仍是选格色。
	return saturate(lerp(baseColor, selectionColor, 0.94 * strength));
}

float GetHexCountryId(float2 cellOffsetCoordinates)
{
	float2 packed = round(SAMPLE_TEXTURE2D_LOD(
		_HexPoliticalData, sampler_HexPoliticalData,
		GetHexLogicalTextureUV(cellOffsetCoordinates), 0).rg * 255.0);
	return packed.x + packed.y * 256.0;
}

float4 GetHexPoliticalColor(float2 cellOffsetCoordinates)
{
	return SAMPLE_TEXTURE2D_LOD(
		_HexPoliticalColorData, sampler_HexPoliticalColorData,
		GetHexLogicalTextureUV(cellOffsetCoordinates), 0);
}

float2 GetClosestHexEdgeNormal(float2 localPosition)
{
	float2 signs = lerp(-1.0, 1.0, step(0.0, localPosition));
	float2 absolutePosition = abs(localPosition);
	float angledDistance = dot(
		absolutePosition, float2(0.5, OUTER_TO_INNER));
	if (absolutePosition.x >= angledDistance)
	{
		return float2(signs.x, 0.0);
	}
	return float2(signs.x * 0.5, signs.y * OUTER_TO_INNER);
}

// Outward normals for the six hex edges (same basis as GetClosestHexEdgeNormal).
float2 GetHexEdgeNormalByIndex(int edgeIndex)
{
	if (edgeIndex == 0) return float2(1.0, 0.0);
	if (edgeIndex == 1) return float2(0.5, OUTER_TO_INNER);
	if (edgeIndex == 2) return float2(-0.5, OUTER_TO_INNER);
	if (edgeIndex == 3) return float2(-1.0, 0.0);
	if (edgeIndex == 4) return float2(-0.5, -OUTER_TO_INNER);
	return float2(0.5, -OUTER_TO_INNER);
}

float IsPoliticalBoundary(HexGridData grid)
{
	float countryId = GetHexCountryId(grid.cellOffsetCoordinates);
	if (countryId < 0.5)
	{
		return 0.0;
	}

	float2 edgeNormal = GetClosestHexEdgeNormal(grid.localPosition);
	HexGridData neighbor = GetHexGridDataFromHexSpace(
		grid.cellCenter + edgeNormal * 0.75);
	float neighborCountryId = GetHexCountryId(
		neighbor.cellOffsetCoordinates);
	// A zero-valued neighbor is water, so the same treatment naturally wraps a
	// country's coastline just like the colored outlines in the reference map.
	return abs(neighborCountryId - countryId) > 0.5 ? 1.0 : 0.0;
}

// Min distance to any political/coast hex edge (0 on the edge, 1 toward center).
// Also returns the outward normal of the nearest border edge (for emboss lighting).
// Checks all 6 sides so straight multi-hex borders keep hex teeth (not closest-edge only).
float EvaluatePoliticalBorderEdge(
	HexGridData grid, out float2 borderEdgeNormal)
{
	borderEdgeNormal = GetClosestHexEdgeNormal(grid.localPosition);
	float countryId = GetHexCountryId(grid.cellOffsetCoordinates);
	if (countryId < 0.5)
	{
		return 1.0;
	}

	float bestDist = 1.0;
	bool found = false;
	for (int edgeIndex = 0; edgeIndex < 6; edgeIndex++)
	{
		float2 edgeNormal = GetHexEdgeNormalByIndex(edgeIndex);
		HexGridData neighbor = GetHexGridDataFromHexSpace(
			grid.cellCenter + edgeNormal * 0.75);
		float neighborCountryId = GetHexCountryId(
			neighbor.cellOffsetCoordinates);
		if (abs(neighborCountryId - countryId) <= 0.5)
		{
			continue;
		}

		float2 unitN = normalize(edgeNormal);
		float along = dot(grid.localPosition, unitN);
		// Edge sits at along ≈ 0.5 in this hex metric.
		float dist = saturate(1.0 - along * 2.0);
		if (!found || dist < bestDist)
		{
			bestDist = dist;
			borderEdgeNormal = edgeNormal;
			found = true;
		}
	}

	return found ? bestDist : 1.0;
}

// Vic3-like temporary occupation: continuous diagonal stripes over the
// whole occupied region (same hatch phase across neighboring cells).
float3 ApplyOccupationStripes(float3 baseColor, HexGridData grid)
{
	float4 occupation = GetHexOccupation(grid.cellOffsetCoordinates);
	float strength = saturate(occupation.a);
	if (strength < 0.01)
	{
		return baseColor;
	}

	// Soften only against unoccupied neighbors — keep interior seams solid.
	float2 edgeNormal = GetClosestHexEdgeNormal(grid.localPosition);
	HexGridData neighbor = GetHexGridDataFromHexSpace(
		grid.cellCenter + edgeNormal * 0.75);
	float neighborOcc = GetHexOccupation(neighbor.cellOffsetCoordinates).a;
	float edgeFade = 1.0;
	if (neighborOcc < 0.01)
	{
		edgeFade = 1.0 - smoothstep(
			0.78 - grid.distanceSmoothing,
			1.0 + grid.distanceSmoothing,
			grid.distanceToCenter);
	}
	strength *= edgeFade;
	if (strength < 0.01)
	{
		return baseColor;
	}

	// Continuous hex-space position — stripes align across tile borders.
	float2 hexPos = grid.cellCenter + grid.localPosition;
	float2 hatchDir = normalize(float2(-0.72, 1.0));
	float spacing = 0.30;
	float along = dot(hexPos, hatchDir) / spacing;
	float hatchDist = abs(frac(along) - 0.5) * spacing;
	float halfWidth = 0.048;
	float aa = max(fwidth(hatchDist), 0.012);
	float stripe = 1.0 - smoothstep(
		halfWidth - aa * 0.25, halfWidth + aa, hatchDist);

	float wash = strength * 0.30;
	float stripeWeight = stripe * strength * 0.82;
	float3 washed = lerp(baseColor, occupation.rgb, wash);
	return saturate(lerp(washed, occupation.rgb, stripeWeight));
}

// Gameplay region selection (AreaInit.RegionSelectionOverlayAlpha = 0.127).
static const float RegionSelectionOverlayAlpha = 0.127;
static const float RegionSelectionOverlayAlphaTolerance = 0.018;

bool IsRegionSelectionOverlay(float4 overlay)
{
	return abs(overlay.a - RegionSelectionOverlayAlpha) <
		RegionSelectionOverlayAlphaTolerance;
}

float3 ApplyRegionSelectionOutline(float3 baseColor, HexGridData grid)
{
	float bestRim = 0.0;
	float aa = max(grid.distanceSmoothing, 0.004);

	for (int edgeIndex = 0; edgeIndex < 6; edgeIndex++)
	{
		float2 edgeNormal = GetHexEdgeNormalByIndex(edgeIndex);
		HexGridData neighbor = GetHexGridDataFromHexSpace(
			grid.cellCenter + edgeNormal * 0.75);
		float4 neighborOverlay = GetHexCellOverlay(
			neighbor.cellOffsetCoordinates);
		if (IsRegionSelectionOverlay(neighborOverlay))
		{
			continue;
		}

		float2 unitN = normalize(edgeNormal);
		float along = dot(grid.localPosition, unitN);
		float outerEdge = smoothstep(0.46 - aa, 0.52 + aa, along);
		float falloff = 1.0 - smoothstep(0.54 + aa, 0.78, along);
		bestRim = max(bestRim, outerEdge * falloff);
	}

	if (bestRim <= 0.001)
	{
		return baseColor;
	}

	float rim = bestRim * bestRim * (3.0 - 2.0 * bestRim);
	float3 ink = float3(0.018, 0.012, 0.008);
	float3 deepGold = float3(0.52, 0.36, 0.10);
	float3 metalGold = float3(0.90, 0.68, 0.22);
	baseColor = lerp(baseColor, ink, rim * 0.82);
	baseColor = lerp(baseColor, deepGold, rim * 0.62);
	baseColor = lerp(baseColor, metalGold, rim * 0.48);
	baseColor += metalGold * rim * 0.22;
	return saturate(baseColor);
}

// Unified gameplay/editor surface treatment. Everything is evaluated in hex
// space, so the wash, selection, grid, and borders remain attached to displaced
// relief instead of becoming a second flat decal mesh.
float3 ApplyHexMapSurfaceOverlay(
	float3 baseColor,
	HexGridData grid,
	float showGrid,
	float showPoliticalBorders)
{
	float4 overlay = GetHexCellOverlay(grid.cellOffsetCoordinates);
	float regionSelection = IsRegionSelectionOverlay(overlay) ? 1.0 : 0.0;
	float overlayInset = 1.0 - smoothstep(
		0.88 - grid.distanceSmoothing,
		1.0 + grid.distanceSmoothing,
		grid.distanceToCenter);
	float overlayWeight = saturate(overlay.a * _HexCellOverlayStrength);
	if (regionSelection < 0.5)
	{
		overlayWeight *= overlayInset;
	}

	baseColor = lerp(baseColor, overlay.rgb, overlayWeight);

	if (regionSelection > 0.5)
	{
		baseColor = ApplyRegionSelectionOutline(baseColor, grid);
	}

	// Fade the ordinary cell lattice before individual cells become sub-pixel.
	// This keeps the strategic overview clean while preserving a readable near
	// grid with derivative-stable antialiasing.
	float gridDistanceFade = 1.0 - smoothstep(
		0.08, 0.30, grid.distanceSmoothing);
	float gridLine = grid.SmoothstepRange(0.955, 1.01) *
		saturate(showGrid) * gridDistanceFade;
	// Keep the authoring lattice strong, while terrain shapes lead the gameplay
	// view. Selection and political boundaries have their own unchanged controls.
	gridLine *= lerp(0.5, 1.0, saturate(_HexEditorShowGrid));
	baseColor = lerp(
		baseColor, _HexGridColor.rgb,
		gridLine * saturate(_HexGridColor.a));

	if (showPoliticalBorders > 0.5 && IsPoliticalBoundary(grid) > 0.5)
	{
		// Near/mid-near political limb:
		// black seam → fixed deepened country rim → soft half-cell wash inward.
		float inside = max(1.0 - grid.distanceToCenter, 0.0);
		float aa = max(grid.distanceSmoothing, 0.004);
		float coreWidth = max(_HexPoliticalBorderWidths.x, aa * 0.9);
		float washReach = max(_HexPoliticalBorderWidths.y, 0.45);

		float4 packedCountryColor = GetHexPoliticalColor(
			grid.cellOffsetCoordinates);
		float3 political = saturate(packedCountryColor.rgb);
		float hasCountryColor = step(0.001, packedCountryColor.a);
		political = lerp(
			saturate(_HexPoliticalBorderGlowColor.rgb),
			political,
			hasCountryColor);

		float luma = dot(political, float3(0.299, 0.587, 0.114));
		// Mid-view style rim: richer chroma, not muddy charcoal.
		float3 rimColor = saturate(luma + (political - luma) * 1.28);
		rimColor = saturate(rimColor * float3(1.04, 1.03, 1.02));
		float3 deepRim = saturate(political * float3(0.78, 0.82, 0.74));
		float3 washColor = lerp(deepRim, rimColor, 0.55);
		washColor = lerp(washColor, political, 0.22);

		// Soft country wash: at least ~half a cell inward from the seam.
		float wash = 1.0 - smoothstep(coreWidth * 1.8, washReach, inside);
		wash = wash * wash * (3.0 - 2.0 * wash);
		baseColor = lerp(baseColor, washColor, wash * 0.38);
		baseColor = lerp(baseColor, deepRim, wash * 0.16);

		// Fixed deepened country band just inside the black divider.
		float rimBand = smoothstep(coreWidth * 0.6, coreWidth * 1.6, inside) *
			(1.0 - smoothstep(coreWidth * 3.2, coreWidth * 6.5, inside));
		rimBand = rimBand * rimBand * (3.0 - 2.0 * rimBand);
		baseColor = lerp(baseColor, rimColor, rimBand * 0.78);
		baseColor = lerp(baseColor, deepRim, rimBand * 0.28);

		// Crisp black country divider on the seam.
		float3 ink = float3(0.018, 0.012, 0.008);
		float blackCore = 1.0 - smoothstep(0.0, coreWidth, inside);
		float blackSoft = 1.0 - smoothstep(0.0, coreWidth * 2.4, inside);
		baseColor = lerp(baseColor, ink, blackSoft * 0.55);
		baseColor = lerp(baseColor, ink, blackCore * 0.92);
	}

	baseColor = ApplyLandBuildSelectionOverlay(baseColor, grid);

	if (grid.IsHighlighted())
	{
		// Black-gold limb like the globe silhouette: thin metal hairline,
		// soft black feather to the hex edge — no green wash.
		float edge = grid.distanceToCenter;
		float aa = max(grid.distanceSmoothing, 0.004);
		float strength = saturate(_HexSelectionColor.a);
		float pulse = 0.92 + 0.08 * sin(_Time.y * 2.4);

		float3 velvet = float3(0.012, 0.009, 0.006);
		float3 deepGold = float3(0.52, 0.34, 0.10);
		float3 metalGold = lerp(
			float3(0.90, 0.66, 0.20),
			saturate(_HexSelectionColor.rgb),
			0.35);

		// Soft black feather toward the silhouette.
		float blackMask = smoothstep(0.86 - aa, 0.955, edge) *
			(1.0 - smoothstep(0.98, 1.02 + aa, edge));
		blackMask = blackMask * blackMask * (3.0 - 2.0 * blackMask);

		// Thin bright gold crest just inside the rim (~globe hairline).
		float goldRise = smoothstep(0.955 - aa, 0.978, edge);
		float goldFall = 1.0 - smoothstep(0.978, 0.998 + aa, edge);
		float goldMask = goldRise * goldFall;
		goldMask = goldMask * goldMask * (3.0 - 2.0 * goldMask);

		float3 gradedGold = lerp(deepGold, metalGold, saturate(goldMask * 1.35));
		baseColor = lerp(baseColor, velvet, blackMask * 0.88 * strength);
		baseColor = lerp(
			baseColor, gradedGold, goldMask * 0.78 * strength * pulse);
		baseColor += gradedGold * goldMask * 0.42 * strength * pulse;
	}

	return saturate(baseColor);
}

// Keep the editor overlay analytic in world-space XZ. Runtime gameplay uses
// the same path, with a separately controlled grid visibility value.
float3 ApplyHFEditorOverlay(float3 baseColor, HexGridData grid)
{
	baseColor = ApplyHexMapSurfaceOverlay(
		baseColor,
		grid,
		max(_HexEditorShowGrid, _HexMapShowGrid),
		1.0);
	// Mid-near / near land is HF Relief — occupation must live here, not only
	// on the Catlike Terrain graph (which clips away in HF Original mode).
	return ApplyOccupationStripes(baseColor, grid);
}
