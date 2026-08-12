using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Component that applies UI commands to the hex map.
/// Public methods are hooked up to the in-game UI.
/// </summary>
public partial class HexMapEditor : MonoBehaviour
{
	static readonly int cellHighlightingId = Shader.PropertyToID(
		"_CellHighlighting");
	static readonly int editorShowGridId = Shader.PropertyToID(
		"_HexEditorShowGrid");

	enum EditorTool
	{
		Terrain,
		Relief,
		Forest,
		Water,
		RoadDraw,
		RoadErase,
		RiverDraw,
		RiverErase,
		// Retained for old scenes and serialized uGUI callbacks. The HF panel
		// never selects this multi-property Catlike brush.
		LegacySurface
	}

	[SerializeField]
	HexGrid hexGrid;

	[SerializeField]
	Material terrainMaterial;

	int activeElevation;
	int activeWaterLevel;

	int activeUrbanLevel, activeFarmLevel, activePlantLevel, activeSpecialIndex;

	int activeTerrainTypeIndex;
	HexLandform activeLandform;
	HexVegetation activeVegetation;
	HexVegetationTint activeVegetationTint;
	int activeVegetationDensity = 60;
	// -2 randomizes each painted cell, -1 preserves its current stamp angle.
	int activeTerrainRotation = -1;

	int brushSize;

	bool applyElevation = true;
	bool applyWaterLevel = true;

	bool applyUrbanLevel, applyFarmLevel, applyPlantLevel, applySpecialIndex;
	bool applyLandform = false;

	enum OptionalToggle
	{
		Ignore, Yes, No
	}

	OptionalToggle riverMode, roadMode, walledMode;

	bool isDrag;
	HexDirection dragDirection;
	int previousCellIndex = -1;
	int pathPreviousCellIndex = -1;
	bool pathHasPreviousPoint;
	Vector3 pathPreviousPoint;
	int hoveredCellIndex = -1;
	EditorTool activeTool;
	bool editMode = true;
	bool gridVisible;
	bool paintSea = true;
	bool overviewInputSuspended;

	public void SetTerrainTypeIndex(int index)
	{
		activeTerrainTypeIndex = index;
		SelectTool(EditorTool.Terrain);
	}

	public void SetApplyElevation(bool toggle)
	{
		applyElevation = toggle;
		if (toggle)
		{
			SelectTool(EditorTool.LegacySurface);
		}
	}

	public void SetElevation(float elevation) =>
		activeElevation = (int)elevation;

	public void SetApplyWaterLevel(bool toggle)
	{
		applyWaterLevel = toggle;
		if (toggle)
		{
			SelectTool(EditorTool.LegacySurface);
		}
	}

	public void SetWaterLevel(float level) => activeWaterLevel = (int)level;

	public void SetApplyUrbanLevel(bool toggle)
	{
		applyUrbanLevel = toggle;
		if (toggle)
		{
			SelectTool(EditorTool.LegacySurface);
		}
	}

	public void SetUrbanLevel(float level) => activeUrbanLevel = (int)level;

	public void SetApplyFarmLevel(bool toggle)
	{
		applyFarmLevel = toggle;
		if (toggle)
		{
			SelectTool(EditorTool.LegacySurface);
		}
	}

	public void SetFarmLevel(float level) => activeFarmLevel = (int)level;

	public void SetApplyPlantLevel(bool toggle)
	{
		applyPlantLevel = toggle;
		if (toggle)
		{
			SelectTool(EditorTool.Forest);
		}
	}

	public void SetPlantLevel(float level)
	{
		activePlantLevel = Mathf.Clamp((int)level, 0, 3);
		activeVegetationDensity = activePlantLevel == 3 ?
			100 : activePlantLevel * 33;
	}

	public void SetApplySpecialIndex(bool toggle)
	{
		applySpecialIndex = toggle;
		if (toggle)
		{
			SelectTool(EditorTool.LegacySurface);
		}
	}

