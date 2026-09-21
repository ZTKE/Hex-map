using System.Collections;
using UnityEngine;

/// <summary>
/// Deterministic, disposable terrain study. The saved scene keeps its grid
/// inactive so DefaultWorldMapBootstrap cannot load or alter the world map.
/// </summary>
[DefaultExecutionOrder(500)]
public sealed class HexNearTerrainShowcase : MonoBehaviour
{
	public const string SceneName = "Civ6 Near Terrain Showcase";
	public HexGrid grid;
	public Camera previewCamera;
	public HexTerrainStyle terrainStyle;
	public bool enableCameraControls = true;
	public bool Ready { get; private set; }
	public int GeneratedCellCount => grid && grid.CellData != null ? grid.CellData.Length : 0;
	public float ViewDistance => distance;
	public Vector3 ViewFocus => focus;

	const int Width = 30, Height = 25;
	/// <summary>Separated fixtures keep ridge topology reproducible for visual and numeric review.</summary>
	public sealed class MountainRangeStudyCase
	{
		public readonly string name;
		public readonly Vector2Int[] cells;
		public MountainRangeStudyCase(string name, params Vector2Int[] cells)
		{ this.name = name; this.cells = cells; }
	}
	public static readonly MountainRangeStudyCase[] MountainRangeStudyCases = {
		new("desert-chain", new(2,18), new(3,18), new(3,19), new(4,19), new(4,20)),
		new("bent-alpine-chain", new(7,18), new(8,18), new(8,19), new(9,19), new(10,19), new(11,19), new(11,20)),
		new("three-cell-junction", new(16,18), new(17,18), new(16,19)),
		new("isolated", new Vector2Int(14,23)),
		new("adjacent-pair", new(18,23), new(19,23)),
		new("dense-snow-cluster", new(25,18), new(25,19), new(26,18), new(25,17), new(24,17), new(24,18), new(24,19))
	};
	// Separate from the six original mountain fixtures. Its western interior
	// remains dry and broad; the eastern river crosses its raised surface.
	public static readonly Vector2Int[] PlateauStudyCells = {
		new(18,8), new(19,8), new(20,8), new(21,8), new(22,8),
		new(18,9), new(19,9), new(20,9), new(21,9), new(22,9),
		new(18,10), new(19,10), new(20,10), new(21,10), new(22,10),
		new(18,11), new(19,11), new(20,11), new(21,11), new(22,11)
	};
	public static readonly Vector2Int PlateauInteriorCell = new(19,10);
	Vector3 focus;
	float distance = 390f, yaw = -12f, pitch = 51f;
	int previousWaterLevel;
	bool previousEditMode;
	bool initialized;

	IEnumerator Start()
	{
		if (!grid || !previewCamera)
		{
			Debug.LogError("Near terrain showcase is missing its grid or camera.", this);
			yield break;
		}
		initialized = true;
		previousWaterLevel = HexMetrics.visualWaterLevel;
		previousEditMode = Shader.IsKeywordEnabled("_HEX_MAP_EDIT_MODE");
		Shader.DisableKeyword("_HEX_MAP_EDIT_MODE");
		HexNearTerrainLighting.Apply(previewCamera, terrainStyle && terrainStyle.UsesNearTerrain);
		HexMetrics.visualWaterLevel = 3;
		grid.gameObject.SetActive(true);
		if (!grid.CreateMap(Width, Height, false)) yield break;
		if (terrainStyle) grid.ConfigureSurface(terrainStyle);

		for (int z = 0; z < Height; z++)
		{
			for (int x = 0; x < Width; x++)
			{
				int index = z * Width + x;
				HexCellData cell = grid.CellData[index];
				int biome = Mathf.Clamp(x / 6, 0, 4);
				// Desert, grassland, plains, tundra, snow. Each has a flat,
				// wooded, rolling-hill and contiguous ridge sample.
				bool ocean = z < 3 + (x / 7 % 2) || x == 0 || x == Width - 1;
				bool lake = (x - 12) * (x - 12) + (z - 9) * (z - 9) <= 5 ||
					(x == 22 || x == 23) && z == 7 || x == 23 && z == 8;
				bool water = ocean || lake;
				cell.values = cell.values.WithElevation(water ? 2 : 3)
					.WithWaterLevel(3).WithTerrainTypeIndex(biome)
					.WithPlantLevel(0).WithUrbanLevel(0).WithFarmLevel(0)
					.WithSpecialIndex(0);
				// Match gameplay with fog disabled and untouched exploration flags.
				// Editor visibility or pre-explored cells would hide a foliage/road regression.
				cell.flags = HexFlags.Explorable;
				cell.countryId = 0;
				cell.hfRiverEdges = 0;
				cell.terrainRotation = (byte)((x * 7 + z * 3) % 6);
				cell.landform = !water && (IsMountainStudyCell(x, z) || x == 23 && (z == 10 || z == 11)) ?
					HexLandform.Mountain : !water && IsPlateauStudyCell(x, z) ?
					HexLandform.Plateau : !water && z >= 12 && z < 17 ?
					HexLandform.Hill : HexLandform.Flat;
				cell.mountainMode = cell.landform != HexLandform.Mountain ? HexMountainMode.Automatic :
					x >= 18 ? HexMountainMode.Massif : HexMountainMode.Range;
				cell.vegetation = biome == 0 ? HexVegetation.Deadwood :
					biome >= 3 ? HexVegetation.Conifer :
					biome == 1 ? HexVegetation.Broadleaf : HexVegetation.Mixed;
				cell.vegetationTint = biome == 0 ? HexVegetationTint.Dry :
					biome >= 3 ? HexVegetationTint.Frost : HexVegetationTint.Natural;
				int density = !water && z >= 6 && z <= 14 ?
					(biome == 0 ? 0 : biome == 4 ? 24 : 35 + (x + z) % 3 * 25) : 0;
				cell.vegetationDensity = (byte)density;
				cell.values = cell.values.WithPlantLevel(density == 0 ? 0 :
					Mathf.Clamp(Mathf.CeilToInt(density * 0.03f), 1, 3));
				grid.CellData[index] = cell;
			}
		}

		// A chain of hex sides, shared on both cells, reaches the coast.
		for (int z = 3; z < 17; z++)
		{
			HexCell riverCell = new(8 + z * Width, grid);
			riverCell.SetHFRiverEdge(HexDirection.E);
			// Odd rows are shifted east by half a cell. Their upper and lower
			// right sides bridge the east edges of the even rows above and below.
			if ((z & 1) != 0)
			{
				riverCell.SetHFRiverEdge(HexDirection.NE);
				riverCell.SetHFRiverEdge(HexDirection.SE);
			}
		}
		for (int x = 3; x < 26; x++)
		{
			HexCell roadCell = new(x + 5 * Width, grid);
			roadCell.AddRoad(HexDirection.E);
		}
		for (int z = 7; z <= 12; z++)
		{
			HexCell riverCell = new(20 + z * Width, grid);
			riverCell.SetHFRiverEdge(HexDirection.E);
			if ((z & 1) != 0)
			{
				riverCell.SetHFRiverEdge(HexDirection.NE);
				riverCell.SetHFRiverEdge(HexDirection.SE);
			}
		}
		grid.RefreshWaterDepths();
		grid.RefreshAllCells();
		grid.SetFogOfWarEnabled(false);
		grid.SetGridVisible(false);
		grid.ShowUI(false);
		grid.RefreshAllChunks();
		focus = new Vector3(Width * HexMetrics.innerDiameter * 0.5f,
			HexMetrics.visualWaterLevel * HexMetrics.elevationStep,
			(Height - 1) * HexMetrics.outerRadius * 0.75f);
		UpdateCamera();
		// Allow chunk LateUpdate, GPU data uploads and the pipeline swap to finish.
		yield return null;
		yield return null;
		Ready = true;
		Debug.Log($"Near terrain showcase ready: {Width}x{Height}, " +
			"five biomes, forest, hills, ridgeline, broad plateau, shore, lake, road and river.", this);
	}

