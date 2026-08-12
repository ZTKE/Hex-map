using UnityEngine;
using UnityEngine.UI;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// Component that represents an entire hexagon map.
/// </summary>
public class HexGrid : MonoBehaviour
{
	/// <summary>
	/// Upper bound used by the runtime map creator. One million logical cells
	/// stays practical on desktop while preventing accidental multi-gigabyte
	/// allocations from malformed input or save files.
	/// </summary>
	public const int MaxSupportedCellCount = 1_000_000;

	/// <summary>
	/// Maps above this size use the pooled, camera-local render window.
	/// </summary>
	public const int ChunkStreamingCellThreshold = 10_000;

	// Detailed high-altitude testing deliberately keeps real chunks visible at
	// every zoom level. These floors also apply when Unity has restored an older
	// in-memory scene backup whose serialized streaming values predate the mode.
	const int detailedWorldMaximumStreamingRadius = 36;
	const int detailedWorldActivationBudget = 8;

	/// <summary>
	/// Raised after a new map has been created or a saved map has finished
	/// loading. Editor-only transient state must not cross this boundary.
	/// </summary>
	public event System.Action MapReset;

	[SerializeField]
	Text cellLabelPrefab;

	[SerializeField]
	HexGridChunk chunkPrefab;

	[SerializeField]
	HexUnit unitPrefab;

	[SerializeField]
	Texture2D noiseSource;
	 
	[SerializeField]
	int seed;

	/// <summary>
	/// Amount of cells in the X dimension.
	/// </summary>
	public int CellCountX
	{ get; private set; }

	/// <summary>
	/// Amount of cells in the Z dimension.
	/// </summary>
	public int CellCountZ
	{ get; private set; }

	/// <summary>
	/// Whether there currently exists a path that should be displayed.
	/// </summary>
	public bool HasPath => currentPathExists;

	/// <summary>
	/// Whether east-west wrapping is enabled.
	/// </summary>
	public bool Wrapping
	{ get; private set; }

	Transform[] columns;

	HexGridChunk[] chunks;

	readonly Stack<HexGridChunk> chunkPool = new();
	readonly HashSet<int> activeChunkIndices = new();
	readonly HashSet<int> desiredChunkIndices = new();
	readonly List<int> chunkChangeBuffer = new();
	readonly List<int> pendingChunkActivations = new();
	readonly List<int> pendingInteractionActivations = new();

	[SerializeField, Min(1)]
	int streamingChunkRadius = 7;

	[SerializeField, Min(1)]
	int maximumStreamingChunkRadius = 8;

	[SerializeField, Min(0)]
	int streamingColliderRadius = 2;

	[SerializeField, Min(1)]
	int chunkActivationsPerFrame = 2;

	bool usesChunkStreaming;
	bool gridUIVisible = true;
	bool overviewMode;
	bool overviewDirty = true;
	int pendingActivationCursor;
	int pendingInteractionCursor;
	int visibleCenterChunkX = int.MinValue;
	int visibleCenterChunkZ = int.MinValue;
	int visibleChunkRadiusX = -1;
	int visibleChunkRadiusZ = -1;

	/// <summary>
	/// Bundled cell data.
	/// </summary>
	public HexCellData[] CellData
	{ get; private set; }

	/// <summary>
	/// Separate cell positions.
	/// </summary>
	public Vector3[] CellPositions
	{ get; private set; }

	public HexUnit[] CellUnits
	{ get; private set; }

	HexCellSearchData[] searchData;

	/// <summary>
	/// Search data array usable for current map.
	/// </summary>
	public HexCellSearchData[] SearchData => searchData;

	int[] cellVisibility;

	readonly Dictionary<int, string> cellLabelTexts = new();
	readonly Dictionary<int, Color> cellHighlights = new();

	/// <summary>
	/// The <see cref="HexCellShaderData"/> container
	/// for cell visualization data.
	/// </summary>
	public HexCellShaderData ShaderData => cellShaderData;

	/// <summary>
	/// Shared CPU view of the shader-displaced HF surface. It is configured by
	/// the chunk style and is the sole height source for physics and objects in
	/// HF Original mode.
	/// </summary>
	public HexSurfaceSampler SurfaceSampler =>
		surfaceSampler ??= new HexSurfaceSampler(this);

	int chunkCountX, chunkCountZ;

	HexCellPriorityQueue searchFrontier;

	int searchFrontierPhase;

	int currentPathFromIndex = -1, currentPathToIndex = -1;
	bool currentPathExists;

	int currentCenterColumnIndex = -1;

	/// <summary>
	/// Whether this map is only instantiating render chunks around the camera.
	/// </summary>
	public bool UsesChunkStreaming => usesChunkStreaming;

	/// <summary>
	/// Number of render chunks that currently exist in the scene.
	/// </summary>
	public int ActiveChunkCount => activeChunkIndices.Count;

	/// <summary>
	/// Whether the streamed map is currently using only its cheap overview.
	/// </summary>
	public bool IsOverviewMode => overviewMode;

	public int PoliticalBorderSegmentCount => overview ?
		overview.PoliticalBorderSegmentCount : 0;

	/// <summary>
	/// Whether cells on this map carry political ownership and a color palette.
	/// </summary>
	public bool HasPoliticalData => countryPalette != null &&
		countryPalette.Length > 1;

	/// <summary>
	/// Install the ID-indexed political palette used by the overview. Cell IDs
	/// remain authoritative; the palette is presentation data.
	/// </summary>
	public void SetCountryPalette(Color32[] palette)
	{
		countryPalette = palette != null && palette.Length > 1 ?
			(Color32[])palette.Clone() : null;
		overviewDirty = true;
	}

	public bool TryGetCountryColor(ushort countryId, out Color32 color)
	{
		if (countryId != 0 && countryPalette != null &&
			countryId < countryPalette.Length)
		{
			color = countryPalette[countryId];
			return color.a != 0;
		}
		color = default;
		return false;
	}

	public ushort GetCountryId(int cellIndex) =>
		CellData[cellIndex].CountryId;

	/// <summary>
	/// Change one cell's political owner. The high-altitude texture and border
	/// contours are rebuilt the next time overview data is requested.
	/// </summary>
	public bool SetCountryId(int cellIndex, ushort countryId)
	{
		if (CellData == null || cellIndex < 0 || cellIndex >= CellData.Length ||
			(countryId != 0 && (countryPalette == null ||
				countryId >= countryPalette.Length ||
				countryPalette[countryId].a == 0)))
		{
			return false;
		}
		HexCellData data = CellData[cellIndex];
		if (data.IsUnderwater && countryId != 0)
		{
			return false;
		}
		if (data.countryId != countryId)
		{
			data.countryId = countryId;
			CellData[cellIndex] = data;
			overviewDirty = true;
		}
		return true;
	}

	public void ClearPoliticalData()
	{
		countryPalette = null;
		overviewDirty = true;
	}

#pragma warning disable IDE0044 // Add readonly modifier
	List<HexUnit> units = new();
#pragma warning restore IDE0044 // Add readonly modifier

	HexCellShaderData cellShaderData;
	HexSurfaceSampler surfaceSampler;
	HexMapOverview overview;
	Color32[] countryPalette;