	public void SetSpecialIndex(float index) => activeSpecialIndex = (int)index;

	public void SetBrushSize(float size) => brushSize = (int)size;

	public void SetRiverMode(int mode)
	{
		riverMode = (OptionalToggle)mode;
		if (riverMode == OptionalToggle.Yes)
		{
			SelectTool(EditorTool.RiverDraw);
		}
		else if (riverMode == OptionalToggle.No)
		{
			SelectTool(EditorTool.RiverErase);
		}
		else if (activeTool == EditorTool.RiverDraw ||
			activeTool == EditorTool.RiverErase)
		{
			SelectTool(EditorTool.Terrain);
		}
	}

	public void SetRoadMode(int mode)
	{
		roadMode = (OptionalToggle)mode;
		if (roadMode == OptionalToggle.Yes)
		{
			SelectTool(EditorTool.RoadDraw);
		}
		else if (roadMode == OptionalToggle.No)
		{
			SelectTool(EditorTool.RoadErase);
		}
		else if (activeTool == EditorTool.RoadDraw ||
			activeTool == EditorTool.RoadErase)
		{
			SelectTool(EditorTool.Terrain);
		}
	}

	public void SetWalledMode(int mode)
	{
		walledMode = (OptionalToggle)mode;
		if (walledMode != OptionalToggle.Ignore)
		{
			SelectTool(EditorTool.LegacySurface);
		}
	}

	public void SetEditMode(bool toggle)
	{
		EndHistoryStroke();
		editMode = toggle;
		if (!toggle)
		{
			ResetPathStroke();
			ClearCellHighlightData();
		}
	}

	public void ShowGrid(bool visible)
	{
		if (visible)
		{
			if (terrainMaterial)
			{
				terrainMaterial.EnableKeyword("_SHOW_GRID");
			}
		}
		else
		{
			if (terrainMaterial)
			{
				terrainMaterial.DisableKeyword("_SHOW_GRID");
			}
		}
		Shader.SetGlobalFloat(editorShowGridId, visible ? 1f : 0f);
		gridVisible = visible;
	}

	void Awake()
	{
		ShowGrid(false);
		Shader.EnableKeyword("_HEX_MAP_EDIT_MODE");
		SetEditMode(true);
		ClearCellHighlightData();
		InitializeHFUI();
	}

	void OnEnable()
	{
		if (hexGrid)
		{
			hexGrid.MapReset += HandleMapReset;
		}
	}

	void OnDisable()
	{
		if (hexGrid)
		{
			hexGrid.MapReset -= HandleMapReset;
		}
		EndHistoryStroke();
		ClearCellHighlightData();
	}

	void Update()
	{
		if (Input.GetMouseButtonUp(0))
		{
			EndHistoryStroke();
		}
		if (hexGrid && hexGrid.IsOverviewMode)
		{
			if (!overviewInputSuspended)
			{
				overviewInputSuspended = true;
				ResetPathStroke();
				ClearCellHighlightData();
			}
			return;
		}
		overviewInputSuspended = false;
		if (!editMode || IsHFModalOpen())
		{
			ResetPathStroke();
			ClearCellHighlightData();
			return;
		}
		HandleHistoryShortcuts();
		HandleLandformShortcuts();
		bool pointerOverUI =
			(EventSystem.current && EventSystem.current.IsPointerOverGameObject()) ||
			IsPointerOverEditorPanel();
		if (IsPathTool(activeTool))
		{
			HandlePathTool(pointerOverUI);
			return;
		}
		if (!pointerOverUI)
		{
			HexCell currentCell = GetCellUnderCursor();
			if (paintOperation == PaintOperation.Brush &&
				Input.GetMouseButton(0))
			{
				if (currentCell)
				{
					BeginHistoryStroke();
				}
				HandleInput();
				return;
			}
			UpdateCellHighlightData(currentCell);
			if (paintOperation != PaintOperation.Brush &&
				Input.GetMouseButtonDown(0) && currentCell)
			{
				BeginHistoryStroke();
				ApplyPaintOperation(currentCell);
				EndHistoryStroke();
				return;
			}
			if (Input.GetKeyDown(KeyCode.U))
			{
				if (Input.GetKey(KeyCode.LeftShift))
				{
					DestroyUnit();
				}
				else
				{
					CreateUnit();
				}
				return;
			}
		}
		else
		{
			ClearCellHighlightData();
		}
		previousCellIndex = -1;
	}

