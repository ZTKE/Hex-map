using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// A pooled chunk's instanced vegetation. Trees have no individual GameObjects,
/// colliders, mesh copies or Update methods. Ground placement uses the same CPU
/// sampler as units, roads and the terrain collider.
/// </summary>
public sealed class HexNearVegetationMesh : MonoBehaviour
{
	// Matches the shader's fixed instance buffer and stays below D3D constant-buffer limits.
	const int maxBatchSize = 256;
	// Re-entering near view can reuse these submission buffers. Bound retained
	// storage when the last visible chunk leaves; style changes may add keys.
	const int maxRetainedDrawGroups = 128;
	static readonly int instanceCellId = Shader.PropertyToID("_HexNearInstanceCell");
	static readonly int instanceTintId = Shader.PropertyToID("_HexNearInstanceTint");
	sealed class Batch
	{
		public HexNearVegetationProfile.Species species;
		public readonly List<Matrix4x4> local = new();
		public readonly List<float> cells = new();
		public readonly List<Vector4> tints = new();
		public Matrix4x4[] world;
		public Bounds localBounds;
		public Bounds worldBounds;
		public Matrix4x4 lastTransform;
		public bool transformReady;
	}
	readonly struct DrawKey : System.IEquatable<DrawKey>
	{
		public readonly Mesh mesh;
		public readonly Material material;
		public readonly int submesh, layer;
		public readonly ShadowCastingMode shadowMode;
		public DrawKey(Mesh mesh, Material material, int submesh, int layer, ShadowCastingMode shadowMode)
		{ this.mesh = mesh; this.material = material; this.submesh = submesh; this.layer = layer; this.shadowMode = shadowMode; }
		public bool Equals(DrawKey other) => mesh == other.mesh && material == other.material &&
			submesh == other.submesh && layer == other.layer && shadowMode == other.shadowMode;
		public override bool Equals(object other) => other is DrawKey key && Equals(key);
		public override int GetHashCode()
		{
			unchecked { return ((((mesh.GetInstanceID() * 397) ^ material.GetInstanceID()) * 397 ^
				submesh) * 397 ^ layer) * 397 ^ (int)shadowMode; }
		}
	}
	sealed class DrawGroup
	{
		public readonly Matrix4x4[] matrices = new Matrix4x4[maxBatchSize];
		public readonly float[] cells = new float[maxBatchSize];
		public readonly Vector4[] tints = new Vector4[maxBatchSize];
		public readonly MaterialPropertyBlock properties = new();
		public int count;
	}
	static readonly List<HexNearVegetationMesh> activeChunks = new();
	static readonly Dictionary<DrawKey, DrawGroup> drawGroups = new();
	static readonly Plane[] cameraPlanes = new Plane[6];
	readonly List<Batch> batches = new();
	readonly Dictionary<HexNearVegetationProfile.Species, Batch> batchBySpecies = new();
	readonly List<Vector2> acceptedPositions = new();
	readonly Vector2[] clusterCenters = new Vector2[3];
	readonly float[] clusterRadii = new float[3];
	readonly Vector2[] clusterAxes = new Vector2[3];
	readonly float[] clusterAspects = new float[3];
	readonly float[] clusterChoices = new float[3];
	readonly float[] clusterTints = new float[3];
	HexGrid grid;
	HexNearVegetationProfile profile;
	int instanceCount;
	int desertInstanceCount;
	bool applied;
	public bool StreamingVisible { get; private set; } = true;

	public int InstanceCount => instanceCount;
	public int DesertInstanceCount => desertInstanceCount;
	public int BatchCount
	{
		get { int count = 0; foreach (Batch batch in batches) if (batch.local.Count > 0) count++; return count; }
	}
	// Submission statistics for the last camera callback, not GPU timing or frame-debugger counters.
	public static int LastCameraId { get; private set; }
	public static int LastDrawCallCount { get; private set; }
	public static int LastDrawnInstanceCount { get; private set; }
	public static int LastShadowedInstanceCount { get; private set; }
	public static int LastShadowOnlyInstanceCount { get; private set; }
	public static int LastShadowOnlyDrawCallCount { get; private set; }
	public bool IsReady => grid && profile && profile.IsReady && SystemInfo.supportsInstancing;

	public void Configure(HexGrid owner, HexNearVegetationProfile recipe)
	{
		if (grid != owner || profile != recipe)
		{
			Clear();
			batches.Clear();
			batchBySpecies.Clear();
		}
		grid = owner;
		profile = recipe;
	}

