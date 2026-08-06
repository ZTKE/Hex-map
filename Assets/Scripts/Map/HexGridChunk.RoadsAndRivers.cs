using UnityEngine;

/// <summary>
/// Road and river overlay topology shared by HF Original and legacy mode.
/// In HF mode these triangles are subsequently subdivided and conformed to
/// the shared shader-equivalent surface sampler.
/// </summary>
public partial class HexGridChunk
{
	void TriangulateWithoutRiver(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		Vector3 center,
		EdgeVertices e)
	{
		TriangulateEdgeFan(center, e, cellIndex);

		if (cell.HasRoads)
		{
			Vector2 interpolators = GetRoadInterpolators(direction, cell);
			TriangulateRoad(
				center,
				Vector3.Lerp(center, e.v1, interpolators.x),
				Vector3.Lerp(center, e.v5, interpolators.y),
				e, cell.HasRoadThroughEdge(direction), cellIndex);
		}
	}

	Vector2 GetRoadInterpolators(HexDirection direction, HexCellData cell)
	{
		Vector2 interpolators;
		if (cell.HasRoadThroughEdge(direction))
		{
			interpolators.x = interpolators.y = 0.5f;
		}
		else
		{
			interpolators.x =
				cell.HasRoadThroughEdge(direction.Previous()) ? 0.5f : 0.25f;
			interpolators.y =
				cell.HasRoadThroughEdge(direction.Next()) ? 0.5f : 0.25f;
		}
		return interpolators;
	}

	void TriangulateAdjacentToRiver(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		Vector3 center,
		EdgeVertices e)
	{
		if (cell.HasRoads)
		{
			TriangulateRoadAdjacentToRiver(
				direction, cell, cellIndex, center, e);
		}

		if (cell.HasRiverThroughEdge(direction.Next()))
		{
			if (cell.HasRiverThroughEdge(direction.Previous()))
			{
				center += HexMetrics.GetSolidEdgeMiddle(direction) *
					(HexMetrics.innerToOuter * 0.5f);
			}
			else if (cell.HasRiverThroughEdge(direction.Previous2()))
			{
				center += HexMetrics.GetFirstSolidCorner(direction) * 0.25f;
			}
		}
		else if (cell.HasRiverThroughEdge(direction.Previous()) &&
			cell.HasRiverThroughEdge(direction.Next2()))
		{
			center += HexMetrics.GetSecondSolidCorner(direction) * 0.25f;
		}

		var m = new EdgeVertices(
			Vector3.Lerp(center, e.v1, 0.5f),
			Vector3.Lerp(center, e.v5, 0.5f));

		TriangulateEdgeStrip(
			m, weights1, cellIndex,
			e, weights1, cellIndex);
		TriangulateEdgeFan(center, m, cellIndex);

		if (!cell.IsUnderwater &&
			cell.landform == HexLandform.Flat &&
			!cell.HasRoadThroughEdge(direction))
		{
			features.AddFeature(
				cell, (center + e.v1 + e.v5) * (1f / 3f));
		}
	}