	void HandleLandformShortcuts()
	{
		if (Input.GetKeyDown(KeyCode.Alpha0))
		{
			activeLandform = HexLandform.Flat;
			SelectTool(EditorTool.Relief);
		}
		else if (Input.GetKeyDown(KeyCode.Alpha1))
		{
			activeLandform = HexLandform.Flat;
			SelectTool(EditorTool.Relief);
		}
		else if (Input.GetKeyDown(KeyCode.Alpha2))
		{
			activeLandform = HexLandform.Hill;
			SelectTool(EditorTool.Relief);
		}
		else if (Input.GetKeyDown(KeyCode.Alpha3))
		{
			activeLandform = HexLandform.Mountain;
			SelectTool(EditorTool.Relief);
		}
	}

	void SelectTool(EditorTool tool)
	{
		activeTool = tool;
		riverMode = tool switch
		{
			EditorTool.RiverDraw => OptionalToggle.Yes,
			EditorTool.RiverErase => OptionalToggle.No,
			_ => OptionalToggle.Ignore
		};
		roadMode = tool switch
		{
			EditorTool.RoadDraw => OptionalToggle.Yes,
			EditorTool.RoadErase => OptionalToggle.No,
			_ => OptionalToggle.Ignore
		};
		ResetPathStroke();
		previousCellIndex = -1;
	}

	static bool IsPathTool(EditorTool tool) =>
		tool == EditorTool.RoadDraw || tool == EditorTool.RoadErase ||
		tool == EditorTool.RiverDraw || tool == EditorTool.RiverErase;

	HexCell GetCellUnderCursor() =>
		TryGetPointerMapPoint(out HexCell cell, out _) ? cell : default;

	bool TryGetPointerMapPoint(out HexCell cell, out Vector3 worldPoint)
	{
		cell = default;
		worldPoint = default;
		Camera camera = Camera.main;
		if (!camera || !hexGrid)
		{
			return false;
		}

		Ray ray = camera.ScreenPointToRay(Input.mousePosition);
		HexCell terrainCell = default;
		float terrainDistance = float.PositiveInfinity;
		if (Physics.Raycast(ray, out RaycastHit terrainHit))
		{
			terrainCell = hexGrid.GetCell(terrainHit.point);
			terrainDistance = terrainHit.distance;
			worldPoint = terrainHit.point;
		}

		if (!hexGrid.SurfaceSampler.UsesHFOriginalSurface)
		{
			cell = terrainCell;
			return cell;
		}

		// HF renders the sea as a collider-free continuous plane. Picking only the
		// relief collider sends an oblique ray through the visible water and selects
		// a displaced seabed cell instead. Prefer the water-plane cell only when it
		// is logically underwater and the plane is in front of the terrain hit, so
		// hills and dry coast continue to use their real surface collider.
		Vector3 waterPoint = hexGrid.transform.TransformPoint(
			new Vector3(
				0f,
				HexMetrics.visualWaterLevel * HexMetrics.elevationStep,
				0f));
		Plane waterPlane = new(hexGrid.transform.up, waterPoint);
		if (waterPlane.Raycast(ray, out float waterDistance) &&
			waterDistance >= 0f && waterDistance <= terrainDistance + 0.001f)
		{
			HexCell waterCell = hexGrid.GetCell(ray.GetPoint(waterDistance));
			if (waterCell && hexGrid.CellData[waterCell.Index].IsUnderwater)
			{
				cell = waterCell;
				worldPoint = ray.GetPoint(waterDistance);
				return true;
			}
		}

		cell = terrainCell;
		return cell;
	}

