using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using HexMap.WorldData;
using UnityEditor;
using UnityEngine;

/// <summary>Read-only audit of the loaded gameplay world, disk river data and Area river crossings.</summary>
public static class HexWorldRiverValidation
{
	const string GameplayScene = "Assets/WarAndPeace/Scenes/Game_2.unity";
	const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
	const BindingFlags InstanceField = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

	[Serializable] public sealed class Report
	{
		public string timestamp, scene, diskMapPath, diskMapSha256, message;
		public bool passed, areaInitialized, areaGridMatches, diskWrapping, loadedWrapping;
		public int diskVersion, width, height, cellsChecked, diskPaletteCount;
		public int diskLandCells, loadedLandCells, diskCountryCount, loadedCountryCount, gameplayCountryCount;
		public int diskUnitCount, diskCityCount, loadedCityCount, gameplayCityCount, cityRecordMismatches;
		public int diskRiverCells, loadedRiverCells, loadedUniqueRiverEdges, loadedLegacyRiverCells;
		public int riverByteMismatches, terrainMismatches, countryMismatches, waterMismatches;
		public int invalidRiverBits, missingRiverNeighbors, riverReciprocityErrors, riverEdgesWithWaterOwners;
		public int gameplayAreas, missingAreas, gameplayCountryMismatches, gameplayRiverListMissing;
		public int expectedGameplayDirectedEdges, actualGameplayDirectedEdges, missingGameplayEdges;
		public int extraGameplayEdges, duplicateGameplayEdges, asymmetricGameplayEdges;
		public int regions, cityRegions, waterRegions, unsettledRegions, regionMembershipErrors, regionCityErrors, regionCountryErrors;
		public int[] firstMismatchCells;
		public string[] failures;
	}

