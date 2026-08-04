using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generates the visual landform layer that sits on top of the logical hex map.
///
/// The mountain path deliberately follows a terrain-style pipeline: matching
/// neighbours first select a topology module (end, bend, ridge, junction, or
/// massif), then a deterministic hash varies that module. Material bands are
/// supplied to the relief shader through UV data so biome ground, scree, rock,
/// high rock, desert strata, and snow can be shaded independently.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HexReliefMesh : MonoBehaviour
{
	const int sectorCount = 60;
	const int ringCount = 14;

	readonly List<Vector3> vertices = new();
	readonly List<Vector3> cellIndices = new();
	readonly List<Vector4> reliefData = new();
	readonly List<Vector4> styleData = new();
	readonly List<Vector4> localData = new();
	readonly List<int> triangles = new();

	Mesh mesh;
	HexTerrainStyle terrainStyle;

	void Awake()
	{
		mesh = new Mesh { name = "Hex Relief Mesh" };
		GetComponent<MeshFilter>().mesh = mesh;
	}

	public void Initialize(Material material, HexTerrainStyle style)
	{
		terrainStyle = style ? style : HexTerrainStyle.RuntimeDefault;
		terrainStyle.ApplyTo(material);
		MeshRenderer renderer = GetComponent<MeshRenderer>();
		renderer.sharedMaterial = material;
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
		renderer.receiveShadows = true;
	}

	public void Clear()
	{
		mesh.Clear();
		vertices.Clear();
		cellIndices.Clear();
		reliefData.Clear();
		styleData.Clear();
		localData.Clear();
		triangles.Clear();
	}

	public void AddCell(
		int cellIndex,
		Vector3 center,
		HexLandform landform,
		int terrainTypeIndex,
		int elevation,
		HexHash hash,
		float ridgeAngle,
		int matchingNeighborMask)
	{
		if (landform == HexLandform.Flat)
		{
			return;
		}
		int moduleIndex = landform == HexLandform.Mountain ?
			terrainStyle.SelectMountainModule(matchingNeighborMask, hash) : -1;
		if (landform == HexLandform.Mountain &&
			terrainStyle.TryGetAuthoredMesh(moduleIndex, out HexTerrainStyle.MountainModule module))
		{
			AddAuthoredMountainCell(
				cellIndex, center, terrainTypeIndex, elevation, hash,
				ridgeAngle, moduleIndex, module);
			return;
		}

		int firstVertex = vertices.Count;
		AddReliefVertex(
			center, Vector2.zero, cellIndex, landform, terrainTypeIndex,
			elevation, hash, ridgeAngle, matchingNeighborMask, moduleIndex);

		for (int ring = 1; ring <= ringCount; ring++)
		{
			// More samples are kept near the summit where the module silhouettes
			// and material bands change most quickly.
			float radius01 = Mathf.Pow(ring / (float)ringCount, 1.32f);
			for (int sector = 0; sector < sectorCount; sector++)
			{
				float angle = sector * (2f * Mathf.PI / sectorCount);
				int side = sector / (sectorCount / 6);
				float sideStep = (sector % (sectorCount / 6)) /
					(float)(sectorCount / 6);
				Vector3 boundary = Vector3.Lerp(
					HexMetrics.GetFirstCorner((HexDirection)side),
					HexMetrics.GetSecondCorner((HexDirection)side),
					sideStep);
				Vector2 local = new(
					Mathf.Sin(angle) * radius01,
					Mathf.Cos(angle) * radius01);
				AddReliefVertex(
					center + boundary * radius01, local, cellIndex, landform,
					terrainTypeIndex, elevation, hash, ridgeAngle,
					matchingNeighborMask, moduleIndex);
			}
		}

		for (int sector = 0; sector < sectorCount; sector++)
		{
			int next = (sector + 1) % sectorCount;
			AddTriangle(firstVertex, firstVertex + 1 + sector, firstVertex + 1 + next);
		}

		for (int ring = 1; ring < ringCount; ring++)
		{
			int innerStart = firstVertex + 1 + (ring - 1) * sectorCount;
			int outerStart = innerStart + sectorCount;
			for (int sector = 0; sector < sectorCount; sector++)
			{
				int next = (sector + 1) % sectorCount;
				int inner = innerStart + sector;
				int innerNext = innerStart + next;
				int outer = outerStart + sector;
				int outerNext = outerStart + next;
				AddTriangle(inner, outer, outerNext);
				AddTriangle(inner, outerNext, innerNext);
			}
		}
	}

	public void Apply()
	{
		mesh.SetVertices(vertices);
		mesh.SetUVs(1, reliefData);
		mesh.SetUVs(2, cellIndices);
		mesh.SetUVs(3, styleData);
		mesh.SetUVs(4, localData);
		mesh.SetTriangles(triangles, 0);
		mesh.RecalculateNormals();

		// The skirt must visually merge back into the base terrain. Keeping its
		// normals close to up also prevents a dark ring around every module.
		Vector3[] normals = mesh.normals;
		for (int i = 0; i < normals.Length; i++)
		{
			float normalBlend = Mathf.SmoothStep(0.025f, 0.28f, reliefData[i].w);
			normals[i] = Vector3.Normalize(Vector3.Lerp(
				Vector3.up, normals[i], normalBlend));
		}
		mesh.normals = normals;
		mesh.RecalculateBounds();
	}

	void AddReliefVertex(
		Vector3 position,
		Vector2 local,
		int cellIndex,
		HexLandform landform,
		int terrainTypeIndex,
		int elevation,
		HexHash hash,
		float ridgeAngle,
		int matchingNeighborMask,
		int moduleIndex)
	{
		float height = EvaluateHeight(
			local, landform, terrainTypeIndex, hash, ridgeAngle,
			matchingNeighborMask, moduleIndex, out Vector2 modulePoint);
		position.y += height;
		vertices.Add(position);
		cellIndices.Add(new Vector3(cellIndex, cellIndex, cellIndex));

		float maximum = terrainStyle.GetMaximumHeight(landform, terrainTypeIndex);
		float edgeFade = Mathf.SmoothStep(0f, 1f, 1f - local.magnitude);
		edgeFade = Mathf.Max(edgeFade, EvaluateConnectionCoverage(
			local, matchingNeighborMask, landform == HexLandform.Mountain));
		reliefData.Add(new Vector4(
			Mathf.Clamp01(height / maximum),
			(float)landform,
			terrainTypeIndex,
			edgeFade));
		styleData.Add(new Vector4(
			hash.a,
			hash.b,
			hash.c,
			Mathf.Clamp01((elevation + 2f) / 10f) + hash.d * 0.001f));
		localData.Add(new Vector4(
			modulePoint.x * 0.5f + 0.5f,
			modulePoint.y * 0.5f + 0.5f,
			moduleIndex,
			0f));
	}

	void AddAuthoredMountainCell(
		int cellIndex,
		Vector3 center,
		int terrainTypeIndex,
		int elevation,
		HexHash hash,
		float ridgeAngle,
		int moduleIndex,
		HexTerrainStyle.MountainModule module)
	{
		Mesh source = module.authoredMesh;
		Vector3[] sourceVertices = source.vertices;
		int[] sourceTriangles = source.triangles;
		int firstVertex = vertices.Count;
		float sin = Mathf.Sin(ridgeAngle);
		float cos = Mathf.Cos(ridgeAngle);
		float maximum = terrainStyle.GetMaximumHeight(
			HexLandform.Mountain, terrainTypeIndex);
		float width = terrainStyle.GetMountainWidth(terrainTypeIndex);

		for (int i = 0; i < sourceVertices.Length; i++)
		{
			Vector3 sourceVertex = sourceVertices[i];
			Vector2 modulePoint = new(
				sourceVertex.x * module.footprintScale.x,
				sourceVertex.z * module.footprintScale.y);
			Vector2 worldPoint = new(
				modulePoint.x * cos - modulePoint.y * sin,
				modulePoint.x * sin + modulePoint.y * cos);
			float height = Mathf.Max(0f, sourceVertex.y) *
				maximum * module.heightScale;
			vertices.Add(center + new Vector3(
				worldPoint.x * HexMetrics.innerRadius * width,
				height,
				worldPoint.y * HexMetrics.outerRadius * width));
			cellIndices.Add(new Vector3(cellIndex, cellIndex, cellIndex));
			float edgeFade = Mathf.SmoothStep(
				0f, 1f, 1f - modulePoint.magnitude);
			reliefData.Add(new Vector4(
				Mathf.Clamp01(height / maximum),
				(float)HexLandform.Mountain,
				terrainTypeIndex,
				edgeFade));
			styleData.Add(new Vector4(
				hash.a, hash.b, hash.c,
				Mathf.Clamp01((elevation + 2f) / 10f) + hash.d * 0.001f));
			localData.Add(new Vector4(
				modulePoint.x * 0.5f + 0.5f,
				modulePoint.y * 0.5f + 0.5f,
				moduleIndex,
				1f));
		}
		for (int i = 0; i < sourceTriangles.Length; i++)
		{
			triangles.Add(firstVertex + sourceTriangles[i]);
		}
	}

	void AddTriangle(int a, int b, int c)
	{
		triangles.Add(a);
		triangles.Add(b);
		triangles.Add(c);
	}

	float EvaluateHeight(
		Vector2 point,
		HexLandform landform,
		int terrainTypeIndex,
		HexHash hash,
		float ridgeAngle,
		int matchingNeighborMask,
		int moduleIndex,
		out Vector2 modulePoint)
	{
		float radius = point.magnitude;
		float connectionHeight = landform == HexLandform.Mountain ?
			EvaluateConnections(point, matchingNeighborMask, true) :
			EvaluateConnections(point, matchingNeighborMask, false);
		if (radius >= 1f)
		{
			modulePoint = point;
			return 0.018f + connectionHeight;
		}

		float sin = Mathf.Sin(ridgeAngle);
		float cos = Mathf.Cos(ridgeAngle);
		modulePoint = new Vector2(
			point.x * cos + point.y * sin,
			-point.x * sin + point.y * cos);
		float edgeFade = Mathf.SmoothStep(0f, 1f, 1f - radius);

		if (landform == HexLandform.Hill)
		{
			return EvaluateHill(
				modulePoint, radius, edgeFade, terrainTypeIndex, hash,
				connectionHeight);
		}

		modulePoint /= terrainStyle.GetMountainWidth(terrainTypeIndex);
		return EvaluateMountain(
			modulePoint, radius, edgeFade, terrainTypeIndex, hash,
			moduleIndex, connectionHeight);
	}

	float EvaluateHill(
		Vector2 p,
		float radius,
		float edgeFade,
		int terrainTypeIndex,
		HexHash hash,
		float connectionHeight)
	{
		float height;
		if (terrainTypeIndex == 0)
		{
			// Desert hills use a directional dune field, matching the separate
			// DuneDesertHills style used by terrain-style renderers.
			float dunePhase = hash.c * Mathf.PI * 2f;
			float dune = 0.5f + 0.5f * Mathf.Sin(
				p.x * 8.2f + p.y * 2.1f + dunePhase);
			dune = Mathf.Pow(dune, 1.8f);
			float swell = Mathf.Exp(-(
				p.x * p.x * 1.45f + p.y * p.y * 2.2f));
			height = 0.32f + swell * (0.72f + dune * 0.56f);
		}
		else
		{
			Vector2 offset = new(
				(hash.b - 0.5f) * 0.28f,
				(hash.c - 0.5f) * 0.2f);
			float main = EllipticalMound(
				p - offset, 0.92f, 0.7f, 1.38f);
			float shoulder = EllipticalMound(
				p + new Vector2(0.34f + offset.y, -0.12f),
				0.6f, 0.48f, 0.68f);
			float back = EllipticalMound(
				p - new Vector2(0.38f, 0.15f - offset.x),
				0.52f, 0.44f, 0.48f);
			height = Mathf.Max(main, Mathf.Max(shoulder, back)) + 0.18f;
		}

		float undulation = 0.045f * Mathf.Sin(
			(p.x * 3.1f - p.y * 2.4f) * Mathf.PI + hash.a * 6f);
		// The original procedural profile was authored around a 1.9-unit
		// reference height. Scale the complete mound (including connections)
		// so the style's hillHeight is a real art-direction control.
		float heightScale = terrainStyle.hillHeight / 1.9f;
		return Mathf.Min(
			((height + undulation) * Mathf.Pow(edgeFade, 0.52f) +
			connectionHeight) * heightScale,
			terrainStyle.hillHeight);
	}

	float EvaluateMountain(
		Vector2 p,
		float radius,
		float edgeFade,
		int terrainTypeIndex,
		HexHash hash,
		int moduleIndex,
		float connectionHeight)
	{
		float maximum = terrainStyle.GetMaximumHeight(
			HexLandform.Mountain, terrainTypeIndex);
		float peak = terrainStyle.SampleMountainHeight(moduleIndex, p) * maximum;
		// Small coordinate-stable cuts break up the sampled height field while
		// preserving the authored module silhouette and saddles.
		float apron = Mathf.Pow(edgeFade, 1.12f) *
			(0.12f + 0.25f * Mathf.Exp(-radius * radius * 2.2f));
		float erosion = 0.11f *
			Mathf.Sin(p.x * 17f + p.y * 7f + hash.b * 8f) *
			Mathf.Sin(p.y * 13f - p.x * 5f + hash.c * 7f) *
			Mathf.SmoothStep(0.08f, 0.78f, peak / maximum);

		float height = (peak + apron + erosion) * Mathf.Pow(edgeFade, 0.22f);
		return Mathf.Min(
			Mathf.Max(height, connectionHeight + apron * 0.42f),
			maximum);
	}

	static float EllipticalMound(
		Vector2 point, float widthX, float widthY, float height)
	{
		float q =
			point.x * point.x / (widthX * widthX) +
			point.y * point.y / (widthY * widthY);
		return Mathf.Exp(-q * 1.7f) * height;
	}

	static float EvaluateConnections(
		Vector2 point, int neighborMask, bool mountain)
	{
		if (neighborMask == 0)
		{
			return 0f;
		}

		float connection = 0f;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if ((neighborMask & (1 << (int)d)) == 0)
			{
				continue;
			}
			Vector3 worldDirection = HexMetrics.GetSolidEdgeMiddle(d).normalized;
			Vector2 direction = new(worldDirection.x, worldDirection.z);
			float along = Vector2.Dot(point, direction);
			float lateral = Mathf.Abs(
				point.x * direction.y - point.y * direction.x);
			float start = mountain ? 0.12f : 0.28f;
			float gate = Mathf.SmoothStep(start, start + 0.26f, along);
			float width = mountain ?
				Mathf.Lerp(0.3f, 0.17f, Mathf.Clamp01(along)) :
				Mathf.Lerp(0.42f, 0.25f, Mathf.Clamp01(along));
			float crossSection = Mathf.Pow(
				Mathf.Clamp01(1f - lateral / width),
				mountain ? 1.35f : 1.8f);
			float ridgeHeight = mountain ?
				Mathf.Lerp(2.05f, 0.56f, Mathf.SmoothStep(0.1f, 1f, along)) :
				Mathf.Lerp(0.42f, 0.12f, Mathf.SmoothStep(0.2f, 1f, along));
			connection = Mathf.Max(
				connection, gate * crossSection * ridgeHeight);
		}
		return connection;
	}

	static float EvaluateConnectionCoverage(
		Vector2 point, int neighborMask, bool mountain)
	{
		if (neighborMask == 0)
		{
			return 0f;
		}
		float coverage = 0f;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if ((neighborMask & (1 << (int)d)) == 0)
			{
				continue;
			}
			Vector3 worldDirection = HexMetrics.GetSolidEdgeMiddle(d).normalized;
			Vector2 direction = new(worldDirection.x, worldDirection.z);
			float along = Vector2.Dot(point, direction);
			float lateral = Mathf.Abs(
				point.x * direction.y - point.y * direction.x);
			float width = mountain ?
				Mathf.Lerp(0.29f, 0.14f, Mathf.Clamp01(along)) :
				Mathf.Lerp(0.4f, 0.22f, Mathf.Clamp01(along));
			float ridge = Mathf.SmoothStep(0.18f, 0.5f, along) *
				Mathf.Pow(Mathf.Clamp01(1f - lateral / width), 1.6f);
			coverage = Mathf.Max(coverage, ridge);
		}
		return coverage;
	}
}