	void TriangulateRoadAdjacentToRiver(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		Vector3 center,
		EdgeVertices e)
	{
		bool hasRoadThroughEdge = cell.HasRoadThroughEdge(direction);
		bool previousHasRiver = cell.HasRiverThroughEdge(direction.Previous());
		bool nextHasRiver = cell.HasRiverThroughEdge(direction.Next());
		Vector2 interpolators = GetRoadInterpolators(direction, cell);
		Vector3 roadCenter = center;

		HexDirection riverIn = cell.IncomingRiver;
		HexDirection riverOut = cell.OutgoingRiver;

		if (cell.HasRiverBeginOrEnd)
		{
			roadCenter += HexMetrics.GetSolidEdgeMiddle(
				(cell.HasIncomingRiver ? riverIn : riverOut).Opposite()
			) * (1f / 3f);
		}
		else if (riverIn == riverOut.Opposite())
		{
			Vector3 corner;
			if (previousHasRiver)
			{
				if (!hasRoadThroughEdge &&
					!cell.HasRoadThroughEdge(direction.Next()))
				{
					return;
				}
				corner = HexMetrics.GetSecondSolidCorner(direction);
			}
			else
			{
				if (!hasRoadThroughEdge &&
					!cell.HasRoadThroughEdge(direction.Previous()))
				{
					return;
				}
				corner = HexMetrics.GetFirstSolidCorner(direction);
			}
			roadCenter += corner * 0.5f;
			if (riverIn == direction.Next() && (
				cell.HasRoadThroughEdge(direction.Next2()) ||
				cell.HasRoadThroughEdge(direction.Opposite())))
			{
				features.AddBridge(roadCenter, center - corner * 0.5f);
			}
			center += corner * 0.25f;
		}
		else if (riverIn == riverOut.Previous())
		{
			roadCenter -= HexMetrics.GetSecondCorner(riverIn) * 0.2f;
		}
		else if (riverIn == riverOut.Next())
		{
			roadCenter -= HexMetrics.GetFirstCorner(riverIn) * 0.2f;
		}
		else if (previousHasRiver && nextHasRiver)
		{
			if (!hasRoadThroughEdge)
			{
				return;
			}
			Vector3 offset =
				HexMetrics.GetSolidEdgeMiddle(direction) *
				HexMetrics.innerToOuter;
			roadCenter += offset * 0.7f;
			center += offset * 0.5f;
		}
		else
		{
			HexDirection middle;
			if (previousHasRiver)
			{
				middle = direction.Next();
			}
			else if (nextHasRiver)
			{
				middle = direction.Previous();
			}
			else
			{
				middle = direction;
			}
			if (!cell.HasRoadThroughEdge(middle) &&
				!cell.HasRoadThroughEdge(middle.Previous()) &&
				!cell.HasRoadThroughEdge(middle.Next()))
			{
				return;
			}
			Vector3 offset = HexMetrics.GetSolidEdgeMiddle(middle);
			roadCenter += offset * 0.25f;
			if (direction == middle &&
				cell.HasRoadThroughEdge(direction.Opposite()))
			{
				features.AddBridge(
					roadCenter,
					center - offset * (HexMetrics.innerToOuter * 0.7f));
			}
		}

		Vector3 mL = Vector3.Lerp(roadCenter, e.v1, interpolators.x);
		Vector3 mR = Vector3.Lerp(roadCenter, e.v5, interpolators.y);
		TriangulateRoad(roadCenter, mL, mR, e, hasRoadThroughEdge, cellIndex);
		if (previousHasRiver)
		{
			TriangulateRoadEdge(roadCenter, center, mL, cellIndex);
		}
		if (nextHasRiver)
		{
			TriangulateRoadEdge(roadCenter, mR, center, cellIndex);
		}
	}

	void TriangulateWithRiverBeginOrEnd(
		HexCellData cell, int cellIndex, Vector3 center, EdgeVertices e)
	{
		var m = new EdgeVertices(
			Vector3.Lerp(center, e.v1, 0.5f),
			Vector3.Lerp(center, e.v5, 0.5f));
		m.v3.y = e.v3.y;

		TriangulateEdgeStrip(
			m, weights1, cellIndex,
			e, weights1, cellIndex);
		TriangulateEdgeFan(center, m, cellIndex);

		if (!cell.IsUnderwater)
		{
			bool reversed = cell.HasIncomingRiver;
			Vector3 indices;
			indices.x = indices.y = indices.z = cellIndex;
			TriangulateRiverQuad(
				m.v2, m.v4, e.v2, e.v4,
				cell.RiverSurfaceY, 0.6f, reversed, indices);
			center.y = m.v2.y = m.v4.y = cell.RiverSurfaceY;
			rivers.AddTriangle(center, m.v2, m.v4);
			if (reversed)
			{
				rivers.AddTriangleUV(
					new Vector2(0.5f, 0.4f),
					new Vector2(1f, 0.2f), new Vector2(0f, 0.2f));
			}
			else
			{
				rivers.AddTriangleUV(
					new Vector2(0.5f, 0.4f),
					new Vector2(0f, 0.6f), new Vector2(1f, 0.6f));
			}
			rivers.AddTriangleCellData(indices, weights1);
		}
	}

