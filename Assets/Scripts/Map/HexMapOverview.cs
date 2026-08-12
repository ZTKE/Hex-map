using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// High-altitude renderer for streamed maps. Its surface shader reads the same
/// logical textures and HF source materials as the detailed map, so the ground
/// is not replaced by a simplified land/sea thumbnail. This component only
/// rasterizes the translucent political tint and builds the country borders.
/// </summary>
[DisallowMultipleComponent]
public sealed class HexMapOverview : MonoBehaviour
{
	readonly struct BorderEdge
	{
		public readonly Vector3 a;
		public readonly Vector3 b;

		public BorderEdge(Vector3 a, Vector3 b)
		{
			this.a = a;
			this.b = b;
		}
	}

	readonly struct BorderPointKey : System.IEquatable<BorderPointKey>
	{
		readonly int x;
		readonly int z;

		public BorderPointKey(Vector3 point)
		{
			x = Mathf.RoundToInt(point.x * 100f);
			z = Mathf.RoundToInt(point.z * 100f);
		}

		public bool Equals(BorderPointKey other) =>
			x == other.x && z == other.z;

		public override bool Equals(object obj) =>
			obj is BorderPointKey other && Equals(other);

		public override int GetHashCode() => unchecked(x * 397) ^ z;
	}

	const string overviewObjectName = "World Overview";
	const string borderObjectName = "World Political Borders";
	const int politicalRasterScale = 2;
	const float borderWidth = 5.5f;
	static readonly Color32 noPoliticalTint = new(255, 255, 255, 0);

	GameObject overviewObject;
	GameObject borderObject;
	Mesh overviewMesh;
	Mesh borderMesh;
	Material overviewMaterial;
	Material borderMaterial;
	Texture2D overviewTexture;
	bool hasPoliticalBorders;
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
			bool showBorders = visible && hasPoliticalBorders;
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
		overviewTexture.wrapModeU = grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		overviewTexture.wrapModeV = TextureWrapMode.Clamp;

