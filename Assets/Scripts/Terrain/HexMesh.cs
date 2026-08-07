using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// Class containing all data used to generate a mesh
/// while triangulating a hex map.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HexMesh : MonoBehaviour
{
	[SerializeField]
	bool useCollider, useCellData, useUVCoordinates, useUV2Coordinates;

	[NonSerialized] List<Vector3> vertices, cellIndices;
	[NonSerialized] List<Color> cellWeights;
	[NonSerialized] List<Vector2> uvs, uv2s;
	[NonSerialized] List<int> triangles;

	Mesh hexMesh;
	MeshCollider meshCollider;

	struct VertexData
	{
		public Vector3 position;
		public Vector3 indices;
		public Color weights;
		public Vector2 uv, uv2;
	}

	void Awake()
	{
		GetComponent<MeshFilter>().mesh = hexMesh = new Mesh();
		if (useCollider)
		{
			meshCollider = gameObject.AddComponent<MeshCollider>();
		}
		hexMesh.name = "Hex Mesh";
	}

	/// <summary>
	/// Clear all data.
	/// </summary>
	public void Clear()
	{
		hexMesh.Clear();
		vertices = ListPool<Vector3>.Get();
		if (useCellData)
		{
			cellWeights = ListPool<Color>.Get();
			cellIndices = ListPool<Vector3>.Get();
		}
		if (useUVCoordinates)
		{
			uvs = ListPool<Vector2>.Get();
		}
		if (useUV2Coordinates)
		{
			uv2s = ListPool<Vector2>.Get();
		}
		triangles = ListPool<int>.Get();
	}

	/// <summary>
	/// Apply all triangulation data to the underlying mesh.
	/// </summary>
	public void Apply()
	{
		hexMesh.indexFormat = vertices.Count > 65535 ?
			UnityEngine.Rendering.IndexFormat.UInt32 :
			UnityEngine.Rendering.IndexFormat.UInt16;
		hexMesh.SetVertices(vertices);
		ListPool<Vector3>.Add(vertices);
		if (useCellData)
		{
			hexMesh.SetColors(cellWeights);
			ListPool<Color>.Add(cellWeights);
			hexMesh.SetUVs(2, cellIndices);
			ListPool<Vector3>.Add(cellIndices);
		}
		if (useUVCoordinates)
		{
			hexMesh.SetUVs(0, uvs);
			ListPool<Vector2>.Add(uvs);
		}
		if (useUV2Coordinates)
		{
			hexMesh.SetUVs(1, uv2s);
			ListPool<Vector2>.Add(uv2s);
		}
		hexMesh.SetTriangles(triangles, 0);
		ListPool<int>.Add(triangles);
		hexMesh.RecalculateNormals();
		if (useCollider)
		{
			meshCollider.sharedMesh = hexMesh;
		}
	}

	/// <summary>
	/// Release generated Catlike geometry without uploading it. HF Original mode
	/// still invokes the proven Catlike triangulation code to derive road, river,
	/// and wall topology, but its hidden terrain mesh must not remain a second
	/// surface or collider.
	/// </summary>
	public void Discard()
	{
		hexMesh.Clear();
		ReleaseConstructionLists();
	}

	public void SetColliderEnabled(bool value)
	{
		if (!meshCollider)
		{
			return;
		}
		meshCollider.enabled = value;
		if (!value)
		{
			meshCollider.sharedMesh = null;
		}
	}

	/// <summary>
	/// Subdivide an overlay and drape every resulting vertex over the shared HF
	/// surface. Subdivision prevents a large road/river triangle from cutting
	/// through curved authored relief between its original corner vertices.
	/// </summary>
	public void ConformToSurface(
		HexGrid grid, int subdivisionLevels, float verticalOffset)
	{
		for (int i = 0; i < subdivisionLevels; i++)
		{
			SubdivideOnce();
		}
		for (int i = 0; i < vertices.Count; i++)
		{
			Vector3 position = vertices[i];
			int rootCellIndex = GetDominantCellIndex(i);
			position.y = rootCellIndex >= 0 ?
				grid.SampleSurfaceHeight(rootCellIndex, position) + verticalOffset :
				grid.SampleSurfaceHeight(position) + verticalOffset;
			vertices[i] = position;
		}
	}

	void SubdivideOnce()
	{
		if (triangles.Count == 0)
		{
			return;
		}

		List<Vector3> newVertices = ListPool<Vector3>.Get();
		List<int> newTriangles = ListPool<int>.Get();
		List<Vector3> newCellIndices = useCellData ?
			ListPool<Vector3>.Get() : null;
		List<Color> newCellWeights = useCellData ?
			ListPool<Color>.Get() : null;
		List<Vector2> newUVs = useUVCoordinates ?
			ListPool<Vector2>.Get() : null;
		List<Vector2> newUV2s = useUV2Coordinates ?
			ListPool<Vector2>.Get() : null;

		for (int i = 0; i < triangles.Count; i += 3)
		{
			VertexData a = GetVertexData(triangles[i]);
			VertexData b = GetVertexData(triangles[i + 1]);
			VertexData c = GetVertexData(triangles[i + 2]);
			// A triangle's cell-index triplet is uniform. Interpolating those
			// integer IDs would address unrelated logical cells in the shader.
			b.indices = c.indices = a.indices;
			VertexData ab = Lerp(a, b);
			VertexData bc = Lerp(b, c);
			VertexData ca = Lerp(c, a);
			AddSubTriangle(a, ab, ca);
			AddSubTriangle(ab, b, bc);
			AddSubTriangle(ca, bc, c);
			AddSubTriangle(ab, bc, ca);
		}

		ReleaseConstructionLists();
		vertices = newVertices;
		triangles = newTriangles;
		cellIndices = newCellIndices;
		cellWeights = newCellWeights;
		uvs = newUVs;
		uv2s = newUV2s;

		void AddSubTriangle(VertexData a, VertexData b, VertexData c)
		{
			int first = newVertices.Count;
			Add(a);
			Add(b);
			Add(c);
			newTriangles.Add(first);
			newTriangles.Add(first + 1);
			newTriangles.Add(first + 2);
		}

		void Add(VertexData data)
		{
			newVertices.Add(data.position);
			newCellIndices?.Add(data.indices);
			newCellWeights?.Add(data.weights);
			newUVs?.Add(data.uv);
			newUV2s?.Add(data.uv2);
		}
	}

	VertexData GetVertexData(int index) => new()
	{
		position = vertices[index],
		indices = useCellData ? cellIndices[index] : Vector3.zero,
		weights = useCellData ? cellWeights[index] : Color.black,
		uv = useUVCoordinates ? uvs[index] : Vector2.zero,
		uv2 = useUV2Coordinates ? uv2s[index] : Vector2.zero
	};

	static VertexData Lerp(VertexData a, VertexData b) => new()
	{
		position = Vector3.Lerp(a.position, b.position, 0.5f),
		indices = a.indices,
		weights = Color.Lerp(a.weights, b.weights, 0.5f),
		uv = Vector2.Lerp(a.uv, b.uv, 0.5f),
		uv2 = Vector2.Lerp(a.uv2, b.uv2, 0.5f)
	};

	int GetDominantCellIndex(int vertexIndex)
	{
		if (!useCellData || vertexIndex >= cellIndices.Count)
		{
			return -1;
		}
		Vector3 indices = cellIndices[vertexIndex];
		Color weights = cellWeights[vertexIndex];
		float index = weights.r >= weights.g ?
			(weights.r >= weights.b ? indices.x : indices.z) :
			(weights.g >= weights.b ? indices.y : indices.z);
		return Mathf.RoundToInt(index);
	}

	void ReleaseConstructionLists()
	{
		if (vertices != null)
		{
			ListPool<Vector3>.Add(vertices);
			vertices = null;
		}
		if (cellWeights != null)
		{
			ListPool<Color>.Add(cellWeights);
			cellWeights = null;
		}
		if (cellIndices != null)
		{
			ListPool<Vector3>.Add(cellIndices);
			cellIndices = null;
		}
		if (uvs != null)
		{
			ListPool<Vector2>.Add(uvs);
			uvs = null;
		}
		if (uv2s != null)
		{
			ListPool<Vector2>.Add(uv2s);
			uv2s = null;
		}
		if (triangles != null)
		{
			ListPool<int>.Add(triangles);
			triangles = null;
		}
	}

	/// <summary>
	/// Expand the renderer bounds after triangulation. Flat, shader-driven
	/// surfaces such as the HF ocean otherwise have a zero-height AABB, which
	/// can be rejected too aggressively near a camera frustum edge.
	/// </summary>
	public void ExpandBounds(Vector3 padding)
	{
		if (!hexMesh || hexMesh.vertexCount == 0)
		{
			return;
		}

		hexMesh.RecalculateBounds();
		Bounds bounds = hexMesh.bounds;
		bounds.SetMinMax(bounds.min - padding, bounds.max + padding);
		hexMesh.bounds = bounds;
	}

	/// <summary>
	/// Add a triangle, applying perturbation to the positions.
	/// </summary>
	/// <param name="v1">First vertex position.</param>
	/// <param name="v2">Second vertex position.</param>
	/// <param name="v3">Third vertex position.</param>
	public void AddTriangle(Vector3 v1, Vector3 v2, Vector3 v3)
	{
		int vertexIndex = vertices.Count;
		vertices.Add(HexMetrics.Perturb(v1));
		vertices.Add(HexMetrics.Perturb(v2));
		vertices.Add(HexMetrics.Perturb(v3));
		triangles.Add(vertexIndex);
		triangles.Add(vertexIndex + 1);
		triangles.Add(vertexIndex + 2);
	}

	// <summary>
	/// Add a triangle verbatim, without perturbing the positions.
	/// </summary>
	/// <param name="v1">First vertex position.</param>
	/// <param name="v2">Second vertex position.</param>
	/// <param name="v3">Third vertex position.</param>
	public void AddTriangleUnperturbed(Vector3 v1, Vector3 v2, Vector3 v3)
	{
		int vertexIndex = vertices.Count;
		vertices.Add(v1);
		vertices.Add(v2);
		vertices.Add(v3);
		triangles.Add(vertexIndex);
		triangles.Add(vertexIndex + 1);
		triangles.Add(vertexIndex + 2);
	}

	/// <summary>
	/// Add UV coordinates for a triangle.
	/// </summary>
	/// <param name="uv1">First UV coordinates.</param>
	/// <param name="uv2">Second UV coordinates.</param>
	/// <param name="uv3">Third UV coordinates.</param>
	public void AddTriangleUV(Vector2 uv1, Vector2 uv2, Vector3 uv3)
	{
		uvs.Add(uv1);
		uvs.Add(uv2);
		uvs.Add(uv3);
	}

	/// <summary>
	/// Add UV2 coordinates for a triangle.
	/// </summary>
	/// <param name="uv1">First UV2 coordinates.</param>
	/// <param name="uv2">Second UV2 coordinates.</param>
	/// <param name="uv3">Third UV2 coordinates.</param>
	public void AddTriangleUV2(Vector2 uv1, Vector2 uv2, Vector3 uv3)
	{
		uv2s.Add(uv1);
		uv2s.Add(uv2);
		uv2s.Add(uv3);
	}

	/// <summary>
	/// Add cell data for a triangle.
	/// </summary>
	/// <param name="indices">Terrain type indices.</param>
	/// <param name="weights1">First terrain weights.</param>
	/// <param name="weights2">Second terrain weights.</param>
	/// <param name="weights3">Third terrain weights.</param>
	public void AddTriangleCellData(
		Vector3 indices, Color weights1, Color weights2, Color weights3)
	{
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellWeights.Add(weights1);
		cellWeights.Add(weights2);
		cellWeights.Add(weights3);
	}

	/// <summary>
	/// Add cell data for a triangle.
	/// </summary>
	/// <param name="indices">Terrain type indices.</param>
	/// <param name="weights">Terrain weights, uniform per triangle.</param>
	public void AddTriangleCellData(Vector3 indices, Color weights) =>
		AddTriangleCellData(indices, weights, weights, weights);

	/// <summary>
	/// Add a quad, applying perturbation to the positions.
	/// </summary>
	/// <param name="v1">First vertex position.</param>
	/// <param name="v2">Second vertex position.</param>
	/// <param name="v3">Third vertex position.</param>
	/// <param name="v4">Fourth vertex position.</param>
	public void AddQuad(Vector3 v1, Vector3 v2, Vector3 v3, Vector3 v4)
	{
		int vertexIndex = vertices.Count;
		vertices.Add(HexMetrics.Perturb(v1));
		vertices.Add(HexMetrics.Perturb(v2));
		vertices.Add(HexMetrics.Perturb(v3));
		vertices.Add(HexMetrics.Perturb(v4));
		triangles.Add(vertexIndex);
		triangles.Add(vertexIndex + 2);
		triangles.Add(vertexIndex + 1);
		triangles.Add(vertexIndex + 1);
		triangles.Add(vertexIndex + 2);
		triangles.Add(vertexIndex + 3);
	}

	/// <summary>
	/// Add a quad verbatim, without perturbing the positions.
	/// </summary>
	/// <param name="v1">First vertex position.</param>
	/// <param name="v2">Second vertex position.</param>
	/// <param name="v3">Third vertex position.</param>
	/// <param name="v4">Fourth vertex position.</param>
	public void AddQuadUnperturbed(
		Vector3 v1, Vector3 v2, Vector3 v3, Vector3 v4)
	{
		int vertexIndex = vertices.Count;
		vertices.Add(v1);
		vertices.Add(v2);
		vertices.Add(v3);
		vertices.Add(v4);
		triangles.Add(vertexIndex);
		triangles.Add(vertexIndex + 2);
		triangles.Add(vertexIndex + 1);
		triangles.Add(vertexIndex + 1);
		triangles.Add(vertexIndex + 2);
		triangles.Add(vertexIndex + 3);
	}

	/// <summary>
	/// Add UV coordinates for a quad.
	/// </summary>
	/// <param name="uv1">First UV coordinates.</param>
	/// <param name="uv2">Second UV coordinates.</param>
	/// <param name="uv3">Third UV coordinates.</param>
	/// <param name="uv4">Fourth UV coordinates.</param>
	public void AddQuadUV(Vector2 uv1, Vector2 uv2, Vector3 uv3, Vector3 uv4)
	{
		uvs.Add(uv1);
		uvs.Add(uv2);
		uvs.Add(uv3);
		uvs.Add(uv4);
	}

	/// <summary>
	/// Add UV2 coordinates for a quad.
	/// </summary>
	/// <param name="uv1">First UV2 coordinates.</param>
	/// <param name="uv2">Second UV2 coordinates.</param>
	/// <param name="uv3">Third UV2 coordinates.</param>
	/// <param name="uv4">Fourth UV2 coordinates.</param>
	public void AddQuadUV2(Vector2 uv1, Vector2 uv2, Vector3 uv3, Vector3 uv4)
	{
		uv2s.Add(uv1);
		uv2s.Add(uv2);
		uv2s.Add(uv3);
		uv2s.Add(uv4);
	}

	/// <summary>
	/// Add UV coordaintes for a quad.
	/// </summary>
	/// <param name="uMin">Minimum U coordinate.</param>
	/// <param name="uMax">Maximum U coordinate.</param>
	/// <param name="vMin">Minimum V coordinate.</param>
	/// <param name="vMax">Maximum V coorindate.</param>
	public void AddQuadUV(float uMin, float uMax, float vMin, float vMax)
	{
		uvs.Add(new Vector2(uMin, vMin));
		uvs.Add(new Vector2(uMax, vMin));
		uvs.Add(new Vector2(uMin, vMax));
		uvs.Add(new Vector2(uMax, vMax));
	}

	/// <summary>
	/// Add UV2 coordaintes for a quad.
	/// </summary>
	/// <param name="uMin">Minimum U2 coordinate.</param>
	/// <param name="uMax">Maximum U2 coordinate.</param>
	/// <param name="vMin">Minimum V2 coordinate.</param>
	/// <param name="vMax">Maximum V2 coorindate.</param>
	public void AddQuadUV2(float uMin, float uMax, float vMin, float vMax)
	{
		uv2s.Add(new Vector2(uMin, vMin));
		uv2s.Add(new Vector2(uMax, vMin));
		uv2s.Add(new Vector2(uMin, vMax));
		uv2s.Add(new Vector2(uMax, vMax));
	}

	/// <summary>
	/// Add cell data for a quad.
	/// </summary>
	/// <param name="indices">Terrain type indices.</param>
	/// <param name="weights1">First terrain weights.</param>
	/// <param name="weights2">Second terrain weights.</param>
	/// <param name="weights3">Third terrain weights.</param>
	/// <param name="weights4">Fourth terrain weights.</param>
	public void AddQuadCellData(
		Vector3 indices,
		Color weights1, Color weights2, Color weights3, Color weights4)
	{
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellWeights.Add(weights1);
		cellWeights.Add(weights2);
		cellWeights.Add(weights3);
		cellWeights.Add(weights4);
	}

	/// <summary>
	/// Add cell data for a quad.
	/// </summary>
	/// <param name="indices">Terrain type indices.</param>
	/// <param name="weights1">First and second terrain weights,
	/// both the same.</param>
	/// <param name="weights2">Third and fourth terrain weights,
	/// both the same.</param>
	public void AddQuadCellData(
		Vector3 indices, Color weights1, Color weights2) =>
		AddQuadCellData(indices, weights1, weights1, weights2, weights2);

	/// <summary>
	/// Add cell data for a quad.
	/// </summary>
	/// <param name="indices">Terrain type indices.</param>
	/// <param name="weights">Terrain weights, uniform for entire quad.</param>
	public void AddQuadCellData(Vector3 indices, Color weights) =>
		AddQuadCellData(indices, weights, weights, weights, weights);
}
