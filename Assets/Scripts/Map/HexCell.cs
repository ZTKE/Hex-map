using UnityEngine;

/// <summary>
/// Struct that identifies a hex cell.
/// </summary>
[System.Serializable]
public struct HexCell
{
#pragma warning disable IDE0044 // Add readonly modifier
	int index;

	HexGrid grid;
#pragma warning restore IDE0044 // Add readonly modifier

	/// <summary>
	/// Creates a cell given an index and grid.
	/// </summary>
	/// <param name="index">Index of the cell.</param>
	/// <param name="grid">Grid the cell is a part of.</param>
	public HexCell(int index, HexGrid grid)
	{
		this.index = index;
		this.grid = grid;
	}

	/// <summary>
	/// Hexagonal coordinates unique to the cell.
	/// </summary>
	public readonly HexCoordinates Coordinates =>
		grid.CellData[index].coordinates;

	/// <summary>
	/// Unique global index of the cell.
	/// </summary>
	public readonly int Index => index;

	/// <summary>
	/// Visible surface position of the cell center. The grid keeps its logical
	/// Catlike base position internally, while public placement follows the
	/// active HF surface authority.
	/// </summary>
	public readonly Vector3 Position => grid.GetSurfacePosition(index);

	/// <summary>
	/// Set the elevation level.
	/// </summary>
	/// <param name="elevation">Elevation level.</param>
	public readonly void SetElevation (int elevation)
	{
		if (Values.Elevation != elevation)
		{
			Values = Values.WithElevation(elevation);
			grid.ShaderData.ViewElevationChanged(index);
			grid.RefreshWaterDepthsAround(index);
			ValidateRivers();
			HexFlags flags = Flags;
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (flags.HasRoad(d))
				{
					HexCell neighbor = GetNeighbor(d);
					if (Mathf.Abs(elevation - neighbor.Values.Elevation) > 1)
					{
						RemoveRoad(d);
					}
				}
			}
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Set the water level.
	/// </summary>
	/// <param name="waterLevel">Water level.</param>
	public readonly void SetWaterLevel (int waterLevel)
	{
		if (Values.WaterLevel != waterLevel)
		{
			Values = Values.WithWaterLevel(waterLevel);
			grid.ShaderData.ViewElevationChanged(index);
			// A water edit can change the shallow/deep tier of this cell and all
			// directly adjacent sea cells.
			grid.RefreshWaterDepthsAround(index);
			ValidateRivers();
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Set the urban level.
	/// </summary>
	/// <param name="urbanLevel">Urban level.</param>
	public readonly void SetUrbanLevel (int urbanLevel)
	{
		if (Values.UrbanLevel != urbanLevel)
		{
			Values = Values.WithUrbanLevel(urbanLevel);
			Refresh();
		}
	}

	/// <summary>
	/// Set the farm level.
	/// </summary>
	/// <param name="farmLevel">Farm level.</param>
	public readonly void SetFarmLevel (int farmLevel)
	{
		if (Values.UrbanLevel != farmLevel)
		{
			Values = Values.WithFarmLevel(farmLevel);
			Refresh();
		}
	}

	/// <summary>
	/// Set the plant level.
	/// </summary>
	/// <param name="plantLevel">Plant level.</param>
	public readonly void SetPlantLevel(int plantLevel)
	{
		if (Values.PlantLevel != plantLevel)
		{
			Values = Values.WithPlantLevel(plantLevel);
			Refresh();
		}
	}

	/// <summary>
	/// Set the special index.
	/// </summary>
	/// <param name="specialIndex">Special index.</param>
	public readonly void SetSpecialIndex (int specialIndex)
	{
		if (Values.SpecialIndex != specialIndex &&
			Flags.HasNone(HexFlags.River))
		{
			Values = Values.WithSpecialIndex(specialIndex);
			RemoveRoads();
			Refresh();
		}
	}

	/// <summary>
	/// Set whether the cell is walled.
	/// </summary>
	/// <param name="walled">Whether the cell is walled.</param>
	public readonly void SetWalled (bool walled)
	{
		HexFlags flags = Flags;
		HexFlags newFlags = walled ?
			flags.With(HexFlags.Walled) : flags.Without(HexFlags.Walled);
		if (flags != newFlags)
		{
			Flags = newFlags;
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Set the terrain type index.
	/// </summary>
	/// <param name="terrainTypeIndex">Terrain type index.</param>
	public readonly void SetTerrainTypeIndex (int terrainTypeIndex)
	{
		if (Values.TerrainTypeIndex != terrainTypeIndex)
		{
			Values = Values.WithTerrainTypeIndex(terrainTypeIndex);
			grid.ShaderData.RefreshTerrain(index);
			// HF foreground definitions are part of the terrain type, so the chunk
			// batch must be rebuilt together with the logical material byte.
			grid.RefreshCell(index);
		}
	}

	/// <summary>
	/// Unit currently occupying the cell, if any.
	/// </summary>
	public readonly HexUnit Unit
	{
		get => grid.CellUnits[index];
		set => grid.CellUnits[index] = value;
	}

	/// <summary>
	/// Flags of the cell.
	/// </summary>
	public readonly HexFlags Flags
	{
		get => grid.CellData[index].flags;
		set => grid.CellData[index].flags = value;
	}

	/// <summary>
	/// Values of the cell.
	/// </summary>
	public readonly HexValues Values
	{
		get => grid.CellData[index].values;
		set => grid.CellData[index].values = value;
	}

	/// <summary>
	/// Local visual terrain shape.
	/// </summary>
	public readonly HexLandform Landform => grid.CellData[index].landform;

	public readonly void SetLandform(HexLandform landform)
	{
		if (grid.CellData[index].landform != landform)
		{
			grid.CellData[index].landform = landform;
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Get one of the neighbor cells. Only valid if that neighbor exists.
	/// </summary>
	/// <param name="direction">Neighbor direction relative to the cell.</param>
	/// <returns>Neighbor cell, if it exists.</returns>
	public readonly HexCell GetNeighbor(HexDirection direction) =>
		grid.GetCell(Coordinates.Step(direction));

	/// <summary>
	/// Try to get one of the neighbor cells.
	/// </summary>
	/// <param name="direction">Neighbor direction relative to the cell.</param>
	/// <param name="cell">The neighbor cell, if it exists.</param>
	/// <returns>Whether the neighbor exists.</returns>
	public readonly bool TryGetNeighbor(
		HexDirection direction, out HexCell cell) =>
		grid.TryGetCell(Coordinates.Step(direction), out cell);
	
	readonly void RemoveIncomingRiver()
	{
		if (Flags.HasAny(HexFlags.RiverIn))
		{
			HexCell neighbor = GetNeighbor(Flags.RiverInDirection());
			Flags = Flags.Without(HexFlags.RiverIn);
			neighbor.Flags = neighbor.Flags.Without(HexFlags.RiverOut);
			neighbor.Refresh();
			Refresh();
		}
	}

	readonly void RemoveOutgoingRiver()
	{
		if (Flags.HasAny(HexFlags.RiverOut))
		{
			HexCell neighbor = GetNeighbor(Flags.RiverOutDirection());
			Flags = Flags.Without(HexFlags.RiverOut);
			neighbor.Flags = neighbor.Flags.Without(HexFlags.RiverIn);
			neighbor.Refresh();
			Refresh();
		}
	}

	/// <summary>
	/// Clear the cell of rivers.
	/// </summary>
	public readonly void RemoveRiver()
	{
		RemoveIncomingRiver();
		RemoveOutgoingRiver();
	}

	static bool CanRiverFlow (HexValues from, HexValues to) =>
		from.Elevation >= to.Elevation || from.WaterLevel == to.Elevation;

	/// <summary>
	/// Set the outgoing river.
	/// </summary>
	/// <param name="direction">River direction.</param>
	public readonly void SetOutgoingRiver (HexDirection direction)
	{
		if (Flags.HasRiverOut(direction))
		{
			return;
		}

		HexCell neighbor = GetNeighbor(direction);
		if (!CanRiverFlow(Values, neighbor.Values))
		{
			return;
		}

		RemoveOutgoingRiver();
		if (Flags.HasRiverIn(direction))
		{
			RemoveIncomingRiver();
		}

		Flags = Flags.WithRiverOut(direction);
		Values = Values.WithSpecialIndex(0);
		neighbor.RemoveIncomingRiver();
		neighbor.Flags = neighbor.Flags.WithRiverIn(direction.Opposite());
		neighbor.Values = neighbor.Values.WithSpecialIndex(0);

		RemoveRoad(direction);
	}

	/// <summary>
	/// Add a road in the given direction.
	/// </summary>
	/// <param name="direction">Road direction.</param>
	public readonly void AddRoad(HexDirection direction)
	{
		HexFlags flags = Flags;
		HexCell neighbor = GetNeighbor(direction);
		if (
			!flags.HasRoad(direction) && !flags.HasRiver(direction) &&
			Values.SpecialIndex == 0 && neighbor.Values.SpecialIndex == 0 &&
			Mathf.Abs(Values.Elevation - neighbor.Values.Elevation) <= 1
		)
		{
			Flags = flags.WithRoad(direction);
			neighbor.Flags = neighbor.Flags.WithRoad(direction.Opposite());
			neighbor.Refresh();
			Refresh();
		}
	}

	/// <summary>
	/// Clear the cell of roads.
	/// </summary>
	public readonly void RemoveRoads()
	{
		HexFlags flags = Flags;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (flags.HasRoad(d))
			{
				RemoveRoad(d);
			}
		}
	}

	readonly void ValidateRivers()
	{
		HexFlags flags = Flags;
		if (flags.HasAny(HexFlags.RiverOut) &&
			!CanRiverFlow(Values, GetNeighbor(flags.RiverOutDirection()).Values)
		)
		{
			RemoveOutgoingRiver();
		}
		if (flags.HasAny(HexFlags.RiverIn) &&
			!CanRiverFlow(GetNeighbor(flags.RiverInDirection()).Values, Values))
		{
			RemoveIncomingRiver();
		}
	}

	readonly void RemoveRoad(HexDirection direction)
	{
		Flags = Flags.WithoutRoad(direction);
		HexCell neighbor = GetNeighbor(direction);
		neighbor.Flags = neighbor.Flags.WithoutRoad(direction.Opposite());
		neighbor.Refresh();
		Refresh();
	}

	readonly void Refresh() => grid.RefreshCell(index);

	/// <inheritdoc/>
	public readonly override bool Equals(object obj) =>
		obj is HexCell cell && this == cell;

	/// <inheritdoc/>
	public readonly override int GetHashCode() =>
		grid != null ? index.GetHashCode() ^ grid.GetHashCode() : 0;
	
	/// <summary>
	/// A cell counts as true if it is part of a grid.
	/// </summary>
	/// <param name="cell">The cell to check.</param>
	public static implicit operator bool(HexCell cell) => cell.grid != null;

	public static bool operator ==(HexCell a, HexCell b) =>
		a.index == b.index && a.grid == b.grid;
	
	public static bool operator !=(HexCell a, HexCell b) =>
		a.index != b.index || a.grid != b.grid;
}
