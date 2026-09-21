using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Conservative XZ projection of a camera frustum clipped to a terrain-height
/// slab. Storage is reused; row-strip clipping avoids a far-plane AABB scan.
/// </summary>
public sealed class HexStreamingFootprint
{
	static readonly int[] Edges = {
		0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4,
		0, 4, 1, 5, 2, 6, 3, 7
	};
	static readonly IComparer<Vector2> PointOrder = Comparer<Vector2>.Create(
		(a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
	readonly Vector2[] points = new Vector2[32];
	readonly Vector2[] hull = new Vector2[64];
	int pointCount, hullCount;
	float minimumZ, maximumZ;

	/// <param name="corners">Local frustum corners: near BL, BR, TR, TL,
	/// followed by far BL, BR, TR, TL. Include actual near/far clipping planes.</param>
	public bool Build(Vector3[] corners, float minimumHeight, float maximumHeight)
	{
		pointCount = hullCount = 0;
		if (corners == null || corners.Length < 8 ||
			!Finite(minimumHeight) || !Finite(maximumHeight) ||
			minimumHeight > maximumHeight) return false;
		for (int i = 0; i < 8; i++)
		{
			Vector3 p = corners[i];
			if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) return false;
			if (p.y >= minimumHeight && p.y <= maximumHeight) AddPoint(p);
		}
		for (int i = 0; i < Edges.Length; i += 2)
		{
			Vector3 a = corners[Edges[i]], b = corners[Edges[i + 1]];
			AddHeightIntersection(a, b, minimumHeight);
			AddHeightIntersection(a, b, maximumHeight);
		}
		if (pointCount == 0) return false;
		Array.Sort(points, 0, pointCount, PointOrder);
		int uniqueCount = 0;
		for (int i = 0; i < pointCount; i++)
		{
			if (uniqueCount == 0 || points[i].x != points[uniqueCount - 1].x ||
				points[i].y != points[uniqueCount - 1].y)
				points[uniqueCount++] = points[i];
		}
		for (int i = 0; i < uniqueCount; i++)
		{
			while (hullCount >= 2 && Cross(hull[hullCount - 2], hull[hullCount - 1], points[i]) <= 0d) hullCount--;
			hull[hullCount++] = points[i];
		}
		int lowerCount = hullCount;
		for (int i = uniqueCount - 2; i >= 0; i--)
		{
			while (hullCount > lowerCount && Cross(hull[hullCount - 2], hull[hullCount - 1], points[i]) <= 0d) hullCount--;
			hull[hullCount++] = points[i];
		}
		if (hullCount > 1) hullCount--;
		minimumZ = maximumZ = hull[0].y;
		for (int i = 1; i < hullCount; i++)
		{
			minimumZ = Mathf.Min(minimumZ, hull[i].y);
			maximumZ = Mathf.Max(maximumZ, hull[i].y);
		}
		return true;
	}

	/// <summary>
	/// Add every chunk whose nominal rectangle plus geometry/motion allowance
	/// intersects the convex footprint. A wrapped row visits each logical column
	/// at most once; the finite map extent bounds both row and column work.
	/// </summary>
	public void AddChunks(int cellCountX, int cellCountZ, int chunkSizeX,
		int chunkSizeZ, float cellWidth, float rowHeight, bool wrapping,
		float paddingX, float paddingZ, HashSet<int> destination)
	{
		if (hullCount == 0 || cellCountX <= 0 || cellCountZ <= 0 ||
			chunkSizeX <= 0 || chunkSizeZ <= 0 || cellWidth <= 0f || rowHeight <= 0f) return;
		int columns = (cellCountX + chunkSizeX - 1) / chunkSizeX;
		int rows = (cellCountZ + chunkSizeZ - 1) / chunkSizeZ;
		float width = cellWidth * chunkSizeX, height = rowHeight * chunkSizeZ;
		float mapWidth = cellCountX * cellWidth;
		// Include touching boundaries and absorb float roundoff at large map XZ.
		paddingX = Mathf.Max(0f, paddingX) + .01f;
		paddingZ = Mathf.Max(0f, paddingZ) + .01f;
		int firstRow = Mathf.Clamp(Mathf.FloorToInt((minimumZ - paddingZ) / height), 0, rows - 1);
		int lastRow = Mathf.Clamp(Mathf.FloorToInt((maximumZ + paddingZ) / height), 0, rows - 1);
		for (int z = firstRow; z <= lastRow; z++)
		{
			float lowZ = z * height - paddingZ;
			float highZ = Mathf.Min((z + 1) * height, cellCountZ * rowHeight) + paddingZ;
			if (!TryGetRowRange(lowZ, highZ, out float lowX, out float highX)) continue;
			lowX -= paddingX; highX += paddingX;
			if (!wrapping)
			{
				AddColumnRange(lowX, highX, mapWidth, width, columns, z, destination);
			}
			else if ((double)highX - lowX >= mapWidth)
			{
				for (int x = 0; x < columns; x++) destination.Add(x + z * columns);
			}
			else
			{
				// Normalize against the actual map width, rather than a rounded-up
				// chunk width. Partial final chunks therefore wrap without a gap.
				double copy = Math.Floor((double)lowX / mapWidth);
				float normalizedLow = (float)(lowX - copy * mapWidth);
				float normalizedHigh = normalizedLow + (highX - lowX);
				AddColumnRange(normalizedLow, normalizedHigh, mapWidth, width, columns, z, destination);
				if (normalizedHigh >= mapWidth)
					AddColumnRange(0f, normalizedHigh - mapWidth, mapWidth, width, columns, z, destination);
			}
		}
	}

	static void AddColumnRange(float low, float high, float mapWidth,
		float chunkWidth, int columns, int z, HashSet<int> destination)
	{
		if (high < 0f || low > mapWidth) return;
		int first = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, low) / chunkWidth), 0, columns - 1);
		int last = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(mapWidth, high) / chunkWidth), 0, columns - 1);
		for (int x = first; x <= last; x++) destination.Add(x + z * columns);
	}

	bool TryGetRowRange(float lowZ, float highZ, out float lowX, out float highX)
	{
		lowX = float.PositiveInfinity; highX = float.NegativeInfinity;
		for (int i = 0; i < hullCount; i++)
		{
			Vector2 a = hull[i], b = hull[(i + 1) % hullCount];
			if (a.y >= lowZ && a.y <= highZ) IncludeX(a.x, ref lowX, ref highX);
			IncludeRowIntersection(a, b, lowZ, ref lowX, ref highX);
			IncludeRowIntersection(a, b, highZ, ref lowX, ref highX);
		}
		return lowX <= highX;
	}

	static void IncludeRowIntersection(Vector2 a, Vector2 b, float z,
		ref float minimum, ref float maximum)
	{
		if ((a.y < z && b.y > z) || (a.y > z && b.y < z))
		{
			double t = ((double)z - a.y) / ((double)b.y - a.y);
			IncludeX((float)(a.x + ((double)b.x - a.x) * t), ref minimum, ref maximum);
		}
	}
	static void IncludeX(float x, ref float minimum, ref float maximum)
	{
		minimum = Mathf.Min(minimum, x); maximum = Mathf.Max(maximum, x);
	}
	void AddHeightIntersection(Vector3 a, Vector3 b, float height)
	{
		if ((a.y < height && b.y > height) || (a.y > height && b.y < height))
		{
			double t = ((double)height - a.y) / ((double)b.y - a.y);
			AddPoint(new Vector3((float)(a.x + ((double)b.x - a.x) * t), height,
				(float)(a.z + ((double)b.z - a.z) * t)));
		}
	}
	void AddPoint(Vector3 p) => points[pointCount++] = new Vector2(p.x, p.z);
	static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
		((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);
	static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
