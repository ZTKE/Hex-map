#ifndef HEX_RELIEF_TESSELLATION_INCLUDED
#define HEX_RELIEF_TESSELLATION_INCLUDED

// Hull-stage metadata only: retain distant mountain silhouettes without sampling
// authored height arrays or changing the shared terrain surface implementation.
float HFNearReliefSourceMinimum(float2 requestedOffset)
{
	float2 offset;
	if (!HFResolveOffset(requestedOffset, offset)) return 1.0;
	float4 encoded = HFSampleShapeTexel(offset);
	uint waterFlags = (uint)round(encoded.b * 255.0);
	if ((waterFlags & HF_SHAPE_UNDERWATER_BIT) != 0u) return 1.0;
	uint landform = (uint)round(encoded.r * 255.0) >> 6u;
	return landform == 2u ? 6.0 : (landform == 1u ? 4.0 : 1.0);
}

float HFNearReliefCellMinimum(float2 requestedRoot)
{
	float2 root;
	if (!HFResolveOffset(requestedRoot, root)) return 1.0;
	float result = HFNearReliefSourceMinimum(root);
	[branch] if (result >= 6.0) return result;

	// The same thirteen possible mountain sources cover the complete hexagon:
	// every omitted source is at least 2.598 radii away, beyond the 2.4 support.
	// Hill support is only 1.45, so the six two-ring corner sources need no hill floor.
	[loop] for (int direction = 0; direction < 6; direction++)
	{
		float2 first = HFNeighborOffset(root, direction);
		result = max(result, HFNearReliefSourceMinimum(first));
		[branch] if (result >= 6.0) return result;
		float cornerMinimum = HFNearReliefSourceMinimum(
			HFNeighborOffset(first, (direction + 1) % 6));
		[branch] if (cornerMinimum >= 6.0) return cornerMinimum;
	}
	return result;
}

float2 HFNearReliefPatchMinimum(float2 rootOffset, int outerDirection)
{
	[branch] if (_HexNearTerrainEnabled <= 0.5) return 1.0;
	float rootMinimum = HFNearReliefCellMinimum(rootOffset);
	[branch] if (rootMinimum >= 6.0) return float2(rootMinimum, rootMinimum);
	float neighborMinimum = HFNearReliefCellMinimum(
		HFNeighborOffset(rootOffset, outerDirection));
	// Radial edges share one logical root. Outer edges share an unordered pair
	// of resolved roots, including east/west wrapping, so both owners agree.
	return float2(rootMinimum, max(rootMinimum, neighborMinimum));
}

#endif
