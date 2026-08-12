using System;
using System.Collections.Generic;
using UnityEngine;

namespace HexMap.WorldData
{
	/// <summary>
	/// One row from the sphere project's country_settings_5.tsv file.
	/// The TSV is the primary source for country names and IDs.
	/// </summary>
	[Serializable]
	public struct SphereCountrySetting
	{
		public string name;
		public int id;
		public Color32 color;
	}

	/// <summary>
	/// DTO matching CityBrushCities.json in the sphere project.
	/// Keep this source shape separate from the flat-map runtime model so the
	/// original brush data can always be reimported without information loss.
	/// </summary>
	[Serializable]
	public sealed class SphereCityDatabase
	{
		public int version = 1;
		public string savedAt = string.Empty;
		public List<SphereCityRecord> cities = new();
	}

	[Serializable]
	public sealed class SphereCityRecord
	{
		public int tileId;
		public string cityName = string.Empty;
		public int belongId;
		public int areaId;
		public int terrainId;
		public SerializableVector3 rawPosition;
		public SerializableVector3 position;
	}

	[Serializable]
	public struct SerializableVector3
	{
		public float x;
		public float y;
		public float z;

		public readonly Vector3 ToVector3() => new(x, y, z);

		public SerializableVector3(Vector3 value)
		{
			x = value.x;
			y = value.y;
			z = value.z;
		}
	}

	/// <summary>
	/// Country definition used by the flat map. IDs remain stable across the
	/// migration; country_settings_5.tsv supplies names while the final sphere
	/// cell buffer supplies the actual raster color.
	/// </summary>
	[Serializable]
	public sealed class CountryDefinition
	{
		public int id;
		public string name = string.Empty;
		public Color32 color = new(255, 255, 255, 255);
		public bool definedInPrimaryTable;
		public bool usedBySourceMap;
	}

	/// <summary>
	/// Sparse flat-map city record. sourceCountryId preserves the brush-time
	/// value; countryId is refreshed from the final sphere cell ownership.
	/// </summary>
	[Serializable]
	public sealed class WorldCityData
	{
		public string name = string.Empty;
		public int sourceTileId = -1;
		public int sourceCountryId;
		public int countryId;
		public int areaId;
		public int terrainId;
		public SerializableVector3 sourceSpherePosition;
		public double longitude;
		public double latitude;
		public double u;
		public double v;
		public int targetCellIndex = -1;
	}

	/// <summary>
	/// Serializable migration/runtime document shared by the importer, save
	/// system, political renderer, and city layer.
	/// </summary>
	[Serializable]
	public sealed class WorldPoliticalMapData
	{
		public int version = 1;
		public string source = string.Empty;
		public int sourceRasterWidth;
		public int sourceRasterHeight;
		public bool wrapsEastWest = true;
		public List<CountryDefinition> countries = new();
		public List<WorldCityData> cities = new();
	}
}
