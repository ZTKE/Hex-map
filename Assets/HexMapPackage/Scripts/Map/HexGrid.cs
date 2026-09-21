using UnityEngine;
using UnityEngine.UI;
using System.IO;
using System.Collections.Generic;
using HexMap.WorldData;

/// <summary>
/// Component that represents an entire hexagon map.
/// </summary>
public partial class HexGrid : MonoBehaviour
{
	static readonly int cellHighlightingId = Shader.PropertyToID(
		"_CellHighlighting");
	static readonly int mapShowGridId = Shader.PropertyToID("_HexMapShowGrid");
	static readonly int gridColorId = Shader.PropertyToID("_HexGridColor");
	static readonly int selectionColorId = Shader.PropertyToID(
		"_HexSelectionColor");
	static readonly int politicalBorderCoreColorId = Shader.PropertyToID(
		"_HexPoliticalBorderCoreColor");
	static readonly int politicalBorderGlowColorId = Shader.PropertyToID(
		"_HexPoliticalBorderGlowColor");
	static readonly int politicalBorderWidthsId = Shader.PropertyToID(
		"_HexPoliticalBorderWidths");
	static readonly int cellOverlayStrengthId = Shader.PropertyToID(
		"_HexCellOverlayStrength");

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
	// every zoom level. The radius floor also applies when Unity has restored an
	// older in-memory scene backup whose serialized value predates the mode.
	const int detailedWorldMaximumStreamingRadius = 36;
	const int maximumInactiveChunkCacheCapacity = 512;

	/// <summary>
	/// Raised after a new map has been created or a saved map has finished
	/// loading. Editor-only transient state must not cross this boundary.
	/// </summary>
	public event System.Action MapReset;

	/// <summary>
	/// Raised when the sparse city document is replaced independently of a full
	/// map reset (for example by the editor world baker).
	/// </summary>
	public event System.Action CityDataChanged;

	/// <summary>
	/// Fired when leaving overview still requires cold terrain after restoring
	/// the resident/cache window. Used by the cloud load overlay.
	/// </summary>
	public event System.Action OverviewToDetailStarted;

	/// <summary>
	/// Fired when the desired stream window still has missing chunks (camera pan,
	/// zoom, or overview exit). Cloud veil should cover immediately.
	/// </summary>
	public event System.Action StreamingGapsDetected;

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

	[Header("Strategy Map Overlays")]
	[SerializeField]
	bool showGrid = true;

	[SerializeField]
	Color gridColor = new(0.055f, 0.045f, 0.035f, 0.46f);

	[SerializeField]
	Color selectionColor = new(0.92f, 0.68f, 0.22f, 0.92f);

	[SerializeField]
	Color politicalBorderCoreColor = new(0.018f, 0.012f, 0.008f, 0.96f);

	[SerializeField]
	Color politicalBorderGlowColor = new(0.72f, 0.62f, 0.48f, 0.55f);

	[SerializeField, Range(0.002f, 0.08f)]
	float politicalBorderCoreWidth = 0.022f;

	[SerializeField, Range(0.08f, 0.65f)]
	float politicalBorderGlowWidth = 0.52f;

	[SerializeField, Range(0f, 1f)]
	float cellOverlayStrength = 0.82f;

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
	readonly Dictionary<int, HexGridChunk> cachedChunks = new();
	readonly Dictionary<int, LinkedListNode<int>> cachedChunkNodes = new();
	readonly LinkedList<int> cachedChunkLru = new();
	readonly HashSet<int> activeChunkIndices = new();
	readonly HashSet<int> desiredChunkIndices = new();
	readonly List<int> chunkChangeBuffer = new();
	readonly List<int> pendingChunkActivations = new();
	readonly List<int> pendingInteractionActivations = new();

	[Tooltip("Fallback chunk radius when no valid camera ground footprint is available.")]
	[SerializeField, Min(1)]
	int streamingChunkRadius = 7;

	[SerializeField, Min(1)]
	int maximumStreamingChunkRadius = 8;

	[SerializeField, Min(0)]
	int streamingColliderRadius = 2;

	[Tooltip("Build exact HF MeshColliders near the camera. Gameplay can disable " +
		"this and use the grid's analytical surface ray picking instead.")]
	[SerializeField]
	bool useStreamingSurfaceColliders = true;

	[Tooltip("Minimum number of recently visited, fully generated chunks retained " +
		"for instant reuse. Adapts to the visited window, up to 512. Zero disables " +
		"off-screen caching; overview still suspends the current detailed window.")]
	[SerializeField, Min(0)]
	int streamingChunkCacheCapacity = 64;

	bool usesChunkStreaming;
	[Tooltip("Shared main-thread budget for chunk cells, details and mesh uploads.")]
	[SerializeField, Range(0.5f, 12f)] float streamingCpuBudgetMilliseconds = 5f;
	int currentBuildChunkIndex = -1;
	int chunkBuildCount;
	bool detailFeaturesVisible = true;
	public int ChunkBuildCount => chunkBuildCount;
	/// <summary>Changes only when logical surface data is edited, not while streaming.</summary>
	public uint SurfaceRevision { get; private set; }
	public double LastStreamingCpuMilliseconds { get; private set; }
	public double TotalStreamingWindowCpuMilliseconds { get; private set; }
	public int PendingChunkBuildCount
	{
		get
		{
			if (overviewMode || chunks == null) return 0;
			int count = 0;
			foreach (int index in activeChunkIndices)
				if (chunks[index] && chunks[index].IsBuildPending) count++;
			return count;
		}
	}

	public void SetDetailFeaturesVisible(bool visible)
	{
		detailFeaturesVisible = visible;
		if (chunks == null) return;
		foreach (int index in activeChunkIndices)
			if (chunks[index]) chunks[index].SetDetailFeaturesVisible(visible);
		// Cached off-screen chunks remain disabled; their detail batches survive.
		foreach (HexGridChunk chunk in cachedChunks.Values)
			if (chunk) chunk.SetDetailFeaturesVisible(visible);
	}
	bool gridUIVisible = true;
	bool overviewMode;
	bool detailChunksSuspended;
	int streamWindowHighWatermark;
	int chunkCacheHitCount;
	/// <summary>
	/// Keep the political overview mesh under streaming gaps while the cloud
	/// overlay covers the first frames of a mid→mid-near transition.
	/// </summary>
	bool overviewHeldUnderDetail;
	/// <summary>
	/// Cloud veil is actively covering unfinished desired chunks (any detailed view).
	/// </summary>
	bool streamingCoverActive;
	bool pendingCoverWindowExpand;
	int visibleChunkUpdateDepth;
	Vector3 lastViewCameraPosition;
	Vector2Int lastRequestedChunkRadii;
	bool hasLastViewCamera;
	bool overviewDirty = true;
	bool overviewFullRebuild = true;
	bool overviewBordersDirty = true;
	readonly HashSet<ushort> overviewDirtyCountries = new();
	bool globalOceanMode;
	bool globalOceanDirty = true;
	int pendingActivationCursor;
	int pendingInteractionCursor;
	int visibleCenterChunkX = int.MinValue;
	int visibleCenterChunkZ = int.MinValue;
	int visibleChunkRadiusX = -1;
	int visibleChunkRadiusZ = -1;
	Vector3 lastStreamLocalPosition;
	bool hasStreamLocalPosition;

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
	bool fogOfWarEnabled = true;

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
	int selectedCellIndex = -1;

	/// <summary>
	/// Whether this map is only instantiating render chunks around the camera.
	/// </summary>
	public bool UsesChunkStreaming => usesChunkStreaming;

	/// <summary>
	/// Number of render chunks that currently exist in the scene.
	/// </summary>
	public int ActiveChunkCount => detailChunksSuspended ? 0 : activeChunkIndices.Count;

	/// <summary>Generated off-screen chunks, including the suspended detail window.</summary>
	public int CachedChunkCount => cachedChunks.Count +
		(detailChunksSuspended ? activeChunkIndices.Count : 0);

	/// <summary>Bound chunk renderers retained across detailed and overview views.</summary>
	public int ResidentChunkCount => activeChunkIndices.Count + cachedChunks.Count;

	/// <summary>Ready chunks reused without regeneration since the current map loaded.</summary>
	public int ChunkCacheHitCount => chunkCacheHitCount;

	/// <summary>
	/// Whether the streamed map is currently using only its cheap overview.
	/// </summary>
	public bool IsOverviewMode => overviewMode;

	/// <summary>
	/// Overview mesh is still drawn under unfinished detailed chunks after leaving
	/// overview mode (cloud transition hold).
	/// </summary>
	public bool IsOverviewHeldUnderDetail => overviewHeldUnderDetail;

	/// <summary>
	/// Cloud veil is covering streaming gaps in detailed (non-overview) view.
	/// </summary>
	public bool IsStreamingCoverActive => streamingCoverActive;

	/// <summary>
	/// Camera pans should stay slower while cloud cover or overview hold is active.
	/// </summary>
	public bool ShouldDampenCameraForStreaming =>
		streamingCoverActive;

	/// <summary>
	/// Whether any currently desired stream chunk is still missing.
	/// </summary>
	public bool HasMissingDesiredChunks
	{
		get
		{
			if (!usesChunkStreaming || overviewMode || chunks == null)
			{
				return false;
			}

			foreach (int chunkIndex in desiredChunkIndices)
			{
				if (!chunks[chunkIndex] || !chunks[chunkIndex].HasReadyGeometry)
				{
					return true;
				}
			}
			return pendingActivationCursor < pendingChunkActivations.Count;
		}
	}