	public static string Run(HexGrid grid, string artifactPath, out bool passed, out string message)
	{
		Report report = new() { timestamp = DateTime.UtcNow.ToString("O"),
			scene = grid ? grid.gameObject.scene.path : "missing",
			diskMapPath = Path.GetFullPath(Path.Combine(Application.dataPath, "HexMapPackage/Resources/Maps/DefaultWorld.bytes")) };
		List<string> failures = new();
		List<int> mismatches = new();
		try
		{
			if (!EditorApplication.isPlaying || !grid || report.scene != GameplayScene)
				throw new InvalidOperationException("World river validation requires the loaded Game_2 gameplay scene in Play mode.");
			if (grid.CellData == null || grid.CellData.Length != 515900 || grid.CellCountX != 1100 || grid.CellCountZ != 469)
				throw new InvalidOperationException("Expected the complete 515,900-cell gameplay HexGrid.");
			byte[] disk = File.ReadAllBytes(report.diskMapPath);
			using (SHA256 hash = SHA256.Create())
				report.diskMapSha256 = BitConverter.ToString(hash.ComputeHash(disk)).Replace("-", "").ToLowerInvariant();
			using MemoryStream stream = new(disk, false);
			using BinaryReader reader = new(stream);
			report.diskVersion = reader.ReadInt32(); report.width = reader.ReadInt32(); report.height = reader.ReadInt32();
			report.diskWrapping = reader.ReadBoolean(); report.loadedWrapping = grid.Wrapping;
			bool political = reader.ReadBoolean(); report.diskPaletteCount = reader.ReadUInt16();
			if ((report.diskVersion != 12 && report.diskVersion != 13) || report.width != 1100 || report.height != 469 ||
				!political || !grid.HasPoliticalData || !report.diskWrapping || !report.loadedWrapping)
				throw new InvalidDataException("Disk and loaded world must both be a wrapped political v12/v13 1100x469 map.");
			// V13 appends the author's mountain mode after each unchanged V12 cell.
			int cellStride = report.diskVersion >= 13 ? 21 : 20;
			int begin = checked(16 + report.diskPaletteCount * 4), end = checked(begin + 515900 * cellStride);
			if (disk.Length < end + 8) throw new InvalidDataException("Truncated world map.");
			HashSet<int> diskCountries = new(), loadedCountries = new();
			for (int i = 0; i < grid.CellData.Length; i++)
			{
				HexCellData cell = grid.CellData[i]; int offset = begin + cellStride * i;
				bool wet = disk[offset + 1] - 127 < disk[offset + 2];
				int country = disk[offset + 18] | disk[offset + 19] << 8;
				if (!wet) { report.diskLandCells++; if (country > 0) diskCountries.Add(country); }
				if (!cell.IsUnderwater) { report.loadedLandCells++; if (cell.CountryId > 0) loadedCountries.Add(cell.CountryId); }
				if (disk[offset + 17] != 0) report.diskRiverCells++;
				if (cell.HasHFRiver) report.loadedRiverCells++;
				if (cell.HasLegacyRiver) report.loadedLegacyRiverCells++;
				if (disk[offset + 17] != cell.hfRiverEdges) { report.riverByteMismatches++; Remember(mismatches, i); }
				if (country != cell.CountryId) { report.countryMismatches++; Remember(mismatches, i); }
				if (wet != cell.IsUnderwater) { report.waterMismatches++; Remember(mismatches, i); }
				if (cell.TerrainTypeIndex != disk[offset] || (int)cell.landform != disk[offset + 12] ||
					cell.Elevation != disk[offset + 1] - 127 || cell.WaterLevel != disk[offset + 2])
				{ report.terrainMismatches++; Remember(mismatches, i); }
				if ((cell.hfRiverEdges & ~63) != 0 || (disk[offset + 17] & ~63) != 0) report.invalidRiverBits++;
				for (int d = 0; d < 6; d++)
				{
					if (!cell.HasHFRiverThroughEdge((HexDirection)d)) continue;
					int neighbor = Neighbor(i, d, report.width, report.height);
					if (neighbor < 0) { report.missingRiverNeighbors++; Remember(mismatches, i); continue; }
					HexCellData other = grid.CellData[neighbor];
					if (!other.HasHFRiverThroughEdge((HexDirection)((d + 3) % 6)))
					{ report.riverReciprocityErrors++; Remember(mismatches, i); }
					if (i < neighbor)
					{
						report.loadedUniqueRiverEdges++;
						if (cell.IsUnderwater || other.IsUnderwater) { report.riverEdgesWithWaterOwners++; Remember(mismatches, i); }
					}
				}
				report.cellsChecked++;
			}
			report.diskCountryCount = diskCountries.Count; report.loadedCountryCount = loadedCountries.Count;
			reader.BaseStream.Position = end;
			report.diskUnitCount = reader.ReadInt32();
			if (report.diskUnitCount < 0 || report.diskUnitCount > 515900) throw new InvalidDataException("Invalid map unit count.");
			reader.BaseStream.Seek(checked(report.diskUnitCount * 12L), SeekOrigin.Current);
			report.diskCityCount = reader.ReadInt32(); report.loadedCityCount = grid.Cities.Count;
			if (report.diskCityCount < 0 || report.diskCityCount > 100000) throw new InvalidDataException("Invalid map city count.");
			if (report.loadedCityCount != report.diskCityCount) report.cityRecordMismatches++;
			for (int i = 0; i < report.diskCityCount; i++)
			{
				string name = reader.ReadString(); int source = reader.ReadInt32(), sourceCountry = reader.ReadInt32();
				int country = reader.ReadInt32(), area = reader.ReadInt32(), terrain = reader.ReadInt32();
				float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
				double longitude = reader.ReadDouble(), latitude = reader.ReadDouble(), u = reader.ReadDouble(), v = reader.ReadDouble();
				int target = reader.ReadInt32();
				WorldCityData city = i < grid.Cities.Count ? grid.Cities[i] : null;
				if (city == null || city.name != name || city.sourceTileId != source || city.sourceCountryId != sourceCountry ||
					city.countryId != country || city.areaId != area || city.terrainId != terrain || city.targetCellIndex != target ||
					city.sourceSpherePosition.x != x || city.sourceSpherePosition.y != y || city.sourceSpherePosition.z != z ||
					city.longitude != longitude || city.latitude != latitude || city.u != u || city.v != v)
				{ report.cityRecordMismatches++; Remember(mismatches, target); }
			}
			if (reader.BaseStream.Position != reader.BaseStream.Length) failures.Add("Unexpected bytes after the cities tail.");
			AuditGameplay(grid, report, mismatches, failures);
			if (report.loadedRiverCells == 0 || report.diskRiverCells == 0) failures.Add("The world contains no imported HF rivers.");
			if (report.riverByteMismatches > 0) failures.Add("Loaded HF river bytes differ from the on-disk DefaultWorld map.");
			if (report.countryMismatches + report.waterMismatches + report.terrainMismatches > 0)
				failures.Add("Loaded country/water/terrain records differ from the actual disk map.");
			if (report.invalidRiverBits + report.missingRiverNeighbors + report.riverReciprocityErrors + report.riverEdgesWithWaterOwners > 0)
				failures.Add("River masks contain invalid bits, missing or asymmetric neighbors, or forbidden water-bank edges.");
			if (report.loadedLegacyRiverCells > 0) failures.Add("Unexpected legacy center-river flags in the imported world.");
			if (report.cityRecordMismatches > 0) failures.Add("Loaded city records differ from the actual disk map.");
		}
		catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
		report.firstMismatchCells = mismatches.ToArray(); report.failures = failures.ToArray();
		passed = report.passed = failures.Count == 0;
		message = report.message = $"World river validation {(passed ? "passed" : "failed")}: {report.cellsChecked:N0} cells, " +
			$"{report.loadedUniqueRiverEdges:N0} river edges, {report.actualGameplayDirectedEdges:N0} gameplay crossings, " +
			$"{report.loadedCountryCount} mapped countries, {report.loadedCityCount} cities. " + string.Join(" ", report.failures);
		string output = Path.GetFullPath(artifactPath); Directory.CreateDirectory(Path.GetDirectoryName(output));
		File.WriteAllText(output, JsonUtility.ToJson(report, true));
		return output;
	}

