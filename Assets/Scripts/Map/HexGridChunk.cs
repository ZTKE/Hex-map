using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Component that manages a single chunk of <see cref="HexGrid"/>.
/// </summary>
public partial class HexGridChunk : MonoBehaviour
{
	readonly static Color weights1 = new(1f, 0f, 0f);
	readonly static Color weights2 = new(0f, 1f, 0f);
	readonly static Color weights3 = new(0f, 0f, 1f);

	HexGrid grid;

	public HexGrid Grid
	{
		get => grid;
		set
		{
			grid = value;
			if (!grid)
			{
				return;
			}
			HexTerrainStyle activeStyle = terrainStyle ?
				terrainStyle : HexTerrainStyle.RuntimeDefault;
			grid.ConfigureSurface(activeStyle);
			features.Grid = grid;
		}
	}

	[SerializeField]
	HexMesh terrain, rivers, roads, water, waterShore, estuaries;

	[SerializeField]
	HexFeatureManager features;

	[SerializeField]
	Material reliefMaterial;

	[SerializeField]
	Material coastCliffMaterial;

	[SerializeField]
	HexTerrainStyle terrainStyle;

	HexReliefMesh relief;
	HexCoastMesh coastCliffs;
	HexSurfaceCollider surfaceCollider;
	bool useHFOriginalSurface;
	bool interactionEnabled = true;
	bool globalOceanMode;

	public bool InteractionEnabled => interactionEnabled;

	public bool GlobalOceanMode => globalOceanMode;

	int[] cellIndices;
	RectTransform[] cellUIs;
	readonly List<int> validCellIndices = new(
		HexMetrics.chunkSizeX * HexMetrics.chunkSizeZ);

	Canvas gridCanvas;

	void Awake()
	{
		HexTerrainStyle activeStyle = terrainStyle ?
			terrainStyle : HexTerrainStyle.RuntimeDefault;
		useHFOriginalSurface = activeStyle.UsesHFOriginalSurface;
		gridCanvas = GetComponentInChildren<Canvas>();
		cellIndices = new int[HexMetrics.chunkSizeX * HexMetrics.chunkSizeZ];
		cellUIs = new RectTransform[cellIndices.Length];
		for (int i = 0; i < cellIndices.Length; i++)
		{
			cellIndices[i] = -1;
		}
		GameObject reliefObject = new("Relief");
		reliefObject.transform.SetParent(transform, false);
		reliefObject.layer = terrain.gameObject.layer;
		relief = reliefObject.AddComponent<HexReliefMesh>();
		relief.Initialize(reliefMaterial, terrainStyle);

		GameObject coastObject = new("Coast Cliffs");
		coastObject.transform.SetParent(transform, false);
		coastObject.layer = terrain.gameObject.layer;
		coastCliffs = coastObject.AddComponent<HexCoastMesh>();
		coastCliffs.Initialize(coastCliffMaterial, terrainStyle);

		GameObject colliderObject = new("HF Surface Collider");
		colliderObject.transform.SetParent(transform, false);
		colliderObject.layer = terrain.gameObject.layer;
		surfaceCollider = colliderObject.AddComponent<HexSurfaceCollider>();
	}

	/// <summary>
	/// Add a cell to the chunk.
	/// </summary>
	/// <param name="index">Index of the cell for the chunk.</param>
	/// <param name="cellIndex">Index of the cell to add.</param>
	/// <param name="cellLabelPrefab">Prefab used when the pooled chunk needs a
	/// label for this local slot for the first time.</param>
	/// <returns>The pooled UI transform, or null for an unused edge slot.</returns>
	public RectTransform AddCell(
		int index, int cellIndex, Text cellLabelPrefab)
	{
		cellIndices[index] = cellIndex;
		if (cellIndex >= 0)
		{
			validCellIndices.Add(cellIndex);
		}
		RectTransform cellUI = cellUIs[index];
		if (!cellUI)
		{
			Text label = Instantiate(cellLabelPrefab);
			cellUI = cellUIs[index] = label.rectTransform;
			cellUI.SetParent(gridCanvas.transform, false);
		}
		cellUI.gameObject.SetActive(cellIndex >= 0);
		return cellIndex >= 0 ? cellUI : null;
	}

	/// <summary>
	/// Get the pooled label bound to a logical cell in this chunk.
	/// </summary>
	public RectTransform GetCellUI(int localIndex, int expectedCellIndex) =>
		cellIndices[localIndex] == expectedCellIndex ? cellUIs[localIndex] : null;

	/// <summary>
	/// Clear logical bindings before returning this renderer to the chunk pool.
	/// </summary>
	public void UnbindCells()
	{
		validCellIndices.Clear();
		for (int i = 0; i < cellIndices.Length; i++)
		{
			cellIndices[i] = -1;
			if (cellUIs[i])
			{
				cellUIs[i].gameObject.SetActive(false);
			}
		}
	}