	/// <summary>
	/// Whether one continuous sea replaces chunk-local water in the current
	/// medium / far view.
	/// </summary>
	public bool IsGlobalOceanMode => globalOceanMode;

	/// <summary>
	/// Fraction of currently desired stream chunks that already exist (0..1).
	/// Returns 1 when not streaming or still in overview.
	/// </summary>
	public float DesiredChunkActivationProgress
	{
		get
		{
			if (!usesChunkStreaming || overviewMode || chunks == null)
			{
				return 1f;
			}

			int desired = desiredChunkIndices.Count;
			if (desired <= 0)
			{
				return pendingChunkActivations.Count > 0 &&
					pendingActivationCursor < pendingChunkActivations.Count ?
					0f : 1f;
			}

			int missing = 0;
			foreach (int chunkIndex in desiredChunkIndices)
			{
				if (!chunks[chunkIndex] || !chunks[chunkIndex].HasReadyGeometry)
				{
					missing++;
				}
			}
			return 1f - missing / (float)desired;
		}
	}

	/// <summary>
	/// Center-out reveal radius (0..1) based on the nearest still-missing desired
	/// chunk. Matches activation order (sorted by distance from the camera chunk).
	/// </summary>
	public float DesiredChunkRevealRadius
	{
		get
		{
			if (!usesChunkStreaming || overviewMode || chunks == null)
			{
				return 1f;
			}

			if (desiredChunkIndices.Count == 0)
			{
				return DesiredChunkActivationProgress >= 0.999f ? 1f : 0f;
			}

			float maxDesired = 0f;
			float minMissing = float.PositiveInfinity;
			bool anyMissing = false;
			foreach (int chunkIndex in desiredChunkIndices)
			{
				float dist = Mathf.Sqrt(GetChunkDistanceSquared(chunkIndex));
				if (dist > maxDesired)
				{
					maxDesired = dist;
				}
				if (!chunks[chunkIndex] || !chunks[chunkIndex].HasReadyGeometry)
				{
					anyMissing = true;
					if (dist < minMissing)
					{
						minMissing = dist;
					}
				}
			}

			if (!anyMissing)
			{
				return 1f;
			}
			if (maxDesired < 0.001f)
			{
				return 0f;
			}

			// Frontier sits on the nearest unloaded ring; pad slightly so the
			// soft cloud edge opens over already-ready terrain.
			return Mathf.Clamp01((minMissing + 0.35f) / (maxDesired + 0.35f));
		}
	}

	public int PoliticalBorderSegmentCount => overview ?
		overview.PoliticalBorderSegmentCount : 0;

	/// <summary>
	/// Sparse city records bound to cells in the current map. A newly-created
	/// map intentionally starts with an empty list.
	/// </summary>
	public IReadOnlyList<WorldCityData> Cities => cities;

	public int CityCount => cities.Count;

	/// <summary>
	/// Whether cells on this map carry political ownership and a color palette.
	/// </summary>
	public bool HasPoliticalData => countryPalette != null &&
		countryPalette.Length > 1;

	/// <summary>
	/// Monotonically increasing runtime version for political ownership data.
	/// Presentation adapters can use this to refresh another projection without
	/// scanning all 515,900 cells every frame.
	/// </summary>
	public uint PoliticalRevision => politicalRevision;

	/// <summary>Monotonically increasing runtime version for country colors.</summary>
	public uint CountryPaletteRevision => countryPaletteRevision;

	/// <summary>
	/// Install the ID-indexed political palette used by the overview. Cell IDs
	/// remain authoritative; the palette is presentation data.
	/// </summary>
	public void SetCountryPalette(Color32[] palette)
	{
		SetCountryPalette(palette, true);
	}

	void SetCountryPalette(Color32[] palette, bool refreshShaderColors)
	{
		countryPalette = palette != null && palette.Length > 1 ?
			(Color32[])palette.Clone() : null;
		unchecked
		{
			countryPaletteRevision += 1;
		}
		UploadCountryPaletteTexture();
		if (refreshShaderColors)
		{
			cellShaderData?.RefreshPoliticalPalette();
		}
		MarkOverviewFullDirty(bordersToo: false);
		if (overviewMode)
		{
			EnsureOverview();
		}
	}

	void UploadCountryPaletteTexture()
	{
		if (countryPalette == null || countryPalette.Length < 2)
		{
			Shader.SetGlobalTexture(CountryPaletteShaderId, Texture2D.whiteTexture);
			return;
		}

		if (!countryPaletteTexture ||
			countryPaletteTexture.width != CountryPaletteTextureSize)
		{
			if (countryPaletteTexture)
			{
				Destroy(countryPaletteTexture);
			}

			countryPaletteTexture = new Texture2D(
				CountryPaletteTextureSize, 1, TextureFormat.RGBA32, false, true)
			{
				name = "HexCountryPalette",
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave
			};
		}

		Color32[] pixels = new Color32[CountryPaletteTextureSize];
		for (int i = 0; i < CountryPaletteTextureSize; i++)
		{
			pixels[i] = i < countryPalette.Length ?
				countryPalette[i] : new Color32(0, 0, 0, 0);
		}

		countryPaletteTexture.SetPixels32(pixels);
		countryPaletteTexture.Apply(false, false);
		Shader.SetGlobalTexture(CountryPaletteShaderId, countryPaletteTexture);
	}

	void OnDestroy()
	{
		if (countryPaletteTexture)
		{
			Destroy(countryPaletteTexture);
			countryPaletteTexture = null;
		}
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
			ushort previousCountryId = data.countryId;
			data.countryId = countryId;
			CellData[cellIndex] = data;
			unchecked
			{
				politicalRevision += 1;
			}
			cellShaderData.RefreshPolitical(cellIndex);
			MarkOverviewCountriesDirty(previousCountryId, countryId);
		}
		return true;
	}

	public void ClearPoliticalData()
	{
		ClearPoliticalData(true);
	}

	void ClearPoliticalData(bool refreshShaderColors)
	{
		bool hadPalette = countryPalette != null;
		countryPalette = null;
		if (hadPalette)
		{
			unchecked
			{
				countryPaletteRevision += 1;
			}
		}
		if (refreshShaderColors)
		{
			cellShaderData?.RefreshPoliticalPalette();
		}
		MarkOverviewFullDirty(bordersToo: true);
	}

	/// <summary>
	/// Replace the current map's city records. Invalid or submerged bindings are
	/// rejected so gameplay code can safely treat every city as a land location.
	/// </summary>
	public void SetCities(IEnumerable<WorldCityData> source)
	{
		cities.Clear();
		if (source != null && CellData != null)
		{
			foreach (WorldCityData city in source)
			{
				if (city == null || city.targetCellIndex < 0 ||
					city.targetCellIndex >= CellData.Length ||
					CellData[city.targetCellIndex].IsUnderwater)
				{
					continue;
				}
				cities.Add(city);
			}
		}
		CityDataChanged?.Invoke();
	}

	public void ClearCityData()
	{
		if (cities.Count == 0)
		{
			return;
		}
		cities.Clear();
		CityDataChanged?.Invoke();
	}

#pragma warning disable IDE0044 // Add readonly modifier
	List<HexUnit> units = new();
