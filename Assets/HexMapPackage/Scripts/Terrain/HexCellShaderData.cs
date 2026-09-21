using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Component that manages cell data used by shaders.
/// </summary>
public class HexCellShaderData : MonoBehaviour
{
	const float transitionSpeed = 255f;

	Texture2D cellTexture;
	Texture2D politicalTexture;
	Texture2D politicalColorTexture;
	Texture2D overlayTexture;
	Texture2D occupationTexture;
	Texture2D landBuildSelectionTexture;

	Texture2D terrainShapeTexture;
	Texture2D waterTopologyTexture;
	Texture2D mountainRidgeTexture;

	Color32[] cellTextureData;
	Color32[] politicalTextureData;
	Color32[] politicalColorTextureData;
	Color32[] overlayTextureData;
	Color32[] occupationTextureData;
	byte[] landBuildSelectionTextureData;

	Color32[] terrainShapeTextureData;
	byte[] waterTopologyTextureData;
	byte[] mountainRidgeTextureData;
	bool[] mountainRidgeValid;

	bool[] visibilityTransitions;

	List<int> transitioningCellIndices = new();

	bool needsVisibilityReset;

	bool terrainShapeDirty;
	bool politicalTextureDirty;
	bool overlayTextureDirty;
	bool occupationTextureDirty;
	bool landBuildSelectionTextureDirty;

	public const byte LandBuildSelectionNone = 0;
	public const byte LandBuildSelectionBlocked = 1;
	public const byte LandBuildSelectionAllowed = 2;
	/// <summary>海运等：已选登船港，不可再次点选。</summary>
	public const byte LandBuildSelectionSeaLiftEmbark = 3;
	public const byte LandBuildSelectionAllowedHover = 4;
	/// <summary>海军微操：本航程内可达海洋。</summary>
	public const byte LandBuildSelectionAllowedSea = 5;
	public const byte LandBuildSelectionAllowedSeaHover = 6;
	/// <summary>海军选格：不可达/不可点的海洋（深蓝灰，与可达海蓝区分）。</summary>
	public const byte LandBuildSelectionBlockedSea = 7;

	public HexGrid Grid { get; set; }

	public bool ImmediateMode { get; set; }

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	static void RebindLoadedLogicalTextures()
	{
		HexCellShaderData[] instances =
			Resources.FindObjectsOfTypeAll<HexCellShaderData>();
		for (int i = 0; i < instances.Length; i++)
		{
			if (instances[i] && instances[i].gameObject.scene.IsValid())
			{
				instances[i].RebindAfterReload();
			}
		}
	}

	void RebindAfterReload()
	{
		if (Grid == null)
		{
			Grid = GetComponent<HexGrid>();
		}
		if (Grid == null || Grid.CellData == null || Grid.CellData.Length == 0)
		{
			return;
		}

		int cellCount = Grid.CellData.Length;
		if (!cellTexture || !terrainShapeTexture || !waterTopologyTexture ||
			!mountainRidgeTexture || !politicalTexture ||
			!politicalColorTexture ||
			!overlayTexture || !occupationTexture || !landBuildSelectionTexture ||
			cellTextureData == null ||
			politicalTextureData == null ||
			politicalColorTextureData == null || overlayTextureData == null ||
			occupationTextureData == null ||
			landBuildSelectionTextureData == null ||
			terrainShapeTextureData == null || waterTopologyTextureData == null ||
			mountainRidgeTextureData == null ||
			mountainRidgeValid == null ||
			visibilityTransitions == null ||
			cellTextureData.Length != cellCount ||
			politicalTextureData.Length != cellCount ||
			politicalColorTextureData.Length != cellCount ||
			overlayTextureData.Length != cellCount ||
			occupationTextureData.Length != cellCount ||
			terrainShapeTextureData.Length != cellCount ||
			waterTopologyTextureData.Length != cellCount ||
			mountainRidgeTextureData.Length != cellCount ||
			mountainRidgeValid.Length != cellCount ||
			visibilityTransitions.Length != cellCount)
		{
			Initialize(Grid.CellCountX, Grid.CellCountZ);
		}
		else
		{
			Shader.SetGlobalTexture("_HexCellData", cellTexture);
			Shader.SetGlobalTexture("_HexPoliticalData", politicalTexture);
			Shader.SetGlobalTexture(
				"_HexPoliticalColorData", politicalColorTexture);
			Shader.SetGlobalTexture("_HexCellOverlayData", overlayTexture);
			Shader.SetGlobalTexture("_HexOccupationData", occupationTexture);
			Shader.SetGlobalTexture(
				"_HexLandBuildSelectionData", landBuildSelectionTexture);
			Shader.SetGlobalTexture(
				"_HexTerrainShapeData", terrainShapeTexture);
			Shader.SetGlobalTexture("_HexWaterTopologyData", waterTopologyTexture);
			Shader.SetGlobalTexture("_HexMountainRidgeData", mountainRidgeTexture);
			Shader.SetGlobalVector(
				"_HexCellData_TexelSize",
				new Vector4(
					1f / Grid.CellCountX, 1f / Grid.CellCountZ,
					Grid.CellCountX, Grid.CellCountZ));
			Shader.SetGlobalFloat(
				"_HexTerrainShapeWrap", Grid.Wrapping ? 1f : 0f);
		}

		// Repack every texel as the logical layout can change between script
		// versions (currently the spare neighbor-mask bits carry HF forest type).
		for (int i = 0; i < cellCount; i++)
		{
			RefreshTerrain(i);
			RefreshTerrainShape(i);
			RefreshPolitical(i);
			RefreshVisibility(i);
		}
		HexGridChunk[] chunks =
			Grid.GetComponentsInChildren<HexGridChunk>(true);
		for (int i = 0; i < chunks.Length; i++)
		{
			chunks[i].Refresh();
		}
		enabled = true;
	}

