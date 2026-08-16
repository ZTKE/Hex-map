using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Component that generates hex maps.
/// </summary>
public class HexMapGenerator : MonoBehaviour
{
	[SerializeField]
	HexGrid grid;

	[SerializeField]
	bool useFixedSeed;

	[SerializeField]
	int seed;

	[SerializeField, Range(0f, 0.5f)]
	float jitterProbability = 0.25f;

	[SerializeField, Range(20, 200)]
	int chunkSizeMin = 30;

	[SerializeField, Range(20, 200)]
	int chunkSizeMax = 100;

	[SerializeField, Range(0f, 1f)]
	float highRiseProbability = 0.25f;

	[SerializeField, Range(0f, 0.4f)]
	float sinkProbability = 0.2f;

	[SerializeField, Range(5, 95)]
	int landPercentage = 50;

	[SerializeField, Range(1, 5)]
	int waterLevel = 3;

	[SerializeField, Range(-4, 0)]
	int elevationMinimum = -2;

	[SerializeField, Range(6, 10)]
	int elevationMaximum = 8;

	[SerializeField, Range(0, 10)]
	int mapBorderX = 5;

	[SerializeField, Range(0, 10)]
	int mapBorderZ = 5;

	[SerializeField, Range(0, 10)]
	int regionBorder = 5;

	[SerializeField, Range(1, 4)]
	int regionCount = 1;

	[SerializeField, Range(0, 100)]
	int erosionPercentage = 50;

	[SerializeField, Range(0f, 1f)]
	float startingMoisture = 0.1f;

	[SerializeField, Range(0f, 1f)]
	float evaporationFactor = 0.5f;

	[SerializeField, Range(0f, 1f)]
	float precipitationFactor = 0.25f;

	[SerializeField, Range(0f, 1f)]
	float runoffFactor = 0.25f;

	[SerializeField, Range(0f, 1f)]
	float seepageFactor = 0.125f;

	[SerializeField]
	HexDirection windDirection = HexDirection.NW;

	[SerializeField, Range(1f, 10f)]
	float windStrength = 4f;

	[SerializeField, Range(0, 20)]
	int riverPercentage = 10;

	[SerializeField, Range(0f, 1f)]
	float extraLakeProbability = 0.25f;

	[SerializeField, Range(0f, 1f)]
	float lowTemperature = 0f;

	[SerializeField, Range(0f, 1f)]
	float highTemperature = 1f;

	public enum HemisphereMode
	{
		Both, North, South
	}

	[SerializeField]
	HemisphereMode hemisphere;

	[SerializeField, Range(0f, 1f)]
	float temperatureJitter = 0.1f;

	HexCellPriorityQueue searchFrontier;

	int searchFrontierPhase;

	int cellCount, landCells;

	int temperatureJitterChannel;

	struct MapRegion
	{
		public int xMin, xMax, zMin, zMax;
	}

	List<MapRegion> regions;

	struct ClimateData {
		public float clouds, moisture;
	}

	List<ClimateData> climate = new();
	List<ClimateData> nextClimate = new();

	List<HexDirection> flowDirections = new();

	struct Biome
	{
		public int terrain, plant;

		public Biome(int terrain, int plant)
		{
			this.terrain = terrain;
			this.plant = plant;
		}
	}

	/// <summary>
	/// One complete HoneyFramework terrain definition adapted to the compact
	/// logical fields used by this project. HF selects a definition first; its
	/// ground, relief, foreground species, count, and colour then travel together.
	/// Keeping that coupling prevents coast distance or a later biome pass from
	/// accidentally deciding where forests and mountains may exist.
	/// </summary>
	readonly struct HFTerrainDefinition
	{
		public readonly int terrain;
		public readonly HexLandform landform;
		public readonly HexVegetation vegetation;
		public readonly byte vegetationDensity;
		public readonly HexVegetationTint vegetationTint;

		public HFTerrainDefinition(
			int terrain,
			HexLandform landform,
			HexVegetation vegetation,
			int vegetationDensity,
			HexVegetationTint vegetationTint = HexVegetationTint.Natural)
		{
			this.terrain = terrain;
			this.landform = landform;
			this.vegetation = vegetation;
			this.vegetationDensity =
				(byte)Mathf.Clamp(vegetationDensity, 0, 100);
			this.vegetationTint = vegetationTint;
		}
	}

	// These are the seven normal definitions in HoneyFramework.MHTerrain.xml.
	// Density converts each definition's authored sprite count to this project's
	// 0..100 foreground scale: Dirt 10, Mountain 24, dense Plains 85, and the
	// second forested Plains 55 sprites. Hill, bare Plains, and Marsh have none.
	static readonly HFTerrainDefinition[] hfTerrainDefinitions =
	{
		new(0, HexLandform.Flat, HexVegetation.Deadwood, 14,
			HexVegetationTint.Dry),
		new(1, HexLandform.Mountain, HexVegetation.Mixed, 28),
		new(1, HexLandform.Hill, HexVegetation.Mixed, 0),
		new(1, HexLandform.Flat, HexVegetation.Mixed, 0),
		new(1, HexLandform.Flat, HexVegetation.Mixed, 100),
		new(2, HexLandform.Flat, HexVegetation.Mixed, 0),
		new(1, HexLandform.Flat, HexVegetation.ColdMixed, 79,
			HexVegetationTint.DeepGreen)
	};