#pragma warning restore IDE0044 // Add readonly modifier

	HexCellShaderData cellShaderData;
	HexSurfaceSampler surfaceSampler;
	HexMapOverview overview;
	HexGlobalOcean globalOcean;
	HexCityLayer cityLayer;
	Color32[] countryPalette;
	Texture2D countryPaletteTexture;
	uint politicalRevision;
	uint countryPaletteRevision;
	const int CountryPaletteTextureSize = 256;
	static readonly int CountryPaletteShaderId =
		Shader.PropertyToID("_CountryPalette");
	readonly List<WorldCityData> cities = new();

	void Awake()
	{
		CellCountX = 20;
		CellCountZ = 15;
		HexMetrics.noiseSource = noiseSource;
		HexMetrics.InitializeHashGrid(seed);
		HexUnit.unitPrefab = unitPrefab;
		ApplySurfaceOverlayShaderGlobals();
		ClearSelectedCell();
		surfaceSampler = new HexSurfaceSampler(this);
		cellShaderData = gameObject.AddComponent<HexCellShaderData>();
		cellShaderData.Grid = this;
		overview = GetComponent<HexMapOverview>();
		if (!overview)
		{
			overview = gameObject.AddComponent<HexMapOverview>();
		}
		globalOcean = GetComponent<HexGlobalOcean>();
		if (!globalOcean)
		{
			globalOcean = gameObject.AddComponent<HexGlobalOcean>();
		}
		cityLayer = GetComponentInChildren<HexCityLayer>(true);
		if (!cityLayer)
		{
			GameObject cityLayerObject = new("World Cities");
			cityLayerObject.transform.SetParent(transform, false);
			cityLayer = cityLayerObject.AddComponent<HexCityLayer>();
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
		if (globalOcean)
		{
			globalOcean.SetVisible(false);
		}
		ClearPoliticalData(false);
		ClearCityData();
		overviewMode = false;
		detailChunksSuspended = false;
		currentBuildChunkIndex = -1;
		chunkBuildCount = 0;
		unchecked { SurfaceRevision++; }
		LastStreamingCpuMilliseconds = 0;
		streamWindowHighWatermark = 0;
		chunkCacheHitCount = 0;
		overviewHeldUnderDetail = false;
		streamingCoverActive = false;
		hasLastViewCamera = false;
		MarkOverviewFullDirty(bordersToo: true);
		globalOceanMode = false;
		globalOceanDirty = true;
		InvalidateStreamingWindow();
		if (columns != null)
		{
			for (int i = 0; i < columns.Length; i++)
			{
				Destroy(columns[i].gameObject);
			}
		}
		DestroyCachedChunks();
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
		ClearSelectedCell();

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
		ApplySurfaceOverlayShaderGlobals();
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
	public HexCell GetCell(Ray ray) => TryGetCellFromRaySurface(ray);

	HexCell TryGetCellFromRaySurface(Ray ray)
	{
		Vector3 localOrigin = transform.InverseTransformPoint(ray.origin);
		Vector3 localDirection = transform.InverseTransformVector(ray.direction);
		if (localDirection.sqrMagnitude < 0.000001f)
		{
			return default;
		}
		localDirection.Normalize();
		if (Mathf.Abs(localDirection.y) < 0.00001f)
		{
			return default;
		}

		float waterY = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		float surfaceY = waterY;
		HexCell cell = default;
		for (int iteration = 0; iteration < 3; iteration++)
		{
			float distance = (surfaceY - localOrigin.y) / localDirection.y;
			if (distance < 0f)
			{
				return default;
			}
			Vector3 localPoint = localOrigin + localDirection * distance;
			cell = GetCell(HexCoordinates.FromPosition(localPoint));
			if (!cell)
			{
				return default;
			}
			surfaceY = CellData[cell.Index].IsUnderwater ?
				waterY : SampleSurfaceHeight(cell.Index, localPoint);
		}
		return cell;
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
	public bool IsCellVisible(int cellIndex) =>
		!fogOfWarEnabled || cellVisibility[cellIndex] > 0;

	/// <summary>
	/// Enable or disable visibility-based map darkening. Disabling fog of war
	/// keeps the logical visibility counters intact while rendering every cell
	/// as visible, so the existing unit vision system can be restored later.
	/// </summary>
	public void SetFogOfWarEnabled(bool enabled)
	{
		if (fogOfWarEnabled == enabled)
		{
			return;
		}

		fogOfWarEnabled = enabled;
		if (CellData == null || cellVisibility == null || cellShaderData == null)
		{
			return;
		}

		bool originalImmediateMode = cellShaderData.ImmediateMode;
		cellShaderData.ImmediateMode = true;
		for (int i = 0; i < CellData.Length; i++)
		{
			cellShaderData.RefreshVisibility(i);
		}
		cellShaderData.ImmediateMode = originalImmediateMode;
	}

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
		LastStreamingCpuMilliseconds = 0;
		if (!usesChunkStreaming || overviewMode) return;
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
		double ticksPerMillisecond = System.Diagnostics.Stopwatch.Frequency / 1000.0;
		double deadline = started + Mathf.Clamp(streamingCpuBudgetMilliseconds, 0.5f, 12f) * ticksPerMillisecond;
		int operations = 0;
		// A permanent political underlay is not a loading emergency. Never burst
		// eight complete chunks into LateUpdate when the camera moves.
		while (operations++ < 2048 && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
		{
			bool hasActivation = pendingActivationCursor < pendingChunkActivations.Count;
			int nextIndex = hasActivation ? pendingChunkActivations[pendingActivationCursor] : -1;
			if (hasActivation && cachedChunks.ContainsKey(nextIndex))
			{
				ProcessPendingChunkActivations(1);
				continue;
			}
			if (currentBuildChunkIndex < 0 || currentBuildChunkIndex >= chunks.Length ||
				!activeChunkIndices.Contains(currentBuildChunkIndex) ||
				!chunks[currentBuildChunkIndex] || !chunks[currentBuildChunkIndex].IsBuildPending)
			{
				currentBuildChunkIndex = FindNextChunkBuild();
			}
			// Publish missing ground before spending the budget on hidden/detail
			// batches. A camera pan must not wait behind a forest catch-up queue.
			if (hasActivation &&
				(currentBuildChunkIndex < 0 || chunks[currentBuildChunkIndex].HasReadyGeometry))
			{
				ProcessPendingChunkActivations(1);
				currentBuildChunkIndex = -1;
				continue;
			}
			if (currentBuildChunkIndex >= 0)
			{
				HexGridChunk chunk = chunks[currentBuildChunkIndex];
				int before = chunk.GeometryBuildCount;
				chunk.BuildStep();
				chunkBuildCount += chunk.GeometryBuildCount - before;
				continue;
			}
			if (hasActivation)
			{
				ProcessPendingChunkActivations(1);
				continue;
			}
			if (pendingInteractionCursor < pendingInteractionActivations.Count)
				ProcessPendingInteractionActivations(1);
			break;
		}
		LastStreamingCpuMilliseconds =
			(System.Diagnostics.Stopwatch.GetTimestamp() - started) / ticksPerMillisecond;
	}

	int FindNextChunkBuild()
	{
		int best = -1, nearest = int.MaxValue;
		foreach (int index in activeChunkIndices)
		{
			HexGridChunk chunk = chunks[index];
			if (!chunk || !chunk.IsBuildPending) continue;
			int distance = GetChunkDistanceSquared(index);
			if (chunk.HasReadyGeometry) distance += 1000000;
			if (distance < nearest) { best = index; nearest = distance; }
		}
		return best;
	}

	/// <summary>
	/// Cloud overlay end hook. Detailed streaming keeps the political underlay;
	/// only overview mode / map reset clears it.
	/// </summary>
	public void ReleaseHeldOverview(bool force = false)
	{
		if (externalPresentationCamera)
		{
			overviewHeldUnderDetail = false;
			if (overview) overview.SetVisible(false);
			return;
		}
		if (!overviewMode && usesChunkStreaming)
		{
			overviewHeldUnderDetail = true;
			if (overview)
			{
				overview.SetVisible(true);
			}
			return;
		}

		overviewHeldUnderDetail = false;
		if (overview && !overviewMode)
		{
			overview.SetVisible(false);
		}
	}

	/// <summary>
	/// Append indices of desired chunks that are not instantiated yet.
	/// </summary>
	public int CopyMissingDesiredChunkIndices(List<int> buffer)
	{
		if (buffer == null)
		{
			return 0;
		}

		buffer.Clear();
		if (!usesChunkStreaming || overviewMode || chunks == null)
		{
			return 0;
		}

		foreach (int chunkIndex in desiredChunkIndices)
		{
			if (!chunks[chunkIndex] || !chunks[chunkIndex].HasReadyGeometry)
			{
				buffer.Add(chunkIndex);
			}
		}
		return buffer.Count;
	}

	/// <summary>
	/// Local-space ground rectangle for a chunk (y ignored). Used for gap masks.
	/// </summary>
	public bool TryGetChunkLocalRect(
		int chunkIndex, out Vector3 min, out Vector3 max)
	{
		min = max = Vector3.zero;
		if (chunks == null || chunkIndex < 0 ||
			chunkIndex >= chunks.Length || chunkCountX <= 0)
		{
			return false;
		}

		int chunkX = chunkIndex % chunkCountX;
		int chunkZ = chunkIndex / chunkCountX;
		float xMin = chunkX * HexMetrics.chunkSizeX * HexMetrics.innerDiameter;
		float zMin = chunkZ * HexMetrics.chunkSizeZ *
			(HexMetrics.outerRadius * 1.5f);
		float xMax = xMin +
			HexMetrics.chunkSizeX * HexMetrics.innerDiameter;
		float zMax = zMin +
			HexMetrics.chunkSizeZ * (HexMetrics.outerRadius * 1.5f);
		min = new Vector3(xMin, 0f, zMin);
		max = new Vector3(xMax, 0f, zMax);
		return true;
	}

	/// <summary>
	/// Track the cloud veil independently from the permanent political underlay.
	/// The camera's existing margin owns prefetching; cover does not expand it.
	/// </summary>
	public void SetStreamingCoverActive(bool active)
	{
		streamingCoverActive = active;
		pendingCoverWindowExpand = false;
	}

	void RequestCoverWindowExpand()
	{
		if (overviewMode || !usesChunkStreaming || !hasLastViewCamera)
		{
			return;
		}

		if (visibleChunkUpdateDepth > 0)
		{
			pendingCoverWindowExpand = true;
			return;
		}

		ExpandCoverStreamingWindow();
	}

	void ExpandCoverStreamingWindow()
	{
		pendingCoverWindowExpand = false;
		Vector2Int radii = lastRequestedChunkRadii;
		Vector2Int pad = GetCloudTransitionStreamPadding();
		radii = new Vector2Int(radii.x + pad.x, radii.y + pad.y);
		InvalidateStreamingWindow();
		UpdateVisibleChunks(lastViewCameraPosition, radii);
	}

	/// <summary>
	/// Extra chunk-window padding while cloud cover is active.
	/// </summary>
	// Camera radii already include a stable prefetch margin. Expanding the same
	// window because it is loading creates more loading and delays every reveal.
	public Vector2Int GetCloudTransitionStreamPadding() => Vector2Int.zero;

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
	/// window. Leaving overview holds the political mesh under streaming gaps and
	/// raises <see cref="OverviewToDetailStarted"/> for the cloud wipe overlay.
	/// </summary>
	/// <param name="cameraWorldPosition">Camera rig position (wrapping / ocean).</param>
	/// <param name="streamingFocusWorldPosition">
	/// Optional ground look-at used as the chunk-window center. When omitted,
	/// <paramref name="cameraWorldPosition"/> is used.
	/// </param>
	public void UpdateCameraView(
		Vector3 cameraWorldPosition, bool requestOverview,
		Vector2Int requestedChunkRadii = default,
		bool requestGlobalOcean = false,
		Vector3 streamingFocusWorldPosition = default)
	{
		// An explicitly registered presentation owns its own camera and LOD.
		// Legacy bootstrap / map camera calls must not replace that stream window.
		if (externalPresentationCamera || PresentationSuppressed) return;
		SetGlobalOceanMode(requestGlobalOcean && !requestOverview);
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
			overviewHeldUnderDetail = false;
			streamingCoverActive = false;
			EnsureOverview();
			overview.SetVisible(true);
			if (!overviewMode)
			{
				overviewMode = true;
				SuspendDetailChunksForOverview();
			}
			return;
		}

		bool leavingOverview = overviewMode;
		if (overviewMode)
		{
			overviewMode = false;
			// Keep the cheap political mesh under detailed streaming gaps for the
			// whole session (HOI-style underlay) so pans never flash clear-color.
			overviewHeldUnderDetail = usesChunkStreaming;
			if (overviewHeldUnderDetail)
			{
				EnsureOverview();
				overview.SetVisible(true);
			}
			InvalidateStreamingWindow();
			Vector3 localPosition =
				transform.InverseTransformPoint(cameraWorldPosition);
			currentCenterColumnIndex = -1;
			CenterMap(localPosition.x);
		}

		if (overview && !overviewHeldUnderDetail)
		{
			overview.SetVisible(false);
		}
		else if (overview && overviewHeldUnderDetail)
		{
			overview.SetVisible(true);
		}

		Vector3 streamFocus = cameraWorldPosition;
		if (streamingFocusWorldPosition != Vector3.zero)
		{
			streamFocus = streamingFocusWorldPosition;
		}

		lastViewCameraPosition = streamFocus;
		lastRequestedChunkRadii = requestedChunkRadii;
		hasLastViewCamera = true;

		if (overviewHeldUnderDetail || streamingCoverActive)
		{
			Vector2Int pad = GetCloudTransitionStreamPadding();
			requestedChunkRadii = new Vector2Int(
				requestedChunkRadii.x + pad.x,
				requestedChunkRadii.y + pad.y);
		}
		UpdateVisibleChunks(streamFocus, requestedChunkRadii);
		ResumeDetailChunksAfterOverview();
		if (leavingOverview && HasMissingDesiredChunks) OverviewToDetailStarted?.Invoke();
	}

	/// <summary>
	/// Switch between chunk-local water and one grid-wide water surface. The
	/// transition invalidates only the render window; logical cells stay loaded.
	/// </summary>
	public void SetGlobalOceanMode(bool value)
	{
		if (value)
		{
			EnsureGlobalOcean();
			if (globalOceanDirty && globalOcean)
			{
				globalOcean.Rebuild(this);
				globalOceanDirty = !globalOcean.IsReady;
			}
			value = globalOcean && globalOcean.IsReady;
		}

		if (globalOcean)
		{
			globalOcean.SetVisible(value);
		}
		if (globalOceanMode == value)
		{
			return;
		}

		globalOceanMode = value;
		foreach (int chunkIndex in activeChunkIndices)
		{
			HexGridChunk chunk = chunks[chunkIndex];
			if (chunk)
			{
				chunk.SetGlobalOceanMode(value);
			}
		}
		InvalidateStreamingWindow();
	}

	void EnsureGlobalOcean()
	{
		if (globalOcean)
		{
			return;
		}
		globalOcean = GetComponent<HexGlobalOcean>();
		if (!globalOcean)
		{
			globalOcean = gameObject.AddComponent<HexGlobalOcean>();
		}
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
	/// Rebuild the overview atlas when political ownership or palette changed.
	/// </summary>
	public void RefreshOverviewIfDirty()
	{
		if (!overviewDirty)
		{
			return;
		}

		EnsureOverview();
	}

	void LateUpdate()
	{
		if (PresentationSuppressed) return;
		if (overviewDirty && overviewMode && usesChunkStreaming && !externalPresentationCamera)
		{
			EnsureOverview();
		}
	}

	/// <summary>Keep data, ownership revisions and save APIs alive while a
	/// different surface renders the game. No second terrain stream is needed.</summary>
	public bool PresentationSuppressed { get; private set; }
	/// <summary>An external native map owns startup; retain only the palette/API host.</summary>
	public bool SkipDefaultWorldBootstrap { get; set; }
	public void SetPresentationSuppressed(bool suppressed)
	{
		PresentationSuppressed = suppressed;
		if (!suppressed) { InvalidateStreamingWindow(); return; }
		if (overview) overview.SetVisible(false);
		if (globalOcean) globalOcean.SetVisible(false);
		SuspendDetailChunksForOverview();
		foreach (var renderer in GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
		foreach (var collider in GetComponentsInChildren<Collider>(true)) collider.enabled = false;
		foreach (var city in GetComponentsInChildren<HexCityLayer>(true)) city.enabled = false;
	}

	void MarkOverviewFullDirty(bool bordersToo)
	{
		overviewDirty = true;
		overviewFullRebuild = true;
		overviewDirtyCountries.Clear();
		if (bordersToo)
		{
			overviewBordersDirty = true;
		}
	}

	public void ClearCellOccupation(int cellIndex)
	{
		if (cellShaderData)
		{
			cellShaderData.ClearCellOccupation(cellIndex);
		}
	}

	/// <summary>
	/// Temporary occupation stripe wash (Vic3-style). Leaves core CountryId /
	/// overview borders untouched until a diplomatic refresh.
	/// </summary>
	public void SetCellOccupation(int cellIndex, Color occupierColor)
	{
		if (!cellShaderData || CellData == null ||
			cellIndex < 0 || cellIndex >= CellData.Length)
		{
			return;
		}
		if (CellData[cellIndex].IsUnderwater || occupierColor.a <= 0f)
		{
			ClearCellOccupation(cellIndex);
			return;
		}
		occupierColor.a = Mathf.Clamp(occupierColor.a, 0.55f, 0.85f);
		cellShaderData.SetCellOccupation(cellIndex, occupierColor);
	}

	/// <summary>陆地建造选格：见 <see cref="HexCellShaderData.LandBuildSelectionBlocked"/>。</summary>
	public void SetLandBuildSelectionState(int cellIndex, byte state)
	{
		cellShaderData?.SetLandBuildSelection(cellIndex, state);
	}

	public void ClearLandBuildSelectionState(int cellIndex)
	{
		cellShaderData?.ClearLandBuildSelection(cellIndex);
	}

	public void FlushLandBuildSelectionTexture() =>
		cellShaderData?.FlushLandBuildSelectionTexture();

	public HexCellShaderData ShaderDataForBuildSelection => cellShaderData;

	/// <summary>
	/// Mark specific countries dirty so the next overview rebuild refreshes their
	/// political wash / borders — including a country that now owns zero tiles.
	/// </summary>
	public void InvalidateOverviewCountries(ushort countryA, ushort countryB = 0)
	{
		if (countryA != 0)
		{
			MarkOverviewCountriesDirty(countryA, 0);
		}
		if (countryB != 0 && countryB != countryA)
		{
			MarkOverviewCountriesDirty(countryB, 0);
		}
	}

	void MarkOverviewCountriesDirty(ushort previousCountryId, ushort nextCountryId)
	{
		overviewDirty = true;
		overviewBordersDirty = true;
		if (previousCountryId != 0)
		{
			overviewDirtyCountries.Add(previousCountryId);
		}
		if (nextCountryId != 0)
		{
			overviewDirtyCountries.Add(nextCountryId);
		}
	}

	/// <summary>
	/// Snapshot and clear overview dirty flags for one Rebuild pass.
	/// </summary>
	internal void TakeOverviewDirtyState(
		out bool fullRebuild,
		out bool bordersDirty,
		HashSet<ushort> dirtyCountries)
	{
		fullRebuild = overviewFullRebuild;
		bordersDirty = overviewBordersDirty;
		dirtyCountries.Clear();
		if (!fullRebuild)
		{
			foreach (ushort countryId in overviewDirtyCountries)
			{
				dirtyCountries.Add(countryId);
			}
		}

		overviewFullRebuild = false;
		overviewBordersDirty = false;
		overviewDirtyCountries.Clear();
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

		long windowStarted = System.Diagnostics.Stopwatch.GetTimestamp();
		visibleChunkUpdateDepth++;
		try
		{
			UpdateVisibleChunksBody(cameraWorldPosition, requestedChunkRadii);
		}
		finally
		{
			visibleChunkUpdateDepth--;
			if (visibleChunkUpdateDepth == 0 && pendingCoverWindowExpand)
			{
				ExpandCoverStreamingWindow();
			}
			TotalStreamingWindowCpuMilliseconds +=
				(System.Diagnostics.Stopwatch.GetTimestamp() - windowStarted) *
				1000.0 / System.Diagnostics.Stopwatch.Frequency;
		}
	}

	void UpdateVisibleChunksBody(
		Vector3 cameraWorldPosition, Vector2Int requestedChunkRadii)
	{
		Vector3 localPosition = transform.InverseTransformPoint(cameraWorldPosition);
		HexCoordinates coordinates = HexCoordinates.FromPosition(localPosition);
		int centerZ = coordinates.Z;
		int centerX = coordinates.X + centerZ / 2;
		int centerChunkX = Mathf.FloorToInt(
			centerX / (float)HexMetrics.chunkSizeX);
		int centerChunkZ = Mathf.FloorToInt(
			centerZ / (float)HexMetrics.chunkSizeZ);
		int renderRadiusX = requestedChunkRadii.x > 0 ?
			requestedChunkRadii.x :
			streamingChunkRadius;
		int renderRadiusZ = requestedChunkRadii.y > 0 ?
			requestedChunkRadii.y :
			streamingChunkRadius;
		int maximumRadius = Mathf.Max(
			detailedWorldMaximumStreamingRadius,
			maximumStreamingChunkRadius);
		renderRadiusX = Mathf.Clamp(renderRadiusX, 1, maximumRadius);
		renderRadiusZ = Mathf.Clamp(renderRadiusZ, 1, maximumRadius);

		float rebuildDist =
			HexMetrics.innerDiameter * HexMetrics.chunkSizeX * 0.28f;
		bool centerUnchanged =
			centerChunkX == visibleCenterChunkX &&
			centerChunkZ == visibleCenterChunkZ &&
			renderRadiusX == visibleChunkRadiusX &&
			renderRadiusZ == visibleChunkRadiusZ;
		bool movedLittle = hasStreamLocalPosition &&
			(localPosition - lastStreamLocalPosition).sqrMagnitude <
			rebuildDist * rebuildDist;
		Camera streamingCamera = externalPresentationCamera ? externalPresentationCamera : Camera.main;
		bool viewportChanged = StreamingViewportChanged(streamingCamera, rebuildDist);
		if (centerUnchanged && movedLittle && !viewportChanged)
		{
			return;
		}
		visibleCenterChunkX = centerChunkX;
		visibleCenterChunkZ = centerChunkZ;
		visibleChunkRadiusX = renderRadiusX;
		visibleChunkRadiusZ = renderRadiusZ;
		lastStreamLocalPosition = localPosition;
		hasStreamLocalPosition = true;

		RememberStreamingViewport(streamingCamera);
		desiredChunkIndices.Clear();
		bool hasViewportFootprint = IncludeViewportEdgeChunks(streamingCamera);
		// Keep only a small interaction/prefetch core in addition to the actual
		// footprint. Approximate radii remain a camera-unavailable fallback.
		int coreRadius = useStreamingSurfaceColliders ? Mathf.Max(1, streamingColliderRadius) : 1;
		int desiredRadiusX = hasViewportFootprint ? coreRadius : renderRadiusX;
		int desiredRadiusZ = hasViewportFootprint ? coreRadius : renderRadiusZ;
		for (int zOffset = -desiredRadiusZ;
			zOffset <= desiredRadiusZ; zOffset++)
		{
			int z = centerChunkZ + zOffset;
			if (z < 0 || z >= chunkCountZ)
			{
				continue;
			}
			for (int xOffset = -desiredRadiusX;
				xOffset <= desiredRadiusX; xOffset++)
			{
				int x = centerChunkX + xOffset;
				TryAddDesiredChunkXY(x, z);
			}
		}

		streamWindowHighWatermark = Mathf.Max(streamWindowHighWatermark,
			Mathf.Max(desiredChunkIndices.Count, activeChunkIndices.Count));

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
		// Activation and collider queues are consumed only from Update. Processing
		// here as well could schedule two budgets before LateUpdate, making many
		// complete terrain triangulations land in the same rendered frame.
	}

	readonly HexStreamingFootprint streamingViewportFootprint = new();
	readonly Vector3[] streamingFrustumCorners = new Vector3[8];
	readonly HashSet<int> viewportChunkIndices = new();
	Camera lastFootprintCamera;
	Matrix4x4 lastFootprintProjection, lastFootprintWorldToLocal;
	Quaternion lastFootprintRotation;
	Vector3 lastFootprintCameraLocalPosition;
	bool hasFootprintCameraState;
	HexTerrainStyle footprintEnvelopeStyle;
	HexNearTerrainProfile footprintEnvelopeProfile;
	int footprintEnvelopeStyleRevision, footprintEnvelopeWaterLevel;
	uint footprintEnvelopeShapeRevision, footprintEnvelopeSurfaceRevision;
	uint footprintEnvelopeVersion, lastFootprintEnvelopeVersion;
	bool hasFootprintHeightEnvelope;
	float footprintMinimumHeight, footprintMaximumHeight;

	bool StreamingViewportChanged(Camera cam, float movementAllowance)
	{
		EnsureFootprintHeightEnvelope();
		if (!cam) return hasFootprintCameraState;
		Vector3 localCamera = transform.InverseTransformPoint(cam.transform.position);
		return !hasFootprintCameraState || cam != lastFootprintCamera ||
			!cam.projectionMatrix.Equals(lastFootprintProjection) ||
			!cam.transform.rotation.Equals(lastFootprintRotation) ||
			!transform.worldToLocalMatrix.Equals(lastFootprintWorldToLocal) ||
			localCamera.y != lastFootprintCameraLocalPosition.y ||
			(localCamera - lastFootprintCameraLocalPosition).sqrMagnitude >= movementAllowance * movementAllowance ||
			footprintEnvelopeVersion != lastFootprintEnvelopeVersion;
	}

	void RememberStreamingViewport(Camera cam)
	{
		hasFootprintCameraState = cam;
		lastFootprintCamera = cam;
		lastFootprintEnvelopeVersion = footprintEnvelopeVersion;
		if (!cam) return;
		lastFootprintProjection = cam.projectionMatrix;
		lastFootprintRotation = cam.transform.rotation;
		lastFootprintWorldToLocal = transform.worldToLocalMatrix;
		lastFootprintCameraLocalPosition = transform.InverseTransformPoint(cam.transform.position);
	}

	/// <summary>
	/// The actual camera footprint owns visible coverage. Requested/configured
	/// radii are a fallback only when a valid camera/ground intersection is absent.
	/// One whole chunk of geometric/pan allowance protects the throttled window.
	/// </summary>
	bool IncludeViewportEdgeChunks(Camera cam)
	{
		if (!cam || chunks == null) return false;
		EnsureFootprintHeightEnvelope();
		for (int i = 0; i < 8; i++)
		{
			int corner = i & 3;
			float x = corner == 1 || corner == 2 ? 1f : 0f;
			float y = corner >= 2 ? 1f : 0f;
			float depth = i < 4 ? cam.nearClipPlane : cam.farClipPlane;
			streamingFrustumCorners[i] = transform.InverseTransformPoint(
				cam.ViewportToWorldPoint(new Vector3(x, y, depth)));
		}
		if (!streamingViewportFootprint.Build(streamingFrustumCorners,
			footprintMinimumHeight, footprintMaximumHeight)) return false;
		viewportChunkIndices.Clear();
		streamingViewportFootprint.AddChunks(CellCountX, CellCountZ,
			HexMetrics.chunkSizeX, HexMetrics.chunkSizeZ,
			HexMetrics.innerDiameter, HexMetrics.outerRadius * 1.5f, Wrapping,
			HexMetrics.innerDiameter * HexMetrics.chunkSizeX,
			HexMetrics.outerRadius * 1.5f * HexMetrics.chunkSizeZ,
			viewportChunkIndices);
		foreach (int index in viewportChunkIndices)
		{
			// Keep the existing global-ocean/interaction policy, and let the
			// shared scheduler restore cached terrain or build missing chunks.
			TryAddDesiredChunkXY(index % chunkCountX, index / chunkCountX);
		}
		return true;
	}

	void EnsureFootprintHeightEnvelope()
	{
		HexTerrainStyle style = SurfaceStyle;
		HexNearTerrainProfile profile = style ? style.nearTerrainProfile : null;
		int styleRevision = style ? style.RuntimeRevision : 0;
		uint shapeRevision = profile ? profile.ShapeRevision : 0;
		if (hasFootprintHeightEnvelope && footprintEnvelopeStyle == style &&
			footprintEnvelopeProfile == profile && footprintEnvelopeStyleRevision == styleRevision &&
			footprintEnvelopeShapeRevision == shapeRevision && footprintEnvelopeSurfaceRevision == SurfaceRevision &&
			footprintEnvelopeWaterLevel == HexMetrics.visualWaterLevel) return;
		footprintEnvelopeStyle = style; footprintEnvelopeProfile = profile;
		footprintEnvelopeStyleRevision = styleRevision; footprintEnvelopeShapeRevision = shapeRevision;
		footprintEnvelopeSurfaceRevision = SurfaceRevision; footprintEnvelopeWaterLevel = HexMetrics.visualWaterLevel;
		hasFootprintHeightEnvelope = true;
		unchecked { footprintEnvelopeVersion++; }

		// Height is bounded analytically, independent of the 515,900 logical
		// elevations. Legacy shader data clamps its base to [0,30]; HF texture
		// heights are UNorm and Near uses bounded maxima/smooth unions.
		float datum = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		float minimum = Mathf.Min(0f, datum - 6f + HexMetrics.streamBedElevationOffset * HexMetrics.elevationStep);
		float legacy = style ? Mathf.Max(style.hillHeight, Mathf.Max(style.mountainHeight, style.desertMountainHeight)) : 0f;
		float maximum = Mathf.Max(datum, 30f + Mathf.Max(0f, legacy) + .018f);
		float overlay = 0f;
		if (style)
		{
			float scale = Mathf.Max(0f, style.hfOriginalHeightScale);
			minimum = Mathf.Min(minimum, datum - .3f * scale + .018f);
			maximum = Mathf.Max(maximum, datum + .5f * scale + .018f);
			overlay = Mathf.Max(0f, Mathf.Max(style.hfRoadSurfaceOffset, style.hfRiverSurfaceOffset));
		}
		if (profile)
		{
			float mountain = Mathf.Max(0f, Mathf.Max(profile.mountainHeight, profile.desertMountainHeight));
			float peak = 1.14f * mountain * Mathf.Max(1f, profile.ridgeStrength);
			float saddle = Mathf.Max(1f, Mathf.Max(profile.mountainChainSaddle + .02f, profile.mountainMassifSaddle + .05f));
			float range = 1.14f * mountain * saddle * Mathf.Max(0f, profile.mountainRangeStrength) *
				(1f + Mathf.Clamp(profile.mountainSlopeDetail, 0f, .35f));
			float lowerMass = mountain * Mathf.Clamp(profile.mountainBaseHeight, 0f, .6f);
			float hills = (1f - 16f / 255f) * Mathf.Max(.18f, Mathf.Max(profile.hillHeight, profile.plateauRelief));
			float dunes = Mathf.Max(0f, Mathf.Max(profile.desertDuneHeight, profile.desertHillDuneHeight));
			float near = datum + .178f + Mathf.Max(0f, profile.plateauHeight) +
				Mathf.Max(Mathf.Max(Mathf.Max(peak, range) + .09f, lowerMass), hills + dunes) +
				.5f * Mathf.Max(0f, profile.mountainFootBlend);
			maximum = Mathf.Max(maximum, near);
			minimum = Mathf.Min(minimum, datum - .302f);
		}
		// Coast skirts and stable ocean bottoms sit slightly below their datum;
		// a small additional tolerance also absorbs float error in projections.
		footprintMinimumHeight = Mathf.Min(minimum, datum - 1.56f) - .08f;
		footprintMaximumHeight = maximum + overlay + .02f;
	}

	void TryAddDesiredChunkXY(int x, int z)
	{
		if (z < 0 || z >= chunkCountZ)
		{
			return;
		}

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
			return;
		}

		int chunkIndex = x + z * chunkCountX;
		if (globalOceanMode &&
			!ShouldChunkHaveInteraction(chunkIndex) &&
			IsPureOceanChunk(chunkIndex))
		{
			return;
		}

		desiredChunkIndices.Add(chunkIndex);
	}

	bool IsPureOceanChunk(int chunkIndex)
	{
		int chunkX = chunkIndex % chunkCountX;
		int chunkZ = chunkIndex / chunkCountX;
		int xMin = chunkX * HexMetrics.chunkSizeX;
		int zMin = chunkZ * HexMetrics.chunkSizeZ;
		int xMax = Mathf.Min(xMin + HexMetrics.chunkSizeX, CellCountX);
		int zMax = Mathf.Min(zMin + HexMetrics.chunkSizeZ, CellCountZ);
		bool hasCell = false;
		for (int z = zMin; z < zMax; z++)
		{
			int row = z * CellCountX;
			for (int x = xMin; x < xMax; x++)
			{
				hasCell = true;
				if (!CellData[row + x].IsUnderwater)
				{
					return false;
				}
			}
		}
		return hasCell;
	}

	void RebuildPendingChunkActivations()
	{
		pendingChunkActivations.Clear();
		pendingActivationCursor = 0;
		foreach (int chunkIndex in desiredChunkIndices)
		{
			if (!chunks[chunkIndex])
			{
				if (cachedChunks.TryGetValue(chunkIndex, out HexGridChunk cached) &&
					cached && cached.HasReadyGeometry)
					ActivateChunk(chunkIndex);
				else pendingChunkActivations.Add(chunkIndex);
			}
		}
		pendingChunkActivations.Sort((a, b) =>
			GetChunkDistanceSquared(a).CompareTo(GetChunkDistanceSquared(b)));
		if (pendingChunkActivations.Count > 0 && !overviewMode)
		{
			StreamingGapsDetected?.Invoke();
		}
	}

	int GetChunkDistanceSquared(int chunkIndex)
	{
		int x = chunkIndex % chunkCountX;
		int z = chunkIndex / chunkCountX;
		int xDistance = Mathf.Abs(x - visibleCenterChunkX);
		if (Wrapping)
		{
			xDistance %= chunkCountX;
			xDistance = Mathf.Min(xDistance, chunkCountX - xDistance);
		}
		int zDistance = Mathf.Abs(z - visibleCenterChunkZ);
		return xDistance * xDistance + zDistance * zDistance;
	}

	bool ShouldChunkHaveInteraction(int chunkIndex)
	{
		if (!useStreamingSurfaceColliders || streamingColliderRadius < 0)
		{
			return false;
		}
		int x = chunkIndex % chunkCountX;
		int z = chunkIndex / chunkCountX;
		int xDistance = Mathf.Abs(x - visibleCenterChunkX);
		if (Wrapping)
		{
			xDistance %= chunkCountX;
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

	void SuspendDetailChunksForOverview()
	{
		// Keep the complete current window bound. Sending hundreds of resident
		// chunks through a small off-screen LRU evicted most of the same window
		// on every zoom-out, forcing it to regenerate on the next zoom-in.
		detailChunksSuspended = true;
		streamWindowHighWatermark = Mathf.Max(
			streamWindowHighWatermark, activeChunkIndices.Count);
		foreach (int chunkIndex in activeChunkIndices)
		{
			HexGridChunk chunk = chunks[chunkIndex];
			if (chunk) chunk.SetStreamingVisible(false);
		}
		desiredChunkIndices.Clear();
		pendingChunkActivations.Clear();
		pendingActivationCursor = 0;
		pendingInteractionActivations.Clear();
		pendingInteractionCursor = 0;
		InvalidateStreamingWindow();
	}

	void ResumeDetailChunksAfterOverview()
	{
		if (!detailChunksSuspended) return;
		detailChunksSuspended = false;
		// UpdateVisibleChunks has already released residents outside the new
		// viewport. Only the still-desired chunks are made visible again.
		foreach (int chunkIndex in activeChunkIndices)
		{
			HexGridChunk chunk = chunks[chunkIndex];
			if (!chunk) continue;
			if (chunk.HasReadyGeometry) chunkCacheHitCount++;
			chunk.SetGlobalOceanMode(globalOceanMode);
			chunk.ShowUI(gridUIVisible);
			chunk.SetStreamingVisible(chunk.HasReadyGeometry);
		}
	}

	void InvalidateStreamingWindow()
	{
		visibleCenterChunkX = int.MinValue;
		visibleCenterChunkZ = int.MinValue;
		visibleChunkRadiusX = -1;
		visibleChunkRadiusZ = -1;
		hasStreamLocalPosition = false;
	}

	void ActivateChunk(int chunkIndex)
	{
		int chunkX = chunkIndex % chunkCountX;
		int chunkZ = chunkIndex / chunkCountX;
		bool reusedGeneratedChunk = TryTakeCachedChunk(
			chunkIndex, out HexGridChunk chunk);
		if (reusedGeneratedChunk)
		{
			chunkCacheHitCount++;
		}
		else if (chunkPool.Count > 0)
		{
			chunk = chunkPool.Pop();
			chunk.gameObject.SetActive(!usesChunkStreaming);
			// A pooled renderer may contain old published meshes. Keep it hidden
			// until its new geometry has been uploaded by the shared scheduler.
		}
		else
		{
			chunk = Instantiate(chunkPrefab);
		}

		PositionColumn(chunkX);
		if (chunk.transform.parent != columns[chunkX])
		{
			chunk.transform.SetParent(columns[chunkX], false);
		}
		chunk.Grid = this;
		chunk.SetDetailFeaturesVisible(detailFeaturesVisible);
		chunk.SetGlobalOceanMode(globalOceanMode);
		chunk.SetInteractionEnabled(
			!usesChunkStreaming || ShouldChunkHaveInteraction(chunkIndex),
			reusedGeneratedChunk);
		chunks[chunkIndex] = chunk;
		activeChunkIndices.Add(chunkIndex);
		if (reusedGeneratedChunk)
		{
			// Update interaction and presentation while the static cache mask is
			// still applied; only then restore the requested collider/render state.
			chunk.SetStreamingVisible(true);
			RefreshChunkUIBindings(chunkIndex, chunk);
			chunk.ShowUI(gridUIVisible);
			return;
		}

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
					localIndex, cellIndex, cellLabelPrefab,
					!usesChunkStreaming || cellLabelTexts.ContainsKey(cellIndex) ||
					cellHighlights.ContainsKey(cellIndex));
				if (cellIndex < 0)
				{
					continue;
				}
				Vector3 position = CellPositions[cellIndex];
				if (cellUI) cellUI.anchoredPosition = new Vector2(position.x, position.z);
				RefreshCellPosition(cellIndex);
				if (cellUI) ApplyCellUIState(cellIndex, cellUI);
			}
		}
		chunk.ShowUI(gridUIVisible);
		chunk.Refresh();
		if (usesChunkStreaming) chunk.gameObject.SetActive(false);
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
		// A cached chunk still belongs to this column. Retaining its parent also
		// avoids editor hierarchy scans on every out-and-back camera movement.
		// Eviction reparents it only when it actually returns to the renderer pool.
		chunk.SetStreamingVisible(false);
		if (!chunk.IsStaticStreamingHidden)
			chunk.SetInteractionEnabled(false, false);
		CacheReleasedChunk(chunkIndex, chunk);
	}

	void RefreshChunkUIBindings(int chunkIndex, HexGridChunk chunk)
	{
		int chunkX = chunkIndex % chunkCountX;
		int chunkZ = chunkIndex / chunkCountX;
		for (int localZ = 0; localZ < HexMetrics.chunkSizeZ; localZ++)
		{
			int z = chunkZ * HexMetrics.chunkSizeZ + localZ;
			for (int localX = 0; localX < HexMetrics.chunkSizeX; localX++)
			{
				int x = chunkX * HexMetrics.chunkSizeX + localX;
				if (x >= CellCountX || z >= CellCountZ)
				{
					continue;
				}
				int localIndex = localX + localZ * HexMetrics.chunkSizeX;
				int cellIndex = x + z * CellCountX;
				RectTransform cellUI = chunk.GetCellUI(localIndex, cellIndex,
					cellLabelTexts.ContainsKey(cellIndex) || cellHighlights.ContainsKey(cellIndex));
				if (cellUI)
				{
					Vector3 position = CellPositions[cellIndex];
					cellUI.anchoredPosition = new Vector2(position.x, position.z);
					Vector3 local = cellUI.localPosition;
					local.z = -GetSurfacePosition(cellIndex).y;
					cellUI.localPosition = local;
					ApplyCellUIState(cellIndex, cellUI);
				}
			}
		}
	}

	void CacheReleasedChunk(int chunkIndex, HexGridChunk chunk)
	{
		int capacity = streamingChunkCacheCapacity <= 0 ? 0 :
			Mathf.Min(maximumInactiveChunkCacheCapacity,
				Mathf.Max(streamingChunkCacheCapacity, streamWindowHighWatermark));
		// Retain valid terrain even if its first near-detail pass is still pending.
		// Only incomplete or invalidated terrain must return to the renderer pool.
		if (capacity == 0 || !chunk.HasReadyGeometry)
		{
			ReturnChunkToPool(chunk);
			return;
		}

		cachedChunks.Add(chunkIndex, chunk);
		cachedChunkNodes.Add(
			chunkIndex, cachedChunkLru.AddLast(chunkIndex));
		while (cachedChunks.Count > capacity)
		{
			EvictOldestCachedChunk();
		}
	}

	bool TryTakeCachedChunk(int chunkIndex, out HexGridChunk chunk)
	{
		if (!cachedChunks.TryGetValue(chunkIndex, out chunk))
		{
			return false;
		}
		cachedChunks.Remove(chunkIndex);
		if (cachedChunkNodes.TryGetValue(
			chunkIndex, out LinkedListNode<int> node))
		{
			cachedChunkNodes.Remove(chunkIndex);
			cachedChunkLru.Remove(node);
		}
		if (!chunk || !chunk.HasReadyGeometry)
		{
			ReturnChunkToPool(chunk);
			chunk = null;
			return false;
		}
		return true;
	}

	void EvictOldestCachedChunk()
	{
		LinkedListNode<int> node = cachedChunkLru.First;
		if (node == null)
		{
			return;
		}
		int chunkIndex = node.Value;
		cachedChunkLru.RemoveFirst();
		cachedChunkNodes.Remove(chunkIndex);
		if (cachedChunks.TryGetValue(chunkIndex, out HexGridChunk chunk))
		{
			cachedChunks.Remove(chunkIndex);
			ReturnChunkToPool(chunk);
		}
	}

	void InvalidateCachedChunkForCell(int cellIndex)
	{
		if (cellIndex < 0 || cellIndex >= CellData.Length)
		{
			return;
		}
		int x = cellIndex % CellCountX;
		int z = cellIndex / CellCountX;
		int chunkIndex = x / HexMetrics.chunkSizeX +
			(z / HexMetrics.chunkSizeZ) * chunkCountX;
		if (TryTakeCachedChunk(chunkIndex, out HexGridChunk chunk))
		{
			ReturnChunkToPool(chunk);
		}
	}

	void ClearCachedChunks()
	{
		foreach (HexGridChunk chunk in cachedChunks.Values)
		{
			ReturnChunkToPool(chunk);
		}
		cachedChunks.Clear();
		cachedChunkNodes.Clear();
		cachedChunkLru.Clear();
	}

	void DestroyCachedChunks()
	{
		foreach (HexGridChunk chunk in cachedChunks.Values)
		{
			if (chunk)
			{
				Destroy(chunk.gameObject);
			}
		}
		cachedChunks.Clear();
		cachedChunkNodes.Clear();
		cachedChunkLru.Clear();
	}

	void ReturnChunkToPool(HexGridChunk chunk)
	{
		if (!chunk)
		{
			return;
		}
		chunk.ResetStreamingVisibilityForBuild();
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

	bool TryGetCellUI(int cellIndex, out RectTransform cellUI, bool create = false)
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
		cellUI = chunk.GetCellUI(localIndex, cellIndex, create);
		if (cellUI && create)
		{
			Vector3 position = CellPositions[cellIndex];
			cellUI.anchoredPosition = new Vector2(position.x, position.z);
			Vector3 local = cellUI.localPosition;
			local.z = -GetSurfacePosition(cellIndex).y;
			cellUI.localPosition = local;
		}
		return cellUI;
	}

	void ApplyCellUIState(int cellIndex, RectTransform cellUI)
	{
		Text label = cellUI.GetComponent<Text>();
		bool hasLabel = cellLabelTexts.TryGetValue(
			cellIndex, out string text) && !string.IsNullOrEmpty(text);
		label.text = hasLabel ? text : null;
		Image highlight = cellUI.GetChild(0).GetComponent<Image>();
		bool hasHighlight = cellHighlights.TryGetValue(
			cellIndex, out Color color);
		if (hasHighlight)
		{
			highlight.color = color;
			highlight.enabled = true;
		}
		else
		{
			highlight.enabled = false;
		}
		SetCellUIActive(cellIndex, hasLabel || hasHighlight);
	}

	void SetCellUIActive(int cellIndex, bool active)
	{
		HexGridChunk chunk = GetChunkForCell(cellIndex);
		if (!chunk)
		{
			return;
		}
		int z = cellIndex / CellCountX;
		int x = cellIndex - z * CellCountX;
		int localIndex = x % HexMetrics.chunkSizeX +
			z % HexMetrics.chunkSizeZ * HexMetrics.chunkSizeX;
		chunk.SetCellUIActive(localIndex, cellIndex, active);
	}

	void RefreshCellUIActiveState(int cellIndex)
	{
		bool hasLabel = cellLabelTexts.TryGetValue(
			cellIndex, out string text) && !string.IsNullOrEmpty(text);
		SetCellUIActive(
			cellIndex, hasLabel || cellHighlights.ContainsKey(cellIndex));
	}

	/// <summary>
	/// Refresh the chunk the cell is part of.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCell(int cellIndex)
	{
		unchecked { SurfaceRevision++; }
		MarkOverviewFullDirty(bordersToo: true);
		InvalidateCachedChunkForCell(cellIndex);
		if (globalOceanMode)
		{
			InvalidateStreamingWindow();
		}
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

	public HexTerrainStyle SurfaceStyle => SurfaceSampler.Style;

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
	/// Refresh the cell, its two-ring surface dependents, and its unit.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCellWithDependents (int cellIndex)
	{
		unchecked { SurfaceRevision++; }
		MarkOverviewFullDirty(bordersToo: true);
		if (globalOceanMode)
		{
			InvalidateStreamingWindow();
		}
		cellShaderData.RefreshTerrainShapeWithDependents(cellIndex);
		// Near mountain mass and connectors depend on each source's immediate
		// neighbourhood. A changed source can influence surface samples two rings
		// farther away, so its CPU-built roads, trees and colliders need three rings.
		int supportRings = SurfaceStyle && SurfaceStyle.UsesNearTerrain ? 3 : 2;
		HashSet<int> affectedCells = new() { cellIndex };
		List<int> frontier = new() { cellIndex };
		for (int ring = 0; ring < supportRings; ring++)
		{
			List<int> next = new();
			foreach (int sourceIndex in frontier)
			{
				HexCoordinates source = CellData[sourceIndex].coordinates;
				for (HexDirection direction = HexDirection.NE; direction <= HexDirection.NW; direction++)
				{
					if (TryGetCellIndex(source.Step(direction), out int neighborIndex) && affectedCells.Add(neighborIndex))
						next.Add(neighborIndex);
				}
			}
			frontier = next;
		}
		HashSet<HexGridChunk> affectedChunks = new();
		foreach (int affectedIndex in affectedCells)
		{
			InvalidateCachedChunkForCell(affectedIndex);
			HexGridChunk affectedChunk = GetChunkForCell(affectedIndex);
			if (affectedChunk && affectedChunks.Add(affectedChunk))
			{
				affectedChunk.Refresh();
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
		MarkOverviewFullDirty(bordersToo: true);
		globalOceanDirty = true;
		ClearCachedChunks();
		if (globalOceanMode)
		{
			globalOcean.Rebuild(this);
			globalOceanDirty = !globalOcean.IsReady;
			InvalidateStreamingWindow();
		}
		RefreshWaterDepths();
		for (int i = 0; i < CellData.Length; i++)
		{
			SearchData[i].searchPhase = 0;
			RefreshCellPosition(i);
			ShaderData.RefreshTerrain(i);
			ShaderData.RefreshTerrainShape(i);
			ShaderData.RefreshPolitical(i);
			ShaderData.RefreshVisibility(i);
		}
		// The detail window can now remain bound while overview is showing.
		// Full logical-map changes must invalidate those suspended meshes too.
		RefreshAllChunks();
	}

	/// <summary>
	/// Rebuild every visual chunk after restoring an editor history snapshot.
	/// </summary>
	public void RefreshAllChunks()
	{
		unchecked { SurfaceRevision++; }
		ClearCachedChunks();
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
			writer.Write((byte)data.mountainMode);
		}

		writer.Write(units.Count);
		for (int i = 0; i < units.Count; i++)
		{
			units[i].Save(writer);
		}

		writer.Write(cities.Count);
		for (int i = 0; i < cities.Count; i++)
		{
			WriteCity(writer, cities[i]);
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
		ClearPoliticalData(false);
		ClearCityData();
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
		SetCountryPalette(loadedCountryPalette, false);

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
					reader.ReadByte(), 0, (int)HexVegetation.Jungle);
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
			data.mountainMode = header >= 13 ?
				(HexMountainMode)Mathf.Clamp(reader.ReadByte(), 0, 2) : HexMountainMode.Automatic;
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

		if (header >= 12)
		{
			int cityCount = reader.ReadInt32();
			if (cityCount < 0 || cityCount > 100_000)
			{
				throw new InvalidDataException(
					$"Map city count {cityCount:N0} is outside the supported range.");
			}
			for (int i = 0; i < cityCount; i++)
			{
				WorldCityData city = ReadCity(reader);
				if (city.targetCellIndex >= 0 &&
					city.targetCellIndex < CellData.Length &&
					!CellData[city.targetCellIndex].IsUnderwater)
				{
					cities.Add(city);
				}
			}
		}

		cellShaderData.ImmediateMode = originalImmediateMode;
		MapReset?.Invoke();
	}

	static void WriteCity(BinaryWriter writer, WorldCityData city)
	{
		writer.Write(city.name ?? string.Empty);
		writer.Write(city.sourceTileId);
		writer.Write(city.sourceCountryId);
		writer.Write(city.countryId);
		writer.Write(city.areaId);
		writer.Write(city.terrainId);
		writer.Write(city.sourceSpherePosition.x);
		writer.Write(city.sourceSpherePosition.y);
		writer.Write(city.sourceSpherePosition.z);
		writer.Write(city.longitude);
		writer.Write(city.latitude);
		writer.Write(city.u);
		writer.Write(city.v);
		writer.Write(city.targetCellIndex);
	}

	static WorldCityData ReadCity(BinaryReader reader) => new()
	{
		name = reader.ReadString(),
		sourceTileId = reader.ReadInt32(),
		sourceCountryId = reader.ReadInt32(),
		countryId = reader.ReadInt32(),
		areaId = reader.ReadInt32(),
		terrainId = reader.ReadInt32(),
		sourceSpherePosition = new SerializableVector3
		{
			x = reader.ReadSingle(),
			y = reader.ReadSingle(),
			z = reader.ReadSingle()
		},
		longitude = reader.ReadDouble(),
		latitude = reader.ReadDouble(),
		u = reader.ReadDouble(),
		v = reader.ReadDouble(),
		targetCellIndex = reader.ReadInt32()
	};

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
		if (TryGetCellUI(cellIndex, out RectTransform cellUI, create: !string.IsNullOrEmpty(text)))
		{
			cellUI.GetComponent<Text>().text = text;
			RefreshCellUIActiveState(cellIndex);
		}
	}

	void DisableHighlight(int cellIndex)
	{
		cellHighlights.Remove(cellIndex);
		cellShaderData.ClearCellOverlay(cellIndex);
		if (TryGetCellUI(cellIndex, out RectTransform cellUI))
		{
			cellUI.GetChild(0).GetComponent<Image>().enabled = false;
			RefreshCellUIActiveState(cellIndex);
		}
	}

	void EnableHighlight(int cellIndex, Color color)
	{
		if (color.a <= 0f)
		{
			DisableHighlight(cellIndex);
			return;
		}
		// Even callers passing Color.red / Color.white receive a HOI-style wash,
		// never an opaque replacement of the authored terrain.
		color.a = Mathf.Min(color.a, 0.30f);
		cellHighlights[cellIndex] = color;
		cellShaderData.SetCellOverlay(cellIndex, color);
		if (TryGetCellUI(cellIndex, out RectTransform cellUI, create: true))
		{
			Image highlight = cellUI.GetChild(0).GetComponent<Image>();
			highlight.color = color;
			highlight.enabled = true;
			RefreshCellUIActiveState(cellIndex);
		}
	}

	void ApplySurfaceOverlayShaderGlobals()
	{
		Shader.SetGlobalFloat(mapShowGridId, showGrid ? 1f : 0f);
		Shader.SetGlobalColor(gridColorId, gridColor);
		Shader.SetGlobalColor(selectionColorId, selectionColor);
		Shader.SetGlobalColor(
			politicalBorderCoreColorId, politicalBorderCoreColor);
		Shader.SetGlobalColor(
			politicalBorderGlowColorId, politicalBorderGlowColor);
		Shader.SetGlobalVector(
			politicalBorderWidthsId,
			new Vector4(
				politicalBorderCoreWidth,
				Mathf.Max(politicalBorderGlowWidth, politicalBorderCoreWidth),
				0f,
				0f));
		Shader.SetGlobalFloat(
			cellOverlayStrengthId,
			overviewMode ? 0f : cellOverlayStrength);
	}

	/// <summary>
	/// Re-push overlay globals after overview↔detail switches.
	/// </summary>
	public void RefreshSurfaceOverlayShaderGlobals() =>
		ApplySurfaceOverlayShaderGlobals();

	/// <summary>Show or hide the detailed hex lattice.</summary>
	public void SetGridVisible(bool visible)
	{
		showGrid = visible;
		Shader.SetGlobalFloat(mapShowGridId, visible ? 1f : 0f);
	}

	/// <summary>
	/// Select one logical cell with a thin black-gold rim (globe-limb style).
	/// This is independent from persistent gameplay tint data.
	/// </summary>
	public void SetSelectedCell(int cellIndex, Color color)
	{
		if (CellData == null || cellIndex < 0 || cellIndex >= CellData.Length)
		{
			ClearSelectedCell();
			return;
		}

		selectedCellIndex = cellIndex;
		selectionColor = color;
		if (selectionColor.a <= 0f)
		{
			selectionColor.a = 0.92f;
		}
		Shader.SetGlobalColor(selectionColorId, selectionColor);
		HexCoordinates coordinates = CellData[cellIndex].coordinates;
		Shader.SetGlobalVector(
			cellHighlightingId,
			new Vector4(
				coordinates.HexX,
				coordinates.HexZ,
				0.5f,
				Wrapping ? CellCountX : 0f));
	}

	public void SetSelectedCell(int cellIndex) =>
		SetSelectedCell(cellIndex, selectionColor);

	/// <summary>Clear the gameplay cell selection without removing tints.</summary>
	public void ClearSelectedCell()
	{
		selectedCellIndex = -1;
		Shader.SetGlobalVector(
			cellHighlightingId, new Vector4(0f, 0f, -1f, 0f));
	}

	public int SelectedCellIndex => selectedCellIndex;

	/// <summary>
	/// Apply a persistent, translucent color wash to one cell. Terrain texture,
	/// relief, water motion, and lighting remain visible below the tint.
	/// </summary>
	public void SetCellTint(
		int cellIndex, Color color, float opacity = 0.22f)
	{
		if (CellData == null || cellIndex < 0 || cellIndex >= CellData.Length)
		{
			return;
		}
		if (color.a <= 0f || opacity <= 0f)
		{
			DisableHighlight(cellIndex);
			return;
		}
		color.a = Mathf.Clamp(opacity, 0f, 0.40f);
		EnableHighlight(cellIndex, color);
	}

	public void ClearCellTint(int cellIndex) => ClearCellHighlight(cellIndex);

	/// <summary>
	/// Show a persistent gameplay highlight for a cell. The color is retained
	/// even while its streamed chunk is inactive and is restored on activation.
	/// </summary>
	public void SetCellHighlight(int cellIndex, Color color)
	{
		if (CellData == null || cellIndex < 0 || cellIndex >= CellData.Length)
		{
			return;
		}
		EnableHighlight(cellIndex, color);
	}

	/// <summary>Clear a persistent gameplay highlight from a cell.</summary>
	public void ClearCellHighlight(int cellIndex)
	{
		if (CellData == null || cellIndex < 0 || cellIndex >= CellData.Length)
		{
			return;
		}
		DisableHighlight(cellIndex);
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

		for (int i = 0; i < columns.Length; i++)
		{
			// Large streamed maps can have hundreds of empty column transforms. Only
			// columns with a live chunk, unit, or other child participate in rendering;
			// newly activated empty columns are positioned by ActivateChunk.
			if (columns[i].childCount > 0)
			{
				PositionColumn(i);
			}
		}
	}

	void PositionColumn(int columnIndex)
	{
		if (columns == null || columnIndex < 0 ||
			columnIndex >= columns.Length || currentCenterColumnIndex < 0)
		{
			return;
		}

		int minColumnIndex = currentCenterColumnIndex - chunkCountX / 2;
		int maxColumnIndex = currentCenterColumnIndex + chunkCountX / 2;
		float x = columnIndex < minColumnIndex ?
			CellCountX * HexMetrics.innerDiameter :
			columnIndex > maxColumnIndex ?
			-CellCountX * HexMetrics.innerDiameter : 0f;
		columns[columnIndex].localPosition = new Vector3(x, 0f, 0f);
	}
}