		GetMapBounds(grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		Color32[] pixels = new Color32[width * height];
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
					pixels[pixel] = GetPoliticalTint(grid, cellIndex);
				}
				else
				{
					pixels[pixel] = noPoliticalTint;
				}
			}
		}

		overviewTexture.SetPixels32(pixels);
		overviewTexture.Apply(false, false);
	}

	static int GetRasterScale(HexGrid grid)
	{
		int maxTextureSize = SystemInfo.maxTextureSize;
		return grid.CellCountX * politicalRasterScale <= maxTextureSize &&
			grid.CellCountZ * politicalRasterScale <= maxTextureSize ?
			politicalRasterScale : 1;
	}

	static Color32 GetPoliticalTint(HexGrid grid, int cellIndex)
	{
		HexCellData cell = grid.CellData[cellIndex];
		if (cell.IsUnderwater ||
			!grid.TryGetCountryColor(cell.CountryId, out Color32 country))
		{
			return noPoliticalTint;
		}

		// Slightly mute source colors. The shader applies this only after it has
		// evaluated the exact map surface from the live logical textures.
		int average = (country.r + country.g + country.b) / 3;
		return new Color32(
			(byte)((country.r * 4 + average) / 5),
			(byte)((country.g * 4 + average) / 5),
			(byte)((country.b * 4 + average) / 5),
			255);
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
		if (!grid.HasPoliticalData)
		{
			hasPoliticalBorders = false;
			politicalBorderSegmentCount = 0;
			if (borderMesh)
			{
				borderMesh.Clear();
			}
			if (borderObject)
			{
				borderObject.SetActive(false);
			}
			return;
		}

		EnsureBorderRenderer();
		if (!borderMesh || !borderMaterial)
		{
			return;
		}

		Dictionary<int, List<BorderEdge>> groups = new();
		int sourceSegmentCount = 0;
		for (int cellIndex = 0; cellIndex < grid.CellData.Length; cellIndex++)
		{
			HexCellData cell = grid.CellData[cellIndex];
			if (cell.IsUnderwater || cell.CountryId == 0)
			{
				continue;
			}
			for (HexDirection direction = HexDirection.NE;
				direction <= HexDirection.NW; direction++)
			{
				if (!grid.TryGetCellIndex(
					cell.coordinates.Step(direction), out int neighborIndex) ||
					neighborIndex <= cellIndex)
				{
					continue;
				}
				HexCellData neighbor = grid.CellData[neighborIndex];
				if (neighbor.IsUnderwater || neighbor.CountryId == 0 ||
					neighbor.CountryId == cell.CountryId)
				{
					continue;
				}

				ushort lowId = cell.CountryId < neighbor.CountryId ?
					cell.CountryId : neighbor.CountryId;
				ushort highId = cell.CountryId < neighbor.CountryId ?
					neighbor.CountryId : cell.CountryId;
				int groupKey = (lowId << 16) | highId;
				if (!groups.TryGetValue(groupKey, out List<BorderEdge> edges))
				{
					edges = new List<BorderEdge>();
					groups.Add(groupKey, edges);
				}
				Vector3 center = grid.CellPositions[cellIndex];
				edges.Add(new BorderEdge(
					center + HexMetrics.GetFirstCorner(direction),
					center + HexMetrics.GetSecondCorner(direction)));
				sourceSegmentCount += 1;
			}
		}

		List<Vector3> segments = new(sourceSegmentCount * 4);
		foreach (List<BorderEdge> edges in groups.Values)
		{
			AppendSmoothedBorderSegments(edges, segments);
		}

		hasPoliticalBorders = sourceSegmentCount > 0;
		politicalBorderSegmentCount = sourceSegmentCount;
		if (!hasPoliticalBorders)
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

	static void AppendSmoothedBorderSegments(
		List<BorderEdge> edges, List<Vector3> outputSegments)
	{
		Dictionary<BorderPointKey, List<int>> adjacency = new();
		for (int i = 0; i < edges.Count; i++)
		{
			AddIncidentEdge(new BorderPointKey(edges[i].a), i);
			AddIncidentEdge(new BorderPointKey(edges[i].b), i);
		}

		bool[] visited = new bool[edges.Count];
		for (int i = 0; i < edges.Count; i++)
		{
			BorderPointKey a = new(edges[i].a);
			BorderPointKey b = new(edges[i].b);
			if (adjacency[a].Count != 2 || adjacency[b].Count != 2)
			{
				BorderPointKey start = adjacency[a].Count != 2 ? a : b;
				TraceAndSmooth(i, start);
			}
		}
		for (int i = 0; i < edges.Count; i++)
		{
			if (!visited[i])
			{
				TraceAndSmooth(i, new BorderPointKey(edges[i].a));
			}
		}

		void AddIncidentEdge(BorderPointKey point, int edgeIndex)
		{
			if (!adjacency.TryGetValue(point, out List<int> incidents))
			{
				incidents = new List<int>(2);
				adjacency.Add(point, incidents);
			}
			incidents.Add(edgeIndex);
		}

		void TraceAndSmooth(int firstEdge, BorderPointKey start)
		{
			if (visited[firstEdge])
			{
				return;
			}
			List<Vector3> path = new();
			BorderPointKey current = start;
			path.Add(GetPoint(edges[firstEdge], current));
			while (true)
			{
				int nextEdge = -1;
				List<int> incidents = adjacency[current];
				for (int i = 0; i < incidents.Count; i++)
				{
					if (!visited[incidents[i]])
					{
						nextEdge = incidents[i];
						break;
					}
				}
				if (nextEdge < 0)
				{
					break;
				}
				visited[nextEdge] = true;
				BorderEdge edge = edges[nextEdge];
				BorderPointKey a = new(edge.a);
				BorderPointKey next = a.Equals(current) ?
					new BorderPointKey(edge.b) : a;
				path.Add(GetPoint(edge, next));
				current = next;
				if (current.Equals(start))
				{
					break;
				}
			}

			bool closed = path.Count > 2 && current.Equals(start);
			AppendChaikinPath(path, closed, outputSegments);
		}
	}

	static Vector3 GetPoint(BorderEdge edge, BorderPointKey key) =>
		new BorderPointKey(edge.a).Equals(key) ? edge.a : edge.b;

	static void AppendChaikinPath(
		List<Vector3> path, bool closed, List<Vector3> outputSegments)
	{
		if (path.Count < 2)
		{
			return;
		}
		if (path.Count == 2)
		{
			outputSegments.Add(path[0]);
			outputSegments.Add(path[1]);
			return;
		}

		int uniqueCount = closed ? path.Count - 1 : path.Count;
		List<Vector3> smooth = new(uniqueCount * 2 + 2);
		if (!closed)
		{
			smooth.Add(path[0]);
		}
		int segmentCount = closed ? uniqueCount : uniqueCount - 1;
		for (int i = 0; i < segmentCount; i++)
		{
			Vector3 a = path[i];
			Vector3 b = path[(i + 1) % uniqueCount];
			smooth.Add(Vector3.Lerp(a, b, 0.25f));
			smooth.Add(Vector3.Lerp(a, b, 0.75f));
		}
		if (closed)
		{
			smooth.Add(smooth[0]);
		}
		else
		{
			smooth.Add(path[path.Count - 1]);
		}

		for (int i = 0; i < smooth.Count - 1; i++)
		{
			outputSegments.Add(smooth[i]);
			outputSegments.Add(smooth[i + 1]);
		}
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