	void Awake()
	{
		CellCountX = 20;
		CellCountZ = 15;
		HexMetrics.noiseSource = noiseSource;
		HexMetrics.InitializeHashGrid(seed);
		HexUnit.unitPrefab = unitPrefab;
		surfaceSampler = new HexSurfaceSampler(this);
		cellShaderData = gameObject.AddComponent<HexCellShaderData>();
		cellShaderData.Grid = this;
		overview = GetComponent<HexMapOverview>();
		if (!overview)
		{
			overview = gameObject.AddComponent<HexMapOverview>();
		}
		CreateMap(CellCountX, CellCountZ, Wrapping);
	}

	/// <summary>
	/// Add a unit to the map.
	/// </summary>
	/// <param name="unit">Unit to add.</param>
	/// <param name="location">Cell in which to place the unit.</param>
	/// <param name="orientation">Orientation of the unit.</param>
	public void AddUnit(HexUnit unit, HexCell location, float orientation)
	{
		units.Add(unit);
		unit.Grid = this;
		unit.Location = location;
		unit.Orientation = orientation;
	}

	/// <summary>
	/// Remove a unit from the map.
	/// </summary>
	/// <param name="unit">The unit to remove.</param>
	public void RemoveUnit(HexUnit unit)
	{
		units.Remove(unit);
		unit.Die();
	}

	/// <summary>
	/// Make a game object a child of a map column.
	/// </summary>
	/// <param name="child"><see cref="Transform"/>
	/// of the child game object.</param>
	/// <param name="columnIndex">Index of the parent column.</param>
	public void MakeChildOfColumn(Transform child, int columnIndex) =>
		child.SetParent(columns[columnIndex], false);

	/// <summary>
	/// Create a new map.
	/// </summary>
	/// <param name="x">X size of the map.</param>
	/// <param name="z">Z size of the map.</param>
	/// <param name="wrapping">Whether the map wraps east-west.</param>
	/// <returns>Whether the map was successfully created.</returns>
	public bool CreateMap(int x, int z, bool wrapping)
	{
		if (!TryValidateMapSize(x, z, out string validationMessage))
		{
			Debug.LogError(validationMessage);
			return false;
		}

		ClearPath();
		ClearUnits();
		if (overview)
		{
			overview.SetVisible(false);
		}
		ClearPoliticalData();
		overviewMode = false;
		overviewDirty = true;
		InvalidateStreamingWindow();
		if (columns != null)
		{
			for (int i = 0; i < columns.Length; i++)
			{
				Destroy(columns[i].gameObject);
			}
		}
		while (chunkPool.Count > 0)
		{
			HexGridChunk pooledChunk = chunkPool.Pop();
			if (pooledChunk)
			{
				Destroy(pooledChunk.gameObject);
			}
		}
		activeChunkIndices.Clear();
		desiredChunkIndices.Clear();
		chunkChangeBuffer.Clear();
		pendingChunkActivations.Clear();
		pendingActivationCursor = 0;
		pendingInteractionActivations.Clear();
		pendingInteractionCursor = 0;
		// CreateCells updates logical positions before the replacement chunk array
		// exists. Clear stale references from the previous map so UI lookup cannot
		// index the old array with the new chunk dimensions.
		chunks = null;
		columns = null;

		CellCountX = x;
		CellCountZ = z;
		Wrapping = wrapping;
		currentCenterColumnIndex = -1;
		HexMetrics.wrapSize = wrapping ? CellCountX : 0;
		chunkCountX = Mathf.CeilToInt(
			CellCountX / (float)HexMetrics.chunkSizeX);
		chunkCountZ = Mathf.CeilToInt(
			CellCountZ / (float)HexMetrics.chunkSizeZ);
		usesChunkStreaming = (long)CellCountX * CellCountZ >
			ChunkStreamingCellThreshold;
		cellShaderData.Initialize(CellCountX, CellCountZ);
		CreateCells();
		CreateChunks();
		MapReset?.Invoke();
		return true;
	}

	/// <summary>
	/// Validate a requested logical map size before allocating its data.
	/// </summary>
	public static bool TryValidateMapSize(
		int x, int z, out string validationMessage)
	{
		if (x <= 0 || z <= 0)
		{
			validationMessage = "Map width and height must both be positive.";
			return false;
		}

		long cellCount = (long)x * z;
		if (cellCount > MaxSupportedCellCount)
		{
			validationMessage =
				$"Map contains {cellCount:N0} cells; the supported maximum is " +
				$"{MaxSupportedCellCount:N0}.";
			return false;
		}

		int maximumTextureSize = SystemInfo.maxTextureSize;
		if (maximumTextureSize > 0 &&
			(x > maximumTextureSize || z > maximumTextureSize))
		{
			validationMessage =
				$"This device supports at most {maximumTextureSize:N0} cells " +
				"along either map dimension.";
			return false;
		}

		validationMessage = null;
		return true;
	}

	void CreateChunks()
	{
		columns = new Transform[chunkCountX];
		for (int x = 0; x < chunkCountX; x++)
		{
			columns[x] = new GameObject("Column").transform;
			columns[x].SetParent(transform, false);
		}

		chunks = new HexGridChunk[chunkCountX * chunkCountZ];
		if (!usesChunkStreaming)
		{
			for (int i = 0; i < chunks.Length; i++)
			{
				ActivateChunk(i);
			}
		}
	}

	void CreateCells()
	{
		CellData = new HexCellData[CellCountZ * CellCountX];
		CellPositions = new Vector3[CellData.Length];
		CellUnits = new HexUnit[CellData.Length];
		searchData = new HexCellSearchData[CellData.Length];
		cellVisibility = new int[CellData.Length];
		cellLabelTexts.Clear();
		cellHighlights.Clear();

		for (int z = 0, i = 0; z < CellCountZ; z++)
		{
			for (int x = 0; x < CellCountX; x++)
			{
				CreateCell(x, z, i++);
			}
		}
	}

	void ClearUnits()
	{
		for (int i = 0; i < units.Count; i++)
		{
			units[i].Die();
		}
		units.Clear();
	}

	void OnEnable()
	{
		if (!HexMetrics.noiseSource)
		{
			HexMetrics.noiseSource = noiseSource;
			HexMetrics.InitializeHashGrid(seed);
			HexUnit.unitPrefab = unitPrefab;
			HexMetrics.wrapSize = Wrapping ? CellCountX : 0;
			ResetVisibility();
		}
	}

	/// <summary>
	/// Get a cell given a <see cref="Ray"/>.
	/// </summary>
	/// <param name="ray"><see cref="Ray"/> used to perform a raycast.</param>
	/// <returns>The hit cell, if any.</returns>
	public HexCell GetCell(Ray ray)
	{
		if (Physics.Raycast(ray, out RaycastHit hit))
		{
			return GetCell(hit.point);
		}
		return default;
	}

	/// <summary>
	/// Get the cell that contains a position.
	/// </summary>
	/// <param name="position">Position to check.</param>
	/// <returns>The cell containing the position, if it exists.</returns>
	public HexCell GetCell(Vector3 position)
	{
		position = transform.InverseTransformPoint(position);
		HexCoordinates coordinates = HexCoordinates.FromPosition(position);
		return GetCell(coordinates);
	}

