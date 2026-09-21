using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A CPU-tessellated collider for the shader-displaced HF surface.
/// Catlike's terrain topology remains available to legacy mode, but it no
/// longer acts as an invisible, differently-shaped interaction surface.
/// </summary>
[RequireComponent(typeof(MeshCollider))]
public sealed class HexSurfaceCollider : MonoBehaviour
{
	readonly List<Vector3> vertices = new();
	readonly List<int> triangles = new();

	Mesh mesh;
	MeshCollider meshCollider;

	void Awake()
	{
		EnsureMesh();
	}

	public void Build(
		HexGrid grid, IReadOnlyList<int> cellIndices, int subdivisions)
	{
		EnsureMesh();
		subdivisions = Mathf.Clamp(subdivisions, 2, 8);
		vertices.Clear();
		triangles.Clear();

		for (int i = 0; i < cellIndices.Count; i++)
		{
			int cellIndex = cellIndices[i];
			Vector3 center = grid.CellPositions[cellIndex];
			for (int direction = 0; direction < 6; direction++)
			{
				AddWedge(
					grid, cellIndex, center,
					HexMetrics.GetFirstCorner((HexDirection)direction),
					HexMetrics.GetSecondCorner((HexDirection)direction),
					subdivisions);
			}
		}

		mesh.Clear();
		mesh.indexFormat = vertices.Count > 65535 ?
			UnityEngine.Rendering.IndexFormat.UInt32 :
			UnityEngine.Rendering.IndexFormat.UInt16;
		mesh.SetVertices(vertices);
		mesh.SetTriangles(triangles, 0, true);
		// Reassigning is required for Unity to recook a modified MeshCollider.
		meshCollider.sharedMesh = null;
		meshCollider.sharedMesh = mesh;
		meshCollider.enabled = true;
		Debug.Assert(
			meshCollider.sharedMesh == mesh && mesh.vertexCount == vertices.Count,
			"HF surface collider cooking did not retain the generated mesh.", this);
	}

	public void Clear()
	{
		EnsureMesh();
		if (meshCollider)
		{
			meshCollider.sharedMesh = null;
			meshCollider.enabled = false;
		}
		if (mesh)
		{
			mesh.Clear();
		}
		vertices.Clear();
		triangles.Clear();
	}

	void EnsureMesh()
	{
		if (!meshCollider)
		{
			meshCollider = GetComponent<MeshCollider>();
		}
		if (mesh)
		{
			return;
		}
		mesh = meshCollider.sharedMesh;
		if (!mesh)
		{
			mesh = new Mesh { name = "HF Surface Collider" };
			mesh.MarkDynamic();
		}
	}

	void AddWedge(
		HexGrid grid, int cellIndex, Vector3 center,
		Vector3 cornerA, Vector3 cornerB, int subdivisions)
	{
		int firstVertex = vertices.Count;
		for (int a = 0; a <= subdivisions; a++)
		{
			for (int b = 0; b <= subdivisions - a; b++)
			{
				Vector3 position = WedgePoint(
					center, cornerA, cornerB, a, b, subdivisions);
				position.y = grid.SampleSurfaceHeight(cellIndex, position);
				vertices.Add(position);
			}
		}

		for (int a = 0; a < subdivisions; a++)
		{
			for (int b = 0; b < subdivisions - a; b++)
			{
				int p0 = firstVertex + WedgeVertexIndex(a, b, subdivisions);
				int p1 = firstVertex + WedgeVertexIndex(a + 1, b, subdivisions);
				int p2 = firstVertex + WedgeVertexIndex(a, b + 1, subdivisions);
				AddTriangle(p0, p1, p2);
				if (a + b < subdivisions - 1)
				{
					int p3 = firstVertex +
						WedgeVertexIndex(a + 1, b + 1, subdivisions);
					AddTriangle(p1, p3, p2);
				}
			}
		}
	}

	static Vector3 WedgePoint(
		Vector3 center, Vector3 cornerA, Vector3 cornerB,
		int a, int b, int subdivisions) =>
		center + (cornerA * a + cornerB * b) / subdivisions;

	static int WedgeVertexIndex(int a, int b, int subdivisions) =>
		a * (subdivisions + 1) - a * (a - 1) / 2 + b;

	void AddTriangle(int a, int b, int c)
	{
		triangles.Add(a);
		triangles.Add(b);
		triangles.Add(c);
	}
}