	static readonly float[] temperatureBands = { 0.1f, 0.3f, 0.6f };

	static readonly float[] moistureBands = { 0.12f, 0.28f, 0.85f };

	static readonly Biome[] biomes = {
		new(0, 0), new(4, 0), new(4, 0), new(4, 0),
		new(0, 0), new(2, 0), new(2, 1), new(2, 2),
		new(0, 0), new(1, 0), new(1, 1), new(1, 2),
		new(0, 0), new(1, 1), new(1, 2), new(1, 3)
	};

	/// <summary>
	/// Generate a random hex map.
	/// </summary>
	/// <param name="x">X size of the map.</param>
	/// <param name="z">Z size of the map.</param>
	/// <param name="wrapping">Whether east-west wrapping is enabled.</param>
	/// <returns>Whether the map passed validation and was generated.</returns>
	public bool GenerateMap(int x, int z, bool wrapping)
	{
		if (!HexGrid.TryValidateMapSize(x, z, out string validationMessage))
		{
			Debug.LogError(validationMessage);
			return false;
		}
		Random.State originalRandomState = Random.state;
		if (!useFixedSeed)
		{
			seed = Random.Range(0, int.MaxValue);
			seed ^= (int)System.DateTime.Now.Ticks;
			seed ^= (int)Time.unscaledTime;
			seed &= int.MaxValue;
		}
		Random.InitState(seed);

		cellCount = x * z;
		// Rendering uses one tabletop datum even when the simulation creates
		// elevated inland lakes. This keeps high logical lake levels from
		// becoming water platforms suspended above the flattened map.
		HexMetrics.visualWaterLevel = waterLevel;
		if (!grid.CreateMap(x, z, wrapping))
		{
			Random.state = originalRandomState;
			return false;
		}
		searchFrontier ??= new HexCellPriorityQueue(grid);
		for (int i = 0; i < cellCount; i++)
		{
			grid.CellData[i].values = grid.CellData[i].values.WithWaterLevel(
				waterLevel);
		}
		CreateRegions();
		CreateLand();
		ErodeLand();
		GenerateHFLand(x, z);
		grid.RefreshAllCells();

		Random.state = originalRandomState;
		return true;
	}

	/// <summary>
	/// Generate terrain while preserving the land and water silhouette of an
	/// equirectangular source image. The source pixel array must use Unity's
	/// bottom-left origin (the order returned by Texture2D.GetPixels32).
	/// </summary>
	public bool GenerateMapFromLandMask(
		Color32[] sourcePixels,
		int sourceWidth,
		int sourceHeight,
		int x,
		int z,
		bool wrapping,
		int worldSeed,
		Color32 oceanColor,
		float sourceVMin = 0f,
		float sourceVMax = 1f)
	{
		if (sourcePixels == null || sourceWidth <= 0 || sourceHeight <= 0 ||
			sourcePixels.Length != sourceWidth * sourceHeight)
		{
			Debug.LogError("The world land-mask image is invalid.");
			return false;
		}
		if (sourceVMin < 0f || sourceVMax > 1f ||
			sourceVMin >= sourceVMax)
		{
			Debug.LogError(
				"The world land-mask latitude crop must satisfy " +
				"0 <= sourceVMin < sourceVMax <= 1.");
			return false;
		}
		if (!HexGrid.TryValidateMapSize(x, z, out string validationMessage))
		{
			Debug.LogError(validationMessage);
			return false;
		}

		Random.State originalRandomState = Random.state;
		seed = worldSeed & int.MaxValue;
		Random.InitState(seed);
		cellCount = x * z;
		HexMetrics.visualWaterLevel = waterLevel;
		if (!grid.CreateMap(x, z, wrapping))
		{
			Random.state = originalRandomState;
			return false;
		}

		bool[] landMask = BuildLandMask(
			sourcePixels, sourceWidth, sourceHeight, x, z, oceanColor,
			sourceVMin, sourceVMax);
		landCells = ApplyMaskedElevation(landMask, x, z);
		try
		{
			GenerateHFLand(x, z);
			grid.RefreshAllCells();
		}
		finally
		{
			Random.state = originalRandomState;
		}
		return true;
	}

