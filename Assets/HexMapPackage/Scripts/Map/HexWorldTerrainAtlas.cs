using System;
using System.IO;
using UnityEngine;

/// <summary>
/// A baked geographic terrain classification, aligned to the existing world
/// grid. Records are south-to-north, row-major, with odd rows shifted east by
/// half a cell, exactly like GenerateMapFromLandMask. This is classification
/// data, not an elevation map: applying it never moves coasts or rewrites rivers.
/// </summary>
public sealed class HexWorldTerrainAtlas
{
	public const string ResourcePath = "Maps/EarthTerrain";
	public const int HeaderSize = 24;
	public const int RecordSize = 6;
	const string Magic = "HXEARTH1";
	const float CropTolerance = 0.000001f;

	readonly byte[] records;

	public int Width { get; }
	public int Height { get; }
	public float SourceVMin { get; }
	public float SourceVMax { get; }

	HexWorldTerrainAtlas(
		byte[] records, int width, int height, float sourceVMin, float sourceVMax)
	{
		this.records = records;
		Width = width;
		Height = height;
		SourceVMin = sourceVMin;
		SourceVMax = sourceVMax;
	}

	public static bool TryLoad(
		TextAsset asset, out HexWorldTerrainAtlas atlas, out string error)
	{
		if (!asset)
		{
			atlas = null;
			error = "The Earth terrain atlas is missing. Expected Resources/" +
				ResourcePath + ".bytes; build the geographic atlas before baking the world.";
			return false;
		}
		return TryLoad(asset.bytes, out atlas, out error);
	}

	/// <summary>
	/// Validate the entire file before accepting it. The accepted payload is
	/// copied so later changes to the caller's buffer cannot invalidate a check.
	/// </summary>
	public static bool TryLoad(
		byte[] bytes, out HexWorldTerrainAtlas atlas, out string error)
	{
		atlas = null;
		if (bytes == null || bytes.Length < HeaderSize)
		{
			error = "The Earth terrain atlas header is missing or truncated.";
			return false;
		}
		for (int i = 0; i < Magic.Length; i++)
		{
			if (bytes[i] != Magic[i])
			{
				error = "Unsupported Earth terrain atlas format; expected HXEARTH1.";
				return false;
			}
		}

		using BinaryReader reader = new(new MemoryStream(bytes, false));
		reader.BaseStream.Position = Magic.Length;
		int width = reader.ReadInt32();
		int height = reader.ReadInt32();
		float sourceVMin = reader.ReadSingle();
		float sourceVMax = reader.ReadSingle();
		if (width <= 0 || height <= 0 ||
			(long)width * height > (int.MaxValue - HeaderSize) / RecordSize)
		{
			error = "The Earth terrain atlas dimensions are invalid or too large.";
			return false;
		}
		if (!IsValidCrop(sourceVMin, sourceVMax))
		{
			error = "The Earth terrain atlas latitude crop must be finite and satisfy " +
				"0 <= sourceVMin < sourceVMax <= 1.";
			return false;
		}
		int cellCount = width * height;
		int expectedLength = HeaderSize + cellCount * RecordSize;
		if (bytes.Length != expectedLength)
		{
			error = $"The Earth terrain atlas has {bytes.Length:N0} bytes; " +
				$"{width}x{height} requires exactly {expectedLength:N0}.";
			return false;
		}
		for (int i = 0, offset = HeaderSize; i < cellCount; i++, offset += RecordSize)
		{
			if (bytes[offset] > 4 || bytes[offset + 1] > 3 ||
				bytes[offset + 2] > 6 || bytes[offset + 3] > 100 ||
				bytes[offset + 4] > 5 || bytes[offset + 5] > 5)
			{
				error = $"Invalid Earth terrain record at ({i % width}, {i / width}): " +
					$"terrain={bytes[offset]}, landform={bytes[offset + 1]}, " +
					$"vegetation={bytes[offset + 2]}, density={bytes[offset + 3]}, " +
					$"tint={bytes[offset + 4]}, rotation={bytes[offset + 5]}. " +
					"Expected ranges 0..4, 0..3, 0..6, 0..100, 0..5, 0..5.";
				return false;
			}
		}
		byte[] records = new byte[cellCount * RecordSize];
		Buffer.BlockCopy(bytes, HeaderSize, records, 0, records.Length);
		atlas = new HexWorldTerrainAtlas(records, width, height, sourceVMin, sourceVMax);
		error = null;
		return true;
	}

	/// <summary>
	/// Call before replacing a grid. Matching dimensions alone is insufficient:
	/// a different latitude crop would place every terrain feature incorrectly.
	/// </summary>
	public bool ValidateGeometry(
		int width, int height, float sourceVMin, float sourceVMax, out string error)
	{
		if (width != Width || height != Height ||
			!IsValidCrop(sourceVMin, sourceVMax) ||
			Math.Abs(sourceVMin - SourceVMin) > CropTolerance ||
			Math.Abs(sourceVMax - SourceVMax) > CropTolerance)
		{
			error = $"Earth terrain atlas geometry mismatch: atlas={Width}x{Height}, " +
				$"crop={SourceVMin:R}..{SourceVMax:R}; requested={width}x{height}, " +
				$"crop={sourceVMin:R}..{sourceVMax:R}. Rebuild the atlas for this grid.";
			return false;
		}
		error = null;
		return true;
	}

	/// <summary>
	/// Replace only dry-cell terrain presentation and the corresponding compact
	/// plant tier. Elevation, water, all flags, roads, rivers, owners, settlements,
	/// farms and underwater records remain untouched. Geometry checks complete
	/// before the first cell is changed. Call once when authoring/loading a map.
	/// </summary>
	public bool ApplyToGrid(
		HexGrid grid, float sourceVMin, float sourceVMax,
		out string error, bool refresh = true)
	{
		if (!grid || grid.CellData == null)
		{
			error = "Cannot apply the Earth terrain atlas: the grid has not been created.";
			return false;
		}
		if (!ValidateGeometry(
			grid.CellCountX, grid.CellCountZ, sourceVMin, sourceVMax, out error))
		{
			return false;
		}
		if (grid.CellData.Length != Width * Height)
		{
			error = "Cannot apply the Earth terrain atlas: grid dimensions and cell data disagree.";
			return false;
		}

		for (int i = 0, offset = 0; i < grid.CellData.Length; i++, offset += RecordSize)
		{
			HexCellData cell = grid.CellData[i];
			if (cell.IsUnderwater)
			{
				continue;
			}
			byte density = records[offset + 3];
			int plantLevel = density == 0 ? 0 : density <= 33 ? 1 : density <= 66 ? 2 : 3;
			cell.values = cell.values.WithTerrainTypeIndex(records[offset]).WithPlantLevel(plantLevel);
			cell.landform = (HexLandform)records[offset + 1];
			cell.vegetation = (HexVegetation)records[offset + 2];
			cell.vegetationDensity = density;
			cell.vegetationTint = (HexVegetationTint)records[offset + 4];
			cell.terrainRotation = records[offset + 5];
			grid.CellData[i] = cell;
		}
		if (refresh)
		{
			grid.RefreshAllCells();
		}
		error = null;
		return true;
	}

	static bool IsValidCrop(float min, float max) =>
		!float.IsNaN(min) && !float.IsInfinity(min) &&
		!float.IsNaN(max) && !float.IsInfinity(max) &&
		min >= 0f && min < max && max <= 1f;
}
