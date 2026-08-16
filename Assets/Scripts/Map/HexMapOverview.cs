using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Strategic mid/far renderer for streamed maps. It deliberately owns a
/// separate parchment-style surface so the detailed HF terrain remains a
/// near-view concern. Country colors, broad logical relief, coast ink, and
/// political borders are baked from the resident cell data.
/// </summary>
[DisallowMultipleComponent]
public sealed class HexMapOverview : MonoBehaviour
{
	const string overviewObjectName = "World Overview";
	const string borderObjectName = "World Political Borders";
	const int politicalRasterScale = 2;
	const float borderWidth = 4.2f;
	static readonly Color32 oceanAtlasPixel = new(199, 214, 219, 0);
	static readonly Color32 unownedLandAtlasPixel = new(224, 214, 184, 255);

	GameObject overviewObject;
	GameObject borderObject;
	Mesh overviewMesh;
	Mesh borderMesh;
	Material overviewMaterial;
	Material borderMaterial;
	Texture2D overviewTexture;
	Texture2D overviewReliefTexture;
	bool hasMapBorders;
	int politicalBorderSegmentCount;

	public bool IsVisible => overviewObject && overviewObject.activeSelf;
	public int PoliticalBorderSegmentCount => politicalBorderSegmentCount;

	public void Rebuild(HexGrid grid)
	{
		if (!grid || grid.CellData == null || grid.CellData.Length == 0)
		{
			SetVisible(false);
			return;
		}

		EnsureOverviewRenderer();
		if (!overviewMaterial)
		{
			return;
		}

		RebuildOverviewMesh(grid);
		RebuildTexture(grid);
		overviewMaterial.SetTexture("_MainTex", overviewTexture);
		overviewMaterial.SetTexture("_ReliefTex", overviewReliefTexture);
		RebuildPoliticalBorders(grid);
	}

	public void SetVisible(bool visible)
	{
		if (overviewObject && overviewObject.activeSelf != visible)
		{
			overviewObject.SetActive(visible);
		}
		if (borderObject)
		{
			bool showBorders = visible && hasMapBorders;
			if (borderObject.activeSelf != showBorders)
			{
				borderObject.SetActive(showBorders);
			}
		}
	}

