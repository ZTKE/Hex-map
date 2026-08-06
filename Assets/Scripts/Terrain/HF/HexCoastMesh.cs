using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds the vertical coastal bank between the shallow-water strip and land.
/// This is a separate terrain element so beaches, estuaries, and rock cliffs
/// can use independent materials, like an ArtDef coastline collection.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public sealed class HexCoastMesh : MonoBehaviour
{
	readonly List<Vector3> vertices = new();
	readonly List<Vector4> styleData = new();
	readonly List<Vector3> cellIndices = new();
	readonly List<int> triangles = new();

	Mesh mesh;

	void Awake()
	{
		mesh = new Mesh { name = "Hex Coast Cliff Mesh" };
		GetComponent<MeshFilter>().mesh = mesh;
	}

	public void Initialize(Material material, HexTerrainStyle style)
	{
		HexTerrainStyle activeStyle = style ? style : HexTerrainStyle.RuntimeDefault;
		activeStyle.ApplyTo(material);
		MeshRenderer renderer = GetComponent<MeshRenderer>();
		renderer.sharedMaterial = material;
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
		renderer.receiveShadows = true;
	}

	public void Clear()
	{
		mesh.Clear();
		vertices.Clear();
		styleData.Clear();
		cellIndices.Clear();
		triangles.Clear();
	}

	public void AddEdge(
		EdgeVertices waterEdge,
		float landY,
		int landCellIndex,
		int terrainTypeIndex,
		HexLandform landform,
		HexHash hash)
	{
		float height = landY - waterEdge.v1.y;
		if (height < 0.08f)
		{
			return;
		}

		float rocky = landform == HexLandform.Flat ? 0.22f :
			landform == HexLandform.Hill ? 0.72f : 1f;
		AddSegment(waterEdge.v1, waterEdge.v2, landY,
			landCellIndex, terrainTypeIndex, rocky, hash.a);
		AddSegment(waterEdge.v2, waterEdge.v3, landY,
			landCellIndex, terrainTypeIndex, rocky, hash.b);
		AddSegment(waterEdge.v3, waterEdge.v4, landY,
			landCellIndex, terrainTypeIndex, rocky, hash.c);
		AddSegment(waterEdge.v4, waterEdge.v5, landY,
			landCellIndex, terrainTypeIndex, rocky, hash.d);
	}

	public void Apply()
	{
		mesh.SetVertices(vertices);
		mesh.SetUVs(0, styleData);
		mesh.SetUVs(1, cellIndices);
		mesh.SetTriangles(triangles, 0);
		mesh.RecalculateNormals();
		mesh.RecalculateBounds();
	}

	void AddSegment(
		Vector3 bottomA,
		Vector3 bottomB,
		float landY,
		int cellIndex,
		int terrainTypeIndex,
		float rocky,
		float seed)
	{
		int first = vertices.Count;
		Vector3 topA = new(bottomA.x, landY - 0.015f, bottomA.z);
		Vector3 topB = new(bottomB.x, landY - 0.015f, bottomB.z);
		bottomA.y -= 0.06f;
		bottomB.y -= 0.06f;

		vertices.Add(bottomA);
		vertices.Add(bottomB);
		vertices.Add(topA);
		vertices.Add(topB);
		styleData.Add(new Vector4(terrainTypeIndex, rocky, seed, 0f));
		styleData.Add(new Vector4(terrainTypeIndex, rocky, seed, 0f));
		styleData.Add(new Vector4(terrainTypeIndex, rocky, seed, 1f));
		styleData.Add(new Vector4(terrainTypeIndex, rocky, seed, 1f));
		Vector3 indices = new(cellIndex, cellIndex, cellIndex);
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellIndices.Add(indices);
		cellIndices.Add(indices);

		triangles.Add(first);
		triangles.Add(first + 2);
		triangles.Add(first + 1);
		triangles.Add(first + 1);
		triangles.Add(first + 2);
		triangles.Add(first + 3);
	}
}