	bool[] BuildLandMask(
		Color32[] sourcePixels,
		int sourceWidth,
		int sourceHeight,
		int targetWidth,
		int targetHeight,
		Color32 oceanColor,
		float sourceVMin,
		float sourceVMax)
	{
		bool[] landMask = new bool[targetWidth * targetHeight];
		float du = 0.32f / targetWidth;
		float sourceVRange = sourceVMax - sourceVMin;
		float dv = 0.32f / targetHeight * sourceVRange;
		for (int row = 0, index = 0; row < targetHeight; row++)
		{
			float v = Mathf.Lerp(
				sourceVMin, sourceVMax, (row + 0.5f) / targetHeight);
			float rowOffset = (row & 1) == 0 ? 0f : 0.5f;
			for (int column = 0; column < targetWidth; column++, index++)
			{
				float u = Mathf.Repeat(
					(column + rowOffset + 0.5f) / targetWidth, 1f);
				bool centerIsLand = IsSourceLand(
					sourcePixels, sourceWidth, sourceHeight, u, v, oceanColor);
				int landVotes = centerIsLand ? 1 : 0;
				landVotes += IsSourceLand(
					sourcePixels, sourceWidth, sourceHeight,
					u - du, v - dv, oceanColor) ? 1 : 0;
				landVotes += IsSourceLand(
					sourcePixels, sourceWidth, sourceHeight,
					u + du, v - dv, oceanColor) ? 1 : 0;
				landVotes += IsSourceLand(
					sourcePixels, sourceWidth, sourceHeight,
					u - du, v + dv, oceanColor) ? 1 : 0;
				landVotes += IsSourceLand(
					sourcePixels, sourceWidth, sourceHeight,
					u + du, v + dv, oceanColor) ? 1 : 0;
				landMask[index] = centerIsLand || landVotes >= 3;
			}
		}
		return landMask;
	}

	static bool IsSourceLand(
		Color32[] sourcePixels,
		int sourceWidth,
		int sourceHeight,
		float u,
		float v,
		Color32 oceanColor)
	{
		int sourceX = Mathf.Clamp(
			Mathf.FloorToInt(Mathf.Repeat(u, 1f) * sourceWidth),
			0, sourceWidth - 1);
		int sourceY = Mathf.Clamp(
			Mathf.FloorToInt(Mathf.Clamp01(v) * sourceHeight),
			0, sourceHeight - 1);
		Color32 color = sourcePixels[sourceX + sourceY * sourceWidth];
		return color.r != oceanColor.r || color.g != oceanColor.g ||
			color.b != oceanColor.b;
	}

	int ApplyMaskedElevation(bool[] landMask, int width, int height)
	{
		int count = 0;
		for (int row = 0, index = 0; row < height; row++)
		{
			for (int column = 0; column < width; column++, index++)
			{
				HexCellData cell = grid.CellData[index];
				cell.values = cell.values.WithWaterLevel(waterLevel);
				if (!landMask[index])
				{
					cell.values = cell.values.
						WithElevation(waterLevel - 1).
						WithTerrainTypeIndex(0).
						WithPlantLevel(0);
					cell.landform = HexLandform.Flat;
					cell.vegetationDensity = 0;
					grid.CellData[index] = cell;
					continue;
				}

				count += 1;
				cell.values = cell.values.
					WithElevation(waterLevel).
					WithTerrainTypeIndex(1).
					WithPlantLevel(0);
				cell.landform = HexLandform.Flat;
				cell.vegetation = HexVegetation.Mixed;
				cell.vegetationDensity = 0;
				cell.vegetationTint = HexVegetationTint.Natural;
				grid.CellData[index] = cell;
			}
		}
		return count;
	}

	/// <summary>
	/// Assign complete HF definitions to every dry cell using HoneyFramework's
	/// default Random generator mode. Every hex independently selects one of the
	/// authored definitions, then its rotated mixer stamp overlaps its neighbors.
	/// Random.InitState at the generation entry point keeps the result repeatable.
	/// </summary>
	void GenerateHFLand(int width, int height)
	{
		for (int row = 0, index = 0; row < height; row++)
		{
			for (int column = 0; column < width; column++, index++)
			{
				HexCellData cell = grid.CellData[index];
				bool underwater = cell.IsUnderwater;
				cell.values = cell.values.
					WithWaterLevel(waterLevel).
					WithElevation(underwater ? waterLevel - 1 : waterLevel);
				cell.hfRiverEdges = 0;
				cell.terrainRotation = (byte)Random.Range(0, 6);

				if (underwater)
				{
					cell.values = cell.values.
						WithTerrainTypeIndex(0).
						WithPlantLevel(0);
					cell.landform = HexLandform.Flat;
					cell.vegetation = HexVegetation.Mixed;
					cell.vegetationDensity = 0;
					cell.vegetationTint = HexVegetationTint.Natural;
					grid.CellData[index] = cell;
					continue;
				}

				HFTerrainDefinition definition = hfTerrainDefinitions[
					Random.Range(0, hfTerrainDefinitions.Length)];
				int plantLevel = GetHFPlantLevel(definition.vegetationDensity);
				cell.values = cell.values.
					WithTerrainTypeIndex(definition.terrain).
					WithPlantLevel(plantLevel);
				cell.landform = definition.landform;
				cell.vegetation = definition.vegetation;
				cell.vegetationDensity = definition.vegetationDensity;
				cell.vegetationTint = definition.vegetationTint;
				grid.CellData[index] = cell;
			}
		}
	}

	static int GetHFPlantLevel(int density) =>
		density <= 0 ? 0 : density <= 33 ? 1 : density <= 66 ? 2 : 3;

