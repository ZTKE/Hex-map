using UnityEngine;

/// <summary>
/// Immediate-mode front end for the HF map editing tools. Keeping presentation
/// here leaves HexMapEditor focused on map interaction and brush semantics.
/// </summary>
public partial class HexMapEditor
{
	static readonly string[] hfToolCategoryNames =
	{
		"Terrain", "Relief", "Forest", "Sea", "Road", "River"
	};

	static readonly string[] hfTerrainNames =
	{
		"Desert", "Grass", "Plains", "Tundra", "Snow"
	};

	static readonly string[] hfReliefNames =
	{
		"Flat", "Hill", "Mountain"
	};

	static readonly string[] hfVegetationNames =
	{
		"Natural Mix", "Broadleaf", "Sapling", "Conifer", "Deadwood", "Cold Mix"
	};

	static readonly string[] hfVegetationTintNames =
	{
		"Natural", "Deep Green", "Autumn", "Dry", "Frost", "Pale"
	};

	static readonly string[] hfTerrainRotationNames =
	{
		"Random", "Keep", "0 deg", "60 deg", "120 deg", "180 deg", "240 deg", "300 deg"
	};

	static readonly string[] landSeaNames = { "Land", "Sea" };
	static readonly string[] pathActionNames = { "Draw", "Erase" };
	static readonly string[] paintOperationNames = { "Brush", "Fill", "Replace" };

	const float hfPanelMaximumWidth = 960f;
	const float hfPanelHeight = 182f;
	const float hfTerrainPanelHeight = 206f;
	const float hfForestPanelHeight = 232f;
	const float hfPanelMargin = 8f;

	SaveLoadMenu saveLoadMenu;
	NewMapMenu newMapMenu;

	void InitializeHFUI()
	{
		saveLoadMenu = FindObjectOfType<SaveLoadMenu>(true);
		newMapMenu = FindObjectOfType<NewMapMenu>(true);
		activeTerrainTypeIndex = Mathf.Clamp(activeTerrainTypeIndex, 0, 4);
		activePlantLevel = Mathf.Clamp(activePlantLevel, 0, 3);
		activeVegetation = (HexVegetation)Mathf.Clamp(
			(int)activeVegetation, 0, hfVegetationNames.Length - 1);
		activeVegetationTint = (HexVegetationTint)Mathf.Clamp(
			(int)activeVegetationTint, 0, hfVegetationTintNames.Length - 1);
		activeVegetationDensity = Mathf.Clamp(activeVegetationDensity, 0, 100);
		activeTerrainRotation = Mathf.Clamp(activeTerrainRotation, -2, 5);
		brushSize = Mathf.Clamp(brushSize, 0, 6);
		SelectTool(EditorTool.Terrain);
	}

	void OnGUI()
	{
		if (IsHFModalOpen())
		{
			return;
		}

		Rect panel = GetHFPanelRect();
		GUILayout.BeginArea(panel, GUI.skin.box);
		DrawHFHeader();

		int category = GetToolCategory(activeTool);
		int selectedCategory = GUILayout.Toolbar(
			category, hfToolCategoryNames, GUILayout.Height(24f));
		if (selectedCategory != category)
		{
			SelectToolCategory(selectedCategory);
		}

		DrawPaintOperationControl();

		if (activeTool == EditorTool.Forest)
		{
			DrawForestControls();
		}
		else if (activeTool == EditorTool.Terrain)
		{
			DrawTerrainControls();
		}
		else
		{
			DrawActiveToolControl();
		}

		GUILayout.Label(GetActiveToolHint(), GUILayout.Height(18f));
		GUILayout.Label(GetHoverDescription(), GUILayout.Height(18f));
		GUILayout.EndArea();
	}