	void CreateUnit()
	{
		HexCell cell = GetCellUnderCursor();
		if (cell && !cell.Unit)
		{
			hexGrid.AddUnit(
				Instantiate(HexUnit.unitPrefab), cell, Random.Range(0f, 360f)
			);
		}
	}

	void DestroyUnit()
	{
		HexCell cell = GetCellUnderCursor();
		if (cell && cell.Unit)
		{
			hexGrid.RemoveUnit(cell.Unit);
		}
	}

	void HandlePathTool(bool pointerOverUI)
	{
		if (activeTool == EditorTool.RiverDraw ||
			activeTool == EditorTool.RiverErase)
		{
			HandleRiverEdgeTool(pointerOverUI);
			return;
		}
		if (pointerOverUI)
		{
			ResetPathStroke();
			ClearCellHighlightData();
			return;
		}

		bool hasPoint = TryGetPointerMapPoint(
			out HexCell currentCell, out Vector3 worldPoint);
		UpdateCellHighlightData(currentCell);
		if (!Input.GetMouseButton(0))
		{
			ResetPathStroke();
			return;
		}
		if (!hasPoint || !currentCell)
		{
			ResetPathStroke();
			return;
		}
		BeginHistoryStroke();

		if (pathPreviousCellIndex < 0)
		{
			pathPreviousCellIndex = currentCell.Index;
			if (activeTool == EditorTool.RoadErase)
			{
				Vector3 localPoint =
					hexGrid.transform.InverseTransformPoint(worldPoint);
				if (TryGetNearestRoadDirection(
					currentCell, localPoint, out HexDirection direction))
				{
					currentCell.RemoveRoadThroughEdge(direction);
				}
			}
			return;
		}
		if (pathPreviousCellIndex == currentCell.Index)
		{
			return;
		}

		ApplyPath(hexGrid.GetCell(pathPreviousCellIndex), currentCell);
		pathPreviousCellIndex = currentCell.Index;
	}

	bool TryGetNearestRoadDirection(
		HexCell cell, Vector3 localPoint, out HexDirection nearest)
	{
		Vector3 center = hexGrid.CellPositions[cell.Index];
		Vector2 point = new(localPoint.x - center.x, localPoint.z - center.z);
		if (hexGrid.Wrapping)
		{
			float mapWidth = HexMetrics.innerDiameter * hexGrid.CellCountX;
			if (point.x < -mapWidth * 0.5f)
			{
				point.x += mapWidth;
			}
			else if (point.x > mapWidth * 0.5f)
			{
				point.x -= mapWidth;
			}
		}

		nearest = HexDirection.NE;
		float nearestDistance = float.PositiveInfinity;
		bool found = false;
		for (HexDirection direction = HexDirection.NE;
			direction <= HexDirection.NW; direction++)
		{
			if (!cell.Flags.HasRoad(direction))
			{
				continue;
			}
			Vector3 first = HexMetrics.GetFirstCorner(direction);
			Vector3 second = HexMetrics.GetSecondCorner(direction);
			Vector2 edgeCenter = new(
				(first.x + second.x) * 0.5f,
				(first.z + second.z) * 0.5f);
			float distance = DistanceToSegmentSquared(
				point, Vector2.zero, edgeCenter);
			if (distance < nearestDistance)
			{
				nearestDistance = distance;
				nearest = direction;
				found = true;
			}
		}
		return found;
	}