	void CreateRegions()
	{
		if (regions == null)
		{
			regions = new();
		}
		else
		{
			regions.Clear();
		}

		int borderX = grid.Wrapping ? regionBorder : mapBorderX;
		MapRegion region;
		switch (regionCount)
		{
		default:
			if (grid.Wrapping)
			{
				borderX = 0;
			}
			region.xMin = borderX;
			region.xMax = grid.CellCountX - borderX;
			region.zMin = mapBorderZ;
			region.zMax = grid.CellCountZ - mapBorderZ;
			regions.Add(region);
			break;
		case 2:
			if (Random.value < 0.5f)
			{
				region.xMin = borderX;
				region.xMax = grid.CellCountX / 2 - regionBorder;
				region.zMin = mapBorderZ;
				region.zMax = grid.CellCountZ - mapBorderZ;
				regions.Add(region);
				region.xMin = grid.CellCountX / 2 + regionBorder;
				region.xMax = grid.CellCountX - borderX;
				regions.Add(region);
			}
			else
			{
				if (grid.Wrapping)
				{
					borderX = 0;
				}
				region.xMin = borderX;
				region.xMax = grid.CellCountX - borderX;
				region.zMin = mapBorderZ;
				region.zMax = grid.CellCountZ / 2 - regionBorder;
				regions.Add(region);
				region.zMin = grid.CellCountZ / 2 + regionBorder;
				region.zMax = grid.CellCountZ - mapBorderZ;
				regions.Add(region);
			}
			break;
		case 3:
			region.xMin = borderX;
			region.xMax = grid.CellCountX / 3 - regionBorder;
			region.zMin = mapBorderZ;
			region.zMax = grid.CellCountZ - mapBorderZ;
			regions.Add(region);
			region.xMin = grid.CellCountX / 3 + regionBorder;
			region.xMax = grid.CellCountX * 2 / 3 - regionBorder;
			regions.Add(region);
			region.xMin = grid.CellCountX * 2 / 3 + regionBorder;
			region.xMax = grid.CellCountX - borderX;
			regions.Add(region);
			break;
		case 4:
			region.xMin = borderX;
			region.xMax = grid.CellCountX / 2 - regionBorder;
			region.zMin = mapBorderZ;
			region.zMax = grid.CellCountZ / 2 - regionBorder;
			regions.Add(region);
			region.xMin = grid.CellCountX / 2 + regionBorder;
			region.xMax = grid.CellCountX - borderX;
			regions.Add(region);
			region.zMin = grid.CellCountZ / 2 + regionBorder;
			region.zMax = grid.CellCountZ - mapBorderZ;
			regions.Add(region);
			region.xMin = borderX;
			region.xMax = grid.CellCountX / 2 - regionBorder;
			regions.Add(region);
			break;
		}
	}

	void CreateLand()
	{
		int landBudget = Mathf.RoundToInt(cellCount * landPercentage * 0.01f);
		landCells = landBudget;
		for (int guard = 0; guard < 10000; guard++)
		{
			bool sink = Random.value < sinkProbability;
			for (int i = 0; i < regions.Count; i++)
			{
				MapRegion region = regions[i];
				int chunkSize = Random.Range(chunkSizeMin, chunkSizeMax - 1);
					if (sink)
				{
					landBudget = SinkTerrain(chunkSize, landBudget, region);
				}
				else
				{
					landBudget = RaiseTerrain(chunkSize, landBudget, region);
					if (landBudget == 0)
					{
						return;
					}
				}
			}
		}
		if (landBudget > 0)
		{
			Debug.LogWarning(
				"Failed to use up " + landBudget + " land budget.");
			landCells -= landBudget;
		}
	}

