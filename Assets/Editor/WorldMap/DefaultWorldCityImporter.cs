using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HexMap.WorldData;
using UnityEngine;

/// <summary>
/// Converts the edited sphere city's unit-vector position through the exact
/// equirectangular projection and latitude crop used by DefaultWorldMapBaker.
/// </summary>
public static class DefaultWorldCityImporter
{
	const string CityAssetPath =
		"Assets/MapData/WW2Migration/CityBrushCities.json";
	const string SphereCountryDataAssetPath =
		"Assets/MapData/WW2Migration/vert_buf_data_5.bytes";
	const int MaximumLandSearchRadius = 12;

	public readonly struct ImportResult
	{
		public readonly int imported;
		public readonly int relocatedToLand;
		public readonly int sharedCells;

		public ImportResult(int imported, int relocatedToLand, int sharedCells)
		{
			this.imported = imported;
			this.relocatedToLand = relocatedToLand;
			this.sharedCells = sharedCells;
		}
	}

	public static ImportResult Import(
		HexGrid grid, float sourceVMin, float sourceVMax)
	{
		if (!grid || grid.CellData == null || grid.CellData.Length == 0)
		{
			throw new ArgumentException("A generated HexGrid is required.", nameof(grid));
		}
		if (sourceVMin < 0f || sourceVMax > 1f || sourceVMax <= sourceVMin)
		{
			throw new ArgumentOutOfRangeException(
				nameof(sourceVMin), "The source latitude crop is invalid.");
		}

		SphereCityDatabase source = LoadCityDatabase();
		ushort[] finalOwnerByTile = LoadFinalSphereOwners();
		List<WorldCityData> imported = new(source.cities.Count);
		Dictionary<int, int> citiesPerCell = new();
		int relocatedToLand = 0;
		int sharedCells = 0;

		for (int i = 0; i < source.cities.Count; i++)
		{
			SphereCityRecord record = source.cities[i];
			if (record == null || string.IsNullOrWhiteSpace(record.cityName) ||
				record.tileId < 0 || record.tileId >= finalOwnerByTile.Length)
			{
				continue;
			}

			Vector3 spherePosition = record.rawPosition.ToVector3();
			if (spherePosition.sqrMagnitude < 0.5f)
			{
				continue;
			}
			spherePosition.Normalize();
			double longitudeRadians = Math.Atan2(
				spherePosition.z, spherePosition.x);
			double latitudeRadians = Math.Asin(
				Math.Clamp(spherePosition.y, -1f, 1f));
			double u = Repeat01(longitudeRadians / (Math.PI * 2.0) + 0.5);
			double v = latitudeRadians / Math.PI + 0.5;
			if (v < sourceVMin || v > sourceVMax)
			{
				continue;
			}

			ushort countryId = finalOwnerByTile[record.tileId];
			int initialCell = GetNearestProjectedCell(
				grid, u, v, sourceVMin, sourceVMax);
			int targetCell = FindNearestLandCell(
				grid, initialCell, u, v, sourceVMin, sourceVMax, countryId);
			if (targetCell < 0)
			{
				continue;
			}
			if (targetCell != initialCell)
			{
				relocatedToLand += 1;
			}

			if (citiesPerCell.TryGetValue(targetCell, out int cityCount))
			{
				citiesPerCell[targetCell] = cityCount + 1;
				sharedCells += 1;
			}
			else
			{
				citiesPerCell.Add(targetCell, 1);
			}

			imported.Add(new WorldCityData
			{
				name = record.cityName.Trim(),
				sourceTileId = record.tileId,
				sourceCountryId = record.belongId,
				countryId = countryId,
				areaId = record.areaId,
				terrainId = record.terrainId,
				sourceSpherePosition = new SerializableVector3(spherePosition),
				longitude = longitudeRadians * Mathf.Rad2Deg,
				latitude = latitudeRadians * Mathf.Rad2Deg,
				u = u,
				v = v,
				targetCellIndex = targetCell
			});
		}

		grid.SetCities(imported);
		return new ImportResult(imported.Count, relocatedToLand, sharedCells);
	}

