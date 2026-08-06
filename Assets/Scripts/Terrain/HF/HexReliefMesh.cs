using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds low-memory patches for GPU reconstructed terrain relief.
///
/// Each represented cell contributes only seven CPU vertices. The tessellation
/// shader reads the map-wide logical shape texture and reconstructs the actual
/// hill, mountain, neighbor blend, and river cut on the GPU. This keeps the HF
/// terrain-stamp idea without allocating baked height maps per chunk.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HexReliefMesh : MonoBehaviour
{
	readonly List<Vector3> vertices = new();
	readonly List<Vector3> cellIndices = new();
	readonly List<Vector2> localPositions = new();
	readonly List<int> triangles = new();

	Mesh mesh;
	HexTerrainStyle terrainStyle;

	void Awake()
	{
		mesh = new Mesh { name = "Hex Logical Relief Patches" };
		mesh.MarkDynamic();
		GetComponent<MeshFilter>().mesh = mesh;
	}

	public void Initialize(Material material, HexTerrainStyle style)
	{
		terrainStyle = style ? style : HexTerrainStyle.RuntimeDefault;
		terrainStyle.ApplyTo(material);
		MeshRenderer renderer = GetComponent<MeshRenderer>();
		renderer.sharedMaterial = material;
		renderer.shadowCastingMode =
			UnityEngine.Rendering.ShadowCastingMode.On;
		renderer.receiveShadows = true;
	}

	public void Clear()
	{
		mesh.Clear();
		vertices.Clear();
		cellIndices.Clear();
		localPositions.Clear();
		triangles.Clear();
	}

	/// <summary>
	/// Add one seven-vertex hex patch. Shape and material data are deliberately
	/// absent from the mesh; the global logical texture owns those values.
	/// </summary>
	public void AddCell(int cellIndex, Vector3 center)
	{
		int firstVertex = vertices.Count;
		Vector3 baseCenter = new(center.x, 0f, center.z);
		AddVertex(baseCenter, Vector2.zero, cellIndex);

		for (int side = 0; side < 6; side++)
		{
			float angle = side * (Mathf.PI / 3f);
			AddVertex(
				baseCenter + HexMetrics.GetFirstCorner((HexDirection)side),
				new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)),
				cellIndex);
		}

		for (int side = 0; side < 6; side++)
		{
			int next = (side + 1) % 6;
			triangles.Add(firstVertex);
			triangles.Add(firstVertex + 1 + side);
			triangles.Add(firstVertex + 1 + next);
		}
	}

	public void Apply()
	{
		mesh.SetVertices(vertices);
		mesh.SetUVs(1, cellIndices);
		mesh.SetUVs(2, localPositions);
		mesh.SetTriangles(triangles, 0);
		mesh.RecalculateNormals();

		if (vertices.Count > 0)
		{
			mesh.RecalculateBounds();
			Bounds bounds = mesh.bounds;
			float maximumHeight = Mathf.Max(
				terrainStyle.hillHeight,
				Mathf.Max(
					terrainStyle.mountainHeight,
					terrainStyle.desertMountainHeight));
			bounds.SetMinMax(
				new Vector3(bounds.min.x, 0f, bounds.min.z),
				new Vector3(bounds.max.x, 31f + maximumHeight, bounds.max.z));
			mesh.bounds = bounds;
		}
	}

	void AddVertex(Vector3 position, Vector2 localPosition, int cellIndex)
	{
		vertices.Add(position);
		cellIndices.Add(new Vector3(cellIndex, cellIndex, cellIndex));
		localPositions.Add(localPosition);
	}
}
