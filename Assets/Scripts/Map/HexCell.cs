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
			if (Values.IsUnderwater)
			{
				RemoveRoads();
			}
			// HF road placement follows the rendered HF surface, not Catlike's
			// hidden logical elevation steps. Preserve those paths when simulation
			// elevation changes; legacy mode keeps its original slope validation.
			else if (!grid.SurfaceSampler.UsesHFOriginalSurface)
			{
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
			if (Values.IsUnderwater)
			{
				RemoveRoads();
			}
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
		plantLevel = Mathf.Clamp(plantLevel, 0, 3);
		SetVegetation(
			grid.CellData[index].vegetation,
			plantLevel == 3 ? 100 : plantLevel * 33);
	}

	/// <summary>
	/// Set HF vegetation species and visual density without touching ground or
	/// relief. The legacy 0-3 plant tier remains synchronized for gameplay cost.
	/// </summary>
	public readonly void SetVegetation(
		HexVegetation vegetation, int density)
	{
		SetVegetation(
			vegetation, density, grid.CellData[index].vegetationTint);
	}

	/// <summary>
	/// Set HF vegetation species, density, and colour theme atomically.
	/// </summary>
	public readonly void SetVegetation(
		HexVegetation vegetation, int density, HexVegetationTint tint)
	{
		density = Mathf.Clamp(density, 0, 100);
		int plantLevel = density == 0 ? 0 :
			Mathf.Clamp(Mathf.CeilToInt(density * (3f / 100f)), 1, 3);
		HexCellData data = grid.CellData[index];
		if (data.vegetation == vegetation && data.vegetationTint == tint &&
			data.VegetationDensity == density &&
			data.PlantLevel == plantLevel)
		{
			return;
		}

		data.vegetation = vegetation;
		data.vegetationTint = tint;
		data.vegetationDensity = (byte)density;
		data.values = data.values.WithPlantLevel(plantLevel);
		grid.CellData[index] = data;
		Refresh();
	}

	/// <summary>
	/// Set the special index.
	/// </summary>
	/// <param name="specialIndex">Special index.</param>
	public readonly void SetSpecialIndex (int specialIndex)
	{
		if (Values.SpecialIndex != specialIndex &&
			!grid.CellData[index].HasRiver)
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
			// Rebuild the chunk because the visible HF ground material changed.
			// Vegetation species and density remain independent cell data.
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
	/// Rotate the authored HF stamp without changing its material or relief.
	/// Neighbor chunks are refreshed because HF stamps overlap cell boundaries.
	/// </summary>
	public readonly void SetTerrainRotation(int rotationStep)
	{
		rotationStep = ((rotationStep % 6) + 6) % 6;
		if (grid.CellData[index].TerrainRotation != rotationStep)
		{
			grid.CellData[index].terrainRotation = (byte)rotationStep;
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
		for (HexDirection direction = HexDirection.NE;
			direction <= HexDirection.NW; direction++)
		{
			RemoveHFRiverEdge(direction);
		}
	}

	/// <summary>
	/// Remove the river segment that crosses one edge, preserving the other side
	/// of the cell when possible.
	/// </summary>
	public readonly void RemoveRiverThroughEdge(HexDirection direction)
	{
		if (Flags.HasRiverIn(direction))
		{
			RemoveIncomingRiver();
		}
		if (Flags.HasRiverOut(direction))
		{
			RemoveOutgoingRiver();
		}
		RemoveHFRiverEdge(direction);
	}

	/// <summary>
	/// Place an HF river on the complete shared boundary in a direction. Both
	/// adjacent cells carry the bit so GPU and CPU neighborhood sampling remain
	/// invariant regardless of which cell owns the rendered patch.
	/// </summary>
	public readonly void SetHFRiverEdge(HexDirection direction)
	{
		HexCellData data = grid.CellData[index];
		byte bit = (byte)(1 << (int)direction);
		bool changed = (data.hfRiverEdges & bit) == 0 ||
			data.SpecialIndex != 0;
		data.hfRiverEdges |= bit;
		data.values = data.values.WithSpecialIndex(0);
		grid.CellData[index] = data;

		bool hasNeighbor = TryGetNeighbor(
			direction, out HexCell neighbor);
		if (data.flags.HasRoad(direction) && hasNeighbor)
		{
			RemoveRoad(direction);
		}

		if (hasNeighbor)
		{
			HexCellData neighborData = grid.CellData[neighbor.index];
			byte oppositeBit =
				(byte)(1 << (int)direction.Opposite());
			changed |= (neighborData.hfRiverEdges & oppositeBit) == 0 ||
				neighborData.SpecialIndex != 0;
			neighborData.hfRiverEdges |= oppositeBit;
			neighborData.values = neighborData.values.WithSpecialIndex(0);
			grid.CellData[neighbor.index] = neighborData;
			if (changed)
			{
				neighbor.Refresh();
			}
		}
		if (changed)
		{
			Refresh();
		}
	}

	readonly void RemoveHFRiverEdge(HexDirection direction)
	{
		HexCellData data = grid.CellData[index];
		byte bit = (byte)(1 << (int)direction);
		bool changed = (data.hfRiverEdges & bit) != 0;
		data.hfRiverEdges &= (byte)~bit;
		grid.CellData[index] = data;
		if (TryGetNeighbor(direction, out HexCell neighbor))
		{
			HexCellData neighborData = grid.CellData[neighbor.index];
			byte oppositeBit =
				(byte)(1 << (int)direction.Opposite());
			changed |= (neighborData.hfRiverEdges & oppositeBit) != 0;
			neighborData.hfRiverEdges &= (byte)~oppositeBit;
			grid.CellData[neighbor.index] = neighborData;
			if (changed)
			{
				neighbor.Refresh();
			}
		}
		if (changed)
		{
			Refresh();
		}
	}

	static bool CanRiverFlow (HexValues from, HexValues to) =>
		from.Elevation >= to.Elevation || from.WaterLevel == to.Elevation;

	/// <summary>
	/// Set the outgoing river.
	/// </summary>
	/// <param name="direction">River direction.</param>
	public readonly void SetOutgoingRiver(HexDirection direction) =>
		SetOutgoingRiver(direction, false);

	/// <summary>
	/// Set an HF river segment on the complete boundary in the supplied direction.
	/// Kept as the directional-path API used by existing editor code.
	/// </summary>
	public readonly void SetHFOutgoingRiver(HexDirection direction) =>
		SetHFRiverEdge(direction);

	readonly void SetOutgoingRiver(
		HexDirection direction, bool useHFPathRule)
	{
		if (Flags.HasRiverOut(direction))
		{
			return;
		}

		HexCell neighbor = GetNeighbor(direction);
		if (!useHFPathRule && !CanRiverFlow(Values, neighbor.Values))
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
	public readonly void AddRoad(HexDirection direction) =>
		AddRoad(direction, false);

	/// <summary>
	/// Add a road using HF surface ownership. Logical Catlike elevation is not a
	/// valid slope test for a road that conforms to the rendered HF surface.
	/// </summary>
	public readonly void AddHFRoad(HexDirection direction) =>
		AddRoad(direction, true);

	readonly void AddRoad(HexDirection direction, bool useHFPathRule)
	{
		HexFlags flags = Flags;
		HexCell neighbor = GetNeighbor(direction);
		if (
			!flags.HasRoad(direction) &&
			!grid.CellData[index].HasRiverThroughEdge(direction) &&
			!Values.IsUnderwater && !neighbor.Values.IsUnderwater &&
			Values.SpecialIndex == 0 && neighbor.Values.SpecialIndex == 0 &&
			(useHFPathRule ||
				Mathf.Abs(Values.Elevation - neighbor.Values.Elevation) <= 1)
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

	/// <summary>
	/// Remove only the road segment that crosses one edge.
	/// </summary>
	public readonly void RemoveRoadThroughEdge(HexDirection direction)
	{
		if (Flags.HasRoad(direction))
		{
			RemoveRoad(direction);
		}
	}

	readonly void ValidateRivers()
	{
		// HF's visible surface is authored independently of Catlike elevation.
		// Direction remains useful for river animation and serialization, but the
		// legacy elevation rule is not a valid reason to delete an HF path.
		if (grid.SurfaceSampler.UsesHFOriginalSurface)
		{
			return;
		}
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
