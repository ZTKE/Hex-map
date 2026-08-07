using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Reconstructs HoneyFramework foreground sprites as one billboard mesh per
/// chunk. HF originally batched its tree atlas this way; keeping the batch and
/// sampling the logical terrain height in the vertex shader avoids both a
/// GameObject per tree and a CPU copy of the height map.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class HexHFForegroundMesh : MonoBehaviour
{
	List<Vector3> centers;
	List<Vector2> atlasUVs;
	List<Vector2> billboardOffsets;
	List<Vector3> cellIndices;
	List<Vector2> localPositions;
	List<Color> colors;
	List<int> triangles;

	Mesh mesh;

	enum SpriteKind
	{
		Tree14,
		DeadTree07,
		Tree04,
		DeadTree02,
		Tree07
	}

	readonly struct AtlasEntry
	{
		public readonly Rect uv;
		public readonly Vector2Int pixels;
		public readonly Vector2 pivot;

		public AtlasEntry(Rect uv, int width, int height, float pivotX, float pivotY)
		{
			this.uv = uv;
			pixels = new Vector2Int(width, height);
			pivot = new Vector2(pivotX / width, pivotY / height);
		}
	}

	void Awake()
	{
		mesh = new Mesh { name = "HF Foreground Chunk Batch" };
		mesh.MarkDynamic();
		GetComponent<MeshFilter>().mesh = mesh;
	}

	public void Initialize(Material material)
	{
		MeshRenderer renderer = GetComponent<MeshRenderer>();
		renderer.sharedMaterial = material;
		renderer.shadowCastingMode = ShadowCastingMode.Off;
		renderer.receiveShadows = false;
	}

	public void Clear()
	{
		mesh.Clear();
		ReleaseLists();
		centers = ListPool<Vector3>.Get();
		atlasUVs = ListPool<Vector2>.Get();
		billboardOffsets = ListPool<Vector2>.Get();
		cellIndices = ListPool<Vector3>.Get();
		localPositions = ListPool<Vector2>.Get();
		colors = ListPool<Color>.Get();
		triangles = ListPool<int>.Get();
	}

	/// <summary>
	/// Adds the HF foreground definition matching this project's logical cell.
	/// Species and exact density are independent from ground material and relief.
	/// </summary>
	public void AddCell(HexCellData cell, int cellIndex, Vector3 center)
	{
		int density = Mathf.Clamp(cell.VegetationDensity, 0, 100);
		if (density <= 0)
		{
			return;
		}

		uint randomState = unchecked((uint)(
			cellIndex * 747796405 +
			cell.coordinates.X * 2891336453L +
			cell.coordinates.Z * 1181783497L +
			(int)cell.vegetation * 2246822519L));

		switch (cell.vegetation)
		{
			case HexVegetation.Broadleaf:
				AddGroup(SpriteKind.Tree14, ScaleForestCount(68, density),
					ApplyVegetationTint(HexColor(0x76, 0x83, 0x36), cell.vegetationTint),
					cellIndex, center, ref randomState);
				break;
			case HexVegetation.Sapling:
				AddGroup(SpriteKind.Tree04, ScaleForestCount(82, density),
					ApplyVegetationTint(HexColor(0x79, 0x86, 0x38), cell.vegetationTint),
					cellIndex, center, ref randomState);
				break;
			case HexVegetation.Conifer:
				AddGroup(SpriteKind.Tree07, ScaleForestCount(72, density),
					ApplyVegetationTint(HexColor(0x4f, 0x70, 0x38), cell.vegetationTint),
					cellIndex, center, ref randomState);
				break;
			case HexVegetation.Deadwood:
				AddGroup(SpriteKind.DeadTree02, ScaleForestCount(38, density),
					ApplyVegetationTint(HexColor(0x78, 0x6b, 0x4d), cell.vegetationTint),
					cellIndex, center, ref randomState);
				AddGroup(SpriteKind.DeadTree07, ScaleForestCount(34, density),
					ApplyVegetationTint(HexColor(0x6d, 0x62, 0x49), cell.vegetationTint),
					cellIndex, center, ref randomState);
				break;
			case HexVegetation.ColdMixed:
				AddGroup(SpriteKind.Tree07, ScaleForestCount(34, density),
					ApplyVegetationTint(HexColor(0x3d, 0x5c, 0x3e), cell.vegetationTint),
					cellIndex, center, ref randomState);
				AddGroup(SpriteKind.DeadTree02, ScaleForestCount(36, density),
					ApplyVegetationTint(HexColor(0x68, 0x70, 0x5a), cell.vegetationTint),
					cellIndex, center, ref randomState);
				break;
			default:
				AddGroup(SpriteKind.Tree14, ScaleForestCount(15, density),
					ApplyVegetationTint(HexColor(0x71, 0x79, 0x2f), cell.vegetationTint),
					cellIndex, center, ref randomState);
				AddGroup(SpriteKind.Tree04, ScaleForestCount(30, density),
					ApplyVegetationTint(HexColor(0x71, 0x79, 0x2f), cell.vegetationTint),
					cellIndex, center, ref randomState);
				AddGroup(SpriteKind.Tree07, ScaleForestCount(40, density),
					ApplyVegetationTint(HexColor(0x65, 0x78, 0x35), cell.vegetationTint),
					cellIndex, center, ref randomState);
				break;
		}
	}

	static int ScaleForestCount(int denseCount, int density) =>
		Mathf.Max(1, Mathf.RoundToInt(denseCount * density / 100f));

	static Color ApplyVegetationTint(Color color, HexVegetationTint tint)
	{
		Color multiplier = tint switch
		{
			HexVegetationTint.DeepGreen => HexColor(0x9a, 0xc0, 0x82),
			HexVegetationTint.Autumn => HexColor(0xe0, 0x86, 0x3f),
			HexVegetationTint.Dry => HexColor(0xc3, 0xa5, 0x68),
			HexVegetationTint.Frost => HexColor(0x9e, 0xb8, 0xb4),
			HexVegetationTint.Pale => HexColor(0xc8, 0xc5, 0x9d),
			_ => Color.white
		};
		return new Color(
			Mathf.Clamp01(color.r * multiplier.r),
			Mathf.Clamp01(color.g * multiplier.g),
			Mathf.Clamp01(color.b * multiplier.b), 1f);
	}

	public void Apply()
	{
		bool hasGeometry = centers.Count > 0;
		BuildSortedTriangles();
		mesh.SetVertices(centers);
		mesh.SetUVs(0, atlasUVs);
		mesh.SetUVs(1, billboardOffsets);
		mesh.SetUVs(2, cellIndices);
		mesh.SetUVs(3, localPositions);
		mesh.SetColors(colors);
		mesh.SetTriangles(triangles, 0);

		if (hasGeometry)
		{
			mesh.RecalculateBounds();
			Bounds bounds = mesh.bounds;
			// Vertex billboarding and logical height displacement happen on the GPU.
			bounds.Expand(new Vector3(14f, 48f, 14f));
			mesh.bounds = bounds;
		}
		// Mesh owns the uploaded data now. Returning the construction lists keeps
		// dense HF forests from retaining a second CPU copy in every chunk.
		ReleaseLists();
	}

	void BuildSortedTriangles()
	{
		triangles.Clear();
		int spriteCount = centers.Count / 4;
		List<int> order = ListPool<int>.Get();
		for (int i = 0; i < spriteCount; i++)
		{
			order.Add(i);
		}
		// HF sorts foreground back-to-front by Z before emitting one transparent
		// mesh. Triangle order therefore remains meaningful even inside one batch.
		order.Sort((a, b) =>
			centers[b * 4].z.CompareTo(centers[a * 4].z));
		for (int i = 0; i < order.Count; i++)
		{
			int firstVertex = order[i] * 4;
			triangles.Add(firstVertex + 2);
			triangles.Add(firstVertex + 1);
			triangles.Add(firstVertex);
			triangles.Add(firstVertex);
			triangles.Add(firstVertex + 3);
			triangles.Add(firstVertex + 2);
		}
		ListPool<int>.Add(order);
	}

	void OnDestroy() => ReleaseLists();

	void ReleaseLists()
	{
		if (centers == null)
		{
			return;
		}
		ListPool<Vector3>.Add(centers);
		ListPool<Vector2>.Add(atlasUVs);
		ListPool<Vector2>.Add(billboardOffsets);
		ListPool<Vector3>.Add(cellIndices);
		ListPool<Vector2>.Add(localPositions);
		ListPool<Color>.Add(colors);
		ListPool<int>.Add(triangles);
		centers = null;
		atlasUVs = null;
		billboardOffsets = null;
		cellIndices = null;
		localPositions = null;
		colors = null;
		triangles = null;
	}

	void AddGroup(
		SpriteKind kind, int count, Color baseTint,
		int cellIndex, Vector3 center, ref uint randomState)
	{
		for (int i = 0; i < count; i++)
		{
			float radius =
				(Next01(ref randomState) + Next01(ref randomState)) *
				0.5f * HexMetrics.outerRadius * 1.1f;
			float angle = Next01(ref randomState) * Mathf.PI * 2f;
			Vector2 offset = new(
				Mathf.Cos(angle) * radius,
				Mathf.Sin(angle) * radius);
			Vector3 spriteCenter = new(
				center.x + offset.x, 0f, center.z + offset.y);
			float scale = Mathf.Lerp(
				0.004f, 0.0052f, Next01(ref randomState)) *
				HexMetrics.outerRadius;
			Color tint = RandomizeTint(baseTint, ref randomState);
			AddSprite(kind, spriteCenter, offset / HexMetrics.outerRadius,
				cellIndex, scale, tint);
		}
	}

	void AddSprite(
		SpriteKind kind, Vector3 center, Vector2 localPosition,
		int cellIndex, float scale, Color tint)
	{
		AtlasEntry entry = GetEntry(kind);
		float width = entry.pixels.x * scale;
		float height = entry.pixels.y * scale;
		// HF flips the atlas pivot on X before constructing each quad.
		float left = -entry.pivot.x * width;
		float right = (1f - entry.pivot.x) * width;
		float bottom = -(1f - entry.pivot.y) * height;
		float top = entry.pivot.y * height;

		AddVertex(center, new Vector2(entry.uv.xMin, entry.uv.yMax),
			new Vector2(left, top), localPosition, cellIndex, tint);
		AddVertex(center, new Vector2(entry.uv.xMin, entry.uv.yMin),
			new Vector2(left, bottom), localPosition, cellIndex, tint * 0.2f);
		AddVertex(center, new Vector2(entry.uv.xMax, entry.uv.yMin),
			new Vector2(right, bottom), localPosition, cellIndex, tint * 1.8f);
		AddVertex(center, new Vector2(entry.uv.xMax, entry.uv.yMax),
			new Vector2(right, top), localPosition, cellIndex, tint);

	}

	void AddVertex(
		Vector3 center, Vector2 uv, Vector2 billboardOffset,
		Vector2 localPosition, int cellIndex, Color color)
	{
		centers.Add(center);
		atlasUVs.Add(uv);
		billboardOffsets.Add(billboardOffset);
		cellIndices.Add(new Vector3(cellIndex, cellIndex, cellIndex));
		localPositions.Add(localPosition);
		colors.Add(color);
	}

	static AtlasEntry GetEntry(SpriteKind kind) => kind switch
	{
		SpriteKind.Tree14 => new AtlasEntry(
			new Rect(0f, 0.75f, 0.5f, 0.25f), 128, 64, 88f, 58f),
		SpriteKind.DeadTree07 => new AtlasEntry(
			new Rect(0.5078125f, 0.75f, 0.25f, 0.25f), 64, 64, 48f, 58f),
		SpriteKind.Tree04 => new AtlasEntry(
			new Rect(0f, 0.1015625f, 0.25f, 0.125f), 64, 32, 41f, 28f),
		SpriteKind.DeadTree02 => new AtlasEntry(
			new Rect(0f, 0.4921875f, 0.25f, 0.25f), 64, 64, 45f, 62f),
		_ => new AtlasEntry(
			new Rect(0f, 0.234375f, 0.25f, 0.25f), 64, 64, 46f, 62f)
	};

	static float Next01(ref uint state)
	{
		state = unchecked(state * 1664525u + 1013904223u);
		return (state >> 8) * (1f / 16777216f);
	}

	static Color RandomizeTint(Color color, ref uint state)
	{
		const float radius = 30f / 255f;
		return new Color(
			Mathf.Clamp01(color.r * Mathf.Lerp(1f - radius, 1f + radius,
				Next01(ref state))),
			Mathf.Clamp01(color.g * Mathf.Lerp(1f - radius, 1f + radius,
				Next01(ref state))),
			Mathf.Clamp01(color.b * Mathf.Lerp(1f - radius, 1f + radius,
				Next01(ref state))),
			1f);
	}

	static Color HexColor(byte r, byte g, byte b) =>
		new(r / 255f, g / 255f, b / 255f, 1f);
}