	/// <summary>
	/// Get the cell with specific <see cref="HexCoordinates"/>.
	/// </summary>
	/// <param name="coordinates"><see cref="HexCoordinates"/>
	/// of the cell.</param>
	/// <returns>The cell with the given coordinates, if it exists.</returns>
	public HexCell GetCell(HexCoordinates coordinates)
	{
		int z = coordinates.Z;
		int x = coordinates.X + z / 2;
		if (z < 0 || z >= CellCountZ || x < 0 || x >= CellCountX)
		{
			return default;
		}
		return new HexCell(x + z * CellCountX, this);
	}

	/// <summary>
	/// Try to get the cell with specific <see cref="HexCoordinates"/>.
	/// </summary>
	/// <param name="coordinates"><see cref="HexCoordinates"/>
	/// of the cell.</param>
	/// <param name="cell">The cell, if it exists.</param>
	/// <returns>Whether the cell exists.</returns>
	public bool TryGetCell(HexCoordinates coordinates, out HexCell cell)
	{
		int z = coordinates.Z;
		int x = coordinates.X + z / 2;
		if (z < 0 || z >= CellCountZ || x < 0 || x >= CellCountX)
		{
			cell = default;
			return false;
		}
		cell = new HexCell(x + z * CellCountX, this);
		return true;
	}

	/// <summary>
	/// Try to get the cell index for specific <see cref="HexCoordinates"/>.
	/// </summary>
	/// <param name="coordinates"><see cref="HexCoordinates"/>
	/// of the cell.</param>
	/// <param name="cell">The cell index, if it exists, otherwise -1.</param>
	/// <returns>Whether the cell index exists.</returns>
	public bool TryGetCellIndex(HexCoordinates coordinates, out int cellIndex)
	{
		int z = coordinates.Z;
		int x = coordinates.X + z / 2;
		if (z < 0 || z >= CellCountZ || x < 0 || x >= CellCountX)
		{
			cellIndex = -1;
			return false;
		}
		cellIndex = x + z * CellCountX;
		return true;
	}

	/// <summary>
	/// Get the cell index with specific offset coordinates.
	/// </summary>
	/// <param name="xOffset">X array offset coordinate.</param>
	/// <param name="zOffset">Z array offset coordinate.</param>
	/// <returns>Cell index.</returns>
	public int GetCellIndex(int xOffset, int zOffset) =>
		xOffset + zOffset * CellCountX;

	/// <summary>
	/// Get the cell with a specific index.
	/// </summary>
	/// <param name="cellIndex">Cell index, which should be valid.</param>
	/// <returns>The indicated cell.</returns>
	public HexCell GetCell(int cellIndex) => new(cellIndex, this);

	/// <summary>
	/// Check whether a cell is visibile.
	/// </summary>
	/// <param name="cellIndex">Index of the cell to check.</param>
	/// <returns>Whether the cell is visible.</returns>
	public bool IsCellVisible(int cellIndex) => cellVisibility[cellIndex] > 0;

	/// <summary>
	/// Control whether the map UI should be visible or hidden.
	/// </summary>
	/// <param name="visible">Whether the UI should be visibile.</param>
	public void ShowUI(bool visible)
	{
		gridUIVisible = visible;
		for (int i = 0; i < chunks.Length; i++)
		{
			if (chunks[i])
			{
				chunks[i].ShowUI(visible);
			}
		}
	}

	void Update()
	{
		if (usesChunkStreaming && !overviewMode)
		{
			ProcessPendingChunkActivations(Mathf.Max(
				chunkActivationsPerFrame, detailedWorldActivationBudget));
			ProcessPendingInteractionActivations(1);
		}
	}

	void CreateCell(int x, int z, int i)
	{
		Vector3 position;
		position.x = (x + z * 0.5f - z / 2) * HexMetrics.innerDiameter;
		position.y = 0f;
		position.z = z * (HexMetrics.outerRadius * 1.5f);

		var cell = new HexCell(i, this);
		CellPositions[i] = position;
		CellData[i].coordinates = HexCoordinates.FromOffsetCoordinates(x, z);
		CellData[i].terrainRotation = (byte)Mathf.Clamp(
			Mathf.FloorToInt(HexMetrics.SampleHashGrid(position).a * 6f), 0, 5);

		bool explorable = Wrapping ?
			z > 0 && z < CellCountZ - 1 :
			x > 0 && z > 0 && x < CellCountX - 1 && z < CellCountZ - 1;
		cell.Flags = explorable ?
			cell.Flags.With(HexFlags.Explorable) :
			cell.Flags.Without(HexFlags.Explorable);

		cell.Values = cell.Values.WithElevation(0);
		RefreshCellPosition(i);
	}

	/// <summary>
	/// Select high-altitude overview rendering or the camera-local detailed
	/// window. When overview is not requested it is fully hidden; empty space
	/// during activation is intentional because only real chunks may be shown.
	/// </summary>
	public void UpdateCameraView(
		Vector3 cameraWorldPosition, bool requestOverview,
		Vector2Int requestedChunkRadii = default)
	{
		if (!usesChunkStreaming)
		{
			if (overview)
			{
				overview.SetVisible(false);
			}
			return;
		}
		if (requestOverview && overviewMode && !overviewDirty)
		{
			return;
		}

		if (requestOverview)
		{
			EnsureOverview();
			overview.SetVisible(true);
			if (!overviewMode)
			{
				overviewMode = true;
				ReleaseAllActiveChunks();
			}
			return;
		}
		if (overview)
		{
			overview.SetVisible(false);
		}

		if (overviewMode)
		{
			overviewMode = false;
			InvalidateStreamingWindow();
			Vector3 localPosition =
				transform.InverseTransformPoint(cameraWorldPosition);
			// Columns are deliberately left untouched while the cheap overview is
			// active. Recenter them once when detailed rendering resumes.
			currentCenterColumnIndex = -1;
			CenterMap(localPosition.x);
		}
		UpdateVisibleChunks(cameraWorldPosition, requestedChunkRadii);
	}

	void EnsureOverview()
	{
		if (!overview)
		{
			overview = GetComponent<HexMapOverview>();
			if (!overview)
			{
				overview = gameObject.AddComponent<HexMapOverview>();
			}
		}
		if (!overviewDirty)
		{
			return;
		}
		overview.Rebuild(this);
		overviewDirty = false;
	}

