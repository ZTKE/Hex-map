using UnityEngine;

/// <summary>
/// Water and shoreline triangulation for both surface modes.
/// HF Original uses the continuous ocean plane; the Catlike shoreline and
/// estuary routines remain here as an explicit legacy compatibility path.
/// </summary>
public partial class HexGridChunk
{
	bool IsWithinHFOceanStampReach(HexCellData cell)
	{
		if (cell.IsUnderwater)
		{
			return true;
		}

		// HF's 1.6-scale square stamps can rotate far enough to affect the
		// diagonal half of hex ring two. Stopping the water mesh after only one
		// dry ring exposes its straight hex boundary before the reconstructed
		// height mask reaches the real shoreline.
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (Grid.TryGetCellIndex(
				cell.coordinates.Step(d), out int neighborIndex))
			{
				HexCellData neighbor = Grid.CellData[neighborIndex];
				if (neighbor.IsUnderwater)
				{
					return true;
				}
				for (HexDirection second = HexDirection.NE;
					second <= HexDirection.NW; second++)
				{
					if (Grid.TryGetCellIndex(
						neighbor.coordinates.Step(second), out int secondIndex) &&
						Grid.CellData[secondIndex].IsUnderwater)
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	/// <summary>
	/// HF intersects one continuous horizontal water plane with its baked
	/// terrain height field. Tile the sea and the complete dry coast reach with
	/// full hexes; the opaque HF relief surface naturally hides every part that is
	/// still above water, leaving the Water_h shoreline instead of a straight
	/// Catlike edge strip.
	/// </summary>
	void TriangulateHFOceanCell(int cellIndex, Vector3 center)
	{
		// Original HF uses a single datum for the terrain mesh and water plane.
		// The height texture's 0.5 contour, rather than Catlike's -0.5 water
		// offset, owns the shoreline.
		center.y = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		Vector3 indices = new(cellIndex, cellIndex, cellIndex);
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			water.AddTriangle(
				center,
				center + HexMetrics.GetFirstCorner(d),
				center + HexMetrics.GetSecondCorner(d));
			water.AddTriangleCellData(indices, weights1);
		}
	}

	void TriangulateWater(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		Vector3 center)
	{
		center.y = cell.WaterSurfaceY;
		HexCoordinates neighborCoordinates = cell.coordinates.Step(direction);
		if (Grid.TryGetCellIndex(neighborCoordinates, out int neighborIndex) &&
			!Grid.CellData[neighborIndex].IsUnderwater)
		{
			TriangulateWaterShore(
				direction, cell, cellIndex, neighborIndex,
				neighborCoordinates.ColumnIndex, center);
		}
		else
		{
			TriangulateOpenWater(
				cell.coordinates, direction, cellIndex, neighborIndex, center);
		}
	}

	void TriangulateOpenWater(
		HexCoordinates coordinates,
		HexDirection direction,
		int cellIndex,
		int neighborIndex,
		Vector3 center)
	{
		Vector3 c1 = center + HexMetrics.GetFirstWaterCorner(direction);
		Vector3 c2 = center + HexMetrics.GetSecondWaterCorner(direction);

		water.AddTriangle(center, c1, c2);
		Vector3 indices;
		indices.x = indices.y = indices.z = cellIndex;
		water.AddTriangleCellData(indices, weights1);

		if (direction <= HexDirection.SE && neighborIndex != -1)
		{
			Vector3 bridge = HexMetrics.GetWaterBridge(direction);
			Vector3 e1 = c1 + bridge;
			Vector3 e2 = c2 + bridge;

			water.AddQuad(c1, c2, e1, e2);
			indices.y = neighborIndex;
			water.AddQuadCellData(indices, weights1, weights2);

			if (direction <= HexDirection.E)
			{
				if (!Grid.TryGetCellIndex(
					coordinates.Step(direction.Next()),
					out int nextNeighborIndex) ||
					!Grid.CellData[nextNeighborIndex].IsUnderwater)
				{
					return;
				}
				water.AddTriangle(
					c2, e2, c2 + HexMetrics.GetWaterBridge(direction.Next()));
				indices.z = nextNeighborIndex;
				water.AddTriangleCellData(
					indices, weights1, weights2, weights3);
			}
		}
	}

	void TriangulateWaterShore(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		int neighborIndex,
		int neighborColumnIndex,
		Vector3 center)
	{
		var e1 = new EdgeVertices(
			center + HexMetrics.GetFirstWaterCorner(direction),
			center + HexMetrics.GetSecondWaterCorner(direction));
		water.AddTriangle(center, e1.v1, e1.v2);
		water.AddTriangle(center, e1.v2, e1.v3);
		water.AddTriangle(center, e1.v3, e1.v4);
		water.AddTriangle(center, e1.v4, e1.v5);
		Vector3 indices;
		indices.x = indices.z = cellIndex;
		indices.y = neighborIndex;
		water.AddTriangleCellData(indices, weights1);
		water.AddTriangleCellData(indices, weights1);
		water.AddTriangleCellData(indices, weights1);
		water.AddTriangleCellData(indices, weights1);

		Vector3 center2 = Grid.CellPositions[neighborIndex];
		float landSurfaceY = center2.y;
		int cellColumnIndex = cell.coordinates.ColumnIndex;
		if (neighborColumnIndex < cellColumnIndex - 1)
		{
			center2.x += HexMetrics.wrapSize * HexMetrics.innerDiameter;
		}
		else if (neighborColumnIndex > cellColumnIndex + 1)
		{
			center2.x -= HexMetrics.wrapSize * HexMetrics.innerDiameter;
		}
		center2.y = center.y;
		var e2 = new EdgeVertices(
			center2 + HexMetrics.GetSecondSolidCorner(direction.Opposite()),
			center2 + HexMetrics.GetFirstSolidCorner(direction.Opposite()));

		if (!useHFOriginalSurface &&
			cell.HasLegacyRiverThroughEdge(direction))
		{
			TriangulateEstuary(
				e1, e2, cell.HasIncomingRiverThroughEdge(direction), indices);
		}
		else
		{
			HexCellData landCell = Grid.CellData[neighborIndex];
			coastCliffs.AddEdge(
				e2,
				landSurfaceY,
				neighborIndex,
				landCell.TerrainTypeIndex,
				landCell.landform,
				HexMetrics.SampleHashGrid(center2));
			waterShore.AddQuad(e1.v1, e1.v2, e2.v1, e2.v2);
			waterShore.AddQuad(e1.v2, e1.v3, e2.v2, e2.v3);
			waterShore.AddQuad(e1.v3, e1.v4, e2.v3, e2.v4);
			waterShore.AddQuad(e1.v4, e1.v5, e2.v4, e2.v5);
			waterShore.AddQuadUV(0f, 0f, 0f, 1f);
			waterShore.AddQuadUV(0f, 0f, 0f, 1f);
			waterShore.AddQuadUV(0f, 0f, 0f, 1f);
			waterShore.AddQuadUV(0f, 0f, 0f, 1f);
			waterShore.AddQuadCellData(indices, weights1, weights2);
			waterShore.AddQuadCellData(indices, weights1, weights2);
			waterShore.AddQuadCellData(indices, weights1, weights2);
			waterShore.AddQuadCellData(indices, weights1, weights2);
		}

		HexCoordinates nextNeighborCoordinates = cell.coordinates.Step(
			direction.Next());
		if (Grid.TryGetCellIndex(
			nextNeighborCoordinates, out int nextNeighborIndex))
		{
			Vector3 center3 = Grid.CellPositions[nextNeighborIndex];
			bool nextNeighborIsUnderwater =
				Grid.CellData[nextNeighborIndex].IsUnderwater;
			int nextNeighborColumnIndex = nextNeighborCoordinates.ColumnIndex;
			if (nextNeighborColumnIndex < cellColumnIndex - 1)
			{
				center3.x += HexMetrics.wrapSize * HexMetrics.innerDiameter;
			}
			else if (nextNeighborColumnIndex > cellColumnIndex + 1)
			{
				center3.x -= HexMetrics.wrapSize * HexMetrics.innerDiameter;
			}
			Vector3 v3 = center3 + (nextNeighborIsUnderwater ?
				HexMetrics.GetFirstWaterCorner(direction.Previous()) :
				HexMetrics.GetFirstSolidCorner(direction.Previous()));
			v3.y = center.y;
			waterShore.AddTriangle(e1.v5, e2.v5, v3);
			waterShore.AddTriangleUV(
				new Vector2(0f, 0f),
				new Vector2(0f, 1f),
				new Vector2(0f, nextNeighborIsUnderwater ? 0f : 1f));
			indices.z = nextNeighborIndex;
			waterShore.AddTriangleCellData(
				indices, weights1, weights2, weights3);
		}
	}

	void TriangulateEstuary(
		EdgeVertices e1, EdgeVertices e2, bool incomingRiver, Vector3 indices)
	{
		// Legacy Catlike shoreline caps. HF never calls this method; its river
		// carve meets the continuous sea plane without a separate estuary fan.
		waterShore.AddTriangle(e2.v1, e1.v2, e1.v1);
		waterShore.AddTriangle(e2.v5, e1.v5, e1.v4);
		waterShore.AddTriangleUV(
			new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(0f, 0f));
		waterShore.AddTriangleUV(
			new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(0f, 0f));
		waterShore.AddTriangleCellData(indices, weights2, weights1, weights1);
		waterShore.AddTriangleCellData(indices, weights2, weights1, weights1);

		estuaries.AddQuad(e2.v1, e1.v2, e2.v2, e1.v3);
		estuaries.AddTriangle(e1.v3, e2.v2, e2.v4);
		estuaries.AddQuad(e1.v3, e1.v4, e2.v4, e2.v5);

		estuaries.AddQuadUV(
			new Vector2(0f, 1f), new Vector2(0f, 0f),
			new Vector2(1f, 1f), new Vector2(0f, 0f));
		estuaries.AddTriangleUV(
			new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(1f, 1f));
		estuaries.AddQuadUV(
			new Vector2(0f, 0f), new Vector2(0f, 0f),
			new Vector2(1f, 1f), new Vector2(0f, 1f));
		estuaries.AddQuadCellData(
			indices, weights2, weights1, weights2, weights1);
		estuaries.AddTriangleCellData(indices, weights1, weights2, weights2);
		estuaries.AddQuadCellData(indices, weights1, weights2);

		if (incomingRiver)
		{
			estuaries.AddQuadUV2(
				new Vector2(1.5f, 1f), new Vector2(0.7f, 1.15f),
				new Vector2(1f, 0.8f), new Vector2(0.5f, 1.1f));
			estuaries.AddTriangleUV2(
				new Vector2(0.5f, 1.1f),
				new Vector2(1f, 0.8f),
				new Vector2(0f, 0.8f));
			estuaries.AddQuadUV2(
				new Vector2(0.5f, 1.1f), new Vector2(0.3f, 1.15f),
				new Vector2(0f, 0.8f), new Vector2(-0.5f, 1f));
		}
		else
		{
			estuaries.AddQuadUV2(
				new Vector2(-0.5f, -0.2f), new Vector2(0.3f, -0.35f),
				new Vector2(0f, 0f), new Vector2(0.5f, -0.3f));
			estuaries.AddTriangleUV2(
				new Vector2(0.5f, -0.3f),
				new Vector2(0f, 0f),
				new Vector2(1f, 0f));
			estuaries.AddQuadUV2(
				new Vector2(0.5f, -0.3f), new Vector2(0.7f, -0.35f),
				new Vector2(1f, 0f), new Vector2(1.5f, -0.2f));
		}
	}

}