	void HandleRiverEdgeTool(bool pointerOverUI)
	{
		if (pointerOverUI)
		{
			ResetPathStroke();
			ClearCellHighlightData();
			return;
		}

		bool hasPoint = TryGetPointerMapPoint(
			out HexCell currentCell, out Vector3 worldPoint);
		UpdateCellHighlightData(currentCell);
		if (!Input.GetMouseButton(0))
		{
			ResetPathStroke();
			return;
		}
		if (!hasPoint || !currentCell)
		{
			ResetPathStroke();
			return;
		}

		BeginHistoryStroke();
		Vector3 localPoint = hexGrid.transform.InverseTransformPoint(worldPoint);
		if (!pathHasPreviousPoint)
		{
			ApplyRiverEdgeAt(localPoint);
		}
		else
		{
			float distance = Vector2.Distance(
				new Vector2(pathPreviousPoint.x, pathPreviousPoint.z),
				new Vector2(localPoint.x, localPoint.z));
			int steps = Mathf.Max(1, Mathf.CeilToInt(
				distance / (HexMetrics.outerRadius * 0.2f)));
			for (int step = 1; step <= steps; step++)
			{
				ApplyRiverEdgeAt(Vector3.Lerp(
					pathPreviousPoint, localPoint, step / (float)steps));
			}
		}
		pathPreviousPoint = localPoint;
		pathHasPreviousPoint = true;
	}

	void ApplyRiverEdgeAt(Vector3 localPoint)
	{
		Vector3 worldPoint = hexGrid.transform.TransformPoint(localPoint);
		HexCell cell = hexGrid.GetCell(worldPoint);
		if (!cell)
		{
			return;
		}
		HexDirection direction = GetNearestRiverEdge(cell, localPoint);
		if (activeTool == EditorTool.RiverErase)
		{
			cell.RemoveRiverThroughEdge(direction);
		}
		else
		{
			cell.SetHFRiverEdge(direction);
		}
	}

	HexDirection GetNearestRiverEdge(HexCell cell, Vector3 localPoint)
	{
		Vector3 center = hexGrid.CellPositions[cell.Index];
		Vector2 point = new(localPoint.x - center.x, localPoint.z - center.z);
		if (hexGrid.Wrapping)
		{
			float mapWidth = HexMetrics.innerDiameter * hexGrid.CellCountX;
			if (point.x < -mapWidth * 0.5f)
			{
				point.x += mapWidth;
			}
			else if (point.x > mapWidth * 0.5f)
			{
				point.x -= mapWidth;
			}
		}

		HexDirection nearest = HexDirection.NE;
		float nearestDistance = float.PositiveInfinity;
		for (HexDirection direction = HexDirection.NE;
			direction <= HexDirection.NW; direction++)
		{
			Vector3 first = HexMetrics.GetFirstCorner(direction);
			Vector3 second = HexMetrics.GetSecondCorner(direction);
			float distance = DistanceToSegmentSquared(
				point, new Vector2(first.x, first.z),
				new Vector2(second.x, second.z));
			if (distance < nearestDistance)
			{
				nearestDistance = distance;
				nearest = direction;
			}
		}
		return nearest;
	}

	static float DistanceToSegmentSquared(Vector2 point, Vector2 a, Vector2 b)
	{
		Vector2 edge = b - a;
		float denominator = Mathf.Max(Vector2.Dot(edge, edge), 0.0001f);
		float t = Mathf.Clamp01(Vector2.Dot(point - a, edge) / denominator);
		return (point - (a + edge * t)).sqrMagnitude;
	}

	void ApplyPath(HexCell from, HexCell to)
	{
		HexCoordinates start = from.Coordinates;
		HexCoordinates end = to.Coordinates;
		int endX = GetShortestWrappedTargetX(start, end);
		int endZ = end.Z;
		int endY = -endX - endZ;
		int steps = Mathf.Max(
			Mathf.Abs(endX - start.X),
			Mathf.Max(
				Mathf.Abs(endY - start.Y),
				Mathf.Abs(endZ - start.Z)));
		if (steps <= 0)
		{
			return;
		}

		HexCell previous = from;
		for (int step = 1; step <= steps; step++)
		{
			float t = step / (float)steps;
			HexCoordinates coordinates = CubeRound(Vector3.Lerp(
				new Vector3(start.X, start.Y, start.Z),
				new Vector3(endX, endY, endZ), t));
			if (!hexGrid.TryGetCell(coordinates, out HexCell next) ||
				next.Index == previous.Index)
			{
				continue;
			}
			if (!TryGetDirection(previous, next, out HexDirection direction))
			{
				break;
			}

			if (activeTool == EditorTool.RiverErase)
			{
				previous.RemoveRiverThroughEdge(direction);
			}
			else if (activeTool == EditorTool.RiverDraw)
			{
				previous.SetHFOutgoingRiver(direction);
			}
			else if (activeTool == EditorTool.RoadErase)
			{
				previous.RemoveRoadThroughEdge(direction);
			}
			else
			{
				previous.AddHFRoad(direction);
			}
			previous = next;
		}
	}