	void DrawHFHeader()
	{
		GUILayout.BeginHorizontal();
		string surface = hexGrid.SurfaceSampler.UsesHFOriginalSurface ?
			"HF ORIGINAL" : "LEGACY COMPATIBILITY";
		GUILayout.Label(
			$"HF MAP EDITOR  |  {surface}", GUILayout.MinWidth(250f));
		GUILayout.FlexibleSpace();

		GUI.enabled = CanPickHoveredTool();
		if (GUILayout.Button("Pick", GUILayout.Width(52f)))
		{
			PickHoveredToolSettings();
		}
		GUI.enabled = CanUndoEdit();
		if (GUILayout.Button("Undo", GUILayout.Width(50f)))
		{
			UndoEdit();
		}
		GUI.enabled = CanRedoEdit();
		if (GUILayout.Button("Redo", GUILayout.Width(50f)))
		{
			RedoEdit();
		}

		GUI.enabled = saveLoadMenu;
		if (GUILayout.Button("Save", GUILayout.Width(58f)))
		{
			saveLoadMenu.Open(true);
		}
		if (GUILayout.Button("Load", GUILayout.Width(58f)))
		{
			saveLoadMenu.Open(false);
		}
		GUI.enabled = newMapMenu;
		if (GUILayout.Button("New Map", GUILayout.Width(76f)))
		{
			newMapMenu.Open();
		}
		GUI.enabled = true;

		bool showGrid = GUILayout.Toggle(
			gridVisible, "Grid", GUI.skin.button, GUILayout.Width(58f));
		if (showGrid != gridVisible)
		{
			ShowGrid(showGrid);
		}
		bool editing = GUILayout.Toggle(
			editMode, "Edit", GUI.skin.button, GUILayout.Width(58f));
		if (editing != editMode)
		{
			SetEditMode(editing);
		}
		GUILayout.EndHorizontal();
	}

