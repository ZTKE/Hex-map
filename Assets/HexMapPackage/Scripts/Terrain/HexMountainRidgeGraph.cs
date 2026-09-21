using UnityEngine;

/// <summary>
/// A local, bidirectional ridge graph over dry mountain cells. Each triangle
/// loses its strictly greatest edge in Automatic/Massif mode. Explicit Range
/// keeps all adjacent connections for manually painted mountain walls.
/// </summary>
public static class HexMountainRidgeGraph
{
	const float Sqrt3 = 1.7320508075688772f;

	/// <summary>Stable offset from the hex center, in outer-radius units.</summary>
	public static Vector2 NodeOffset(int x, int z)
	{
		uint hash = HexNearTerrainSurface.Hash(x, z);
		return new Vector2(
			((hash & 255u) / 255f - .5f) * .48f,
			(((hash >> 8) & 255u) / 255f - .5f) * .48f);
	}

	/// <summary>
	/// Six bits in NE, E, SE, SW, W, NW order. A removed edge has a two-edge
	/// mountain path with strictly smaller keys. Recursively replacing removed
	/// edges therefore preserves connectivity, while every triangle loses an edge.
	/// </summary>
	public static int ComputeMask(HexGrid grid, int x, int z)
	{
		if (grid == null || grid.CellData == null ||
			grid.CellCountX <= 0 || grid.CellCountZ <= 0 ||
			!Resolve(grid, ref x, z) || !IsMountain(grid, x, z))
		{
			return 0;
		}

		int mask = 0;
		for (int direction = 0; direction < 6; direction++)
		{
			Vector2Int b = Neighbor(x, z, direction);
			int bx = b.x;
			if (!Resolve(grid, ref bx, b.y) ||
				!IsMountain(grid, bx, b.y) || (bx == x && b.y == z))
			{
				continue;
			}

			// Explicit range painting keeps every adjacent connection, including
			// walls at junctions. Automatic and massif keep the sparse area graph.
			if (grid.CellData[x + z * grid.CellCountX].mountainMode == HexMountainMode.Range ||
				grid.CellData[bx + b.y * grid.CellCountX].mountainMode == HexMountainMode.Range)
			{
				mask |= 1 << direction;
				continue;
			}
			EdgeKey edge = Key(grid, x, z, bx, b.y);
			bool retained = true;
			// Adjacent hexes share exactly these two corner neighbors. No global
			// graph rebuild or traversal is needed after a local terrain edit.
			for (int side = 0; side < 2; side++)
			{
				int cornerDirection = (direction + (side == 0 ? 5 : 1)) % 6;
				Vector2Int c = Neighbor(x, z, cornerDirection);
				int cx = c.x;
				if (!Resolve(grid, ref cx, c.y) ||
					!IsMountain(grid, cx, c.y) ||
					(cx == x && c.y == z) || (cx == bx && c.y == b.y))
				{
					continue;
				}
				if (Greater(edge, Key(grid, x, z, cx, c.y)) &&
					Greater(edge, Key(grid, bx, b.y, cx, c.y)))
				{
					retained = false;
					break;
				}
			}
			if (retained)
			{
				mask |= 1 << direction;
			}
		}
		return mask;
	}

	readonly struct EdgeKey
	{
		public readonly int length, first, second;
		public readonly uint hash;

		public EdgeKey(int length, uint hash, int first, int second)
		{
			this.length = length;
			this.hash = hash;
			this.first = first;
			this.second = second;
		}
	}

	static EdgeKey Key(HexGrid grid, int ax, int az, int bx, int bz)
	{
		int first = ax + az * grid.CellCountX;
		int second = bx + bz * grid.CellCountX;
		if (first > second)
		{
			(first, second) = (second, first);
			(ax, bx) = (bx, ax);
			(az, bz) = (bz, az);
		}

		// Integer doubled X preserves the short local edge across the wrap.
		// Canonical endpoint order also makes floating-point evaluation identical
		// when the same edge is visited from its other endpoint or a third cell.
		int dx2 = (bx - ax) * 2 + (bz & 1) - (az & 1);
		if (grid.Wrapping)
		{
			if (dx2 > grid.CellCountX) dx2 -= grid.CellCountX * 2;
			else if (dx2 < -grid.CellCountX) dx2 += grid.CellCountX * 2;
		}
		Vector2 delta = new Vector2(dx2 * (Sqrt3 * .5f), (bz - az) * 1.5f) +
			(NodeOffset(bx, bz) - NodeOffset(ax, az));
		int quantizedLength = Mathf.RoundToInt(
			(delta.x * delta.x + delta.y * delta.y) * 4096f);
		uint pairHash;
		unchecked
		{
			pairHash = HexNearTerrainSurface.Hash(ax, az) ^
				(HexNearTerrainSurface.Hash(bx, bz) * 1664525u + 1013904223u);
		}
		return new EdgeKey(quantizedLength, pairHash, first, second);
	}

	static bool Greater(EdgeKey a, EdgeKey b)
	{
		if (a.length != b.length) return a.length > b.length;
		if (a.hash != b.hash) return a.hash > b.hash;
		if (a.first != b.first) return a.first > b.first;
		return a.second > b.second;
	}

	static bool IsMountain(HexGrid grid, int x, int z)
	{
		int index = x + z * grid.CellCountX;
		if (index < 0 || index >= grid.CellData.Length) return false;
		HexCellData cell = grid.CellData[index];
		return !cell.IsUnderwater && cell.landform == HexLandform.Mountain;
	}

	static bool Resolve(HexGrid grid, ref int x, int z)
	{
		if (z < 0 || z >= grid.CellCountZ) return false;
		if (grid.Wrapping)
		{
			x = ((x % grid.CellCountX) + grid.CellCountX) % grid.CellCountX;
		}
		return x >= 0 && x < grid.CellCountX;
	}

	static Vector2Int Neighbor(int x, int z, int direction)
	{
		int odd = z & 1;
		return direction switch
		{
			0 => new Vector2Int(x + odd, z + 1),
			1 => new Vector2Int(x + 1, z),
			2 => new Vector2Int(x + odd, z - 1),
			3 => new Vector2Int(x + odd - 1, z - 1),
			4 => new Vector2Int(x - 1, z),
			_ => new Vector2Int(x + odd - 1, z + 1)
		};
	}
}
