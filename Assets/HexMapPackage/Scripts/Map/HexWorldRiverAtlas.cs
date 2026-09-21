using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Offline geographic river edges for the default 1100x469 cropped world.
/// Records are south-to-north row-major bytes; bits 0..5 are HexDirection
/// NE/E/SE/SW/W/NW. Each shared edge is stored on both adjacent dry cells.
/// HXRIVER1 has a 28-byte little-endian header followed by one byte per cell.
/// It does not contain simulation flow, river names, terrain or political data.
/// </summary>
public sealed class HexWorldRiverAtlas
{
	public const string ResourcePath = "Maps/EarthRivers";
	public const int HeaderSize = 28;
	public const int WorldWidth = 1100;
	public const int WorldHeight = 469;
	public const float WorldSourceVMin = 0.15f;
	public const float WorldSourceVMax = 0.8888889f;
	const string Magic = "HXRIVER1";
	const float CropTolerance = 0.000001f;

	readonly byte[] records;

	public int Width { get; }
	public int Height { get; }
	public float SourceVMin { get; }
	public float SourceVMax { get; }
	public int RiverCellCount { get; }
	public int UniqueEdgeCount { get; }

	HexWorldRiverAtlas(byte[] records, float sourceVMin, float sourceVMax,
		int riverCellCount, int edgeCount)
	{
		this.records = records;
		Width = WorldWidth;
		Height = WorldHeight;
		SourceVMin = sourceVMin;
		SourceVMax = sourceVMax;
		RiverCellCount = riverCellCount;
		UniqueEdgeCount = edgeCount;
	}

	public static bool TryLoad(TextAsset asset, out HexWorldRiverAtlas atlas, out string error)
	{
		if (!asset)
		{
			atlas = null;
			error = "The Earth river atlas is missing. Expected Resources/" +
				ResourcePath + ".bytes; build the geographic river atlas before baking the world.";
			return false;
		}
		return TryLoad(asset.bytes, out atlas, out error);
	}

	/// <summary>
	/// Fully validate the file and shared-edge reciprocity before copying its
	/// payload. This parser never changes a grid or relies on global wrap state.
	/// The current grid's dry land is checked separately before application.
	/// </summary>
	public static bool TryLoad(byte[] bytes, out HexWorldRiverAtlas atlas, out string error)
	{
		atlas = null;
		if (bytes == null || bytes.Length < HeaderSize)
		{
			error = "The Earth river atlas header is missing or truncated.";
			return false;
		}
		for (int i = 0; i < Magic.Length; i++)
		{
			if (bytes[i] != Magic[i])
			{
				error = "Unsupported Earth river atlas format; expected HXRIVER1.";
				return false;
			}
		}
		using BinaryReader reader = new(new MemoryStream(bytes, false));
		reader.BaseStream.Position = Magic.Length;
		int width = reader.ReadInt32();
		int height = reader.ReadInt32();
		float sourceVMin = reader.ReadSingle();
		float sourceVMax = reader.ReadSingle();
		int payloadLength = reader.ReadInt32();
		if (!IsWorldGeometry(width, height, sourceVMin, sourceVMax))
		{
			error = "The Earth river atlas requires the 1100x469 horizontally wrapped " +
				"world with latitude crop 0.15..0.8888889.";
			return false;
		}
		int cellCount = WorldWidth * WorldHeight;
		if (payloadLength != cellCount || bytes.Length != HeaderSize + cellCount)
		{
			error = $"The Earth river atlas requires exactly {cellCount:N0} payload bytes " +
				$"and {HeaderSize + cellCount:N0} total bytes; found payload={payloadLength:N0}, " +
				$"total={bytes.Length:N0}.";
			return false;
		}
		int riverCellCount = 0;
		int directedEdgeCount = 0;
		for (int i = 0; i < cellCount; i++)
		{
			byte mask = bytes[HeaderSize + i];
			if ((mask & 0xC0) != 0)
			{
				error = $"Invalid Earth river mask {mask} at ({i % width}, {i / width}); " +
					"only the low six direction bits are supported.";
				return false;
			}
			if (mask != 0)
			{
				riverCellCount++;
			}
			for (int d = 0; d < 6; d++)
			{
				if ((mask & (1 << d)) == 0)
				{
					continue;
				}
				int neighbor = NeighborIndex(i, d);
				if (neighbor < 0 ||
					(bytes[HeaderSize + neighbor] & (1 << ((d + 3) % 6))) == 0)
				{
					error = $"Earth river edge at ({i % width}, {i / width}) direction " +
						$"{(HexDirection)d} has no reciprocal neighbor edge.";
					return false;
				}
				directedEdgeCount++;
			}
		}
		if (directedEdgeCount == 0)
		{
			error = "The Earth river atlas is empty; bake a geographic river network first.";
			return false;
		}
		byte[] records = new byte[cellCount];
		Buffer.BlockCopy(bytes, HeaderSize, records, 0, cellCount);
		atlas = new HexWorldRiverAtlas(records, sourceVMin, sourceVMax,
			riverCellCount, directedEdgeCount / 2);
		error = null;
		return true;
	}