	void DrawPaintOperationControl()
	{
		GUILayout.BeginHorizontal();
		if (IsPathTool(activeTool))
		{
			GUILayout.Label(
				"Operation: edge path  |  Width: 1  |  Drag across adjacent cells",
				GUILayout.Height(22f));
			GUILayout.EndHorizontal();
			return;
		}

		GUILayout.Label("Operation", GUILayout.Width(68f));
		paintOperation = (PaintOperation)GUILayout.Toolbar(
			(int)paintOperation, paintOperationNames,
			GUILayout.Width(246f), GUILayout.Height(22f));
		GUILayout.Space(10f);
		if (paintOperation == PaintOperation.Brush)
		{
			GUILayout.Label($"Radius: {brushSize}", GUILayout.Width(70f));
			int roundedSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(
				brushSize, 0f, 6f, GUILayout.Width(180f)));
			if (roundedSize != brushSize)
			{
				SetBrushSize(roundedSize);
			}
		}
		else
		{
			GUILayout.Label(paintOperation == PaintOperation.Fill ?
				"Connected area matching the clicked cell" :
				"Every matching cell in the map");
		}
		GUILayout.EndHorizontal();
	}

	void DrawTerrainControls()
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label("Material", GUILayout.Width(68f));
		int terrain = GUILayout.Toolbar(
			activeTerrainTypeIndex, hfTerrainNames, GUILayout.Height(22f));
		if (terrain != activeTerrainTypeIndex)
		{
			activeTerrainTypeIndex = terrain;
		}
		GUILayout.EndHorizontal();

		GUILayout.BeginHorizontal();
		GUILayout.Label("HF Rotation", GUILayout.Width(68f));
		int rotation = GUILayout.Toolbar(
			activeTerrainRotation + 2, hfTerrainRotationNames, GUILayout.Height(22f));
		activeTerrainRotation = rotation - 2;
		GUILayout.EndHorizontal();
	}

	void DrawForestControls()
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label("Species", GUILayout.Width(52f));
		int species = GUILayout.Toolbar(
			(int)activeVegetation, hfVegetationNames, GUILayout.Height(22f));
		activeVegetation = (HexVegetation)species;
		GUILayout.EndHorizontal();

		GUILayout.BeginHorizontal();
		GUILayout.Label(
			$"Density: {activeVegetationDensity}%", GUILayout.Width(112f));
		int density = Mathf.RoundToInt(GUILayout.HorizontalSlider(
			activeVegetationDensity, 0f, 100f, GUILayout.Width(260f)));
		if (density != activeVegetationDensity)
		{
			activeVegetationDensity = density;
		}
		GUILayout.Space(8f);
		if (GUILayout.Button("Clear", GUILayout.Width(64f)))
		{
			activeVegetationDensity = 0;
		}
		if (GUILayout.Button("Light", GUILayout.Width(64f)))
		{
			activeVegetationDensity = 25;
		}
		if (GUILayout.Button("Medium", GUILayout.Width(72f)))
		{
			activeVegetationDensity = 55;
		}
		if (GUILayout.Button("Dense", GUILayout.Width(64f)))
		{
			activeVegetationDensity = 100;
		}
		GUILayout.EndHorizontal();

		GUILayout.BeginHorizontal();
		GUILayout.Label("Theme", GUILayout.Width(52f));
		int tint = GUILayout.Toolbar(
			(int)activeVegetationTint, hfVegetationTintNames, GUILayout.Height(22f));
		activeVegetationTint = (HexVegetationTint)tint;
		GUILayout.EndHorizontal();
	}

	void DrawActiveToolControl()
	{
		int selection;
		GUILayout.BeginHorizontal();
		switch (GetToolCategory(activeTool))
		{
			case 0:
				selection = GUILayout.Toolbar(
					activeTerrainTypeIndex, hfTerrainNames, GUILayout.Height(22f));
				if (selection != activeTerrainTypeIndex)
				{
					activeTerrainTypeIndex = selection;
				}
				break;
			case 1:
				selection = GUILayout.Toolbar(
					(int)activeLandform, hfReliefNames, GUILayout.Height(22f));
				if (selection != (int)activeLandform)
				{
					activeLandform = (HexLandform)selection;
				}
				break;
			case 2:
				// Forest has dedicated species, density, and theme controls.
				break;
			case 3:
				selection = GUILayout.Toolbar(
					paintSea ? 1 : 0, landSeaNames, GUILayout.Height(22f));
				paintSea = selection == 1;
				break;
			case 4:
				selection = GUILayout.Toolbar(
					activeTool == EditorTool.RoadErase ? 1 : 0,
					pathActionNames, GUILayout.Height(22f));
				EditorTool roadTool = selection == 0 ?
					EditorTool.RoadDraw : EditorTool.RoadErase;
				if (roadTool != activeTool)
				{
					SelectTool(roadTool);
				}
				break;
			default:
				selection = GUILayout.Toolbar(
					activeTool == EditorTool.RiverErase ? 1 : 0,
					pathActionNames, GUILayout.Height(22f));
				EditorTool riverTool = selection == 0 ?
					EditorTool.RiverDraw : EditorTool.RiverErase;
				if (riverTool != activeTool)
				{
					SelectTool(riverTool);
				}
				break;
		}
		GUILayout.EndHorizontal();
	}

	string GetActiveToolHint() => activeTool switch
	{
		EditorTool.Terrain =>
			"Terrain changes the HF material only; relief and vegetation stay intact.",
		EditorTool.Relief =>
			"Relief paints Flat / Hill / Mountain without changing the terrain material.",
		EditorTool.Forest =>
			"Forest changes only species and density; the selected ground and relief stay intact.",
		EditorTool.Water =>
			"Sea paints land or water ownership while preserving terrain and relief.",
		EditorTool.RoadDraw =>
			"Drag to draw an HF road; skipped hexes are connected automatically.",
		EditorTool.RoadErase =>
			"Drag across road edges to erase them; click a cell to clear its road junction.",
		EditorTool.RiverDraw =>
			"Drag downstream to draw an HF river; skipped hexes are connected automatically.",
		EditorTool.RiverErase =>
			"Drag across river edges to erase them; click a cell to clear its river.",
		_ => "Legacy Catlike compatibility brush."
	};

	string GetHoverDescription()
	{
		if (hoveredCellIndex < 0 ||
			hoveredCellIndex >= hexGrid.CellData.Length)
		{
			return editMode ? "Hover: outside map" : "Editing paused";
		}

		HexCellData data = hexGrid.CellData[hoveredCellIndex];
		int terrain = Mathf.Clamp(
			data.TerrainTypeIndex, 0, hfTerrainNames.Length - 1);
		string routes = data.HasRoads && data.HasRiver ? "Road + River" :
			data.HasRoads ? "Road" : data.HasRiver ? "River" : "No route";
		int vegetation = Mathf.Clamp(
			(int)data.vegetation, 0, hfVegetationNames.Length - 1);
		string forest = data.VegetationDensity > 0 ?
			$"{hfVegetationNames[vegetation]} {data.VegetationDensity}% " +
			$"{hfVegetationTintNames[Mathf.Clamp((int)data.vegetationTint, 0, hfVegetationTintNames.Length - 1)]}" :
			"Clear";
		return $"Hover {data.coordinates}  |  {hfTerrainNames[terrain]}  |  " +
			$"R{data.TerrainRotation * 60} deg  |  {data.landform}  |  {forest}  |  " +
			$"{(data.IsUnderwater ? "Sea" : "Land")}  |  {routes}";
	}

	bool CanPickHoveredTool() =>
		hoveredCellIndex >= 0 && hoveredCellIndex < hexGrid.CellData.Length &&
		!IsPathTool(activeTool);

	void PickHoveredToolSettings()
	{
		if (!CanPickHoveredTool())
		{
			return;
		}

		HexCellData data = hexGrid.CellData[hoveredCellIndex];
		switch (activeTool)
		{
			case EditorTool.Terrain:
				activeTerrainTypeIndex = Mathf.Clamp(
					data.TerrainTypeIndex, 0, hfTerrainNames.Length - 1);
				activeTerrainRotation = data.TerrainRotation;
				break;
			case EditorTool.Relief:
				activeLandform = data.landform;
				break;
			case EditorTool.Forest:
				activeVegetation = data.vegetation;
				activeVegetationTint = data.vegetationTint;
				activeVegetationDensity = data.VegetationDensity;
				break;
			case EditorTool.Water:
				paintSea = data.IsUnderwater;
				break;
		}
	}

	void SelectToolCategory(int category)
	{
		SelectTool(category switch
		{
			0 => EditorTool.Terrain,
			1 => EditorTool.Relief,
			2 => EditorTool.Forest,
			3 => EditorTool.Water,
			4 => EditorTool.RoadDraw,
			_ => EditorTool.RiverDraw
		});
	}

	static int GetToolCategory(EditorTool tool) => tool switch
	{
		EditorTool.Relief => 1,
		EditorTool.Forest => 2,
		EditorTool.Water => 3,
		EditorTool.RoadDraw or EditorTool.RoadErase => 4,
		EditorTool.RiverDraw or EditorTool.RiverErase => 5,
		_ => 0
	};

	Rect GetHFPanelRect()
	{
		float width = Mathf.Min(
			hfPanelMaximumWidth, Mathf.Max(320f, Screen.width - hfPanelMargin * 2f));
		float height = activeTool == EditorTool.Forest ? hfForestPanelHeight :
			activeTool == EditorTool.Terrain ? hfTerrainPanelHeight : hfPanelHeight;
		return new Rect(
			(Screen.width - width) * 0.5f, hfPanelMargin,
			width, height);
	}

	bool IsPointerOverEditorPanel()
	{
		Vector2 guiPosition = new(
			Input.mousePosition.x, Screen.height - Input.mousePosition.y);
		return GetHFPanelRect().Contains(guiPosition);
	}

	bool IsHFModalOpen() =>
		(saveLoadMenu && saveLoadMenu.gameObject.activeInHierarchy) ||
		(newMapMenu && newMapMenu.gameObject.activeInHierarchy);
}