	/// <summary>
	/// Keep only chunks near the camera instantiated for large maps. Logical cell
	/// data remains resident; render objects and cell labels are pooled.
	/// </summary>
	/// <param name="cameraWorldPosition">Camera rig position in world space.</param>
	public void UpdateVisibleChunks(
		Vector3 cameraWorldPosition, Vector2Int requestedChunkRadii = default)
	{
		if (!usesChunkStreaming || overviewMode || chunks == null ||
			chunks.Length == 0)
		{
			return;
		}

		Vector3 localPosition = transform.InverseTransformPoint(cameraWorldPosition);
		HexCoordinates coordinates = HexCoordinates.FromPosition(localPosition);
		int centerZ = coordinates.Z;
		int centerX = coordinates.X + centerZ / 2;
		int centerChunkX = Mathf.FloorToInt(
			centerX / (float)HexMetrics.chunkSizeX);
		int centerChunkZ = Mathf.FloorToInt(
			centerZ / (float)HexMetrics.chunkSizeZ);
		int renderRadiusX = requestedChunkRadii.x > 0 ?
			Mathf.Max(streamingChunkRadius, requestedChunkRadii.x) :
			streamingChunkRadius;
		int renderRadiusZ = requestedChunkRadii.y > 0 ?
			Mathf.Max(streamingChunkRadius, requestedChunkRadii.y) :
			streamingChunkRadius;
		int maximumRadius = Mathf.Max(
			detailedWorldMaximumStreamingRadius,
			maximumStreamingChunkRadius);
		renderRadiusX = Mathf.Clamp(renderRadiusX, 1, maximumRadius);
		renderRadiusZ = Mathf.Clamp(renderRadiusZ, 1, maximumRadius);

		if (centerChunkX == visibleCenterChunkX &&
			centerChunkZ == visibleCenterChunkZ &&
			renderRadiusX == visibleChunkRadiusX &&
			renderRadiusZ == visibleChunkRadiusZ)
		{
			return;
		}
		visibleCenterChunkX = centerChunkX;
		visibleCenterChunkZ = centerChunkZ;
		visibleChunkRadiusX = renderRadiusX;
		visibleChunkRadiusZ = renderRadiusZ;

		desiredChunkIndices.Clear();
		for (int zOffset = -renderRadiusZ;
			zOffset <= renderRadiusZ; zOffset++)
		{
			int z = centerChunkZ + zOffset;
			if (z < 0 || z >= chunkCountZ)
			{
				continue;
			}
			for (int xOffset = -renderRadiusX;
				xOffset <= renderRadiusX; xOffset++)
			{
				int x = centerChunkX + xOffset;
				if (Wrapping)
				{
					x %= chunkCountX;
					if (x < 0)
					{
						x += chunkCountX;
					}
				}
				else if (x < 0 || x >= chunkCountX)
				{
					continue;
				}
				desiredChunkIndices.Add(x + z * chunkCountX);
			}
		}

		chunkChangeBuffer.Clear();
		foreach (int chunkIndex in activeChunkIndices)
		{
			if (!desiredChunkIndices.Contains(chunkIndex))
			{
				chunkChangeBuffer.Add(chunkIndex);
			}
		}
		for (int i = 0; i < chunkChangeBuffer.Count; i++)
		{
			ReleaseChunk(chunkChangeBuffer[i]);
		}

		RebuildPendingChunkActivations();
		UpdateChunkInteractions();
		ProcessPendingChunkActivations(Mathf.Max(
			chunkActivationsPerFrame, detailedWorldActivationBudget));
		ProcessPendingInteractionActivations(1);
	}

	void RebuildPendingChunkActivations()
	{
		pendingChunkActivations.Clear();
		pendingActivationCursor = 0;
		foreach (int chunkIndex in desiredChunkIndices)
		{
			if (!chunks[chunkIndex])
			{
				pendingChunkActivations.Add(chunkIndex);
			}
		}
		pendingChunkActivations.Sort((a, b) =>
			GetChunkDistanceSquared(a).CompareTo(GetChunkDistanceSquared(b)));
	}

	int GetChunkDistanceSquared(int chunkIndex)
	{
		int x = chunkIndex % chunkCountX;
		int z = chunkIndex / chunkCountX;
		int xDistance = Mathf.Abs(x - visibleCenterChunkX);
		if (Wrapping)
		{
			xDistance = Mathf.Min(xDistance, chunkCountX - xDistance);
		}
		int zDistance = Mathf.Abs(z - visibleCenterChunkZ);
		return xDistance * xDistance + zDistance * zDistance;
	}

	bool ShouldChunkHaveInteraction(int chunkIndex)
	{
		if (streamingColliderRadius < 0)
		{
			return false;
		}
		int x = chunkIndex % chunkCountX;
		int z = chunkIndex / chunkCountX;
		int xDistance = Mathf.Abs(x - visibleCenterChunkX);
		if (Wrapping)
		{
			xDistance = Mathf.Min(xDistance, chunkCountX - xDistance);
		}
		return xDistance <= streamingColliderRadius &&
			Mathf.Abs(z - visibleCenterChunkZ) <= streamingColliderRadius;
	}

	void UpdateChunkInteractions()
	{
		pendingInteractionActivations.Clear();
		pendingInteractionCursor = 0;
		foreach (int chunkIndex in activeChunkIndices)
		{
			HexGridChunk chunk = chunks[chunkIndex];
			if (!chunk)
			{
				continue;
			}
			bool shouldInteract = ShouldChunkHaveInteraction(chunkIndex);
			if (!shouldInteract)
			{
				chunk.SetInteractionEnabled(false);
			}
			else if (!chunk.InteractionEnabled)
			{
				pendingInteractionActivations.Add(chunkIndex);
			}
		}
		pendingInteractionActivations.Sort((a, b) =>
			GetChunkDistanceSquared(a).CompareTo(GetChunkDistanceSquared(b)));
	}

	void ProcessPendingInteractionActivations(int budget)
	{
		budget = Mathf.Max(1, budget);
		while (budget-- > 0 &&
			pendingInteractionCursor < pendingInteractionActivations.Count)
		{
			int chunkIndex =
				pendingInteractionActivations[pendingInteractionCursor++];
			HexGridChunk chunk = chunks[chunkIndex];
			if (chunk && ShouldChunkHaveInteraction(chunkIndex))
			{
				chunk.SetInteractionEnabled(true);
			}
		}
		if (pendingInteractionCursor >= pendingInteractionActivations.Count)
		{
			pendingInteractionActivations.Clear();
			pendingInteractionCursor = 0;
		}
	}

	void ProcessPendingChunkActivations(int budget)
	{
		budget = Mathf.Max(1, budget);
		while (budget-- > 0 &&
			pendingActivationCursor < pendingChunkActivations.Count)
		{
			int chunkIndex =
				pendingChunkActivations[pendingActivationCursor++];
			if (desiredChunkIndices.Contains(chunkIndex) && !chunks[chunkIndex])
			{
				ActivateChunk(chunkIndex);
			}
		}
		if (pendingActivationCursor >= pendingChunkActivations.Count)
		{
			pendingChunkActivations.Clear();
			pendingActivationCursor = 0;
		}
	}

	void ReleaseAllActiveChunks()
	{
		chunkChangeBuffer.Clear();
		foreach (int chunkIndex in activeChunkIndices)
		{
			chunkChangeBuffer.Add(chunkIndex);
		}
		for (int i = 0; i < chunkChangeBuffer.Count; i++)
		{
			ReleaseChunk(chunkChangeBuffer[i]);
		}
		desiredChunkIndices.Clear();
		pendingChunkActivations.Clear();
		pendingActivationCursor = 0;
		pendingInteractionActivations.Clear();
		pendingInteractionCursor = 0;
		InvalidateStreamingWindow();
	}

	void InvalidateStreamingWindow()
	{
		visibleCenterChunkX = int.MinValue;
		visibleCenterChunkZ = int.MinValue;
		visibleChunkRadiusX = -1;
		visibleChunkRadiusZ = -1;
	}