	/// <summary>
	/// Refresh the chunk.
	/// </summary>
	public void Refresh() => enabled = true;

	/// <summary>
	/// Physics is only needed near the camera. Far visual chunks retain their
	/// meshes but skip the expensive HF MeshCollider cooking step.
	/// </summary>
	public void SetInteractionEnabled(bool value, bool refresh = true)
	{
		if (interactionEnabled == value)
		{
			return;
		}
		interactionEnabled = value;
		if (!value)
		{
			EnsureSurfaceCollider();
			surfaceCollider.Clear();
			terrain.SetColliderEnabled(false);
		}
		else if (refresh)
		{
			// Geometry is already current for an active streamed chunk. Rebuilding
			// every render mesh just to add physics caused a visible hitch whenever
			// the camera crossed a chunk boundary.
			HexTerrainStyle activeStyle = terrainStyle ?
				terrainStyle : HexTerrainStyle.RuntimeDefault;
			useHFOriginalSurface = activeStyle.UsesHFOriginalSurface;
			if (useHFOriginalSurface)
			{
				EnsureSurfaceCollider();
				surfaceCollider.Build(
					Grid, validCellIndices, activeStyle.hfColliderSubdivisions);
			}
			else
			{
				terrain.SetColliderEnabled(true);
			}
		}
	}

	/// <summary>
	/// Hide only the chunk-local water surfaces while the grid-wide ocean owns
	/// the sea. Terrain, relief, roads, features, and rivers remain detailed.
	/// </summary>
	public void SetGlobalOceanMode(bool value)
	{
		globalOceanMode = value;
		SetMeshRendererEnabled(water, !value);
		SetMeshRendererEnabled(waterShore, !value);
		SetMeshRendererEnabled(estuaries, !value);
	}

	static void SetMeshRendererEnabled(HexMesh mesh, bool value)
	{
		if (!mesh)
		{
			return;
		}
		MeshRenderer renderer = mesh.GetComponent<MeshRenderer>();
		if (renderer)
		{
			renderer.enabled = value;
		}
	}

	/// <summary>
	/// Control whether the map UI is visibile or hidden for the chunk.
	/// </summary>
	/// <param name="visible">Whether the UI should be visible.</param>
	public void ShowUI(bool visible) =>
		gridCanvas.gameObject.SetActive(visible);

	void LateUpdate()
	{
		Triangulate();
		enabled = false;
	}

	/// <summary>
	/// Triangulate everything in the chunk.
	/// </summary>
	public void Triangulate()
	{
		// Enter Play Mode Options can keep scene objects alive across script and
		// style reloads, so do not rely on the value captured once in Awake.
		HexTerrainStyle activeStyle = terrainStyle ?
			terrainStyle : HexTerrainStyle.RuntimeDefault;
		EnsureSurfaceCollider();
		useHFOriginalSurface = activeStyle.UsesHFOriginalSurface;
		Grid.ConfigureSurface(activeStyle);
		features.Grid = Grid;
		// Exact HF mode renders one displaced continuous surface. The Catlike
		// terrain renderer and collider are compatibility-only; letting either sit
		// underneath Relief creates a second map surface whenever HF dips below
		// the common datum.
		terrain.GetComponent<MeshRenderer>().enabled = !useHFOriginalSurface;
		terrain.Clear();
		rivers.Clear();
		roads.Clear();
		water.Clear();
		waterShore.Clear();
		estuaries.Clear();
		features.Clear();
		relief.Clear();
		coastCliffs.Clear();
		for (int i = 0; i < validCellIndices.Count; i++)
		{
			Triangulate(validCellIndices[i]);
		}
		if (useHFOriginalSurface)
		{
			// Catlike triangulation above is intentionally retained as the
			// compatibility/topology source for roads and walls. HF boundary rivers
			// discards its terrain vertices here so they cannot become a hidden
			// second map surface.
			terrain.SetColliderEnabled(false);
			terrain.Discard();
			roads.ConformToSurface(
				Grid, activeStyle.hfOverlaySubdivisionLevels,
				activeStyle.hfRoadSurfaceOffset);
			// HF Relief now cuts and colours the complete river analytically from
			// logical edge flags. Discard Catlike's ribbon so it cannot intersect the
			// tessellated channel or expose its straight triangle topology.
			rivers.Discard();
			if (interactionEnabled)
			{
				surfaceCollider.Build(
					Grid, validCellIndices, activeStyle.hfColliderSubdivisions);
			}
			else
			{
				surfaceCollider.Clear();
			}
		}
		else
		{
			// Explicit legacy compatibility branch. It is never selected merely
			// because an HF texture is missing.
			surfaceCollider.Clear();
			terrain.SetColliderEnabled(interactionEnabled);
			terrain.Apply();
		}
		if (!useHFOriginalSurface)
		{
			rivers.Apply();
		}
		roads.Apply();
		water.Apply();
		if (useHFOriginalSurface)
		{
			// The HF sea is a flat per-chunk plane but its visible silhouette is
			// decided by the displaced relief shader. Give culling a conservative
			// volume so an off-centre camera cannot reject water that is still on
			// screen and reveal the warm seabed underneath.
			water.ExpandBounds(new Vector3(
				HexMetrics.outerRadius,
				HexMetrics.elevationStep * 12f,
				HexMetrics.outerRadius));
		}
		waterShore.Apply();
		estuaries.Apply();
		features.Apply();
		relief.Apply();
		coastCliffs.Apply();
	}