	public void Clear()
	{
		foreach (Batch batch in batches)
		{
			batch.local.Clear();
			batch.cells.Clear();
			batch.tints.Clear();
			batch.transformReady = false;
		}
		acceptedPositions.Clear();
		instanceCount = 0;
		desertInstanceCount = 0;
		applied = false;
	}

	public void Apply()
	{
		foreach (Batch batch in batches)
		{
			// Pooled chunks revisit cells repeatedly. Keep the allocated matrix
			// storage; the local count, never capacity, controls drawing below.
			if (batch.local.Count > 0 &&
				(batch.world == null || batch.world.Length < batch.local.Count))
				batch.world = new Matrix4x4[Mathf.NextPowerOfTwo(batch.local.Count)];
			batch.transformReady = false;
		}
		// All generated instances already passed recipe readiness. Rendering
		// this result need not query every species mesh again for each camera.
		applied = IsReady;
	}

	/// <returns>True when the mesh recipe owns this cell, including excluded terrain.</returns>
	public bool TryAddCell(HexCellData cell, int cellIndex, Vector3 center)
	{
		if (!IsReady) return false;
		// These are visual biome details, never a new saved forest kind. An
		// authored forest always wins; painted woodland is not replaced by cacti.
		if (cell.TerrainTypeIndex == 0 && cell.VegetationDensity <= 0 && profile.HasDesertAccents)
		{
			AddDesertAccents(cell, cellIndex, center);
			return true;
		}
		if (!profile.HasRecipe(cell.vegetation)) return false;
		if (cell.IsUnderwater || cell.IsSpecial ||
			cell.VegetationDensity <= 0) return true;
		bool mountain = cell.landform == HexLandform.Mountain;
		int targetCount = Mathf.RoundToInt(profile.densityPerCell *
			Mathf.Clamp01(cell.VegetationDensity / 100f));
		if (mountain) targetCount = Mathf.RoundToInt(targetCount * Mathf.Clamp01(profile.mountainDensityScale));
		if (cell.UrbanLevel > 0 || cell.FarmLevel > 0)
			targetCount = Mathf.RoundToInt(targetCount * 0.45f);
		if (targetCount <= 0) return true;
		float datum = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		float treeLine = float.PositiveInfinity;
		if (mountain)
		{
			HexNearTerrainProfile terrain = grid.SurfaceStyle ? grid.SurfaceStyle.nearTerrainProfile : null;
			// A low, level patch can carry authored woodland; peaks stay bare even
			// when their summit is flat. Never guess a tree line for a missing recipe.
			if (!terrain || !terrain.IsReady) return true;
			float height = cell.TerrainTypeIndex == 0 ? terrain.desertMountainHeight : terrain.mountainHeight;
			treeLine = datum + height * Mathf.Clamp01(profile.mountainTreeLine);
		}
		uint random = unchecked((uint)cellIndex * 747796405u + 2891336453u);
		acceptedPositions.Clear();
		// Seeds depend only on the logical cell, so pooled chunk order and
		// wrapping never rearrange the groves. Small overlapping crowns leave
		// intentional glades, instead of distributing every tree uniformly.
		int clusterCount = Next(ref random) < 0.5f ? 2 : 3;
		for (int cluster = 0; cluster < clusterCount; cluster++)
		{
			Vector2 grove;
			do
			{
				grove = new Vector2((Next(ref random) * 2f - 1f) * HexMetrics.innerRadius,
					(Next(ref random) * 2f - 1f) * HexMetrics.outerRadius);
			} while (!InsideHex(grove, 0.78f));
			clusterCenters[cluster] = grove;
			clusterRadii[cluster] = profile.clusterRadius * Mathf.Lerp(0.8f, 1.2f, Next(ref random));
			float angle = Next(ref random) * Mathf.PI * 2f;
			clusterAxes[cluster] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
			// Equal-area ellipses break the repeated circular clumps without
			// increasing density, attempts or the chunk's instance budget.
			clusterAspects[cluster] = Mathf.Lerp(0.8f, 1.3f, Next(ref random));
			clusterChoices[cluster] = Next(ref random);
			clusterTints[cluster] = Next(ref random) * 2f - 1f;
		}
		int clusteredTarget = Mathf.RoundToInt(targetCount * Mathf.Clamp01(profile.clusteredFraction));
		int clusteredCount = 0;
		float slopeDegrees = mountain ? Mathf.Min(profile.maxSlopeDegrees, 30f) : profile.maxSlopeDegrees;
		float slopeLimit = Mathf.Tan(Mathf.Clamp(slopeDegrees, 0f, 70f) * Mathf.Deg2Rad);
		float spacingSquared = profile.minimumSpacing * profile.minimumSpacing;
		int coastMask = 0, forestEdgeMask = 0;
		for (int d = 0; d < 6; d++)
		{
			if (!grid.TryGetCellIndex(cell.coordinates.Step((HexDirection)d), out int neighborIndex))
			{ forestEdgeMask |= 1 << d; continue; }
			HexCellData neighbor = grid.CellData[neighborIndex];
			if (neighbor.IsUnderwater) coastMask |= 1 << d;
			if (neighbor.IsUnderwater || neighbor.IsSpecial || neighbor.VegetationDensity <= 0 ||
				(neighbor.landform == HexLandform.Mountain && profile.mountainDensityScale <= 0f))
				forestEdgeMask |= 1 << d;
		}
		for (int attempt = 0; attempt < targetCount * 6 &&
			acceptedPositions.Count < targetCount && instanceCount < profile.maxInstancesPerChunk;
			attempt++)
		{
			// Quota is based on accepted trees, not rejected candidates. The
			// final attempts may scatter freely when a grove falls on a river,
			// cliff or settlement, so clearances do not erase the entire forest.
			bool inCluster = clusteredCount < clusteredTarget && attempt < targetCount * 4 &&
				(acceptedPositions.Count - clusteredCount >= targetCount - clusteredTarget ||
					Next(ref random) < profile.clusteredFraction);
			Vector2 point;
			if (inCluster)
			{
				int cluster = Mathf.Min((int)(Next(ref random) * clusterCount), clusterCount - 1);
				float angle = Next(ref random) * Mathf.PI * 2f;
				float radius = Mathf.Sqrt(Next(ref random)) * clusterRadii[cluster];
				Vector2 axis = clusterAxes[cluster];
				point = clusterCenters[cluster] + radius * (axis * (Mathf.Cos(angle) * clusterAspects[cluster]) +
					new Vector2(-axis.y, axis.x) * (Mathf.Sin(angle) / clusterAspects[cluster]));
			}
			else point = new Vector2(
				(Next(ref random) * 2f - 1f) * HexMetrics.innerRadius,
				(Next(ref random) * 2f - 1f) * HexMetrics.outerRadius);
			if (!InsideHex(point, 0.96f) || !ClearOfInfrastructure(cell, point, coastMask)) continue;
			bool crowded = false;
			foreach (Vector2 previous in acceptedPositions)
				if ((previous - point).sqrMagnitude < spacingSquared)
				{ crowded = true; break; }
			if (crowded) continue;
			float groveCore = GroveInterior(point, clusterCount, out int groveIndex);
			float groupChoice = Next(ref random);
			// Whole groves lean toward one species group, with scattered mixing
			// at their edges. The choice remains uniformly distributed overall.
			if (Next(ref random) < groveCore * 0.65f) groupChoice = clusterChoices[groveIndex];
			HexNearVegetationProfile.Species species = profile.Pick(
				cell.vegetation, groupChoice, Next(ref random));
			if (species == null) continue;
			Vector3 position = center + new Vector3(point.x, 0f, point.y);
			float y = grid.SampleSurfaceHeight(cellIndex, position);
			if (float.IsNaN(y) || float.IsInfinity(y)) continue;
			if (y < datum + 0.04f || y > treeLine) continue;
			const float sampleOffset = 0.35f;
			float dx = (grid.SampleSurfaceHeight(cellIndex,
				position + Vector3.right * sampleOffset) - y) / sampleOffset;
			float dz = (grid.SampleSurfaceHeight(cellIndex,
				position + Vector3.forward * sampleOffset) - y) / sampleOffset;
			float slopeSquared = dx * dx + dz * dz;
			if (float.IsNaN(slopeSquared) || float.IsInfinity(slopeSquared) ||
				slopeSquared > slopeLimit * slopeLimit) continue;
			float size = species.height * Mathf.Lerp(1f - species.scaleVariation,
				1f + species.scaleVariation, Next(ref random));
			float slopeExposure = Mathf.SmoothStep(0f, 1f,
				Mathf.InverseLerp(0.35f, 1f, slopeSquared / Mathf.Max(0.0001f, slopeLimit * slopeLimit)));
			float canopy = Mathf.Lerp(0.88f, 1.14f, groveCore) *
				Mathf.Lerp(0.82f, 1f, ForestInterior(point, forestEdgeMask)) *
				Mathf.Lerp(1f, 0.84f, slopeExposure);
			if (mountain)
			{
				float alpineEdge = Mathf.SmoothStep(0f, 1f,
					Mathf.InverseLerp(Mathf.Lerp(datum, treeLine, 0.65f), treeLine, y));
				canopy *= Mathf.Lerp(0.86f, 0.64f, alpineEdge);
			}
			size *= Mathf.Lerp(1f, canopy, Mathf.Clamp01(profile.canopyStructure));
			if (cell.vegetation == HexVegetation.Sapling) size *= 0.58f;
			float scale = size / Mathf.Max(species.mesh.bounds.size.y, 0.001f);
			Quaternion rotation = Quaternion.Euler(species.rotationOffset) *
				Quaternion.Euler(0f, Next(ref random) * 360f, 0f);
			position.y = y - profile.groundInset;
			Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, Vector3.one * scale) *
				Matrix4x4.Translate(new Vector3(-species.mesh.bounds.center.x,
					-species.mesh.bounds.min.y, -species.mesh.bounds.center.z));
			AddInstance(species, matrix, cellIndex,
				GroveTint(cell.vegetationTint, groveCore, clusterTints[groveIndex], Next(ref random)));
			acceptedPositions.Add(point);
			if (inCluster) clusteredCount++;
			instanceCount++;
		}
		return true;
	}

	void AddDesertAccents(HexCellData cell, int cellIndex, Vector3 center)
	{
		if (cell.IsUnderwater || cell.IsSpecial || cell.landform == HexLandform.Mountain ||
			cell.UrbanLevel > 0 || cell.FarmLevel > 0) return;
		uint random = unchecked((uint)cellIndex * 747796405u + 1181783497u);
		if (Next(ref random) >= Mathf.Clamp01(profile.desertAccentCoverage)) return;
		int maximum = Mathf.Clamp(profile.desertAccentDensity, 0, 12);
		int targetCount = Mathf.Min(maximum, 1 + Mathf.FloorToInt(Next(ref random) * maximum));
		if (targetCount <= 0) return;
		Vector2 cluster;
		do
		{
			cluster = new Vector2((Next(ref random) * 2f - 1f) * HexMetrics.innerRadius,
				(Next(ref random) * 2f - 1f) * HexMetrics.outerRadius);
		} while (!InsideHex(cluster, 0.72f));
		float datum = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		float slopeLimit = Mathf.Tan(Mathf.Clamp(profile.desertAccentMaxSlope, 0f, 45f) * Mathf.Deg2Rad);
		float spacing = Mathf.Max(profile.minimumSpacing, 1.1f);
		float radius = Mathf.Max(.5f, profile.desertAccentClusterRadius);
		int coastMask = 0;
		for (int d = 0; d < 6; d++)
			if (grid.TryGetCellIndex(cell.coordinates.Step((HexDirection)d), out int neighborIndex) &&
				grid.CellData[neighborIndex].IsUnderwater) coastMask |= 1 << d;
		acceptedPositions.Clear();
		for (int attempt = 0; attempt < targetCount * 8 && acceptedPositions.Count < targetCount &&
			instanceCount < profile.maxInstancesPerChunk; attempt++)
		{
			float angle = Next(ref random) * Mathf.PI * 2f;
			float distance = Mathf.Sqrt(Next(ref random)) * radius;
			Vector2 point = cluster + new Vector2(Mathf.Cos(angle) * distance, Mathf.Sin(angle) * distance * .8f);
			if (!InsideHex(point, .94f) || !ClearOfInfrastructure(cell, point, coastMask)) continue;
			bool crowded = false;
			foreach (Vector2 previous in acceptedPositions)
				if ((previous - point).sqrMagnitude < spacing * spacing) { crowded = true; break; }
			if (crowded) continue;
			Vector3 position = center + new Vector3(point.x, 0f, point.y);
			float y = grid.SampleSurfaceHeight(cellIndex, position);
			if (float.IsNaN(y) || float.IsInfinity(y) || y <= datum + .12f) continue;
			// Central differences sample the same deformed surface used by units;
			// a larger footprint catches narrow dune crests as well as cliff faces.
			const float offset = .45f;
			float east = grid.SampleSurfaceHeight(cellIndex, position + Vector3.right * offset);
			float west = grid.SampleSurfaceHeight(cellIndex, position - Vector3.right * offset);
			float north = grid.SampleSurfaceHeight(cellIndex, position + Vector3.forward * offset);
			float south = grid.SampleSurfaceHeight(cellIndex, position - Vector3.forward * offset);
			float dx = (east - west) / (offset * 2f), dz = (north - south) / (offset * 2f);
			float slopeSquared = dx * dx + dz * dz;
			if (float.IsNaN(slopeSquared) || float.IsInfinity(slopeSquared) ||
				slopeSquared > slopeLimit * slopeLimit ||
				Mathf.Abs(east + west - 2f * y) > .28f || Mathf.Abs(north + south - 2f * y) > .28f) continue;
			HexNearVegetationProfile.Species species = profile.PickDesertAccent(Next(ref random));
			if (species == null) continue;
			float scale = species.height * Mathf.Lerp(1f - species.scaleVariation,
				1f + species.scaleVariation, Next(ref random)) / Mathf.Max(.001f, species.mesh.bounds.size.y);
			Quaternion rotation = Quaternion.Euler(species.rotationOffset) * Quaternion.Euler(0f, Next(ref random) * 360f, 0f);
			position.y = y - profile.groundInset;
			Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, Vector3.one * scale) *
				Matrix4x4.Translate(new Vector3(-species.mesh.bounds.center.x,
					-species.mesh.bounds.min.y, -species.mesh.bounds.center.z));
			float tone = Mathf.Lerp(.91f, 1.09f, Next(ref random));
			AddInstance(species, matrix, cellIndex, new Color(tone, tone, tone * .96f, 1f));
			acceptedPositions.Add(point);
			instanceCount++;
			desertInstanceCount++;
		}
	}

	float GroveInterior(Vector2 point, int clusterCount, out int nearest)
	{
		float distanceSquared = float.PositiveInfinity;
		nearest = 0;
		for (int i = 0; i < clusterCount; i++)
		{
			Vector2 delta = point - clusterCenters[i], axis = clusterAxes[i];
			float x = Vector2.Dot(delta, axis) / clusterAspects[i];
			float z = Vector2.Dot(delta, new Vector2(-axis.y, axis.x)) * clusterAspects[i];
			float distance = (x * x + z * z) / Mathf.Max(0.0001f, clusterRadii[i] * clusterRadii[i]);
			if (distance < distanceSquared) { distanceSquared = distance; nearest = i; }
		}
		return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.08f, 1.15f, distanceSquared));
	}

	float ForestInterior(Vector2 point, int edgeMask)
	{
		if (edgeMask == 0 || profile.forestEdgeWidth <= 0f) return 1f;
		float distance = profile.forestEdgeWidth;
		for (int d = 0; d < 6; d++)
		{
			if ((edgeMask & (1 << d)) == 0) continue;
			Vector3 first = HexMetrics.GetFirstCorner((HexDirection)d);
			Vector3 second = HexMetrics.GetSecondCorner((HexDirection)d);
			distance = Mathf.Min(distance, SegmentDistance(point,
				new Vector2(first.x, first.z), new Vector2(second.x, second.z)));
		}
		return Mathf.SmoothStep(0f, 1f, distance / profile.forestEdgeWidth);
	}

	Color GroveTint(HexVegetationTint theme, float groveCore, float groveVariation, float individual)
	{
		float variation = Mathf.Clamp(profile.groveTintVariation, 0f, 0.2f);
		// Taper shared colour toward glades to avoid a visible nearest-grove seam.
		float warmth = variation * (groveVariation * groveCore * 0.75f + (individual * 2f - 1f) * 0.25f);
		float value = 1f - variation * groveCore * 0.35f;
		Color tint = Tint(theme);
		return new Color(tint.r * (1f + warmth * 0.65f) * value,
			tint.g * (1f + warmth * 0.2f) * value, tint.b * (1f - warmth * 0.5f) * value, 1f);
	}

	bool ClearOfInfrastructure(HexCellData cell, Vector2 point, int coastMask)
	{
		if ((cell.UrbanLevel > 0 || cell.FarmLevel > 0) &&
			point.sqrMagnitude < profile.settlementClearance * profile.settlementClearance)
			return false;
		for (int i = 0; i < 6; i++)
		{
			HexDirection direction = (HexDirection)i;
			Vector3 first = HexMetrics.GetFirstCorner(direction);
			Vector3 second = HexMetrics.GetSecondCorner(direction);
			Vector2 a = new(first.x, first.z);
			Vector2 b = new(second.x, second.z);
			Vector2 middle = (a + b) * 0.5f;
			if (cell.flags.HasRoad(direction) &&
				SegmentDistance(point, Vector2.zero, middle) < profile.roadClearance)
				return false;
			if (cell.HasHFRiverThroughEdge(direction) &&
				SegmentDistance(point, a, b) < profile.riverClearance) return false;
			if (cell.HasLegacyRiverThroughEdge(direction) &&
				SegmentDistance(point, Vector2.zero, middle) < profile.riverClearance) return false;
			if ((coastMask & (1 << i)) != 0 &&
				SegmentDistance(point, a, b) < profile.coastClearance) return false;
		}
		return true;
	}

	void AddInstance(HexNearVegetationProfile.Species species, Matrix4x4 matrix,
		int cellIndex, Color tint)
	{
		if (!batchBySpecies.TryGetValue(species, out Batch batch))
		{
			batch = new Batch { species = species };
			batches.Add(batch);
			batchBySpecies.Add(species, batch);
		}
		Bounds instanceBounds = TransformBounds(matrix, species.mesh.bounds);
		if (species.lodMesh) instanceBounds.Encapsulate(TransformBounds(matrix, species.lodMesh.bounds));
		if (batch.local.Count == 0) batch.localBounds = instanceBounds;
		else batch.localBounds.Encapsulate(instanceBounds);
		batch.local.Add(matrix);
		batch.cells.Add(cellIndex);
		batch.tints.Add(tint);
	}

	/// <summary>Suppress both visible trees and shadow-only submissions while cached.</summary>
	public void SetStreamingVisible(bool visible)
	{
		if (StreamingVisible == visible) return;
		StreamingVisible = visible;
		UpdateRenderRegistration();
	}

	void OnEnable() => UpdateRenderRegistration();
	void OnDisable() => RemoveRenderRegistration();

	void UpdateRenderRegistration()
	{
		if (!StreamingVisible || !isActiveAndEnabled)
		{
			RemoveRenderRegistration();
			return;
		}
		if (activeChunks.Contains(this)) return;
		if (activeChunks.Count == 0) RenderPipelineManager.beginCameraRendering += RenderAll;
		activeChunks.Add(this);
	}

	void RemoveRenderRegistration()
	{
		if (!activeChunks.Remove(this) || activeChunks.Count != 0) return;
		RenderPipelineManager.beginCameraRendering -= RenderAll;
		if (drawGroups.Count > maxRetainedDrawGroups) drawGroups.Clear();
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	static void ResetStatics()
	{
		RenderPipelineManager.beginCameraRendering -= RenderAll;
		// Scene reload can be disabled independently from domain reload. Keep
		// surviving enabled chunks, whose OnEnable will not necessarily run again.
		activeChunks.RemoveAll(chunk => !chunk || !chunk.isActiveAndEnabled || !chunk.StreamingVisible);
		drawGroups.Clear();
		if (activeChunks.Count > 0) RenderPipelineManager.beginCameraRendering += RenderAll;
		LastCameraId = LastDrawCallCount = LastDrawnInstanceCount = LastShadowedInstanceCount = 0;
		LastShadowOnlyInstanceCount = LastShadowOnlyDrawCallCount = 0;
	}

	static void RenderAll(ScriptableRenderContext context, Camera camera)
	{
		if (!camera || camera.cameraType == CameraType.Preview) return;
		LastCameraId = camera.GetInstanceID();
		LastDrawCallCount = LastDrawnInstanceCount = LastShadowedInstanceCount = 0;
		LastShadowOnlyInstanceCount = LastShadowOnlyDrawCallCount = 0;
		GeometryUtility.CalculateFrustumPlanes(camera, cameraPlanes);
		Light shadowLight = GetMainShadowLight(camera, out float shadowDistance);
		foreach (DrawGroup group in drawGroups.Values) group.count = 0;
		foreach (HexNearVegetationMesh chunk in activeChunks)
			if (chunk && chunk.isActiveAndEnabled) chunk.QueueVisible(camera, shadowLight, shadowDistance);
		foreach (KeyValuePair<DrawKey, DrawGroup> pair in drawGroups) Flush(pair.Key, pair.Value, camera);
	}

	void QueueVisible(Camera camera, Light shadowLight, float pipelineShadowDistance)
	{
		if (!StreamingVisible || !applied || !grid || !profile || instanceCount == 0 ||
			(camera.cullingMask & (1 << gameObject.layer)) == 0) return;
		Matrix4x4 localToWorld = transform.localToWorldMatrix;
		float drawDistance = Mathf.Max(0f, profile.drawDistance);
		float shadowDistance = Mathf.Min(drawDistance,
			Mathf.Min(Mathf.Max(0f, profile.shadowDistance), pipelineShadowDistance));
		bool canCastShadows = shadowLight && shadowDistance > 0f &&
			(shadowLight.cullingMask & (1 << gameObject.layer)) != 0;
		Vector3 lightDirection = canCastShadows ? shadowLight.transform.forward : Vector3.zero;
		Vector3 receiverNormal = grid.transform.up;
		// HF's normalized height textures cannot descend below datum - .3 *
		// heightScale. The near river floor is datum - .302. This lower bound
		// includes both surfaces and a small margin, without sampling the world.
		HexTerrainStyle style = grid.SurfaceStyle;
		float receiverFloor = HexMetrics.visualWaterLevel * HexMetrics.elevationStep -
			Mathf.Max(.5f, style ? Mathf.Abs(style.hfOriginalHeightScale) * .3f : .5f) - .1f;
		float receiverPlane = Vector3.Dot(receiverNormal,
			grid.transform.TransformPoint(new Vector3(0f, receiverFloor, 0f)));
		foreach (Batch batch in batches)
		{
			int countInBatch = batch.local.Count;
			if (countInBatch == 0 || batch.world == null) continue;
			if (!batch.transformReady || batch.lastTransform != localToWorld)
			{
				for (int i = 0; i < batch.local.Count; i++) batch.world[i] = localToWorld * batch.local[i];
				batch.worldBounds = TransformBounds(localToWorld, batch.localBounds);
				batch.lastTransform = localToWorld;
				batch.transformReady = true;
			}
			float distance = Vector3.Distance(camera.transform.position,
				batch.worldBounds.ClosestPoint(camera.transform.position));
			if (distance > drawDistance) continue;
			bool visible = GeometryUtility.TestPlanesAABB(cameraPlanes, batch.worldBounds);
			bool shadows = canCastShadows && distance <= shadowDistance;
			if (!visible)
			{
				if (!shadows) continue;
				// An offscreen tree can still cast onto visible ground. Project its
				// bounds along the light, then submit only its shadow pass. The
				// finite range cap also handles a horizontal or below-horizon sun.
				Bounds projected = ShadowReceiverBounds(batch.worldBounds, lightDirection,
					receiverNormal, receiverPlane, shadowDistance * 2f + batch.worldBounds.size.magnitude);
				if (!GeometryUtility.TestPlanesAABB(cameraPlanes, projected)) continue;
			}
			Mesh mesh = distance > profile.lodDistance && batch.species.lodMesh ?
				batch.species.lodMesh : batch.species.mesh;
			ShadowCastingMode shadowMode = !visible ? ShadowCastingMode.ShadowsOnly :
				shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
			bool submitted = false;
			for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
			{
				Material material = batch.species.GetMaterial(submesh);
				if (!material) continue;
				submitted = true;
				DrawKey key = new(mesh, material, submesh, gameObject.layer, shadowMode);
				if (!drawGroups.TryGetValue(key, out DrawGroup group))
				{
					group = new DrawGroup();
					drawGroups.Add(key, group);
				}
				for (int start = 0; start < countInBatch;)
				{
					int count = Mathf.Min(maxBatchSize - group.count, countInBatch - start);
					System.Array.Copy(batch.world, start, group.matrices, group.count, count);
					batch.cells.CopyTo(start, group.cells, group.count, count);
					batch.tints.CopyTo(start, group.tints, group.count, count);
					group.count += count;
					start += count;
					if (group.count == maxBatchSize) Flush(key, group, camera);
				}
			}
			if (!submitted) continue;
			if (visible) LastDrawnInstanceCount += countInBatch;
			else LastShadowOnlyInstanceCount += countInBatch;
			if (shadows) LastShadowedInstanceCount += countInBatch;
		}
	}

	static Light GetMainShadowLight(Camera camera, out float shadowDistance)
	{
		shadowDistance = 0f;
		UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
		if (!pipeline || !pipeline.supportsMainLightShadows ||
			pipeline.mainLightRenderingMode != LightRenderingMode.PerPixel || !SystemInfo.supportsShadows ||
			(camera.TryGetComponent(out UniversalAdditionalCameraData data) && !data.renderShadows)) return null;
		HexNearTerrainLighting.TryGetActiveSun(camera, out Light mainLight);
		if (!mainLight || mainLight.shadows == LightShadows.None || mainLight.shadowStrength <= 0f)
			return null;
		shadowDistance = Mathf.Max(0f, Mathf.Min(pipeline.shadowDistance, camera.farClipPlane));
		return shadowDistance > 0f ? mainLight : null;
	}

	static Bounds ShadowReceiverBounds(Bounds caster, Vector3 lightDirection,
		Vector3 receiverNormal, float receiverPlane, float maximumTravel)
	{
		float travel = Mathf.Max(0f, maximumTravel);
		float descent = -Vector3.Dot(lightDirection, receiverNormal);
		if (descent > .0001f)
		{
			Vector3 e = caster.extents;
			float highest = Vector3.Dot(caster.center, receiverNormal) +
				Mathf.Abs(receiverNormal.x) * e.x + Mathf.Abs(receiverNormal.y) * e.y +
				Mathf.Abs(receiverNormal.z) * e.z;
			travel = Mathf.Min(travel, Mathf.Max(0f, highest - receiverPlane) / descent);
		}
		Vector3 displacement = lightDirection * travel;
		Bounds receivers = caster;
		receivers.Encapsulate(caster.min + displacement);
		receivers.Encapsulate(caster.max + displacement);
		return receivers;
	}

	static void Flush(DrawKey key, DrawGroup group, Camera camera)
	{
		if (group.count == 0 || !key.mesh || !key.material) return;
		if (!key.material.enableInstancing) key.material.enableInstancing = true;
		group.properties.SetFloatArray(instanceCellId, group.cells);
		group.properties.SetVectorArray(instanceTintId, group.tints);
		Graphics.DrawMeshInstanced(key.mesh, key.submesh, key.material, group.matrices, group.count,
			group.properties, key.shadowMode,
			true, key.layer, camera, LightProbeUsage.BlendProbes);
		LastDrawCallCount++;
		if (key.shadowMode == ShadowCastingMode.ShadowsOnly) LastShadowOnlyDrawCallCount++;
		group.count = 0;
	}

	static Color Tint(HexVegetationTint tint) => tint switch
	{
		HexVegetationTint.DeepGreen => new Color(0.76f, 0.94f, 0.72f),
		HexVegetationTint.Autumn => new Color(1.13f, 0.72f, 0.39f),
		HexVegetationTint.Dry => new Color(1.08f, 0.90f, 0.63f),
		HexVegetationTint.Frost => new Color(0.87f, 0.98f, 1.08f),
		HexVegetationTint.Pale => new Color(1.04f, 1.02f, 0.87f),
		_ => Color.white
	};

	static bool InsideHex(Vector2 point, float scale) =>
		Mathf.Abs(point.x) <= HexMetrics.innerRadius * scale &&
		Mathf.Abs(point.y) + Mathf.Abs(point.x) / Mathf.Sqrt(3f) <= HexMetrics.outerRadius * scale;

	static float SegmentDistance(Vector2 point, Vector2 start, Vector2 end)
	{
		Vector2 line = end - start;
		float t = Mathf.Clamp01(Vector2.Dot(point - start, line) / Mathf.Max(line.sqrMagnitude, 0.0001f));
		return (point - start - line * t).magnitude;
	}

	static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
	{
		Vector3 x = matrix.MultiplyVector(new Vector3(bounds.extents.x, 0f, 0f));
		Vector3 y = matrix.MultiplyVector(new Vector3(0f, bounds.extents.y, 0f));
		Vector3 z = matrix.MultiplyVector(new Vector3(0f, 0f, bounds.extents.z));
		return new Bounds(matrix.MultiplyPoint3x4(bounds.center), new Vector3(
			Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
			Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
			Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f);
	}

	static float Next(ref uint state)
	{
		state = state * 747796405u + 2891336453u;
		uint value = ((state >> (int)((state >> 28) + 4)) ^ state) * 277803737u;
		value = (value >> 22) ^ value;
		return (value & 0xffffff) / 16777216f;
	}
}