	void ActivateChunk(int chunkIndex)
	{
		int chunkX = chunkIndex % chunkCountX;
		int chunkZ = chunkIndex / chunkCountX;
		HexGridChunk chunk;
		if (chunkPool.Count > 0)
		{
			chunk = chunkPool.Pop();
			chunk.gameObject.SetActive(true);
		}
		else
		{
			chunk = Instantiate(chunkPrefab);
		}

		chunk.transform.SetParent(columns[chunkX], false);
		chunk.Grid = this;
		chunk.SetInteractionEnabled(
			!usesChunkStreaming || ShouldChunkHaveInteraction(chunkIndex), false);
		chunks[chunkIndex] = chunk;
		activeChunkIndices.Add(chunkIndex);

		for (int localZ = 0; localZ < HexMetrics.chunkSizeZ; localZ++)
		{
			int z = chunkZ * HexMetrics.chunkSizeZ + localZ;
			for (int localX = 0; localX < HexMetrics.chunkSizeX; localX++)
			{
				int x = chunkX * HexMetrics.chunkSizeX + localX;
				int localIndex = localX + localZ * HexMetrics.chunkSizeX;
				int cellIndex = x < CellCountX && z < CellCountZ ?
					x + z * CellCountX : -1;
				RectTransform cellUI = chunk.AddCell(
					localIndex, cellIndex, cellLabelPrefab);
				if (cellIndex < 0)
				{
					continue;
				}
				Vector3 position = CellPositions[cellIndex];
				cellUI.anchoredPosition = new Vector2(position.x, position.z);
				RefreshCellPosition(cellIndex);
				ApplyCellUIState(cellIndex, cellUI);
			}
		}
		chunk.ShowUI(gridUIVisible);
		chunk.Refresh();
	}

	void ReleaseChunk(int chunkIndex)
	{
		HexGridChunk chunk = chunks[chunkIndex];
		if (!chunk)
		{
			return;
		}
		chunks[chunkIndex] = null;
		activeChunkIndices.Remove(chunkIndex);
		chunk.SetInteractionEnabled(false, false);
		chunk.UnbindCells();
		chunk.transform.SetParent(transform, false);
		chunk.gameObject.SetActive(false);
		chunkPool.Push(chunk);
	}

	HexGridChunk GetChunkForCell(int cellIndex)
	{
		if (cellIndex < 0 || cellIndex >= CellData.Length || chunks == null)
		{
			return null;
		}
		int z = cellIndex / CellCountX;
		int x = cellIndex - z * CellCountX;
		int chunkIndex =
			x / HexMetrics.chunkSizeX +
			z / HexMetrics.chunkSizeZ * chunkCountX;
		return chunkIndex >= 0 && chunkIndex < chunks.Length ?
			chunks[chunkIndex] : null;
	}

	bool TryGetCellUI(int cellIndex, out RectTransform cellUI)
	{
		HexGridChunk chunk = GetChunkForCell(cellIndex);
		if (!chunk)
		{
			cellUI = null;
			return false;
		}
		int z = cellIndex / CellCountX;
		int x = cellIndex - z * CellCountX;
		int localIndex = x % HexMetrics.chunkSizeX +
			z % HexMetrics.chunkSizeZ * HexMetrics.chunkSizeX;
		cellUI = chunk.GetCellUI(localIndex, cellIndex);
		return cellUI;
	}

	void ApplyCellUIState(int cellIndex, RectTransform cellUI)
	{
		Text label = cellUI.GetComponent<Text>();
		label.text = cellLabelTexts.TryGetValue(cellIndex, out string text) ?
			text : null;
		Image highlight = cellUI.GetChild(0).GetComponent<Image>();
		if (cellHighlights.TryGetValue(cellIndex, out Color color))
		{
			highlight.color = color;
			highlight.enabled = true;
		}
		else
		{
			highlight.enabled = false;
		}
	}

	/// <summary>
	/// Refresh the chunk the cell is part of.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCell(int cellIndex)
	{
		overviewDirty = true;
		HexGridChunk chunk = GetChunkForCell(cellIndex);
		if (chunk)
		{
			chunk.Refresh();
		}
		cellShaderData.RefreshTerrainShapeWithDependents(cellIndex);
	}

	/// <summary>
	/// Bind the map's visual surface style. All chunks in a grid are expected to
	/// share this style; repeated calls are cheap and keep prefab ownership local.
	/// </summary>
	public void ConfigureSurface(HexTerrainStyle style) =>
		SurfaceSampler.Configure(style);

	public float SampleSurfaceHeight(
		Vector3 localPosition, bool carveRiver = true) =>
		SurfaceSampler.SampleHeight(localPosition, carveRiver);

	public float SampleSurfaceHeight(
		int rootCellIndex, Vector3 localPosition, bool carveRiver = true) =>
		SurfaceSampler.SampleHeight(rootCellIndex, localPosition, carveRiver);

	public Vector3 GetSurfacePosition(int cellIndex, float offset = 0f)
	{
		Vector3 position = CellPositions[cellIndex];
		position.y = SampleSurfaceHeight(cellIndex, position) + offset;
		return position;
	}

	/// <summary>
	/// Keep the optional hex edit overlay on the same center height as the HF
	/// surface. CellPositions remains logical Catlike data for generation and
	/// pathfinding; only this visual RectTransform is moved.
	/// </summary>
	public void RefreshCellUISurfacePosition(int cellIndex)
	{
		if (!TryGetCellUI(cellIndex, out RectTransform rectTransform))
		{
			return;
		}
		Vector3 uiPosition = rectTransform.localPosition;
		uiPosition.z = -GetSurfacePosition(cellIndex).y;
		rectTransform.localPosition = uiPosition;
	}