	void EnsureOverviewRenderer()
	{
		if (!overviewObject)
		{
			Transform existing = transform.Find(overviewObjectName);
			overviewObject = existing ? existing.gameObject :
				new GameObject(overviewObjectName);
			overviewObject.transform.SetParent(transform, false);
			overviewObject.layer = gameObject.layer;
		}

		MeshFilter filter = overviewObject.GetComponent<MeshFilter>();
		if (!filter)
		{
			filter = overviewObject.AddComponent<MeshFilter>();
		}
		MeshRenderer renderer = overviewObject.GetComponent<MeshRenderer>();
		if (!renderer)
		{
			renderer = overviewObject.AddComponent<MeshRenderer>();
		}
		ConfigureRenderer(renderer);

		if (!overviewMesh)
		{
			overviewMesh = new Mesh { name = "World Overview Mesh" };
			overviewMesh.MarkDynamic();
		}
		filter.sharedMesh = overviewMesh;

		if (!overviewMaterial)
		{
			Shader shader = Shader.Find("Hex Map/World Overview");
			if (!shader)
			{
				Debug.LogError(
					"World overview shader is missing; high-altitude LOD is unavailable.",
					this);
				return;
			}
			overviewMaterial = new Material(shader)
			{
				name = "World Overview Material",
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		renderer.sharedMaterial = overviewMaterial;
	}

	void EnsureBorderRenderer()
	{
		if (!borderObject)
		{
			Transform existing = transform.Find(borderObjectName);
			borderObject = existing ? existing.gameObject :
				new GameObject(borderObjectName);
			borderObject.transform.SetParent(transform, false);
			borderObject.layer = gameObject.layer;
		}

		MeshFilter filter = borderObject.GetComponent<MeshFilter>();
		if (!filter)
		{
			filter = borderObject.AddComponent<MeshFilter>();
		}
		MeshRenderer renderer = borderObject.GetComponent<MeshRenderer>();
		if (!renderer)
		{
			renderer = borderObject.AddComponent<MeshRenderer>();
		}
		ConfigureRenderer(renderer);

		if (!borderMesh)
		{
			borderMesh = new Mesh
			{
				name = "World Political Border Mesh",
				indexFormat = IndexFormat.UInt32
			};
			borderMesh.MarkDynamic();
		}
		filter.sharedMesh = borderMesh;

		if (!borderMaterial)
		{
			Shader shader = Shader.Find("Hex Map/World Political Border");
			if (!shader)
			{
				Debug.LogError(
					"World political border shader is missing.", this);
				return;
			}
			borderMaterial = new Material(shader)
			{
				name = "World Political Border Material",
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		renderer.sharedMaterial = borderMaterial;
	}

	static void ConfigureRenderer(MeshRenderer renderer)
	{
		renderer.shadowCastingMode = ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = LightProbeUsage.Off;
		renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
	}

	void RebuildTexture(HexGrid grid)
	{
		int scale = GetRasterScale(grid);
		int width = grid.CellCountX * scale;
		int height = grid.CellCountZ * scale;
		if (!overviewTexture || overviewTexture.width != width ||
			overviewTexture.height != height)
		{
			DestroyRuntimeObject(overviewTexture);
			overviewTexture = new Texture2D(
				width, height, TextureFormat.RGBA32, false, false)
			{
				name = "World Political Tint Texture",
				filterMode = FilterMode.Bilinear,
				anisoLevel = 0,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		if (!overviewReliefTexture || overviewReliefTexture.width != width ||
			overviewReliefTexture.height != height)
		{
			DestroyRuntimeObject(overviewReliefTexture);
			overviewReliefTexture = new Texture2D(
				width, height, TextureFormat.RGBA32, false, true)
			{
				name = "World Strategic Relief Texture",
				filterMode = FilterMode.Bilinear,
				anisoLevel = 0,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		overviewTexture.wrapModeU = grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		overviewTexture.wrapModeV = TextureWrapMode.Clamp;
		overviewReliefTexture.wrapModeU = grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		overviewReliefTexture.wrapModeV = TextureWrapMode.Clamp;

		GetMapBounds(grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		Color32[] pixels = new Color32[width * height];
		Color32[] reliefPixels = new Color32[width * height];
		for (int y = 0, pixel = 0; y < height; y++)
		{
			float z = Mathf.Lerp(zMin, zMax, (y + 0.5f) / height);
			for (int x = 0; x < width; x++, pixel++)
			{
				float worldX = Mathf.Lerp(xMin, xMax, (x + 0.5f) / width);
				HexCoordinates coordinates = HexCoordinates.FromPosition(
					new Vector3(worldX, 0f, z));
				if (grid.TryGetCellIndex(coordinates, out int cellIndex))
				{
					pixels[pixel] = GetPoliticalAtlasPixel(grid, cellIndex);
					reliefPixels[pixel] = GetReliefAtlasPixel(grid, cellIndex);
				}
				else
				{
					pixels[pixel] = oceanAtlasPixel;
					reliefPixels[pixel] = Color.clear;
				}
			}
		}

		overviewTexture.SetPixels32(pixels);
		overviewTexture.Apply(false, false);
		overviewReliefTexture.SetPixels32(reliefPixels);
		overviewReliefTexture.Apply(false, false);
	}

	static int GetRasterScale(HexGrid grid)
	{
		int maxTextureSize = SystemInfo.maxTextureSize;
		return grid.CellCountX * politicalRasterScale <= maxTextureSize &&
			grid.CellCountZ * politicalRasterScale <= maxTextureSize ?
			politicalRasterScale : 1;
	}

	static Color32 GetPoliticalAtlasPixel(HexGrid grid, int cellIndex)
	{
		HexCellData cell = grid.CellData[cellIndex];
		if (cell.IsUnderwater)
		{
			return oceanAtlasPixel;
		}
		if (!grid.TryGetCountryColor(cell.CountryId, out Color32 country))
		{
			return unownedLandAtlasPixel;
		}
		country.a = 255;
		return country;
	}

	static Color32 GetReliefAtlasPixel(HexGrid grid, int cellIndex)
	{
		HexCellData cell = grid.CellData[cellIndex];
		if (cell.IsUnderwater)
		{
			return new Color32(0, 0, 0, 0);
		}

		float landformHeight = cell.landform switch
		{
			HexLandform.Mountain => 0.94f,
			HexLandform.Hill => 0.56f,
			_ => 0.18f
		};
		landformHeight += Mathf.Clamp(cell.Elevation, 0, 8) * 0.012f;
		byte height = (byte)Mathf.RoundToInt(
			Mathf.Clamp01(landformHeight) * 255f);
		byte vegetation = (byte)Mathf.RoundToInt(
			Mathf.Clamp01(cell.VegetationDensity / 100f) * 255f);
		return new Color32(height, vegetation, 0, 255);
	}

	void RebuildOverviewMesh(HexGrid grid)
	{
		GetMapBounds(grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		float y = -1f;
		float width = xMax - xMin;
		int copyCount = grid.Wrapping ? 3 : 1;
		Vector3[] vertices = new Vector3[copyCount * 4];
		Vector2[] uvs = new Vector2[copyCount * 4];
		int[] triangles = new int[copyCount * 6];
		for (int copy = 0; copy < copyCount; copy++)
		{
			float offset = grid.Wrapping ? (copy - 1) * width : 0f;
			int vertex = copy * 4;
			vertices[vertex] = new Vector3(xMin + offset, y, zMin);
			vertices[vertex + 1] = new Vector3(xMax + offset, y, zMin);
			vertices[vertex + 2] = new Vector3(xMax + offset, y, zMax);
			vertices[vertex + 3] = new Vector3(xMin + offset, y, zMax);
			uvs[vertex] = Vector2.zero;
			uvs[vertex + 1] = Vector2.right;
			uvs[vertex + 2] = Vector2.one;
			uvs[vertex + 3] = Vector2.up;

			int triangle = copy * 6;
			triangles[triangle] = vertex;
			triangles[triangle + 1] = vertex + 2;
			triangles[triangle + 2] = vertex + 1;
			triangles[triangle + 3] = vertex;
			triangles[triangle + 4] = vertex + 3;
			triangles[triangle + 5] = vertex + 2;
		}

		overviewMesh.Clear();
		overviewMesh.vertices = vertices;
		overviewMesh.uv = uvs;
		overviewMesh.triangles = triangles;
		overviewMesh.RecalculateBounds();
	}

	void RebuildPoliticalBorders(HexGrid grid)
	{
		EnsureBorderRenderer();
		if (!borderMesh || !borderMaterial)
		{
			return;
		}

		List<Vector3> segments = new();
		int politicalSegments = 0;
		for (int cellIndex = 0; cellIndex < grid.CellData.Length; cellIndex++)
		{
			HexCellData cell = grid.CellData[cellIndex];
			if (cell.IsUnderwater)
			{
				continue;
			}
			for (HexDirection direction = HexDirection.NE;
				direction <= HexDirection.NW; direction++)
			{
				bool hasNeighbor = grid.TryGetCellIndex(
					cell.coordinates.Step(direction), out int neighborIndex);
				bool coastline = !hasNeighbor ||
					grid.CellData[neighborIndex].IsUnderwater;
				bool politicalBorder = false;
				if (hasNeighbor && !coastline && neighborIndex > cellIndex)
				{
					ushort neighborCountry =
						grid.CellData[neighborIndex].CountryId;
					politicalBorder = cell.CountryId != 0 &&
						neighborCountry != 0 &&
						neighborCountry != cell.CountryId;
				}
				if (!coastline && !politicalBorder)
				{
					continue;
				}

				Vector3 center = grid.CellPositions[cellIndex];
				segments.Add(center + HexMetrics.GetFirstCorner(direction));
				segments.Add(center + HexMetrics.GetSecondCorner(direction));
				if (politicalBorder)
				{
					politicalSegments += 1;
				}
			}
		}

		hasMapBorders = segments.Count > 0;
		politicalBorderSegmentCount = politicalSegments;
		if (!hasMapBorders)
		{
			borderMesh.Clear();
			borderObject.SetActive(false);
			return;
		}

		float worldWidth = grid.CellCountX * HexMetrics.innerDiameter;
		int copyCount = grid.Wrapping ? 3 : 1;
		int segmentCount = segments.Count / 2;
		Vector3[] vertices = new Vector3[segmentCount * copyCount * 4];
		Vector2[] uvs = new Vector2[vertices.Length];
		int[] triangles = new int[segmentCount * copyCount * 6];
		int vertexCursor = 0;
		int triangleCursor = 0;
		for (int copy = 0; copy < copyCount; copy++)
		{
			float copyOffset = grid.Wrapping ? (copy - 1) * worldWidth : 0f;
			for (int segment = 0; segment < segmentCount; segment++)
			{
				Vector3 a = segments[segment * 2];
				Vector3 b = segments[segment * 2 + 1];
				a.x += copyOffset;
				b.x += copyOffset;
				a.y = b.y = -0.82f;
				Vector3 direction = (b - a).normalized;
				Vector3 side = new Vector3(-direction.z, 0f, direction.x) *
					(borderWidth * 0.5f);
				Vector3 cap = direction * (borderWidth * 0.35f);
				a -= cap;
				b += cap;

				vertices[vertexCursor] = a - side;
				vertices[vertexCursor + 1] = a + side;
				vertices[vertexCursor + 2] = b + side;
				vertices[vertexCursor + 3] = b - side;
				uvs[vertexCursor] = new Vector2(0f, 0f);
				uvs[vertexCursor + 1] = new Vector2(1f, 0f);
				uvs[vertexCursor + 2] = new Vector2(1f, 1f);
				uvs[vertexCursor + 3] = new Vector2(0f, 1f);

				triangles[triangleCursor] = vertexCursor;
				triangles[triangleCursor + 1] = vertexCursor + 1;
				triangles[triangleCursor + 2] = vertexCursor + 2;
				triangles[triangleCursor + 3] = vertexCursor;
				triangles[triangleCursor + 4] = vertexCursor + 2;
				triangles[triangleCursor + 5] = vertexCursor + 3;
				vertexCursor += 4;
				triangleCursor += 6;
			}
		}

		borderMesh.Clear();
		borderMesh.vertices = vertices;
		borderMesh.uv = uvs;
		borderMesh.triangles = triangles;
		borderMesh.RecalculateBounds();
		borderObject.SetActive(IsVisible);
	}

	static void GetMapBounds(
		HexGrid grid, out float xMin, out float xMax,
		out float zMin, out float zMax)
	{
		float width = grid.CellCountX * HexMetrics.innerDiameter;
		float rowHeight = HexMetrics.outerRadius * 1.5f;
		float height = grid.CellCountZ * rowHeight;
		xMin = -HexMetrics.innerDiameter * 0.5f;
		xMax = xMin + width;
		zMin = -rowHeight * 0.5f;
		zMax = zMin + height;
	}

	void OnDestroy()
	{
		DestroyRuntimeObject(overviewMaterial);
		DestroyRuntimeObject(borderMaterial);
		DestroyRuntimeObject(overviewTexture);
		DestroyRuntimeObject(overviewReliefTexture);
		DestroyRuntimeObject(overviewMesh);
		DestroyRuntimeObject(borderMesh);
	}

	static void DestroyRuntimeObject(Object value)
	{
		if (!value)
		{
			return;
		}
		if (Application.isPlaying)
		{
			Destroy(value);
		}
		else
		{
			DestroyImmediate(value);
		}
	}
}