	/// <summary>
	/// Initialze the map data.
	/// </summary>
	/// <param name="x">Map X size.</param>
	/// <param name="z">Map Z size.</param>
	public void Initialize(int x, int z)
	{
		if (cellTexture)
		{
			cellTexture.Reinitialize(x, z);
		}
		else
		{
			cellTexture = new Texture2D(x, z, TextureFormat.RGBA32, false, true)
			{
				filterMode = FilterMode.Point,
				wrapModeU = TextureWrapMode.Repeat,
				wrapModeV = TextureWrapMode.Clamp
			};
			Shader.SetGlobalTexture("_HexCellData", cellTexture);
		}
		Shader.SetGlobalVector(
			"_HexCellData_TexelSize",
			new Vector4(1f / x, 1f / z, x, z));

		if (politicalTexture)
		{
			politicalTexture.Reinitialize(x, z);
		}
		else
		{
			politicalTexture = new Texture2D(
				x, z, TextureFormat.RGBA32, false, true)
			{
				name = "Hex Political Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		politicalTexture.filterMode = FilterMode.Point;
		politicalTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		politicalTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture("_HexPoliticalData", politicalTexture);

		if (politicalColorTexture)
		{
			politicalColorTexture.Reinitialize(x, z);
		}
		else
		{
			politicalColorTexture = new Texture2D(
				x, z, TextureFormat.RGBA32, false, true)
			{
				name = "Hex Political Color Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		politicalColorTexture.filterMode = FilterMode.Point;
		politicalColorTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		politicalColorTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture(
			"_HexPoliticalColorData", politicalColorTexture);

		if (overlayTexture)
		{
			overlayTexture.Reinitialize(x, z);
		}
		else
		{
			overlayTexture = new Texture2D(
				x, z, TextureFormat.RGBA32, false, true)
			{
				name = "Hex Cell Overlay Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		overlayTexture.filterMode = FilterMode.Point;
		overlayTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		overlayTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture("_HexCellOverlayData", overlayTexture);

		if (occupationTexture)
		{
			occupationTexture.Reinitialize(x, z);
		}
		else
		{
			occupationTexture = new Texture2D(
				x, z, TextureFormat.RGBA32, false, true)
			{
				name = "Hex Occupation Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		occupationTexture.filterMode = FilterMode.Point;
		occupationTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		occupationTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture("_HexOccupationData", occupationTexture);

		if (landBuildSelectionTexture)
		{
			landBuildSelectionTexture.Reinitialize(x, z);
		}
		else
		{
			landBuildSelectionTexture = new Texture2D(
				x, z, TextureFormat.R8, false, true)
			{
				name = "Hex Land Build Selection Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		landBuildSelectionTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		landBuildSelectionTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture(
			"_HexLandBuildSelectionData", landBuildSelectionTexture);
		Shader.SetGlobalFloat("_HexLandBuildSelectionActive", 0f);
		Shader.SetGlobalFloat("_HexLandBuildSelectionStrength", 1f);

		if (terrainShapeTexture)
		{
			terrainShapeTexture.Reinitialize(x, z);
		}
		else
		{
			terrainShapeTexture = new Texture2D(
				x, z, TextureFormat.RGBA32, false, true)
			{
				name = "Hex Terrain Shape Data",
				filterMode = FilterMode.Point
			};
			Shader.SetGlobalTexture(
				"_HexTerrainShapeData", terrainShapeTexture);
		}
		terrainShapeTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		terrainShapeTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalFloat(
			"_HexTerrainShapeWrap", Grid.Wrapping ? 1f : 0f);

		// Keep nearshore classification separate from the two-ring HF coast bit:
		// that bit also protects shoreline tessellation and cannot mean one ring.
		if (waterTopologyTexture)
		{
			waterTopologyTexture.Reinitialize(x, z);
		}
		else
		{
			waterTopologyTexture = new Texture2D(x, z, TextureFormat.R8, false, true)
			{
				name = "Hex Water Topology Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		waterTopologyTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		waterTopologyTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture("_HexWaterTopologyData", waterTopologyTexture);

		// High ridges use a sparse six-bit graph. Keep it independent of the
		// full landform neighbor mask and the nearshore water classification.
		if (mountainRidgeTexture)
		{
			mountainRidgeTexture.Reinitialize(x, z);
		}
		else
		{
			mountainRidgeTexture = new Texture2D(x, z, TextureFormat.R8, false, true)
			{
				name = "Hex Mountain Ridge Data",
				filterMode = FilterMode.Point,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		mountainRidgeTexture.filterMode = FilterMode.Point;
		mountainRidgeTexture.wrapModeU = Grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		mountainRidgeTexture.wrapModeV = TextureWrapMode.Clamp;
		Shader.SetGlobalTexture("_HexMountainRidgeData", mountainRidgeTexture);

		if (cellTextureData == null || politicalTextureData == null ||
			politicalColorTextureData == null ||
			overlayTextureData == null || occupationTextureData == null ||
			landBuildSelectionTextureData == null ||
			terrainShapeTextureData == null || waterTopologyTextureData == null ||
			mountainRidgeTextureData == null ||
			mountainRidgeTextureData.Length != x * z ||
			mountainRidgeValid == null || mountainRidgeValid.Length != x * z ||
			cellTextureData.Length != x * z)
		{
			cellTextureData = new Color32[x * z];
			politicalTextureData = new Color32[x * z];
			politicalColorTextureData = new Color32[x * z];
			overlayTextureData = new Color32[x * z];
			occupationTextureData = new Color32[x * z];
			landBuildSelectionTextureData = new byte[x * z];
			terrainShapeTextureData = new Color32[x * z];
			waterTopologyTextureData = new byte[x * z];
			mountainRidgeTextureData = new byte[x * z];
			mountainRidgeValid = new bool[x * z];
			visibilityTransitions = new bool[x * z];
		}
		else
		{
			for (int i = 0; i < cellTextureData.Length; i++)
			{
				cellTextureData[i] = new Color32(0, 0, 0, 0);
				politicalTextureData[i] = new Color32(0, 0, 0, 0);
				politicalColorTextureData[i] = new Color32(0, 0, 0, 0);
				overlayTextureData[i] = new Color32(0, 0, 0, 0);
				occupationTextureData[i] = new Color32(0, 0, 0, 0);
				landBuildSelectionTextureData[i] = LandBuildSelectionNone;
				terrainShapeTextureData[i] = new Color32(0, 0, 0, 0);
				waterTopologyTextureData[i] = 0;
				mountainRidgeTextureData[i] = 0;
				mountainRidgeValid[i] = false;
				visibilityTransitions[i] = false;
			}
		}

		transitioningCellIndices.Clear();
		terrainShapeDirty = true;
		politicalTextureDirty = true;
		overlayTextureDirty = true;
		occupationTextureDirty = true;
		landBuildSelectionTextureDirty = true;
		enabled = true;
	}

	/// <summary>陆地建造/指令选格：0 不参与，1 不可选，2 可选（需全局 Active 才显示）。</summary>
	public void SetLandBuildSelection(int cellIndex, byte state)
	{
		if (landBuildSelectionTextureData == null || cellIndex < 0 ||
			cellIndex >= landBuildSelectionTextureData.Length)
		{
			return;
		}

		if (landBuildSelectionTextureData[cellIndex] == state)
		{
			return;
		}

		landBuildSelectionTextureData[cellIndex] = state;
		landBuildSelectionTextureDirty = true;
		enabled = true;
	}

	public void ClearLandBuildSelection(int cellIndex) =>
		SetLandBuildSelection(cellIndex, LandBuildSelectionNone);

	/// <summary>批量改选格数据后立刻上传，避免等 LateUpdate 才生效。</summary>
	public void FlushLandBuildSelectionTexture()
	{
		if (!landBuildSelectionTextureDirty || landBuildSelectionTexture == null ||
			landBuildSelectionTextureData == null)
		{
			return;
		}

		landBuildSelectionTexture.SetPixelData(landBuildSelectionTextureData, 0);
		landBuildSelectionTexture.Apply(false, false);
		landBuildSelectionTextureDirty = false;
	}

	/// <summary>
	/// Refresh the compact political owner texel. Country IDs are packed into
	/// two bytes so shaders can compare neighboring cells without baking a
	/// separate border mesh for the detailed near surface.
	/// </summary>
	public void RefreshPolitical(int cellIndex)
	{
		if (politicalTextureData == null || cellIndex < 0 ||
			cellIndex >= politicalTextureData.Length)
		{
			return;
		}

		HexCellData cell = Grid.CellData[cellIndex];
		ushort countryId = cell.IsUnderwater ? (ushort)0 : cell.CountryId;
		politicalTextureData[cellIndex] = new Color32(
			(byte)(countryId & 0xff),
			(byte)(countryId >> 8),
			cell.IsUnderwater ? (byte)0 : (byte)255,
			255);
		Color32 countryColor = default;
		if (countryId != 0 && Grid.TryGetCountryColor(
			countryId, out Color32 paletteColor))
		{
			countryColor = paletteColor;
			countryColor.a = 255;
		}
		politicalColorTextureData[cellIndex] = countryColor;
		politicalTextureDirty = true;
		enabled = true;
	}

	/// <summary>
	/// Repack the per-cell political display colors after replacing a palette.
	/// Country IDs stay in their compact texture; this second texture lets both
	/// sides of a close-up border emit their own colored transparent halo.
	/// </summary>
	public void RefreshPoliticalPalette()
	{
		if (Grid == null || Grid.CellData == null ||
			politicalTextureData == null || politicalColorTextureData == null)
		{
			return;
		}
		for (int i = 0; i < Grid.CellData.Length; i++)
		{
			RefreshPolitical(i);
		}
	}

	/// <summary>
	/// Set a translucent gameplay wash for one logical cell. The caller owns the
	/// opacity; shaders preserve terrain detail and feather the tint at the edge.
	/// </summary>
	public void SetCellOverlay(int cellIndex, Color color)
	{
		if (overlayTextureData == null || cellIndex < 0 ||
			cellIndex >= overlayTextureData.Length)
		{
			return;
		}
		overlayTextureData[cellIndex] = (Color32)color;
		overlayTextureDirty = true;
		enabled = true;
	}

	public void ClearCellOverlay(int cellIndex) =>
		SetCellOverlay(cellIndex, Color.clear);

	/// <summary>
	/// Vic3-style temporary occupation wash (diagonal stripes in shaders).
	/// Does not change core CountryId / overview borders.
	/// </summary>
	public void SetCellOccupation(int cellIndex, Color occupierColor)
	{
		if (occupationTextureData == null || cellIndex < 0 ||
			cellIndex >= occupationTextureData.Length)
		{
			return;
		}
		if (occupierColor.a > 0.01f && occupierColor.a < 0.05f)
		{
			occupierColor.a = 0.72f;
		}
		occupationTextureData[cellIndex] = (Color32)occupierColor;
		occupationTextureDirty = true;
		enabled = true;
	}

	public void ClearCellOccupation(int cellIndex)
	{
		if (occupationTextureData == null || cellIndex < 0 ||
			cellIndex >= occupationTextureData.Length)
		{
			return;
		}
		occupationTextureData[cellIndex] = new Color32(0, 0, 0, 0);
		occupationTextureDirty = true;
		enabled = true;
	}

	/// <summary>
	/// Refresh the terrain data of a cell.
	/// Supports water surfaces up to 30 units high.
	/// </summary>
	/// <param name="cell">Cell with changed terrain type.</param>
	public void RefreshTerrain(int cellIndex)
	{
		HexCellData cell = Grid.CellData[cellIndex];
		Color32 data = cellTextureData[cellIndex];
		data.b = cell.IsUnderwater ?
			(byte)(cell.WaterSurfaceY * (255f / 30f)) : (byte)0;
		data.a = (byte)cell.TerrainTypeIndex;
		cellTextureData[cellIndex] = data;
		enabled = true;
	}

	/// <summary>
	/// Refresh the compact logical terrain-shape texel of a cell. The texture is
	/// shared by the complete map, so it replaces per-chunk height maps while
	/// still allowing the GPU to reconstruct blended relief.
	/// </summary>
	/// <param name="cellIndex">Index of the changed cell.</param>
	public void RefreshTerrainShape(int cellIndex)
	{
		if (terrainShapeTextureData == null ||
			cellIndex < 0 || cellIndex >= terrainShapeTextureData.Length)
		{
			return;
		}

		HexCellData cell = Grid.CellData[cellIndex];
		int neighborMask = 0;
		bool hasAdjacentLand = false;
		bool isInCoastInfluenceBand = IsInCoastInfluenceBand(cell);
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (!Grid.TryGetCellIndex(
				cell.coordinates.Step(d), out int neighborIndex))
			{
				continue;
			}
			HexCellData neighbor = Grid.CellData[neighborIndex];
			hasAdjacentLand |= !neighbor.IsUnderwater;
			if (neighbor.landform != cell.landform ||
				cell.landform == HexLandform.Flat)
			{
				continue;
			}
			neighborMask |= 1 << (int)d;
		}

		// HF rotates every complete terrain stamp independently. Connected
		// mountains merge through their mixer/height overlap; aligning stamps to a
		// procedural ridge axis is a newer topology rule and changes HF's artwork.
		float ridgeAngle = cell.TerrainRotation * (Mathf.PI / 3f);
		float angle01 = Mathf.Repeat(
			ridgeAngle / (Mathf.PI * 2f) + 0.5f, 1f);
		int packedLandformAndAngle =
			((int)cell.landform << 6) |
			Mathf.Clamp(Mathf.RoundToInt(angle01 * 63f), 0, 63);

		int riverMask = 0;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (cell.HasRiverThroughEdge(d))
			{
				riverMask |= 1 << (int)d;
			}
		}

		float surfaceY = Mathf.Clamp(Grid.CellPositions[cellIndex].y, 0f, 30f);
		int packedNeighborsAndPlants = neighborMask |
			(Mathf.Clamp(cell.PlantLevel, 0, 3) << 6);
		// The two unused high bits of the river byte carry water topology for
		// shader LOD decisions. Keeping this in the logical map texture lets the
		// hull shader stabilize only coastline patches instead of tessellating the
		// entire world at the maximum level.
		int packedRiverAndWater = riverMask |
			(cell.IsUnderwater ? 1 << 6 : 0) |
			(isInCoastInfluenceBand ? 1 << 7 : 0);
		terrainShapeTextureData[cellIndex] = new Color32(
			(byte)packedLandformAndAngle,
			(byte)packedNeighborsAndPlants,
			(byte)packedRiverAndWater,
			(byte)Mathf.RoundToInt(surfaceY * (255f / 30f)));
		// 255 = land, 128 = water directly adjacent to land, 0 = deep water.
		// Includes islands, inland water and wrapped neighbors. No save-data or
		// authored terrain types are rewritten; edits refresh dependent cells.
		waterTopologyTextureData[cellIndex] = !cell.IsUnderwater ? (byte)255 :
			hasAdjacentLand ? (byte)128 : (byte)0;
		mountainRidgeTextureData[cellIndex] = (byte)(HexMountainRidgeGraph.ComputeMask(
			Grid, cellIndex % Grid.CellCountX, cellIndex / Grid.CellCountX) | ((int)cell.mountainMode << 6));
		mountainRidgeValid[cellIndex] = true;
		terrainShapeDirty = true;
		enabled = true;
	}

	/// <summary>
	/// Return the cached six-bit high-ridge graph mask used by the GPU.
	/// </summary>
	public int GetMountainRidgeMask(int cellIndex)
	{
		if (Grid == null || Grid.CellData == null || Grid.CellCountX <= 0 ||
			cellIndex < 0 || cellIndex >= Grid.CellData.Length)
		{
			return 0;
		}
		bool cacheAvailable = mountainRidgeTextureData != null &&
			mountainRidgeValid != null &&
			cellIndex < mountainRidgeTextureData.Length &&
			cellIndex < mountainRidgeValid.Length;
		if (cacheAvailable && mountainRidgeValid[cellIndex])
		{
			return mountainRidgeTextureData[cellIndex] & 63;
		}

		// CPU sampling can start before this cell's first shape refresh. Zero
		// is a valid isolated-node mask, so validity needs a separate marker.
		int mask = HexMountainRidgeGraph.ComputeMask(
			Grid, cellIndex % Grid.CellCountX, cellIndex / Grid.CellCountX);
		if (cacheAvailable)
		{
			mountainRidgeTextureData[cellIndex] = (byte)(mask | ((int)Grid.CellData[cellIndex].mountainMode << 6));
			mountainRidgeValid[cellIndex] = true;
			terrainShapeDirty = true;
			enabled = true;
		}
		return mask;
	}

	/// <summary>
	/// HF stamps overlap the source cell and its neighbors. Keep one additional
	/// logical ring around a wet/dry transition so every tessellation patch that
	/// can sample a sea stamp receives the same stable minimum subdivision.
	/// </summary>
	bool IsInCoastInfluenceBand(HexCellData cell)
	{
		bool underwater = cell.IsUnderwater;
		for (HexDirection firstDirection = HexDirection.NE;
			firstDirection <= HexDirection.NW; firstDirection++)
		{
			if (!Grid.TryGetCellIndex(
				cell.coordinates.Step(firstDirection), out int firstIndex))
			{
				continue;
			}

			HexCellData first = Grid.CellData[firstIndex];
			if (first.IsUnderwater != underwater)
			{
				return true;
			}

			for (HexDirection secondDirection = HexDirection.NE;
				secondDirection <= HexDirection.NW; secondDirection++)
			{
				if (Grid.TryGetCellIndex(
					first.coordinates.Step(secondDirection), out int secondIndex) &&
					Grid.CellData[secondIndex].IsUnderwater != underwater)
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// Refresh two logical rings around an edit. Both shoreline influence and
	/// the local mountain-triangle decisions can change together.
	/// </summary>
	public void RefreshTerrainShapeWithDependents(int cellIndex)
	{
		HashSet<int> affected = new() { cellIndex };
		HexCoordinates center = Grid.CellData[cellIndex].coordinates;
		for (HexDirection firstDirection = HexDirection.NE;
			firstDirection <= HexDirection.NW; firstDirection++)
		{
			if (!Grid.TryGetCellIndex(
				center.Step(firstDirection), out int firstIndex))
			{
				continue;
			}
			affected.Add(firstIndex);
			HexCoordinates first = Grid.CellData[firstIndex].coordinates;
			for (HexDirection secondDirection = HexDirection.NE;
				secondDirection <= HexDirection.NW; secondDirection++)
			{
				if (Grid.TryGetCellIndex(
					first.Step(secondDirection), out int secondIndex))
				{
					affected.Add(secondIndex);
				}
			}
		}
		foreach (int affectedIndex in affected)
		{
			RefreshTerrainShape(affectedIndex);
		}
	}

	/// <summary>
	/// Refresh visibility of a cell.
	/// </summary>
	/// <param name="cell">Cell with changed visibility.</param>
	public void RefreshVisibility(int cellIndex)
	{
		if (ImmediateMode)
		{
			cellTextureData[cellIndex].r = Grid.IsCellVisible(cellIndex) ?
				(byte)255 : (byte)0;
			cellTextureData[cellIndex].g = Grid.CellData[cellIndex].IsExplored ?
				(byte)255 : (byte)0;
		}
		else if (!visibilityTransitions[cellIndex])
		{
			visibilityTransitions[cellIndex] = true;
			transitioningCellIndices.Add(cellIndex);
		}
		enabled = true;
	}

	/// <summary>
	/// Indicate that view elevation data has changed,
	/// requiring a visibility reset.
	/// Supports water surfaces up to 30 units high.
	/// </summary>
	/// <param name="cell">Changed cell.</param>
	public void ViewElevationChanged(int cellIndex)
	{
		HexCellData cell = Grid.CellData[cellIndex];
		cellTextureData[cellIndex].b = cell.IsUnderwater ?
			(byte)(cell.WaterSurfaceY * (255f / 30f)) : (byte)0;
		needsVisibilityReset = true;
		enabled = true;
	}

	void LateUpdate()
	{
		if (needsVisibilityReset)
		{
			needsVisibilityReset = false;
			Grid.ResetVisibility();
		}

		int delta = (int)(Time.deltaTime * transitionSpeed);
		if (delta == 0)
		{
			delta = 1;
		}
		for (int i = 0; i < transitioningCellIndices.Count; i++)
		{
			if (!UpdateCellData(transitioningCellIndices[i], delta))
			{
				int lastIndex = transitioningCellIndices.Count - 1;
				transitioningCellIndices[i--] =
					transitioningCellIndices[lastIndex];
				transitioningCellIndices.RemoveAt(lastIndex);
			}
		}

		cellTexture.SetPixels32(cellTextureData);
		cellTexture.Apply(false, false);
		if (terrainShapeDirty)
		{
			terrainShapeTexture.SetPixels32(terrainShapeTextureData);
			terrainShapeTexture.Apply(false, false);
			waterTopologyTexture.SetPixelData(waterTopologyTextureData, 0);
			waterTopologyTexture.Apply(false, false);
			mountainRidgeTexture.SetPixelData(mountainRidgeTextureData, 0);
			mountainRidgeTexture.Apply(false, false);
			terrainShapeDirty = false;
		}
		if (politicalTextureDirty)
		{
			politicalTexture.SetPixels32(politicalTextureData);
			politicalTexture.Apply(false, false);
			politicalColorTexture.SetPixels32(politicalColorTextureData);
			politicalColorTexture.Apply(false, false);
			politicalTextureDirty = false;
		}
		if (overlayTextureDirty)
		{
			overlayTexture.SetPixels32(overlayTextureData);
			overlayTexture.Apply(false, false);
			overlayTextureDirty = false;
		}
		if (occupationTextureDirty)
		{
			occupationTexture.SetPixels32(occupationTextureData);
			occupationTexture.Apply(false, false);
			occupationTextureDirty = false;
		}
		if (landBuildSelectionTextureDirty)
		{
			landBuildSelectionTexture.SetPixelData(
				landBuildSelectionTextureData, 0);
			landBuildSelectionTexture.Apply(false, false);
			landBuildSelectionTextureDirty = false;
		}
		enabled = transitioningCellIndices.Count > 0;
	}

	void OnDestroy()
	{
		if (cellTexture)
		{
			Destroy(cellTexture);
		}
		if (politicalTexture)
		{
			Destroy(politicalTexture);
		}
		if (politicalColorTexture)
		{
			Destroy(politicalColorTexture);
		}
		if (overlayTexture)
		{
			Destroy(overlayTexture);
		}
		if (occupationTexture)
		{
			Destroy(occupationTexture);
		}
		if (landBuildSelectionTexture)
		{
			Destroy(landBuildSelectionTexture);
		}
		if (terrainShapeTexture)
		{
			Destroy(terrainShapeTexture);
		}
		if (waterTopologyTexture)
		{
			Destroy(waterTopologyTexture);
		}
		if (mountainRidgeTexture)
		{
			Destroy(mountainRidgeTexture);
		}
	}

	bool UpdateCellData(int index, int delta)
	{
		Color32 data = cellTextureData[index];
		bool stillUpdating = false;

		if (Grid.CellData[index].IsExplored && data.g < 255)
		{
			stillUpdating = true;
			int t = data.g + delta;
			data.g = t >= 255 ? (byte)255 : (byte)t;
		}

		if (Grid.IsCellVisible(index))
		{
			if (data.r < 255)
			{
				stillUpdating = true;
				int t = data.r + delta;
				data.r = t >= 255 ? (byte)255 : (byte)t;
			}
		}
		else if (data.r > 0)
		{
			stillUpdating = true;
			int t = data.r - delta;
			data.r = t < 0 ? (byte)0 : (byte)t;
		}

		if (!stillUpdating)
		{
			visibilityTransitions[index] = false;
		}
		cellTextureData[index] = data;
		return stillUpdating;
	}
}