	/// <summary>
	/// Refresh the cell, all its neighbors, and its unit.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCellWithDependents (int cellIndex)
	{
		overviewDirty = true;
		cellShaderData.RefreshTerrainShapeWithDependents(cellIndex);
		HexGridChunk chunk = GetChunkForCell(cellIndex);
		if (chunk)
		{
			chunk.Refresh();
		}
		HexCoordinates coordinates = CellData[cellIndex].coordinates;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (TryGetCellIndex(coordinates.Step(d), out int neighborIndex))
			{
				HexGridChunk neighborChunk = GetChunkForCell(neighborIndex);
				if (neighborChunk && chunk != neighborChunk)
				{
					neighborChunk.Refresh();
				}
			}
		}
		HexUnit unit = CellUnits[cellIndex];
		if (unit)
		{
			unit.ValidateLocation();
		}
	}

	/// <summary>
	/// Refresh the world position of a cell.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCellPosition (int cellIndex)
	{
		Vector3 position = CellPositions[cellIndex];
		position.y = CellData[cellIndex].VisualElevation * HexMetrics.elevationStep;
		// VisualElevation already provides the deliberate dry-land, shallow-shelf,
		// and deep-ocean tiers. Adding independent Y noise here turned every solid
		// hex interior into a slightly different-height plate, while the connection
		// mesh had to slope between them. Keep height perfectly deterministic; the
		// existing XZ perturbation still breaks up the cell outline and relief owns
		// all intentional hills and mountains.
		CellPositions[cellIndex] = position;

		if (TryGetCellUI(cellIndex, out RectTransform rectTransform))
		{
			Vector3 uiPosition = rectTransform.localPosition;
			uiPosition.z = -position.y;
			rectTransform.localPosition = uiPosition;
		}
	}

	/// <summary>
	/// Rebuild the two visual seabed tiers. Water directly touching dry land is
	/// shallow; every other underwater cell is deep ocean.
	/// </summary>
	public void RefreshWaterDepths()
	{
		for (int i = 0; i < CellData.Length; i++)
		{
			RecalculateWaterDepth(i);
		}
	}

	/// <summary>
	/// Rebuild water depth for an edited cell and the one-ring shoreline that
	/// can be affected by its underwater state.
	/// </summary>
	public void RefreshWaterDepthsAround(int cellIndex)
	{
		RecalculateWaterDepth(cellIndex);
		RefreshCellPosition(cellIndex);
		HexCoordinates coordinates = CellData[cellIndex].coordinates;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (TryGetCellIndex(coordinates.Step(d), out int neighborIndex))
			{
				RecalculateWaterDepth(neighborIndex);
				RefreshCellPosition(neighborIndex);
			}
		}
	}

	void RecalculateWaterDepth(int cellIndex)
	{
		HexCellData data = CellData[cellIndex];
		if (!data.IsUnderwater)
		{
			data.visualWaterDepth = 0;
			CellData[cellIndex] = data;
			return;
		}

		bool touchesLand = false;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (TryGetCellIndex(data.coordinates.Step(d), out int neighborIndex) &&
				!CellData[neighborIndex].IsUnderwater)
			{
				touchesLand = true;
				break;
			}
		}
		data.visualWaterDepth = (byte)(touchesLand ? 1 : 2);
		CellData[cellIndex] = data;
	}

	/// <summary>
	/// Add one HF-only segment beyond a dry river's coastline crossing. The
	/// legacy river flags deliberately stop at the first water cell, so this
	/// extension can be shaded on the water surface without carving the seabed or
	/// changing river gameplay.
	/// </summary>
	/// <param name="landCellIndex">Last dry cell of the river.</param>
	/// <returns>Whether a shallow-water continuation could be added.</returns>
	public bool ExtendHFRiverMouth(int landCellIndex)
	{
		if ((uint)landCellIndex >= (uint)CellData.Length)
		{
			return false;
		}

		HexCellData land = CellData[landCellIndex];
		if (land.IsUnderwater ||
			!land.HasIncomingRiver || !land.HasOutgoingRiver)
		{
			return false;
		}

		HexDirection riverOut = land.OutgoingRiver;
		if (!TryGetCellIndex(
			land.coordinates.Step(riverOut), out int waterCellIndex))
		{
			return false;
		}
		HexCellData water = CellData[waterCellIndex];
		if (!water.IsUnderwater)
		{
			return false;
		}

		HexDirection shoreEdge = riverOut.Opposite();
		HexDirection continuation;
		if (land.IncomingRiver == riverOut.Previous())
		{
			continuation = shoreEdge.Next();
		}
		else if (land.IncomingRiver == riverOut.Next())
		{
			continuation = shoreEdge.Previous();
		}
		else
		{
			return false;
		}

		if (!TryGetCellIndex(
			water.coordinates.Step(continuation), out int nextWaterCellIndex))
		{
			return false;
		}
		HexCellData nextWater = CellData[nextWaterCellIndex];
		if (!nextWater.IsUnderwater)
		{
			return false;
		}

		water.hfRiverEdges |= (byte)(
			(1 << (int)shoreEdge) | (1 << (int)continuation));
		nextWater.hfRiverEdges |=
			(byte)(1 << (int)continuation.Opposite());
		CellData[waterCellIndex] = water;
		CellData[nextWaterCellIndex] = nextWater;
		return true;
	}

	/// <summary>
	/// Restore HF-only mouth continuations for loaded maps, including saves made
	/// before river-mouth surface shading was introduced.
	/// </summary>
	void RebuildHFRiverMouthExtensions()
	{
		for (int i = 0; i < CellData.Length; i++)
		{
			HexCellData cell = CellData[i];
			if (!cell.IsUnderwater && cell.HasOutgoingRiver)
			{
				ExtendHFRiverMouth(i);
			}
		}
	}

	/// <summary>
	/// Refresh all cells, to be done after generating a map.
	/// </summary>
	public void RefreshAllCells()
	{
		overviewDirty = true;
		RefreshWaterDepths();
		for (int i = 0; i < CellData.Length; i++)
		{
			SearchData[i].searchPhase = 0;
			RefreshCellPosition(i);
			ShaderData.RefreshTerrain(i);
			ShaderData.RefreshTerrainShape(i);
			ShaderData.RefreshVisibility(i);
		}
	}

	/// <summary>
	/// Rebuild every visual chunk after restoring an editor history snapshot.
	/// </summary>
	public void RefreshAllChunks()
	{
		for (int i = 0; i < chunks.Length; i++)
		{
			if (chunks[i])
			{
				chunks[i].Refresh();
			}
		}
	}

	/// <summary>
	/// Save the map.
	/// </summary>
	/// <param name="writer"><see cref="BinaryWriter"/> to use.</param>
	public void Save(BinaryWriter writer)
	{
		writer.Write(CellCountX);
		writer.Write(CellCountZ);
		writer.Write(Wrapping);
		writer.Write(HasPoliticalData);
		int paletteLength = HasPoliticalData ?
			Mathf.Min(countryPalette.Length, ushort.MaxValue) : 0;
		writer.Write((ushort)paletteLength);
		for (int i = 0; i < paletteLength; i++)
		{
			Color32 color = countryPalette[i];
			writer.Write(color.r);
			writer.Write(color.g);
			writer.Write(color.b);
			writer.Write(color.a);
		}

		for (int i = 0; i < CellData.Length; i++)
		{
			HexCellData data = CellData[i];
			data.values.Save(writer);
			data.flags.Save(writer);
			writer.Write((byte)data.landform);
			writer.Write((byte)data.vegetation);
			writer.Write((byte)Mathf.Clamp(data.VegetationDensity, 0, 100));
			writer.Write((byte)data.vegetationTint);
			writer.Write((byte)data.TerrainRotation);
			writer.Write((byte)data.HFRiverEdgeMask);
			writer.Write(data.CountryId);
		}

		writer.Write(units.Count);
		for (int i = 0; i < units.Count; i++)
		{
			units[i].Save(writer);
		}
	}

	/// <summary>
	/// Load the map.
	/// </summary>
	/// <param name="reader"><see cref="BinaryReader"/> to use.</param>
	/// <param name="header">Header version.</param>
	public void Load(BinaryReader reader, int header)
	{
		ClearPath();
		ClearUnits();
		ClearPoliticalData();
		int x = 20, z = 15;
		if (header >= 1)
		{
			x = reader.ReadInt32();
			z = reader.ReadInt32();
		}
		bool wrapping = header >= 5 && reader.ReadBoolean();
		bool hasPoliticalData = header >= 10 && reader.ReadBoolean();
		Color32[] loadedCountryPalette = null;
		if (header >= 11)
		{
			int paletteLength = reader.ReadUInt16();
			if (hasPoliticalData && paletteLength > 1)
			{
				loadedCountryPalette = new Color32[paletteLength];
				for (int i = 0; i < paletteLength; i++)
				{
					loadedCountryPalette[i] = new Color32(
						reader.ReadByte(), reader.ReadByte(),
						reader.ReadByte(), reader.ReadByte());
				}
			}
			else
			{
				reader.BaseStream.Seek(paletteLength * 4L, SeekOrigin.Current);
			}
		}
		if (x != CellCountX || z != CellCountZ || this.Wrapping != wrapping)
		{
			if (!CreateMap(x, z, wrapping))
			{
				return;
			}
		}
		SetCountryPalette(loadedCountryPalette);

		bool originalImmediateMode = cellShaderData.ImmediateMode;
		cellShaderData.ImmediateMode = true;

		for (int i = 0; i < CellData.Length; i++)
		{
			HexCellData data = CellData[i];
			data.values = HexValues.Load(reader, header);
			data.flags = data.flags.Load(reader, header);
			data.landform = header >= 6 ?
				(HexLandform)reader.ReadByte() : HexLandform.Flat;
			if (header >= 7)
			{
				data.vegetation = (HexVegetation)Mathf.Clamp(
					reader.ReadByte(), 0, (int)HexVegetation.ColdMixed);
				data.vegetationDensity = (byte)Mathf.Clamp(
					reader.ReadByte(), 0, 100);
			}
			else
			{
				// Version 6 inferred species from terrain and stored only a 0-3
				// density tier. Migrate it once into the independent HF fields.
				data.vegetation = data.TerrainTypeIndex switch
				{
					4 => HexVegetation.ColdMixed,
					3 => HexVegetation.Deadwood,
					_ => HexVegetation.Mixed
				};
				data.vegetationDensity = (byte)(data.PlantLevel == 3 ?
					100 : data.PlantLevel * 33);
			}
			if (header >= 8)
			{
				data.vegetationTint = (HexVegetationTint)Mathf.Clamp(
					reader.ReadByte(), 0, (int)HexVegetationTint.Pale);
				data.terrainRotation = (byte)Mathf.Clamp(
					reader.ReadByte(), 0, 5);
			}
			else
			{
				data.vegetationTint = HexVegetationTint.Natural;
				data.terrainRotation = (byte)Mathf.Clamp(
					Mathf.FloorToInt(
						HexMetrics.SampleHashGrid(CellPositions[i]).a * 6f), 0, 5);
			}
			if (header >= 9)
			{
				data.hfRiverEdges = (byte)(reader.ReadByte() & 0b111111);
			}
			else
			{
				// Older maps stored only center-crossing river directions. Preserve
				// those selected boundaries when migrating to HF edge rivers.
				data.hfRiverEdges = 0;
				for (HexDirection direction = HexDirection.NE;
					direction <= HexDirection.NW; direction++)
				{
					if (data.flags.HasRiver(direction))
					{
						data.hfRiverEdges |= (byte)(1 << (int)direction);
					}
				}
			}
			data.countryId = header >= 11 ? reader.ReadUInt16() : (ushort)0;
			CellData[i] = data;
		}
		SanitizeLoadedRoads();
		RebuildHFRiverMouthExtensions();
		RefreshAllCells();
		for (int i = 0; i < chunks.Length; i++)
		{
			if (chunks[i])
			{
				chunks[i].Refresh();
			}
		}

		if (header >= 2)
		{
			int unitCount = reader.ReadInt32();
			for (int i = 0; i < unitCount; i++)
			{
				HexUnit.Load(reader, this);
			}
		}

		cellShaderData.ImmediateMode = originalImmediateMode;
		MapReset?.Invoke();
	}

	/// <summary>
	/// Remove submerged, out-of-map, and one-sided road links from loaded maps.
	/// Road flags describe a shared center-to-center connection and are valid only
	/// when both dry owners agree on the link.
	/// </summary>
	void SanitizeLoadedRoads()
	{
		for (int i = 0; i < CellData.Length; i++)
		{
			HexCellData cell = CellData[i];
			for (HexDirection direction = HexDirection.NE;
				direction <= HexDirection.NW; direction++)
			{
				if (!cell.flags.HasRoad(direction))
				{
					continue;
				}
				if (!TryGetCellIndex(
					cell.coordinates.Step(direction), out int neighborIndex))
				{
					cell.flags = cell.flags.WithoutRoad(direction);
					continue;
				}

				HexCellData neighbor = CellData[neighborIndex];
				HexDirection opposite = direction.Opposite();
				if (cell.IsUnderwater || neighbor.IsUnderwater ||
					!neighbor.flags.HasRoad(opposite))
				{
					cell.flags = cell.flags.WithoutRoad(direction);
					neighbor.flags = neighbor.flags.WithoutRoad(opposite);
					CellData[neighborIndex] = neighbor;
				}
			}
			CellData[i] = cell;
		}
	}

	/// <summary>
	/// Get a list of cell indices representing the currently visible path.
	/// </summary>
	/// <returns>The current path list, if a visible path exists.</returns>
	public List<int> GetPath()
	{
		if (!currentPathExists)
		{
			return null;
		}
		List<int> path = ListPool<int>.Get();
		for (int i = currentPathToIndex;
			i != currentPathFromIndex;
			i = searchData[i].pathFrom)
		{
			path.Add(i);
		}
		path.Add(currentPathFromIndex);
		path.Reverse();
		return path;
	}

	void SetLabel(int cellIndex, string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			cellLabelTexts.Remove(cellIndex);
		}
		else
		{
			cellLabelTexts[cellIndex] = text;
		}
		if (TryGetCellUI(cellIndex, out RectTransform cellUI))
		{
			cellUI.GetComponent<Text>().text = text;
		}
	}

	void DisableHighlight(int cellIndex)
	{
		cellHighlights.Remove(cellIndex);
		if (TryGetCellUI(cellIndex, out RectTransform cellUI))
		{
			cellUI.GetChild(0).GetComponent<Image>().enabled = false;
		}
	}

	void EnableHighlight(int cellIndex, Color color)
	{
		cellHighlights[cellIndex] = color;
		if (TryGetCellUI(cellIndex, out RectTransform cellUI))
		{
			Image highlight = cellUI.GetChild(0).GetComponent<Image>();
			highlight.color = color;
			highlight.enabled = true;
		}
	}

	/// <summary>
	/// Clear the current path.
	/// </summary>
	public void ClearPath()
	{
		if (currentPathExists)
		{
			int currentIndex = currentPathToIndex;
			while (currentIndex != currentPathFromIndex)
			{
				SetLabel(currentIndex, null);
				DisableHighlight(currentIndex);
				currentIndex = searchData[currentIndex].pathFrom;
			}
			DisableHighlight(currentIndex);
			currentPathExists = false;
		}
		else if (currentPathFromIndex >= 0)
		{
			DisableHighlight(currentPathFromIndex);
			DisableHighlight(currentPathToIndex);
		}
		currentPathFromIndex = currentPathToIndex = -1;
	}

	void ShowPath(int speed)
	{
		if (currentPathExists)
		{
			int currentIndex = currentPathToIndex;
			while (currentIndex != currentPathFromIndex)
			{
				int turn = (searchData[currentIndex].distance - 1) / speed;
				SetLabel(currentIndex, turn.ToString());
				EnableHighlight(currentIndex, Color.white);
				currentIndex = searchData[currentIndex].pathFrom;
			}
		}
		EnableHighlight(currentPathFromIndex, Color.blue);
		EnableHighlight(currentPathToIndex, Color.red);
	}

	/// <summary>
	/// Try to find a path.
	/// </summary>
	/// <param name="fromCell">Cell to start the search from.</param>
	/// <param name="toCell">Cell to find a path towards.</param>
	/// <param name="unit">Unit for which the path is.</param>
	public void FindPath(HexCell fromCell, HexCell toCell, HexUnit unit)
	{
		ClearPath();
		currentPathFromIndex = fromCell.Index;
		currentPathToIndex = toCell.Index;
		currentPathExists = Search(fromCell, toCell, unit);
		ShowPath(unit.Speed);
	}

	bool Search(HexCell fromCell, HexCell toCell, HexUnit unit)
	{
		int speed = unit.Speed;
		searchFrontierPhase += 2;
		searchFrontier ??= new HexCellPriorityQueue(this);
		searchFrontier.Clear();
		
		searchData[fromCell.Index] = new HexCellSearchData
		{
			searchPhase = searchFrontierPhase
		};
		searchFrontier.Enqueue(fromCell.Index);
		while (searchFrontier.TryDequeue(out int currentIndex))
		{
			var current = new HexCell(currentIndex, this);
			int currentDistance = searchData[currentIndex].distance;
			searchData[currentIndex].searchPhase += 1;

			if (current == toCell)
			{
				return true;
			}

			int currentTurn = (currentDistance - 1) / speed;

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (!current.TryGetNeighbor(d, out HexCell neighbor))
				{
					continue;
				}
				HexCellSearchData neighborData = searchData[neighbor.Index];
				if (neighborData.searchPhase > searchFrontierPhase ||
					!unit.IsValidDestination(neighbor))
				{
					continue;
				}
				int moveCost = unit.GetMoveCost(current, neighbor, d);
				if (moveCost < 0)
				{
					continue;
				}

				int distance = currentDistance + moveCost;
				int turn = (distance - 1) / speed;
				if (turn > currentTurn)
				{
					distance = turn * speed + moveCost;
				}

				if (neighborData.searchPhase < searchFrontierPhase)
				{
					searchData[neighbor.Index] = new HexCellSearchData
					{
						searchPhase = searchFrontierPhase,
						distance = distance,
						pathFrom = currentIndex,
						heuristic = neighbor.Coordinates.DistanceTo(
							toCell.Coordinates)
					};
					searchFrontier.Enqueue(neighbor.Index);
				}
				else if (distance < neighborData.distance)
				{
					searchData[neighbor.Index].distance = distance;
					searchData[neighbor.Index].pathFrom = currentIndex;
					searchFrontier.Change(
						neighbor.Index, neighborData.SearchPriority);
				}
			}
		}
		return false;
	}

	/// <summary>
	/// Increase the visibility of all cells relative to a view cell.
	/// </summary>
	/// <param name="fromCell">Cell from which to start viewing.</param>
	/// <param name="range">Visibility range.</param>
	public void IncreaseVisibility(HexCell fromCell, int range)
	{
		List<HexCell> cells = GetVisibleCells(fromCell, range);
		for (int i = 0; i < cells.Count; i++)
		{
			int cellIndex = cells[i].Index;
			if (++cellVisibility[cellIndex] == 1)
			{
				HexCell c = cells[i];
				c.Flags = c.Flags.With(HexFlags.Explored);
				cellShaderData.RefreshVisibility(cellIndex);
			}
		}
		ListPool<HexCell>.Add(cells);
	}

	/// <summary>
	/// Decrease the visibility of all cells relative to a view cell.
	/// </summary>
	/// <param name="fromCell">Cell from which to stop viewing.</param>
	/// <param name="range">Visibility range.</param>
	public void DecreaseVisibility(HexCell fromCell, int range)
	{
		List<HexCell> cells = GetVisibleCells(fromCell, range);
		for (int i = 0; i < cells.Count; i++)
		{
			int cellIndex = cells[i].Index;
			if (--cellVisibility[cellIndex] == 0)
			{
				cellShaderData.RefreshVisibility(cellIndex);
			}
		}
		ListPool<HexCell>.Add(cells);
	}

	/// <summary>
	/// Reset visibility of the entire map, viewing from all units.
	/// </summary>
	public void ResetVisibility()
	{
		for (int i = 0; i < cellVisibility.Length; i++)
		{
			if (cellVisibility[i] > 0)
			{
				cellVisibility[i] = 0;
				cellShaderData.RefreshVisibility(i);
			}
		}
		for (int i = 0; i < units.Count; i++)
		{
			HexUnit unit = units[i];
			IncreaseVisibility(unit.Location, unit.VisionRange);
		}
	}

	List<HexCell> GetVisibleCells(HexCell fromCell, int range)
	{
		List<HexCell> visibleCells = ListPool<HexCell>.Get();

		searchFrontierPhase += 2;
		searchFrontier ??= new HexCellPriorityQueue(this);
		searchFrontier.Clear();

		range += fromCell.Values.ViewElevation;
		searchData[fromCell.Index] = new HexCellSearchData
		{
			searchPhase = searchFrontierPhase,
			pathFrom = searchData[fromCell.Index].pathFrom
		};
		searchFrontier.Enqueue(fromCell.Index);
		HexCoordinates fromCoordinates = fromCell.Coordinates;
		while (searchFrontier.TryDequeue(out int currentIndex))
		{
			var current = new HexCell(currentIndex, this);
			searchData[currentIndex].searchPhase += 1;
			visibleCells.Add(current);

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (!current.TryGetNeighbor(d, out HexCell neighbor))
				{
					continue;
				}
				HexCellSearchData currentData = searchData[neighbor.Index];
				if (currentData.searchPhase > searchFrontierPhase ||
					neighbor.Flags.HasNone(HexFlags.Explorable))
				{
					continue;
				}

				int distance = searchData[currentIndex].distance + 1;
				if (distance + neighbor.Values.ViewElevation > range ||
					distance > fromCoordinates.DistanceTo(neighbor.Coordinates))
				{
					continue;
				}

				if (currentData.searchPhase < searchFrontierPhase)
				{
					searchData[neighbor.Index] = new HexCellSearchData
					{
						searchPhase = searchFrontierPhase,
						distance = distance,
						pathFrom = currentData.pathFrom
					};
					searchFrontier.Enqueue(neighbor.Index);
				}
				else if (distance < searchData[neighbor.Index].distance)
				{
					searchData[neighbor.Index].distance = distance;
					searchFrontier.Change(
						neighbor.Index, currentData.SearchPriority);
				}
			}
		}
		return visibleCells;
	}

	/// <summary>
	/// Center the map given an X position, to facilitate east-west wrapping.
	/// </summary>
	/// <param name="xPosition">X position.</param>
	public void CenterMap(float xPosition)
	{
		if (overviewMode)
		{
			return;
		}
		int centerColumnIndex = (int)
			(xPosition / (HexMetrics.innerDiameter * HexMetrics.chunkSizeX));
		
		if (centerColumnIndex == currentCenterColumnIndex)
		{
			return;
		}
		currentCenterColumnIndex = centerColumnIndex;

		int minColumnIndex = centerColumnIndex - chunkCountX / 2;
		int maxColumnIndex = centerColumnIndex + chunkCountX / 2;

		Vector3 position;
		position.y = position.z = 0f;
		for (int i = 0; i < columns.Length; i++)
		{
			if (i < minColumnIndex)
			{
				position.x = CellCountX * HexMetrics.innerDiameter;
			}
			else if (i > maxColumnIndex)
			{
				position.x = -CellCountX * HexMetrics.innerDiameter;
			}
			else
			{
				position.x = 0f;
			}
			columns[i].localPosition = position;
		}
	}
}