	static void AuditGameplay(HexGrid grid, Report report, List<int> mismatches, List<string> failures)
	{
		Type init = Type.GetType("AreaInit, WarAndPeace", false), managerType = Type.GetType("GameManager, WarAndPeace", false);
		report.areaInitialized = init?.GetProperty("IsInitialized", PublicStatic)?.GetValue(null) is bool ready && ready;
		report.areaGridMatches = ReferenceEquals(init?.GetProperty("Grid", PublicStatic)?.GetValue(null), grid);
		object manager = managerType?.GetProperty("Instance", PublicStatic)?.GetValue(null);
		Array areas = managerType?.GetField("areas", InstanceField)?.GetValue(manager) as Array;
		report.gameplayAreas = areas?.Length ?? 0;
		if (!report.areaInitialized || !report.areaGridMatches || areas == null || areas.Length != grid.CellData.Length)
		{ failures.Add("AreaInit is not initialized against the audited grid, or gameplay Area count does not match."); return; }
		Type areaType = areas.GetType().GetElementType();
		AuditRegions(manager, managerType, areas, areaType, grid, report, failures);
		FieldInfo riverField = areaType.GetField("nearRivers", InstanceField), countryField = areaType.GetField("belong", InstanceField);
		FieldInfo terrainField = areaType.GetField("terrain", InstanceField);
		if (riverField == null || countryField == null || terrainField == null)
		{ failures.Add("Expected gameplay Area fields are unavailable."); return; }
		IList[] lists = new IList[areas.Length]; HashSet<int> countries = new();
		for (int i = 0; i < areas.Length; i++)
		{
			object area = areas.GetValue(i);
			if (area == null) { report.missingAreas++; Remember(mismatches, i); continue; }
			lists[i] = riverField.GetValue(area) as IList;
			if (lists[i] == null) report.gameplayRiverListMissing++;
			int country = (int)countryField.GetValue(area), terrain = (int)terrainField.GetValue(area);
			if (!grid.CellData[i].IsUnderwater && country > 0) countries.Add(country);
			if (country != grid.CellData[i].CountryId) { report.gameplayCountryMismatches++; Remember(mismatches, i); }
			if (terrain == 2) report.gameplayCityCount++;
		}
		report.gameplayCountryCount = countries.Count;
		for (int i = 0; i < areas.Length; i++)
		{
			IList actual = lists[i]; HexCellData cell = grid.CellData[i];
			for (int d = 0; d < 6; d++)
			{
				int neighbor = Neighbor(i, d, report.width, report.height);
				if (neighbor < 0 || cell.IsUnderwater || grid.CellData[neighbor].IsUnderwater ||
					!cell.HasRiverThroughEdge((HexDirection)d)) continue;
				report.expectedGameplayDirectedEdges++;
				if (actual == null || !actual.Contains(neighbor)) { report.missingGameplayEdges++; Remember(mismatches, i); }
			}
			if (actual == null) continue;
			report.actualGameplayDirectedEdges += actual.Count;
			for (int k = 0; k < actual.Count; k++)
			{
				if (!(actual[k] is int neighbor)) { report.extraGameplayEdges++; Remember(mismatches, i); continue; }
				bool expected = false;
				if ((uint)neighbor < (uint)areas.Length && !cell.IsUnderwater && !grid.CellData[neighbor].IsUnderwater)
					for (int d = 0; d < 6; d++)
						if (Neighbor(i, d, report.width, report.height) == neighbor && cell.HasRiverThroughEdge((HexDirection)d)) expected = true;
				if (!expected) { report.extraGameplayEdges++; Remember(mismatches, i); }
				for (int prior = 0; prior < k; prior++)
					if (Equals(actual[prior], neighbor)) { report.duplicateGameplayEdges++; Remember(mismatches, i); break; }
				if ((uint)neighbor >= (uint)areas.Length || lists[neighbor] == null || !lists[neighbor].Contains(i))
				{ report.asymmetricGameplayEdges++; Remember(mismatches, i); }
			}
		}
		if (report.missingAreas + report.gameplayRiverListMissing + report.missingGameplayEdges + report.extraGameplayEdges +
			report.duplicateGameplayEdges + report.asymmetricGameplayEdges > 0)
			failures.Add("Gameplay nearRivers lists do not exactly and bidirectionally represent the loaded land river edges.");
		if (report.gameplayCountryMismatches > 0) failures.Add("Gameplay country owners differ from the loaded map.");
	}