	static SphereCityDatabase LoadCityDatabase()
	{
		string path = Path.Combine(ProjectRoot, CityAssetPath);
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("The sphere city export is missing.", path);
		}
		string json = Encoding.UTF8.GetString(File.ReadAllBytes(path));
		SphereCityDatabase database = JsonUtility.FromJson<SphereCityDatabase>(json);
		if (database?.cities == null)
		{
			throw new InvalidDataException("The sphere city export is invalid.");
		}
		return database;
	}

	static ushort[] LoadFinalSphereOwners()
	{
		string path = Path.Combine(ProjectRoot, SphereCountryDataAssetPath);
		if (!File.Exists(path))
		{
			throw new FileNotFoundException(
				"The sphere country buffer is missing.", path);
		}
		long byteLength = new FileInfo(path).Length;
		if (byteLength <= 0 || byteLength % 16 != 0 ||
			byteLength / 16 > int.MaxValue)
		{
			throw new InvalidDataException(
				"The sphere country buffer must contain four floats per tile.");
		}

		ushort[] owners = new ushort[(int)(byteLength / 16)];
		using BinaryReader reader = new(File.OpenRead(path));
		for (int i = 0; i < owners.Length; i++)
		{
			reader.ReadSingle();
			reader.ReadSingle();
			reader.ReadSingle();
			int rawId = Mathf.RoundToInt(reader.ReadSingle());
			if (rawId < 0 || rawId > ushort.MaxValue)
			{
				throw new InvalidDataException(
					$"Country ID {rawId} at sphere tile {i} is out of range.");
			}
			owners[i] = (ushort)rawId;
		}
		return owners;
	}

	static int GetNearestProjectedCell(
		HexGrid grid, double u, double v, float sourceVMin, float sourceVMax)
	{
		double normalizedV = (v - sourceVMin) / (sourceVMax - sourceVMin);
		int row = Mathf.Clamp(
			Mathf.FloorToInt((float)(normalizedV * grid.CellCountZ)),
			0, grid.CellCountZ - 1);
		double rowOffset = (row & 1) == 0 ? 0.0 : 0.5;
		int column = WrapColumn(
			(int)Math.Round(
				u * grid.CellCountX - rowOffset - 0.5,
				MidpointRounding.AwayFromZero),
			grid.CellCountX);
		return grid.GetCellIndex(column, row);
	}

	static int FindNearestLandCell(
		HexGrid grid, int initialCell, double cityU, double cityV,
		float sourceVMin, float sourceVMax, ushort countryId)
	{
		if (!grid.CellData[initialCell].IsUnderwater &&
			(countryId == 0 || grid.CellData[initialCell].CountryId == countryId))
		{
			return initialCell;
		}

		int initialRow = initialCell / grid.CellCountX;
		int initialColumn = initialCell - initialRow * grid.CellCountX;
		int bestCountryCell = -1;
		int bestLandCell = -1;
		double bestCountryDistance = double.PositiveInfinity;
		double bestLandDistance = double.PositiveInfinity;
		double latitudeScale = Math.Max(
			0.15, Math.Cos((cityV - 0.5) * Math.PI));

		for (int rowOffset = -MaximumLandSearchRadius;
			rowOffset <= MaximumLandSearchRadius; rowOffset++)
		{
			int row = initialRow + rowOffset;
			if (row < 0 || row >= grid.CellCountZ)
			{
				continue;
			}
			for (int columnOffset = -MaximumLandSearchRadius;
				columnOffset <= MaximumLandSearchRadius; columnOffset++)
			{
				int column = grid.Wrapping ?
					WrapColumn(initialColumn + columnOffset, grid.CellCountX) :
					initialColumn + columnOffset;
				if (column < 0 || column >= grid.CellCountX)
				{
					continue;
				}
				int cellIndex = grid.GetCellIndex(column, row);
				HexCellData cell = grid.CellData[cellIndex];
				if (cell.IsUnderwater)
				{
					continue;
				}

				double rowShift = (row & 1) == 0 ? 0.0 : 0.5;
				double cellU = Repeat01(
					(column + rowShift + 0.5) / grid.CellCountX);
				double deltaU = Math.Abs(cellU - cityU);
				deltaU = Math.Min(deltaU, 1.0 - deltaU) * latitudeScale;
				double cellV = sourceVMin + (sourceVMax - sourceVMin) *
					(row + 0.5) / grid.CellCountZ;
				double deltaV = cellV - cityV;
				double distance = deltaU * deltaU + deltaV * deltaV;

				if (distance < bestLandDistance)
				{
					bestLandDistance = distance;
					bestLandCell = cellIndex;
				}
				if (countryId != 0 && cell.CountryId == countryId &&
					distance < bestCountryDistance)
				{
					bestCountryDistance = distance;
					bestCountryCell = cellIndex;
				}
			}
		}

		return bestCountryCell >= 0 ? bestCountryCell : bestLandCell;
	}

	static int WrapColumn(int column, int width)
	{
		column %= width;
		return column < 0 ? column + width : column;
	}

	static double Repeat01(double value) => value - Math.Floor(value);

	static string ProjectRoot =>
		Directory.GetParent(Application.dataPath).FullName;
}