	static int GetShortestWrappedTargetX(
		HexCoordinates start, HexCoordinates end)
	{
		if (!HexMetrics.Wrapping)
		{
			return end.X;
		}
		int bestX = end.X;
		int bestDistance = CubeDistance(start, bestX, end.Z);
		int leftX = end.X - HexMetrics.wrapSize;
		int leftDistance = CubeDistance(start, leftX, end.Z);
		if (leftDistance < bestDistance)
		{
			bestX = leftX;
			bestDistance = leftDistance;
		}
		int rightX = end.X + HexMetrics.wrapSize;
		if (CubeDistance(start, rightX, end.Z) < bestDistance)
		{
			bestX = rightX;
		}
		return bestX;
	}

	static int CubeDistance(
		HexCoordinates start, int endX, int endZ)
	{
		int endY = -endX - endZ;
		return Mathf.Max(
			Mathf.Abs(endX - start.X),
			Mathf.Max(
				Mathf.Abs(endY - start.Y),
				Mathf.Abs(endZ - start.Z)));
	}

	static HexCoordinates CubeRound(Vector3 cube)
	{
		int x = Mathf.RoundToInt(cube.x);
		int y = Mathf.RoundToInt(cube.y);
		int z = Mathf.RoundToInt(cube.z);
		float xDelta = Mathf.Abs(x - cube.x);
		float yDelta = Mathf.Abs(y - cube.y);
		float zDelta = Mathf.Abs(z - cube.z);
		if (xDelta > yDelta && xDelta > zDelta)
		{
			x = -y - z;
		}
		else if (yDelta > zDelta)
		{
			y = -x - z;
		}
		else
		{
			z = -x - y;
		}
		return new HexCoordinates(x, z);
	}

	static bool TryGetDirection(
		HexCell from, HexCell to, out HexDirection direction)
	{
		for (direction = HexDirection.NE;
			direction <= HexDirection.NW; direction++)
		{
			if (from.TryGetNeighbor(direction, out HexCell neighbor) &&
				neighbor == to)
			{
				return true;
			}
		}
		direction = HexDirection.NE;
		return false;
	}

	void ResetPathStroke()
	{
		pathPreviousCellIndex = -1;
		pathHasPreviousPoint = false;
	}

	void HandleInput()
	{
		HexCell currentCell = GetCellUnderCursor();
		if (currentCell)
		{
			if (previousCellIndex >= 0 &&
				previousCellIndex != currentCell.Index)
			{
				ValidateDrag(currentCell);
			}
			else
			{
				isDrag = false;
			}
			EditCells(currentCell);
			previousCellIndex = currentCell.Index;
		}
		else
		{
			previousCellIndex = -1;
		}
		UpdateCellHighlightData(currentCell);
	}

	void UpdateCellHighlightData(HexCell cell)
	{
		if (!cell)
		{
			ClearCellHighlightData();
			return;
		}
		hoveredCellIndex = cell.Index;

		// Works up to brush size 6.
		Shader.SetGlobalVector(
			cellHighlightingId,
			new Vector4(
				cell.Coordinates.HexX,
				cell.Coordinates.HexZ,
				IsPathTool(activeTool) ?
					0.5f : brushSize * brushSize + 0.5f,
				HexMetrics.wrapSize
			)
		);
	}