	void TriangulateWithRiver(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		Vector3 center,
		EdgeVertices e)
	{
		Vector3 centerL, centerR;
		if (cell.HasRiverThroughEdge(direction.Opposite()))
		{
			centerL = center +
				HexMetrics.GetFirstSolidCorner(direction.Previous()) * 0.25f;
			centerR = center +
				HexMetrics.GetSecondSolidCorner(direction.Next()) * 0.25f;
		}
		else if (cell.HasRiverThroughEdge(direction.Next()))
		{
			centerL = center;
			centerR = Vector3.Lerp(center, e.v5, 2f / 3f);
		}
		else if (cell.HasRiverThroughEdge(direction.Previous()))
		{
			centerL = Vector3.Lerp(center, e.v1, 2f / 3f);
			centerR = center;
		}
		else if (cell.HasRiverThroughEdge(direction.Next2()))
		{
			centerL = center;
			centerR = center +
				HexMetrics.GetSolidEdgeMiddle(direction.Next()) *
				(0.5f * HexMetrics.innerToOuter);
		}
		else
		{
			centerL = center +
				HexMetrics.GetSolidEdgeMiddle(direction.Previous()) *
				(0.5f * HexMetrics.innerToOuter);
			centerR = center;
		}
		center = Vector3.Lerp(centerL, centerR, 0.5f);

		var m = new EdgeVertices(
			Vector3.Lerp(centerL, e.v1, 0.5f),
			Vector3.Lerp(centerR, e.v5, 0.5f),
			1f / 6f);
		m.v3.y = center.y = e.v3.y;

		TriangulateEdgeStrip(
			m, weights1, cellIndex,
			e, weights1, cellIndex);

		terrain.AddTriangle(centerL, m.v1, m.v2);
		terrain.AddQuad(centerL, center, m.v2, m.v3);
		terrain.AddQuad(center, centerR, m.v3, m.v4);
		terrain.AddTriangle(centerR, m.v4, m.v5);

		Vector3 indices;
		indices.x = indices.y = indices.z = cellIndex;
		terrain.AddTriangleCellData(indices, weights1);
		terrain.AddQuadCellData(indices, weights1);
		terrain.AddQuadCellData(indices, weights1);
		terrain.AddTriangleCellData(indices, weights1);

		if (!cell.IsUnderwater)
		{
			bool reversed = cell.HasIncomingRiverThroughEdge(direction);
			TriangulateRiverQuad(
				centerL, centerR, m.v2, m.v4,
				cell.RiverSurfaceY, 0.4f, reversed, indices);
			TriangulateRiverQuad(
				m.v2, m.v4, e.v2, e.v4,
				cell.RiverSurfaceY, 0.6f, reversed, indices);
		}
	}

	void TriangulateRiverQuad(
		Vector3 v1, Vector3 v2, Vector3 v3, Vector3 v4,
		float y, float v, bool reversed, Vector3 indices) =>
		TriangulateRiverQuad(v1, v2, v3, v4, y, y, v, reversed, indices);

	void TriangulateRiverQuad(
		Vector3 v1, Vector3 v2, Vector3 v3, Vector3 v4,
		float y1, float y2, float v, bool reversed, Vector3 indices)
	{
		v1.y = v2.y = y1;
		v3.y = v4.y = y2;
		rivers.AddQuad(v1, v2, v3, v4);
		if (reversed)
		{
			rivers.AddQuadUV(1f, 0f, 0.8f - v, 0.6f - v);
		}
		else
		{
			rivers.AddQuadUV(0f, 1f, v, v + 0.2f);
		}
		rivers.AddQuadCellData(indices, weights1, weights2);
	}

	void TriangulateRoad(
		Vector3 center, Vector3 mL, Vector3 mR,
		EdgeVertices e, bool hasRoadThroughCellEdge, float index)
	{
		if (hasRoadThroughCellEdge)
		{
			Vector3 indices;
			indices.x = indices.y = indices.z = index;
			Vector3 mC = Vector3.Lerp(mL, mR, 0.5f);
			TriangulateRoadSegment(
				mL, mC, mR, e.v2, e.v3, e.v4,
				weights1, weights1, indices);
			roads.AddTriangle(center, mL, mC);
			roads.AddTriangle(center, mC, mR);
			roads.AddTriangleUV(
				new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(1f, 0f));
			roads.AddTriangleUV(
				new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f));
			roads.AddTriangleCellData(indices, weights1);
			roads.AddTriangleCellData(indices, weights1);
		}
		else
		{
			TriangulateRoadEdge(center, mL, mR, index);
		}
	}

	void TriangulateRoadEdge(
		Vector3 center, Vector3 mL, Vector3 mR, float index)
	{
		roads.AddTriangle(center, mL, mR);
		roads.AddTriangleUV(
			new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f));
		Vector3 indices;
		indices.x = indices.y = indices.z = index;
		roads.AddTriangleCellData(indices, weights1);
	}

	void TriangulateRoadSegment(
		Vector3 v1, Vector3 v2, Vector3 v3,
		Vector3 v4, Vector3 v5, Vector3 v6,
		Color w1, Color w2, Vector3 indices)
	{
		roads.AddQuad(v1, v2, v4, v5);
		roads.AddQuad(v2, v3, v5, v6);
		roads.AddQuadUV(0f, 1f, 0f, 0f);
		roads.AddQuadUV(1f, 0f, 0f, 0f);
		roads.AddQuadCellData(indices, w1, w2);
		roads.AddQuadCellData(indices, w1, w2);
	}
}
