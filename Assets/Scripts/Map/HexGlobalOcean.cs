using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One continuous water surface used by the medium / far camera view. The
/// shader resolves the live logical water mask, so this mesh never replaces
/// land and does not need a separately baked coast texture.
/// </summary>
[DisallowMultipleComponent]
public sealed class HexGlobalOcean : MonoBehaviour
{
	const string oceanObjectName = "Global Ocean";

	GameObject oceanObject;
	Mesh oceanMesh;
	Material oceanMaterial;

	public bool IsVisible => oceanObject && oceanObject.activeSelf;
	public bool IsReady => oceanMesh && oceanMaterial;

	public void Rebuild(HexGrid grid)
	{
		if (!grid || grid.CellData == null || grid.CellData.Length == 0)
		{
			SetVisible(false);
			return;
		}

		EnsureRenderer();
		if (!oceanMesh || !oceanMaterial)
		{
			return;
		}

		GetMapBounds(
			grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		oceanMaterial.SetTexture("_OceanNoise", HexMetrics.noiseSource);
		float width = xMax - xMin;
		float waterY =
			HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		int copyCount = grid.Wrapping ? 3 : 1;
		Vector3[] vertices = new Vector3[copyCount * 4];
		Vector2[] uvs = new Vector2[copyCount * 4];
		int[] triangles = new int[copyCount * 6];

		for (int copy = 0; copy < copyCount; copy++)
		{
			float offset = grid.Wrapping ? (copy - 1) * width : 0f;
			int vertex = copy * 4;
			vertices[vertex] = new Vector3(xMin + offset, waterY, zMin);
			vertices[vertex + 1] = new Vector3(xMax + offset, waterY, zMin);
			vertices[vertex + 2] = new Vector3(xMax + offset, waterY, zMax);
			vertices[vertex + 3] = new Vector3(xMin + offset, waterY, zMax);
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

		oceanMesh.Clear();
		oceanMesh.vertices = vertices;
		oceanMesh.uv = uvs;
		oceanMesh.triangles = triangles;
		oceanMesh.RecalculateBounds();
	}

	public void SetVisible(bool visible)
	{
		if (oceanObject && oceanObject.activeSelf != visible)
		{
			oceanObject.SetActive(visible);
		}
	}

	void EnsureRenderer()
	{
		if (!oceanObject)
		{
			Transform existing = transform.Find(oceanObjectName);
			oceanObject = existing ? existing.gameObject :
				new GameObject(oceanObjectName);
			oceanObject.transform.SetParent(transform, false);
			oceanObject.layer = gameObject.layer;
		}

		MeshFilter filter = oceanObject.GetComponent<MeshFilter>();
		if (!filter)
		{
			filter = oceanObject.AddComponent<MeshFilter>();
		}
		MeshRenderer renderer = oceanObject.GetComponent<MeshRenderer>();
		if (!renderer)
		{
			renderer = oceanObject.AddComponent<MeshRenderer>();
		}
		renderer.shadowCastingMode = ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = LightProbeUsage.Off;
		renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

		if (!oceanMesh)
		{
			oceanMesh = new Mesh
			{
				name = "Global Ocean Mesh",
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		filter.sharedMesh = oceanMesh;

		if (!oceanMaterial)
		{
			Shader shader = Shader.Find("Hex Map/Global Ocean");
			if (!shader)
			{
				Debug.LogError(
					"Global ocean shader is missing; chunk water will remain enabled.",
					this);
				return;
			}
			oceanMaterial = new Material(shader)
			{
				name = "Global Ocean Material",
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		renderer.sharedMaterial = oceanMaterial;
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
		DestroyRuntimeObject(oceanMaterial);
		DestroyRuntimeObject(oceanMesh);
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