	void ClearCellHighlightData()
	{
		hoveredCellIndex = -1;
		Shader.SetGlobalVector(
			cellHighlightingId, new Vector4(0f, 0f, -1f, 0f));
	}

	void ValidateDrag(HexCell currentCell)
	{
		for (dragDirection = HexDirection.NE;
			dragDirection <= HexDirection.NW;
			dragDirection++)
		{
			if (hexGrid.GetCell(previousCellIndex).GetNeighbor(dragDirection) ==
				currentCell)
			{
				isDrag = true;
				return;
			}
		}
		isDrag = false;
	}

	void EditCells(HexCell center)
	{
		int centerX = center.Coordinates.X;
		int centerZ = center.Coordinates.Z;

		for (int r = 0, z = centerZ - brushSize; z <= centerZ; z++, r++)
		{
			for (int x = centerX - r; x <= centerX + brushSize; x++)
			{
				EditCell(hexGrid.GetCell(new HexCoordinates(x, z)));
			}
		}
		for (int r = 0, z = centerZ + brushSize; z > centerZ; z--, r++)
		{
			for (int x = centerX - brushSize; x <= centerX + r; x++)
			{
				EditCell(hexGrid.GetCell(new HexCoordinates(x, z)));
			}
		}
	}

	void EditCell(HexCell cell)
	{
		if (!cell)
		{
			return;
		}

		switch (activeTool)
		{
			case EditorTool.Terrain:
				cell.SetTerrainTypeIndex(activeTerrainTypeIndex);
				if (activeTerrainRotation == -2)
				{
					cell.SetTerrainRotation(Random.Range(0, 6));
				}
				else if (activeTerrainRotation >= 0)
				{
					cell.SetTerrainRotation(activeTerrainRotation);
				}
				break;
			case EditorTool.Relief:
				cell.SetLandform(activeLandform);
				break;
			case EditorTool.Forest:
				cell.SetVegetation(
					activeVegetation, activeVegetationDensity,
					activeVegetationTint);
				break;
			case EditorTool.Water:
				cell.SetWaterLevel(paintSea ?
					Mathf.Clamp(cell.Values.Elevation + 1, 0, 31) : 0);
				break;
			case EditorTool.LegacySurface:
				EditLegacyCell(cell);
				break;
		}
	}

	void EditLegacyCell(HexCell cell)
	{
		// Compatibility path for old serialized Catlike UI callbacks. The HF
		// editor never combines these properties in one brush stroke.
		if (activeTerrainTypeIndex >= 0)
		{
			cell.SetTerrainTypeIndex(activeTerrainTypeIndex);
		}
		if (applyLandform)
		{
			cell.SetLandform(activeLandform);
		}
		if (applyElevation)
		{
			cell.SetElevation(activeElevation);
		}
		if (applyWaterLevel)
		{
			cell.SetWaterLevel(activeWaterLevel);
		}
		if (applySpecialIndex)
		{
			cell.SetSpecialIndex(activeSpecialIndex);
		}
		if (applyUrbanLevel)
		{
			cell.SetUrbanLevel(activeUrbanLevel);
		}
		if (applyFarmLevel)
		{
			cell.SetFarmLevel(activeFarmLevel);
		}
		if (applyPlantLevel)
		{
			cell.SetPlantLevel(activePlantLevel);
		}
		if (riverMode == OptionalToggle.No)
		{
			cell.RemoveRiver();
		}
		if (roadMode == OptionalToggle.No)
		{
			cell.RemoveRoads();
		}
		if (walledMode != OptionalToggle.Ignore)
		{
			cell.SetWalled(walledMode == OptionalToggle.Yes);
		}
		if (isDrag && cell.TryGetNeighbor(
			dragDirection.Opposite(), out HexCell otherCell))
		{
			if (riverMode == OptionalToggle.Yes)
			{
				otherCell.SetOutgoingRiver(dragDirection);
			}
			if (roadMode == OptionalToggle.Yes)
			{
				otherCell.AddRoad(dragDirection);
			}
		}
	}
}