	static void AuditRegions(object manager, Type managerType, Array areas, Type areaType,
		HexGrid grid, Report report, List<string> failures)
	{
		IDictionary regions = (IDictionary)managerType.GetField("areaDivide").GetValue(manager);
		FieldInfo areaId = areaType.GetField("areaID"), terrain = areaType.GetField("terrain");
		int[] visits = new int[areas.Length];
		report.regions = regions.Count;
		foreach (DictionaryEntry entry in regions)
		{
			int id = (int)entry.Key;
			Type type = entry.Value.GetType();
			int city = (int)type.GetField("keyCityID").GetValue(entry.Value);
			IList members = (IList)type.GetField("areaIDs").GetValue(entry.Value);
			if (city >= 0) report.cityRegions++;
			else if (id >= 3000000) report.unsettledRegions++;
			else report.waterRegions++;
			int cityCount = 0;
			foreach (int cell in members)
			{
				if ((uint)cell >= (uint)areas.Length) { report.regionMembershipErrors++; continue; }
				visits[cell]++;
				object area = areas.GetValue(cell);
				if ((int)areaId.GetValue(area) != id) report.regionMembershipErrors++;
				if ((int)terrain.GetValue(area) == 2) cityCount++;
				bool expectsWater = id >= 1000000 && id < 3000000;
				if (grid.CellData[cell].IsUnderwater != expectsWater) report.regionMembershipErrors++;
				if (city >= 0 && (uint)city < (uint)areas.Length &&
					grid.CellData[cell].CountryId != grid.CellData[city].CountryId) report.regionCountryErrors++;
			}
			if (city >= 0 && ((uint)city >= (uint)areas.Length || cityCount != 1 ||
				(int)areaId.GetValue(areas.GetValue(city)) != id ||
				(int)terrain.GetValue(areas.GetValue(city)) != 2)) report.regionCityErrors++;
		}
		foreach (int count in visits) if (count != 1) report.regionMembershipErrors++;
		if (report.cityRegions != grid.Cities.Count || report.regionMembershipErrors + report.regionCityErrors + report.regionCountryErrors > 0)
			failures.Add("City/sea region membership, unique city centres or country ownership are inconsistent.");
	}

	static int Neighbor(int index, int direction, int width, int height)
	{
		int x = index % width, z = index / width, odd = z & 1;
		switch (direction)
		{
			case 0: x += odd; z++; break; case 1: x++; break; case 2: x += odd; z--; break;
			case 3: x += odd - 1; z--; break; case 4: x--; break; default: x += odd - 1; z++; break;
		}
		return z < 0 || z >= height ? -1 : (x + width) % width + z * width;
	}
	static void Remember(List<int> cells, int index)
	{
		if (cells.Count < 32 && !cells.Contains(index)) cells.Add(index);
	}
}