	public bool ValidateGeometry(int width, int height,
		float sourceVMin, float sourceVMax, out string error)
	{
		if (!IsWorldGeometry(width, height, sourceVMin, sourceVMax))
		{
			error = $"Earth river atlas geometry mismatch: requested {width}x{height}, " +
				$"crop={sourceVMin:R}..{sourceVMax:R}; expected " +
				$"{Width}x{Height}, crop={SourceVMin:R}..{SourceVMax:R}.";
			return false;
		}
		error = null;
		return true;
	}

	/// <summary>Read-only access to the validated mask for one row-major cell.</summary>
	public byte GetRiverEdgeMask(int cellIndex) => records[cellIndex];

	/// <summary>
	/// Validate against a supplied dry-land mask without constructing Unity
	/// objects. Reciprocal validation means checking both endpoint cells is
	/// equivalent to rejecting every record placed on water.
	/// </summary>
	public bool ValidateLandMask(bool[] dryLand, out string error)
	{
		if (dryLand == null || dryLand.Length != records.Length)
		{
			error = "Earth river atlas land-mask dimensions do not match its payload.";
			return false;
		}
		for (int i = 0; i < records.Length; i++)
		{
			if (records[i] != 0 && !dryLand[i])
			{
				error = WaterEdgeError(i);
				return false;
			}
		}
		error = null;
		return true;
	}

	/// <summary>
	/// Validate all geometry and water endpoints before the first mutation, then
	/// replace only hfRiverEdges. Existing terrain, legacy flags, roads, owners
	/// and cities are untouched. Intended for the default-world authoring bake;
	/// ordinary map loads and saved games retain their persisted river masks.
	/// </summary>
	public bool ApplyToGrid(HexGrid grid, float sourceVMin, float sourceVMax,
		out string error, bool refresh = true)
	{
		if (!grid || grid.CellData == null)
		{
			error = "Cannot apply the Earth river atlas: the grid has not been created.";
			return false;
		}
		if (!ApplyToCells(grid.CellData, grid.CellCountX, grid.CellCountZ,
			grid.Wrapping, sourceVMin, sourceVMax, out error))
		{
			return false;
		}
		if (refresh)
		{
			grid.RefreshAllCells();
		}
		return true;
	}

	/// <summary>
	/// The same validated operation on row-major cell data, without Unity scene
	/// lifecycle calls. Used by ApplyToGrid and offline authoring validation.
	/// No records are changed on failure.
	/// </summary>
	public bool ApplyToCells(HexCellData[] cells, int width, int height, bool wrapping,
		float sourceVMin, float sourceVMax, out string error)
	{
		if (!ValidateGeometry(width, height, sourceVMin, sourceVMax, out error))
		{
			return false;
		}
		if (cells == null || !wrapping || cells.Length != records.Length)
		{
			error = "Earth river atlas requires a horizontally wrapped grid with matching cell data.";
			return false;
		}
		for (int i = 0; i < records.Length; i++)
		{
			if (records[i] != 0 && cells[i].IsUnderwater)
			{
				error = WaterEdgeError(i);
				return false;
			}
		}
		for (int i = 0; i < records.Length; i++)
		{
			HexCellData cell = cells[i];
			cell.hfRiverEdges = records[i];
			cells[i] = cell;
		}
		error = null;
		return true;
	}

	string WaterEdgeError(int i) =>
		$"Earth river atlas places a shared river edge on water at ({i % Width}, {i / Width}). " +
		"Rebuild the river atlas for the current coastline; it requires two dry bank cells.";

	static bool IsWorldGeometry(int width, int height, float sourceVMin, float sourceVMax) =>
		width == WorldWidth && height == WorldHeight &&
		!float.IsNaN(sourceVMin) && !float.IsInfinity(sourceVMin) &&
		!float.IsNaN(sourceVMax) && !float.IsInfinity(sourceVMax) &&
		Math.Abs(sourceVMin - WorldSourceVMin) <= CropTolerance &&
		Math.Abs(sourceVMax - WorldSourceVMax) <= CropTolerance;

	static int NeighborIndex(int index, int direction)
	{
		int x = index % WorldWidth, y = index / WorldWidth;
		int odd = y & 1;
		switch (direction)
		{
			case 0: x += odd; y++; break;
			case 1: x++; break;
			case 2: x += odd; y--; break;
			case 3: x += odd - 1; y--; break;
			case 4: x--; break;
			case 5: x += odd - 1; y++; break;
		}
		if (y < 0 || y >= WorldHeight)
		{
			return -1;
		}
		return (x + WorldWidth) % WorldWidth + y * WorldWidth;
	}
}
