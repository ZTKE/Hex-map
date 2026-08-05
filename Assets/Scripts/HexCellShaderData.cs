using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Component that manages cell data used by shaders.
/// </summary>
public class HexCellShaderData : MonoBehaviour
{
	const float transitionSpeed = 255f;

	Texture2D cellTexture;

	Texture2D terrainShapeTexture;

	Color32[] cellTextureData;

	Color32[] terrainShapeTextureData;

	bool[] visibilityTransitions;

	List<int> transitioningCellIndices = new();

	bool needsVisibilityReset;

	bool terrainShapeDirty;

	public HexGrid Grid { get; set; }

	public bool ImmediateMode { get; set; }

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

		if (cellTextureData == null || terrainShapeTextureData == null ||
			cellTextureData.Length != x * z)
		{
			cellTextureData = new Color32[x * z];
			terrainShapeTextureData = new Color32[x * z];
			visibilityTransitions = new bool[x * z];
		}
		else
		{
			for (int i = 0; i < cellTextureData.Length; i++)
			{
				cellTextureData[i] = new Color32(0, 0, 0, 0);
				terrainShapeTextureData[i] = new Color32(0, 0, 0, 0);
				visibilityTransitions[i] = false;
			}
		}

		transitioningCellIndices.Clear();
		terrainShapeDirty = true;
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
		float xx = 0f, xz = 0f, zz = 0f;
		int matchingNeighbors = 0;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (!Grid.TryGetCellIndex(
				cell.coordinates.Step(d), out int neighborIndex) ||
				Grid.CellData[neighborIndex].landform != cell.landform ||
				cell.landform == HexLandform.Flat)
			{
				continue;
			}
			neighborMask |= 1 << (int)d;
			Vector3 direction = HexMetrics.GetSolidEdgeMiddle(d).normalized;
			xx += direction.x * direction.x;
			xz += direction.x * direction.z;
			zz += direction.z * direction.z;
			matchingNeighbors++;
		}

		float ridgeAngle = matchingNeighbors == 0 ?
			(HexMetrics.SampleHashGrid(Grid.CellPositions[cellIndex]).a - 0.5f) *
				Mathf.PI * 2f :
			0.5f * Mathf.Atan2(2f * xz, xx - zz);
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
		terrainShapeTextureData[cellIndex] = new Color32(
			(byte)packedLandformAndAngle,
			(byte)neighborMask,
			(byte)riverMask,
			(byte)Mathf.RoundToInt(surfaceY * (255f / 30f)));
		terrainShapeDirty = true;
		enabled = true;
	}

	/// <summary>
	/// Refresh a cell and its one-ring neighbors because their shared mountain
	/// topology can change together.
	/// </summary>
	public void RefreshTerrainShapeWithDependents(int cellIndex)
	{
		RefreshTerrainShape(cellIndex);
		HexCoordinates coordinates = Grid.CellData[cellIndex].coordinates;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (Grid.TryGetCellIndex(coordinates.Step(d), out int neighborIndex))
			{
				RefreshTerrainShape(neighborIndex);
			}
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
			terrainShapeDirty = false;
		}
		enabled = transitioningCellIndices.Count > 0;
	}

	void OnDestroy()
	{
		if (cellTexture)
		{
			Destroy(cellTexture);
		}
		if (terrainShapeTexture)
		{
			Destroy(terrainShapeTexture);
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