	static bool IsPlateauStudyCell(int x, int z)
	{
		foreach (Vector2Int cell in PlateauStudyCells)
			if (cell.x == x && cell.y == z) return true;
		return false;
	}

	static bool IsMountainStudyCell(int x, int z)
	{
		if (z < 17) return false;
		foreach (MountainRangeStudyCase study in MountainRangeStudyCases)
			foreach (Vector2Int cell in study.cells)
				if (cell.x == x && cell.y == z) return true;
		return false;
	}

	void Update()
	{
		if (!Ready || !enableCameraControls) return;
		Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
		Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
		float speed = distance * Time.unscaledDeltaTime * 0.45f;
		if (Input.GetKey(KeyCode.W)) focus += forward * speed;
		if (Input.GetKey(KeyCode.S)) focus -= forward * speed;
		if (Input.GetKey(KeyCode.A)) focus -= right * speed;
		if (Input.GetKey(KeyCode.D)) focus += right * speed;
		distance = Mathf.Clamp(distance * Mathf.Exp(-Input.mouseScrollDelta.y * 0.08f), 70f, 700f);
		if (Input.GetMouseButton(1))
		{
			yaw += Input.GetAxis("Mouse X") * 2f;
			pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 1.5f, 28f, 80f);
		}
		UpdateCamera();
	}

	void UpdateCamera()
	{
		Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
		previewCamera.transform.SetPositionAndRotation(focus - rotation * Vector3.forward * distance, rotation);
	}

	/// <summary>Fixed review shots; no arbitrary coordinates enter the editor bridge.</summary>
	public void SetView(string view)
	{
		if (!Ready) throw new System.InvalidOperationException("The showcase is not ready.");
		int x, z;
		switch (view)
		{
			case "all":
				focus = new Vector3(Width * HexMetrics.innerDiameter * 0.5f,
					HexMetrics.visualWaterLevel * HexMetrics.elevationStep,
					(Height - 1) * HexMetrics.outerRadius * 0.75f);
				distance = 390f; yaw = -12f; pitch = 51f;
				UpdateCamera(); return;
			case "forest": x = 9; z = 9; distance = 95f; yaw = -18f; pitch = 48f; break;
			case "mountains": x = 9; z = 18; distance = 125f; yaw = -20f; pitch = 43f; break;
			case "desert": x = 3; z = 15; distance = 112f; yaw = -12f; pitch = 45f; break;
			case "snow": x = 25; z = 18; distance = 100f; yaw = -12f; pitch = 45f; break;
			case "plateau": x = 20; z = 10; distance = 130f; yaw = -18f; pitch = 43f; break;
			case "coast": x = 9; z = 4; distance = 115f; yaw = -12f; pitch = 52f; break;
			default: throw new System.ArgumentException("Allowed views: all, forest, mountains, desert, snow, plateau, coast.", nameof(view));
		}
		focus = grid.GetSurfacePosition(x + z * Width);
		UpdateCamera();
	}

	void OnDestroy()
	{
		if (!initialized) return;
		HexNearTerrainLighting.Apply(previewCamera, false);
		HexMetrics.visualWaterLevel = previousWaterLevel;
		if (previousEditMode) Shader.EnableKeyword("_HEX_MAP_EDIT_MODE");
		else Shader.DisableKeyword("_HEX_MAP_EDIT_MODE");
	}
}
