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
			features.ConfigureTerrainStyle(activeStyle);
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
	static Material sharedNearRoadMaterial;
	MeshRenderer roadRenderer;
	Material originalRoadMaterial;
	bool useHFOriginalSurface;
	bool interactionEnabled = true;
	bool globalOceanMode;

	public bool InteractionEnabled => interactionEnabled;

	public bool GlobalOceanMode => globalOceanMode;

	int[] cellIndices;
	RectTransform[] cellUIs;
	Text labelPrefab;
	readonly List<int> validCellIndices = new(
		HexMetrics.chunkSizeX * HexMetrics.chunkSizeZ);

	Canvas gridCanvas;
	bool gridUIVisible = true;
	int activeCellUICount;

	void Awake()
	{
		HexTerrainStyle activeStyle = terrainStyle ?
			terrainStyle : HexTerrainStyle.RuntimeDefault;
		useHFOriginalSurface = activeStyle.UsesHFOriginalSurface;
		ConfigureRoadMaterial(activeStyle);
		features.ConfigureTerrainStyle(activeStyle);
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
		int index, int cellIndex, Text cellLabelPrefab, bool createUI = true)
	{
		labelPrefab = cellLabelPrefab;
		cellIndices[index] = cellIndex;
		if (cellIndex >= 0)
		{
			validCellIndices.Add(cellIndex);
		}
		RectTransform cellUI = GetCellUI(index, cellIndex, createUI);
		SetCellUIActive(index, cellIndex, false);
		return cellIndex >= 0 ? cellUI : null;
	}

	/// <summary>
	/// Get the pooled label bound to a logical cell in this chunk.
	/// </summary>
	public RectTransform GetCellUI(int localIndex, int expectedCellIndex, bool create = false)
	{
		if (expectedCellIndex < 0 || cellIndices[localIndex] != expectedCellIndex) return null;
		if (!cellUIs[localIndex] && create && labelPrefab)
		{
			Text label = Instantiate(labelPrefab, gridCanvas.transform, false);
			cellUIs[localIndex] = label.rectTransform;
			label.gameObject.SetActive(false);
		}
		return cellUIs[localIndex];
	}

	/// <summary>
	/// Empty cell labels must not stay active in gameplay. Thousands of dormant
	/// world-space UI Graphics are cheap while the camera is still but force
	/// Canvas visibility and batch work whenever it moves.
	/// </summary>
	public void SetCellUIActive(
		int localIndex, int expectedCellIndex, bool active)
	{
		if (localIndex < 0 || localIndex >= cellIndices.Length ||
			cellIndices[localIndex] != expectedCellIndex)
		{
			return;
		}

		RectTransform cellUI = cellUIs[localIndex];
		if (!cellUI || cellUI.gameObject.activeSelf == active)
		{
			return;
		}
		cellUI.gameObject.SetActive(active);
		activeCellUICount = Mathf.Max(
			0, activeCellUICount + (active ? 1 : -1));
		RefreshGridCanvasActiveState();
	}

	/// <summary>
	/// Clear logical bindings before returning this renderer to the chunk pool.
	/// </summary>
	public void UnbindCells()
	{
		CancelBuild();
		validCellIndices.Clear();
		for (int i = 0; i < cellIndices.Length; i++)
		{
			cellIndices[i] = -1;
			if (cellUIs[i])
			{
				cellUIs[i].gameObject.SetActive(false);
			}
		}
		activeCellUICount = 0;
		RefreshGridCanvasActiveState();
	}

	/// <summary>
	/// Refresh the chunk.
	/// </summary>
	public void Refresh()
	{
		fullRefreshRequested = true;
		if (Grid && Grid.UsesChunkStreaming) enabled = false;
		else enabled = true;
	}

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
		RestoreStreamingPhysics();
		try
		{
			interactionEnabled = value;
			if (!value)
			{
				EnsureSurfaceCollider();
				surfaceCollider.Clear();
				terrain.SetColliderEnabled(false);
			}
			else if (refresh && geometryReady)
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
		finally
		{
			CaptureAndSuppressStreamingPhysics();
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

	void ConfigureRoadMaterial(HexTerrainStyle style)
	{
		if (!roads) return;
		if (!roadRenderer)
		{
			roadRenderer = roads.GetComponent<MeshRenderer>();
			if (!roadRenderer) return;
			originalRoadMaterial = roadRenderer.sharedMaterial;
		}
		if (style && style.UsesNearTerrain && !sharedNearRoadMaterial)
		{
			// Resources keeps the optional shader available in player builds.
			// All pooled chunks share this one transient material.
			Shader shader = Resources.Load<Shader>("HexNearRoad");
			if (shader && shader.isSupported)
				sharedNearRoadMaterial = new Material(shader)
				{
					name = "Near Road (Runtime)", hideFlags = HideFlags.HideAndDontSave
				};
		}
		roadRenderer.sharedMaterial = style && style.UsesNearTerrain && sharedNearRoadMaterial ?
			sharedNearRoadMaterial : originalRoadMaterial;
	}

	/// <summary>
	/// Control whether the map UI is visibile or hidden for the chunk.
	/// </summary>
	/// <param name="visible">Whether the UI should be visible.</param>
	public void ShowUI(bool visible)
	{
		gridUIVisible = visible;
		RefreshGridCanvasActiveState();
	}

	void RefreshGridCanvasActiveState()
	{
		if (gridCanvas)
		{
			gridCanvas.gameObject.SetActive(
				gridUIVisible && activeCellUICount > 0);
		}
	}

	// A chunk owns its construction buffers across frames. The grid gives all
	// chunks one shared CPU budget, including feature creation and mesh upload.
	struct DetailRequest
	{
		public int cellIndex;
		public Vector3 position;
		public byte kind;
	}
	readonly List<DetailRequest> detailRequests = new(200);
	HexTerrainStyle buildStyle;
	int buildStage, buildCellCursor, detailCursor;
	bool fullRefreshRequested, geometryReady, detailReady;
	bool detailVisible = true;

	public int GeometryBuildCount { get; private set; }
	public bool HasReadyGeometry => geometryReady && !fullRefreshRequested;
	public bool IsReady => geometryReady && !fullRefreshRequested &&
		(!detailVisible || detailReady);
	public bool IsBuildPending => fullRefreshRequested ||
		(buildStage > 0 && buildStage < 8) ||
		(detailVisible && geometryReady && !detailReady);

	public void SetDetailFeaturesVisible(bool visible)
	{
		detailVisible = visible;
		features.SetDetailVisible(visible && detailReady);
		if (Grid && !Grid.UsesChunkStreaming && IsBuildPending) enabled = true;
	}

	void RecordDetail(int cellIndex, Vector3 position, byte kind) =>
		detailRequests.Add(new DetailRequest {
			cellIndex = cellIndex, position = position, kind = kind });

	void LateUpdate()
	{
		// Streamed chunks are advanced exclusively by HexGrid.Update.
		if (Grid && !Grid.UsesChunkStreaming)
			while (IsBuildPending) BuildStep();
		enabled = false;
	}

	/// <summary>Immediate authoring / small-map compatibility entry point.</summary>
	public void Triangulate()
	{
		SetDetailFeaturesVisible(HexMapCamera.MapDetailFeaturesEnabled);
		fullRefreshRequested = true;
		while (IsBuildPending) BuildStep();
	}

	/// <summary>Perform one bounded cell or upload operation, without waiting.</summary>
	public void BuildStep()
	{
		if (fullRefreshRequested)
		{
			CancelBuild();
			BeginBuild();
			return;
		}
		if (buildStage == 0 && geometryReady && detailVisible && !detailReady)
		{
			features.ClearDetailFeatures();
			detailCursor = 0;
			buildStage = 8;
		}
		switch (buildStage)
		{
			case 1:
				if (buildCellCursor < validCellIndices.Count)
					Triangulate(validCellIndices[buildCellCursor++]);
				else buildStage = 2;
				break;
			case 2:
				terrain.SetColliderEnabled(!useHFOriginalSurface && interactionEnabled);
				if (useHFOriginalSurface) { terrain.Discard(); rivers.Discard(); }
				else { terrain.Apply(); rivers.Apply(); }
				buildStage = 3;
				break;
			case 3:
				if (useHFOriginalSurface)
					roads.ConformToSurface(Grid, buildStyle.hfOverlaySubdivisionLevels,
						buildStyle.hfRoadSurfaceOffset);
				buildStage = 4;
				break;
			case 4:
				roads.Apply();
				water.Apply(useHFOriginalSurface);
				if (useHFOriginalSurface)
					water.ExpandBounds(new Vector3(HexMetrics.outerRadius,
						HexMetrics.elevationStep * 12f, HexMetrics.outerRadius));
				buildStage = 5;
				break;
			case 5:
				waterShore.Apply(); estuaries.Apply();
				features.ApplyStructuralFeatures();
				buildStage = 6;
				break;
			case 6:
				relief.Apply(); coastCliffs.Apply();
				buildStage = 7;
				break;
			case 7:
				if (useHFOriginalSurface && interactionEnabled)
					surfaceCollider.Build(Grid, validCellIndices, buildStyle.hfColliderSubdivisions);
				else surfaceCollider.Clear();
				geometryReady = true;
				GeometryBuildCount++;
				buildStage = 0;
				gameObject.SetActive(true);
				break;
			case 8:
				if (!detailVisible) return;
				if (detailCursor < detailRequests.Count)
				{
					DetailRequest request = detailRequests[detailCursor++];
					HexCellData cell = Grid.CellData[request.cellIndex];
					if (request.kind == 0)
						features.AddHFForeground(cell, request.cellIndex, request.position);
					else if (request.kind == 1) features.AddFeature(cell, request.position);
					else features.AddSpecialFeature(cell, request.position);
				}
				else buildStage = 9;
				break;
			case 9:
				if (!detailVisible) return;
				features.ApplyDetailFeatures();
				detailReady = true;
				features.SetDetailVisible(true);
				buildStage = 0;
				break;
		}
	}

	void BeginBuild()
	{
		ResetStreamingVisibilityForBuild();
		buildStyle = terrainStyle ? terrainStyle : HexTerrainStyle.RuntimeDefault;
		EnsureSurfaceCollider();
		useHFOriginalSurface = buildStyle.UsesHFOriginalSurface;
		Grid.ConfigureSurface(buildStyle);
		ConfigureRoadMaterial(buildStyle);
		features.Grid = Grid;
		features.ConfigureTerrainStyle(buildStyle);
		features.SetDetailVisible(false);
		if (Grid.UsesChunkStreaming) gameObject.SetActive(false);
		terrain.GetComponent<MeshRenderer>().enabled = !useHFOriginalSurface;
		terrain.Clear(); rivers.Clear(); roads.Clear(); water.Clear();
		waterShore.Clear(); estuaries.Clear(); features.ClearStructuralFeatures();
		relief.Clear(); coastCliffs.Clear();
		buildCellCursor = 0;
		buildStage = 1;
	}

	/// <summary>Release only owned construction buffers before reuse or edits.</summary>
	public void CancelBuild()
	{
		terrain.CancelConstruction(); rivers.CancelConstruction();
		roads.CancelConstruction(); water.CancelConstruction();
		waterShore.CancelConstruction(); estuaries.CancelConstruction();
		features.CancelConstruction();
		buildStage = 0;
		fullRefreshRequested = geometryReady = detailReady = false;
		detailRequests.Clear();
		features.SetDetailVisible(false);
		enabled = false;
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
				RecordDetail(cellIndex, cellPosition, 0);
			}
		}
		if (useHFOriginalSurface &&
			IsWithinHFOceanStampReach(cell))
		{
			TriangulateHFOceanCell(cellIndex, cellPosition);
		}
		if (!TryTriangulateHFWithoutStructures(cell, cellIndex, cellPosition))
		{
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				Triangulate(d, cell, cellIndex, cellPosition);
			}
		}
		if (!cell.IsUnderwater)
		{
			if (!cell.HasRiver && !cell.HasRoads &&
				cell.landform == HexLandform.Flat)
			{
				RecordDetail(cellIndex, cellPosition, 1);
			}
			if (cell.IsSpecial)
			{
				RecordDetail(cellIndex, cellPosition, 2);
			}
		}
	}

	// HF discards the Catlike terrain mesh. With no nearby legacy structure,
	// its edge fans, terraces and corners have no visible output. Keep the
	// conservative neighbor gate so shared roads and wall corners always use
	// the original topology, including cells loaded from older maps.
	bool TryTriangulateHFWithoutStructures(
		HexCellData cell, int cellIndex, Vector3 center)
	{
		const HexFlags structuralFlags = HexFlags.Roads | HexFlags.River | HexFlags.Walled;
		if (!useHFOriginalSurface || cell.flags.HasAny(structuralFlags))
		{
			return false;
		}
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (Grid.TryGetCellIndex(cell.coordinates.Step(d), out int neighborIndex) &&
				Grid.CellData[neighborIndex].flags.HasAny(structuralFlags))
			{
				return false;
			}
		}

		// Preserve the original request order and floating-point operations.
		// HF boundary rivers suppress these six ordinary feature requests.
		if (!cell.HasHFRiver && !cell.IsUnderwater && cell.landform == HexLandform.Flat)
		{
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				Vector3 first = center + HexMetrics.GetFirstSolidCorner(d);
				Vector3 second = center + HexMetrics.GetSecondSolidCorner(d);
				RecordDetail(cellIndex, (center + first + second) * (1f / 3f), 1);
			}
		}
		return true;
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
				RecordDetail(cellIndex, (center + e.v1 + e.v5) * (1f / 3f), 1);
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