	int RaiseTerrain(int chunkSize, int budget, MapRegion region)
	{
		searchFrontierPhase += 1;
		int firstCellIndex = GetRandomCellIndex(region);
		grid.SearchData[firstCellIndex] = new HexCellSearchData
		{
			searchPhase = searchFrontierPhase
		};
		searchFrontier.Enqueue(firstCellIndex);
		HexCoordinates center = grid.CellData[firstCellIndex].coordinates;

		int rise = Random.value < highRiseProbability ? 2 : 1;
		int size = 0;
		while (size < chunkSize && searchFrontier.TryDequeue(out int index))
		{
			HexCellData current = grid.CellData[index];
			int originalElevation = current.Elevation;
			int newElevation = originalElevation + rise;
			if (newElevation > elevationMaximum)
			{
				continue;
			}
			grid.CellData[index].values =
				current.values.WithElevation(newElevation);
			if (originalElevation < waterLevel &&
				newElevation >= waterLevel && --budget == 0
			)
			{
				break;
			}
			size += 1;

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (grid.TryGetCellIndex(
					current.coordinates.Step(d), out int neighborIndex) &&
					grid.SearchData[neighborIndex].searchPhase <
						searchFrontierPhase)
				{
					grid.SearchData[neighborIndex] = new HexCellSearchData
					{
						searchPhase = searchFrontierPhase,
						distance = grid.CellData[neighborIndex].coordinates.
							DistanceTo(center),
						heuristic = Random.value < jitterProbability ? 1 : 0
					};
					searchFrontier.Enqueue(neighborIndex);
				}
			}
		}
		searchFrontier.Clear();
		return budget;
	}

	int SinkTerrain(int chunkSize, int budget, MapRegion region)
	{
		searchFrontierPhase += 1;
		int firstCellIndex = GetRandomCellIndex(region);
		grid.SearchData[firstCellIndex] = new HexCellSearchData
		{
			searchPhase = searchFrontierPhase
		};
		searchFrontier.Enqueue(firstCellIndex);
		HexCoordinates center = grid.CellData[firstCellIndex].coordinates;

		int sink = Random.value < highRiseProbability ? 2 : 1;
		int size = 0;
		while (size < chunkSize && searchFrontier.TryDequeue(out int index))
		{
			HexCellData current = grid.CellData[index];
			int originalElevation = current.Elevation;
			int newElevation = current.Elevation - sink;
			if (newElevation < elevationMinimum)
			{
				continue;
			}
			grid.CellData[index].values =
				current.values.WithElevation(newElevation);
			if (originalElevation >= waterLevel &&
				newElevation < waterLevel
			)
			{
				budget += 1;
			}
			size += 1;

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (grid.TryGetCellIndex(
					current.coordinates.Step(d), out int neighborIndex) &&
					grid.SearchData[neighborIndex].searchPhase <
						searchFrontierPhase)
				{
					grid.SearchData[neighborIndex] = new HexCellSearchData
					{
						searchPhase = searchFrontierPhase,
						distance = grid.CellData[neighborIndex].coordinates.
							DistanceTo(center),
						heuristic = Random.value < jitterProbability ? 1 : 0
					};
					searchFrontier.Enqueue(neighborIndex);
				}
			}
		}
		searchFrontier.Clear();
		return budget;
	}

	void ErodeLand()
	{
		List<int> erodibleIndices = ListPool<int>.Get();
		for (int i = 0; i < cellCount; i++)
		{
			if (IsErodible(i, grid.CellData[i].Elevation))
			{
				erodibleIndices.Add(i);
			}
		}

		int targetErodibleCount =
			(int)(erodibleIndices.Count * (100 - erosionPercentage) * 0.01f);
		
		while (erodibleIndices.Count > targetErodibleCount)
		{
			int index = Random.Range(0, erodibleIndices.Count);
			int cellIndex = erodibleIndices[index];
			HexCellData cell = grid.CellData[cellIndex];
			int targetCellIndex = GetErosionTarget(cellIndex, cell.Elevation);

			grid.CellData[cellIndex].values = cell.values =
				cell.values.WithElevation(cell.Elevation - 1);

			HexCellData targetCell = grid.CellData[targetCellIndex];
			grid.CellData[targetCellIndex].values = targetCell.values =
				targetCell.values.WithElevation(targetCell.Elevation + 1);

			if (!IsErodible(cellIndex, cell.Elevation))
			{
				int lastIndex = erodibleIndices.Count - 1;
				erodibleIndices[index] = erodibleIndices[lastIndex];
				erodibleIndices.RemoveAt(lastIndex);
			}

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (grid.TryGetCellIndex(
					cell.coordinates.Step(d), out int neighborIndex) &&
					grid.CellData[neighborIndex].Elevation ==
						cell.Elevation + 2 &&
					!erodibleIndices.Contains(neighborIndex))
				{
					erodibleIndices.Add(neighborIndex);
				}
			}

			if (IsErodible(targetCellIndex, targetCell.Elevation) &&
				!erodibleIndices.Contains(targetCellIndex))
			{
				erodibleIndices.Add(targetCellIndex);
			}

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (grid.TryGetCellIndex(
					targetCell.coordinates.Step(d), out int neighborIndex) &&
					neighborIndex != cellIndex &&
					grid.CellData[neighborIndex].Elevation ==
						targetCell.Elevation + 1 &&
					!IsErodible(
						neighborIndex, grid.CellData[neighborIndex].Elevation))
				{
					erodibleIndices.Remove(neighborIndex);
				}
			}
		}

		ListPool<int>.Add(erodibleIndices);
	}

	bool IsErodible(int cellIndex, int cellElevation)
	{
		int erodibleElevation = cellElevation - 2;
		HexCoordinates coordinates = grid.CellData[cellIndex].coordinates;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (grid.TryGetCellIndex(
				coordinates.Step(d), out int neighborIndex) &&
				grid.CellData[neighborIndex].Elevation <= erodibleElevation)
			{
				return true;
			}
		}
		return false;
	}

	int GetErosionTarget (int cellIndex, int cellElevation)
	{
		List<int> candidates = ListPool<int>.Get();
		int erodibleElevation = cellElevation - 2;
		HexCoordinates coordinates = grid.CellData[cellIndex].coordinates;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (grid.TryGetCellIndex(
				coordinates.Step(d), out int neighborIndex) &&
				grid.CellData[neighborIndex].Elevation <= erodibleElevation
			)
			{
				candidates.Add(neighborIndex);
			}
		}
		int target = candidates[Random.Range(0, candidates.Count)];
		ListPool<int>.Add(candidates);
		return target;
	}

	void CreateClimate()
	{
		climate.Clear();
		nextClimate.Clear();
		var initialData = new ClimateData
		{
			moisture = startingMoisture
		};
		var clearData = new ClimateData();
		for (int i = 0; i < cellCount; i++)
		{
			climate.Add(initialData);
			nextClimate.Add(clearData);
		}

		for (int cycle = 0; cycle < 40; cycle++)
		{
			for (int i = 0; i < cellCount; i++)
			{
				EvolveClimate(i);
			}
			(nextClimate, climate) = (climate, nextClimate);
		}
	}

	void EvolveClimate(int cellIndex)
	{
		HexCellData cell = grid.CellData[cellIndex];
		ClimateData cellClimate = climate[cellIndex];

		if (cell.IsUnderwater)
		{
			cellClimate.moisture = 1f;
			cellClimate.clouds += evaporationFactor;
		}
		else
		{
			float evaporation = cellClimate.moisture * evaporationFactor;
			cellClimate.moisture -= evaporation;
			cellClimate.clouds += evaporation;
		}

		float precipitation = cellClimate.clouds * precipitationFactor;
		cellClimate.clouds -= precipitation;
		cellClimate.moisture += precipitation;

		float cloudMaximum = 1f - cell.ViewElevation / (elevationMaximum + 1f);
		if (cellClimate.clouds > cloudMaximum)
		{
			cellClimate.moisture += cellClimate.clouds - cloudMaximum;
			cellClimate.clouds = cloudMaximum;
		}

		HexDirection mainDispersalDirection = windDirection.Opposite();
		float cloudDispersal = cellClimate.clouds * (1f / (5f + windStrength));
		float runoff = cellClimate.moisture * runoffFactor * (1f / 6f);
		float seepage = cellClimate.moisture * seepageFactor * (1f / 6f);
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (!grid.TryGetCellIndex(
				cell.coordinates.Step(d), out int neighborIndex))
			{
				continue;
			}
			ClimateData neighborClimate = nextClimate[neighborIndex];
			if (d == mainDispersalDirection)
			{
				neighborClimate.clouds += cloudDispersal * windStrength;
			}
			else
			{
				neighborClimate.clouds += cloudDispersal;
			}

			int elevationDelta = grid.CellData[neighborIndex].ViewElevation -
				cell.ViewElevation;
			if (elevationDelta < 0)
			{
				cellClimate.moisture -= runoff;
				neighborClimate.moisture += runoff;
			}
			else if (elevationDelta == 0)
			{
				cellClimate.moisture -= seepage;
				neighborClimate.moisture += seepage;
			}

			nextClimate[neighborIndex] = neighborClimate;
		}

		ClimateData nextCellClimate = nextClimate[cellIndex];
		nextCellClimate.moisture += cellClimate.moisture;
		if (nextCellClimate.moisture > 1f)
		{
			nextCellClimate.moisture = 1f;
		}
		nextClimate[cellIndex] = nextCellClimate;
		climate[cellIndex] = new ClimateData();
	}

	void CreateRivers()
	{
		List<int> riverOrigins = ListPool<int>.Get();
		for (int i = 0; i < cellCount; i++)
		{
			HexCellData cell = grid.CellData[i];
			if (cell.IsUnderwater)
			{
				continue;
			}
			ClimateData data = climate[i];
			float weight =
				data.moisture * (cell.Elevation - waterLevel) /
				(elevationMaximum - waterLevel);
			if (weight > 0.75f)
			{
				riverOrigins.Add(i);
				riverOrigins.Add(i);
			}
			if (weight > 0.5f)
			{
				riverOrigins.Add(i);
			}
			if (weight > 0.25f)
			{
				riverOrigins.Add(i);
			}
		}

		int riverBudget = Mathf.RoundToInt(landCells * riverPercentage * 0.01f);
		while (riverBudget > 0 && riverOrigins.Count > 0)
		{
			int index = Random.Range(0, riverOrigins.Count);
			int lastIndex = riverOrigins.Count - 1;
			int originIndex = riverOrigins[index];
			HexCellData origin = grid.CellData[originIndex];
			riverOrigins[index] = riverOrigins[lastIndex];
			riverOrigins.RemoveAt(lastIndex);

			if (!origin.HasRiver)
			{
				bool isValidOrigin = true;
				for (HexDirection d = HexDirection.NE;
					d <= HexDirection.NW; d++)
				{
					if (grid.TryGetCellIndex(
						origin.coordinates.Step(d), out int neighborIndex) &&
						(grid.CellData[neighborIndex].HasRiver ||
							grid.CellData[neighborIndex].IsUnderwater))
					{
						isValidOrigin = false;
						break;
					}
				}
				if (isValidOrigin)
				{
					riverBudget -= CreateRiver(originIndex);
				}
			}
		}

		if (riverBudget > 0)
		{
			Debug.LogWarning("Failed to use up river budget.");
		}

		ListPool<int>.Add(riverOrigins);
	}

	int CreateRiver(int originIndex)
	{
		int length = 1;
		int cellIndex = originIndex;
		int lastLandCellIndex = -1;
		HexCellData cell = grid.CellData[cellIndex];
		HexDirection direction = HexDirection.NE;
		while (!cell.IsUnderwater)
		{
			int minNeighborElevation = int.MaxValue;
			flowDirections.Clear();
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (!grid.TryGetCellIndex(
					cell.coordinates.Step(d), out int neighborIndex))
				{
					continue;
				}
				HexCellData neighbor = grid.CellData[neighborIndex];

				if (neighbor.Elevation < minNeighborElevation)
				{
					minNeighborElevation = neighbor.Elevation;
				}

				if (neighborIndex == originIndex || neighbor.HasIncomingRiver)
				{
					continue;
				}

				if (length > 1)
				{
					// HF rivers are chains of shared hex edges. Consecutive
					// segments must meet at one corner, so the next boundary has
					// to be adjacent to the boundary through which we arrived.
					HexDirection incomingEdge = direction.Opposite();
					if (d != incomingEdge.Previous() &&
						d != incomingEdge.Next())
					{
						continue;
					}
				}

				int delta = neighbor.Elevation - cell.Elevation;
				if (delta > 0)
				{
					continue;
				}

				if (neighbor.HasOutgoingRiver)
				{
					HexDirection neighborIncomingEdge = d.Opposite();
					HexDirection neighborOutgoingEdge =
						neighbor.OutgoingRiver;
					if (neighborOutgoingEdge !=
							neighborIncomingEdge.Previous() &&
						neighborOutgoingEdge != neighborIncomingEdge.Next())
					{
						// Crossing into the neighbor would meet its existing
						// river only at the cell center, not at a corner node.
						continue;
					}
					cell.flags = cell.flags.WithRiverOut(d);
					cell.hfRiverEdges |= (byte)(1 << (int)d);
					neighbor.flags = neighbor.flags.WithRiverIn(d.Opposite());
					neighbor.hfRiverEdges |=
						(byte)(1 << (int)d.Opposite());
					grid.CellData[cellIndex] = cell;
					grid.CellData[neighborIndex] = neighbor;
					return length;
				}

				if (delta < 0)
				{
					flowDirections.Add(d);
					flowDirections.Add(d);
					flowDirections.Add(d);
				}
				if (length == 1 ||
					(d != direction.Next2() && d != direction.Previous2()))
				{
					flowDirections.Add(d);
				}
				flowDirections.Add(d);
			}

			if (flowDirections.Count == 0)
			{
				if (length == 1)
				{
					return 0;
				}

				if (minNeighborElevation >= cell.Elevation)
				{
					cell.values = cell.values.WithWaterLevel(
						minNeighborElevation);
					if (minNeighborElevation == cell.Elevation)
					{
						cell.values = cell.values.WithElevation(
							minNeighborElevation - 1);
					}
					grid.CellData[cellIndex].values = cell.values;
				}
				break;
			}

			direction = flowDirections[Random.Range(0, flowDirections.Count)];
			cell.flags = cell.flags.WithRiverOut(direction);
			cell.hfRiverEdges |= (byte)(1 << (int)direction);
			grid.TryGetCellIndex(
				cell.coordinates.Step(direction), out int outIndex);
			HexCellData outCell = grid.CellData[outIndex];
			outCell.flags = outCell.flags.WithRiverIn(direction.Opposite());
			outCell.hfRiverEdges |=
				(byte)(1 << (int)direction.Opposite());
			grid.CellData[outIndex] = outCell;

			length += 1;

			if (minNeighborElevation >= cell.Elevation &&
				Random.value < extraLakeProbability)
			{
				cell.values = cell.values.WithWaterLevel(cell.Elevation);
				cell.values = cell.values.WithElevation(cell.Elevation - 1);
			}
			grid.CellData[cellIndex] = cell;
			lastLandCellIndex = cellIndex;
			cellIndex = outIndex;
			cell = grid.CellData[cellIndex];
		}
		if (cell.IsUnderwater && lastLandCellIndex >= 0)
		{
			grid.ExtendHFRiverMouth(lastLandCellIndex);
		}
		return length;
	}

	void SetTerrainType()
	{
		temperatureJitterChannel = Random.Range(0, 4);
		int rockDesertElevation =
			elevationMaximum - (elevationMaximum - waterLevel) / 2;
		
		for (int i = 0; i < cellCount; i++)
		{
			HexCellData cell = grid.CellData[i];
			float temperature = DetermineTemperature(i, cell);
			float moisture = climate[i].moisture;
			if (!cell.IsUnderwater)
			{
				int t = 0;
				for (; t < temperatureBands.Length; t++)
				{
					if (temperature < temperatureBands[t])
					{
						break;
					}
				}
				int m = 0;
				for (; m < moistureBands.Length; m++)
				{
					if (moisture < moistureBands[m])
					{
						break;
					}
				}
				Biome cellBiome = biomes[t * 4 + m];

				if (cellBiome.terrain == 0)
				{
					if (cell.Elevation >= rockDesertElevation)
					{
						cellBiome.terrain = 3;
					}
				}
				else if (cell.Elevation == elevationMaximum)
				{
					cellBiome.terrain = 4;
				}

				if (cellBiome.terrain == 4)
				{
					cellBiome.plant = 0;
				}
				else if (cellBiome.plant < 3 && cell.HasRiver)
				{
					cellBiome.plant += 1;
				}
				HexCellData generatedCell = grid.CellData[i];
				generatedCell.values = cell.values.
					WithTerrainTypeIndex(cellBiome.terrain).
					WithPlantLevel(cellBiome.plant);
				generatedCell.vegetation = cellBiome.terrain switch
				{
					4 => HexVegetation.ColdMixed,
					3 => HexVegetation.Deadwood,
					_ => HexVegetation.Mixed
				};
				generatedCell.vegetationDensity = (byte)(cellBiome.plant == 3 ?
					100 : cellBiome.plant * 33);
				grid.CellData[i] = generatedCell;
			}
			else
			{
				int terrain;
				if (cell.Elevation == waterLevel - 1)
				{
					int cliffs = 0, slopes = 0;
					for (HexDirection d = HexDirection.NE;
						d <= HexDirection.NW; d++)
					{
						if (!grid.TryGetCellIndex(
							cell.coordinates.Step(d), out int neighborIndex))
						{
							continue;
						}
						int delta = grid.CellData[neighborIndex].Elevation -
							cell.WaterLevel;
						if (delta == 0)
						{
							slopes += 1;
						}
						else if (delta > 0)
						{
							cliffs += 1;
						}
					}

					if (cliffs + slopes > 3)
					{
						terrain = 1;
					}
					else if (cliffs > 0)
					{
						terrain = 3;
					}
					else if (slopes > 0)
					{
						terrain = 0;
					}
					else
					{
						terrain = 1;
					}
				}
				else if (cell.Elevation >= waterLevel)
				{
					terrain = 1;
				}
				else if (cell.Elevation < 0)
				{
					terrain = 3;
				}
				else
				{
					terrain = 2;
				}

				if (terrain == 1 && temperature < temperatureBands[0])
				{
					terrain = 2;
				}
				grid.CellData[i].values =
					cell.values.WithTerrainTypeIndex(terrain);
			}
		}
	}

	void SetLandforms()
	{
		bool[] mountainSeeds = new bool[cellCount];
		for (int i = 0; i < cellCount; i++)
		{
			HexCellData cell = grid.CellData[i];
			if (cell.IsUnderwater || cell.HasRiver || cell.IsSpecial)
			{
				continue;
			}
			int heightAboveWater = cell.Elevation - waterLevel;
			float seedOffset = (seed % 997) * 0.013f;
			float ridgeNoise = 1f - Mathf.Abs(
				Mathf.PerlinNoise(
					cell.coordinates.HexX * 0.115f + seedOffset,
					cell.coordinates.HexZ * 0.115f - seedOffset) * 2f - 1f);
			// The narrow band supplies the spine; a second pass grows a few
			// neighboring cells so topology modules form readable mountain chains.
			mountainSeeds[i] =
				(heightAboveWater >= 4 && ridgeNoise > 0.965f) ||
				(heightAboveWater >= 2 && ridgeNoise > 0.985f);
		}

		for (int i = 0; i < cellCount; i++)
		{
			HexCellData cell = grid.CellData[i];
			if (cell.IsUnderwater || cell.HasRiver || cell.IsSpecial)
			{
				cell.landform = HexLandform.Flat;
			}
			else
			{
				HexHash hash = HexMetrics.SampleHashGrid(grid.CellPositions[i]);
				int heightAboveWater = cell.Elevation - waterLevel;
				bool mountain = mountainSeeds[i];
				if (!mountain && heightAboveWater >= 2 && hash.e < 0.42f)
				{
					for (HexDirection d = HexDirection.NE;
						d <= HexDirection.NW; d++)
					{
						if (grid.TryGetCellIndex(
							cell.coordinates.Step(d), out int neighborIndex) &&
							mountainSeeds[neighborIndex])
						{
							mountain = true;
							break;
						}
					}
				}
				if (mountain)
				{
					cell.landform = HexLandform.Mountain;
				}
				else if (
					(heightAboveWater >= 1 && hash.b < 0.34f) ||
					hash.b < 0.08f)
				{
					cell.landform = HexLandform.Hill;
				}
				else
				{
					cell.landform = HexLandform.Flat;
				}
			}
			grid.CellData[i] = cell;
		}
	}

	float DetermineTemperature(int cellIndex, HexCellData cell)
	{
		float latitude = (float)cell.coordinates.Z /
			grid.CellCountZ;
		if (hemisphere == HemisphereMode.Both)
		{
			latitude *= 2f;
			if (latitude > 1f)
			{
				latitude = 2f - latitude;
			}
		}
		else if (hemisphere == HemisphereMode.North)
		{
			latitude = 1f - latitude;
		}

		float temperature =
			Mathf.LerpUnclamped(lowTemperature, highTemperature, latitude);

		temperature *= 1f -
			(cell.ViewElevation - waterLevel) /
			(elevationMaximum - waterLevel + 1f);

		float jitter = HexMetrics.SampleNoise(
			grid.CellPositions[cellIndex] * 0.1f)[temperatureJitterChannel];

		temperature += (jitter * 2f - 1f) * temperatureJitter;

		return temperature;
	}

	int GetRandomCellIndex (MapRegion region) => grid.GetCellIndex(
		Random.Range(region.xMin, region.xMax),
		Random.Range(region.zMin, region.zMax));
}