	void EnsureSurfaceCollider()
	{
		if (surfaceCollider)
		{
			return;
		}
		Transform existing = transform.Find("HF Surface Collider");
		if (existing)
		{
			surfaceCollider = existing.GetComponent<HexSurfaceCollider>();
		}
		if (!surfaceCollider)
		{
			GameObject colliderObject = new("HF Surface Collider");
			colliderObject.transform.SetParent(transform, false);
			colliderObject.layer = terrain.gameObject.layer;
			surfaceCollider = colliderObject.AddComponent<HexSurfaceCollider>();
		}
	}

	void Triangulate(int cellIndex)
	{
		HexCellData cell = Grid.CellData[cellIndex];
		Vector3 cellPosition = Grid.CellPositions[cellIndex];
		if (useHFOriginalSurface)
		{
			Grid.RefreshCellUISurfacePosition(cellIndex);
		}
		// HF owns one continuous chunk surface. Every dry cell participates even
		// when it is flat, contains a road, or hosts a special feature; otherwise
		// the sparse patch set itself becomes a visible hexagonal mask.
		if (useHFOriginalSurface || !cell.IsUnderwater)
		{
			relief.AddCell(cellIndex, cellPosition);
		}
		if (!cell.IsUnderwater)
		{
			if (!cell.IsSpecial)
			{
				features.AddHFForeground(cell, cellIndex, cellPosition);
			}
		}
		if (useHFOriginalSurface &&
			IsWithinHFOceanStampReach(cell))
		{
			TriangulateHFOceanCell(cellIndex, cellPosition);
		}
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			Triangulate(d, cell, cellIndex, cellPosition);
		}
		if (!cell.IsUnderwater)
		{
			if (!cell.HasRiver && !cell.HasRoads &&
				cell.landform == HexLandform.Flat)
			{
				features.AddFeature(cell, cellPosition);
			}
			if (cell.IsSpecial)
			{
				features.AddSpecialFeature(cell, cellPosition);
			}
		}
	}

	void Triangulate(
		HexDirection direction,
		HexCellData cell,
		int cellIndex,
		Vector3 center)
	{
		var e = new EdgeVertices(
			center + HexMetrics.GetFirstSolidCorner(direction),
			center + HexMetrics.GetSecondSolidCorner(direction));

		if (useHFOriginalSurface && cell.HasHFRiver)
		{
			// HF rivers live on shared outer edges and are evaluated analytically by
			// Relief. Keep only ordinary terrain/road topology here; generating a
			// center ribbon would reintroduce the geometry this mode replaces.
			TriangulateWithoutRiver(direction, cell, cellIndex, center, e);
		}
		else if (cell.HasLegacyRiver)
		{
			if (cell.HasRiverThroughEdge(direction))
			{
				e.v3.y = cell.StreamBedY;
				if (cell.HasRiverBeginOrEnd)
				{
					TriangulateWithRiverBeginOrEnd(cell, cellIndex, center, e);
				}
				else
				{
					TriangulateWithRiver(direction, cell, cellIndex, center, e);
				}
			}
			else
			{
				TriangulateAdjacentToRiver(
					direction, cell, cellIndex, center, e);
			}
		}
		else
		{
			TriangulateWithoutRiver(direction, cell, cellIndex, center, e);
			if (!cell.IsUnderwater &&
				cell.landform == HexLandform.Flat &&
				!cell.HasRoadThroughEdge(direction))
			{
				features.AddFeature(
					cell, (center + e.v1 + e.v5) * (1f / 3f));
			}
		}

		if (direction <= HexDirection.SE)
		{
			TriangulateConnection(direction, cell, cellIndex, center.y, e);
		}

		if (cell.IsUnderwater && !useHFOriginalSurface)
		{
			TriangulateWater(direction, cell, cellIndex, center);
		}
	}

}
