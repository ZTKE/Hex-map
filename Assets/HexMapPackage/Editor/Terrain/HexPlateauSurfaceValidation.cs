using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Actual-art checks on the disposable plateau fixture; never writes world or scene data.</summary>
public static class HexPlateauSurfaceValidation
{
	[Serializable] public sealed class BoundaryResult
	{
		public string neighbor;
		public int edges, samples;
		public float maximumCpuGpuError, maximumCpuGap, maximumGpuGap;
	}
	[Serializable] public sealed class Report
	{
		public string timestamp, graphicsDevice, comparison;
		public bool passed;
		public int plateauCells, sampleCount, readbackCount, invalidReadbacks, interiorSamples, riverSamples;
		public float heightTolerance = .05f, seamTolerance = .05f;
		public float plateauHeight, plateauRelief, minimumInteriorLift, interiorCpuHeightRange, interiorGpuHeightRange;
		public float maximumCpuGpuError, maximumCpuGap, maximumGpuGap, oceanFallbackError, riverCarvingError, lowlandRiverCarvingError;
		public BoundaryResult[] boundaries;
		public string[] failures;
	}
	sealed class Sample
	{
		public int cell;
		public Vector2 local;
		public float cpu, gpu;
	}
	struct Seam { public int a, b; public string category; }

	public static string Run(HexNearTerrainShowcase showcase, string artifactPath,
		out bool passed, out string summary)
	{
		if (!EditorApplication.isPlaying || !showcase || !showcase.Ready ||
			showcase.gameObject.scene.name != HexNearTerrainShowcase.SceneName)
			throw new InvalidOperationException("Plateau validation requires the ready disposable showcase.");
		HexNearTerrainProfile profile = showcase.terrainStyle ? showcase.terrainStyle.nearTerrainProfile : null;
		if (!profile || !profile.IsReady || !showcase.terrainStyle.UsesNearTerrain)
			throw new InvalidOperationException("Plateau validation requires an enabled, ready near terrain profile.");
		HexGrid grid = showcase.grid;
		List<Sample> samples = new();
		List<Seam> seams = new();
		Dictionary<string, BoundaryResult> boundaries = new();
		Report report = new() { timestamp = DateTime.UtcNow.ToString("O"),
			graphicsDevice = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType,
			plateauHeight = profile.plateauHeight, plateauRelief = profile.plateauRelief,
			minimumInteriorLift = float.PositiveInfinity,
			comparison = "Actual relief-shader float readback versus grid.SampleSurfaceHeight at identical positions. " +
				"Every plateau boundary is evaluated from both roots, including mountains, hills, flat land, water and carved rivers. " +
				"A dry interior patch must remain raised and broad; the lift baseline is a disposable profile clone with plateauHeight=0. " +
				"Ocean fallback, raised plateau river beds and unchanged lowland river beds are additional CPU contract checks." };
		foreach (Vector2Int coord in HexNearTerrainShowcase.PlateauStudyCells)
		{
			int index = coord.x + coord.y * grid.CellCountX;
			if (grid.CellData[index].landform != HexLandform.Plateau || grid.CellData[index].IsUnderwater)
				throw new InvalidOperationException("Unexpected plateau fixture contents at " + coord);
			report.plateauCells++;
			Add(grid, samples, index, grid.CellPositions[index]);
			HexCell cell = new(index, grid);
			for (HexDirection direction = HexDirection.NE; direction <= HexDirection.NW; direction++)
			{
				if (!cell.TryGetNeighbor(direction, out HexCell neighbor)) continue;
				HexCellData neighborData = grid.CellData[neighbor.Index];
				if (neighborData.landform == HexLandform.Plateau && neighbor.Index < index) continue;
				string category = neighborData.IsUnderwater ? "water" : neighborData.landform.ToString();
				if (!boundaries.TryGetValue(category, out BoundaryResult boundary))
					boundaries.Add(category, boundary = new BoundaryResult { neighbor = category });
				boundary.edges++;
				Vector3 a = grid.CellPositions[index], b = grid.CellPositions[neighbor.Index];
				Vector3 tangent = new Vector3(-(b - a).z, 0f, (b - a).x).normalized * HexMetrics.outerRadius;
				for (int offset = -4; offset <= 4; offset++)
				{
					Vector3 point = (a + b) * .5f + tangent * (offset * .115f);
					int first = samples.Count;
					Add(grid, samples, index, point);
					Add(grid, samples, neighbor.Index, point);
					seams.Add(new Seam { a = first, b = first + 1, category = category });
					boundary.samples += 2;
					if (grid.CellData[index].HasHFRiver || neighborData.HasHFRiver) report.riverSamples += 2;
				}
			}
		}
		Vector2Int interior = HexNearTerrainShowcase.PlateauInteriorCell;
		int interiorIndex = interior.x + interior.y * grid.CellCountX;
		int interiorStart = samples.Count;
		HexNearTerrainProfile baseline = Object.Instantiate(profile);
		baseline.hideFlags = HideFlags.HideAndDontSave;
		baseline.plateauHeight = 0f;
		float minimumCpu = float.PositiveInfinity, maximumCpu = float.NegativeInfinity;
		try
		{
			for (int z = -4; z <= 4; z++)
				for (int x = -4; x <= 4; x++)
				{
					Vector3 point = grid.CellPositions[interiorIndex] + new Vector3(x * .13f, 0f, z * .13f) * HexMetrics.outerRadius;
					Add(grid, samples, interiorIndex, point);
					Sample sample = samples[samples.Count - 1];
					float baseHeight = HexNearTerrainSurface.Evaluate(grid, baseline, interior.x, interior.y,
						sample.local, 0f, 1f, 0f, false);
					report.minimumInteriorLift = Mathf.Min(report.minimumInteriorLift, sample.cpu - baseHeight);
					minimumCpu = Mathf.Min(minimumCpu, sample.cpu); maximumCpu = Mathf.Max(maximumCpu, sample.cpu);
					report.interiorSamples++;
				}
		}
		finally { Object.DestroyImmediate(baseline); }
		report.interiorCpuHeightRange = maximumCpu - minimumCpu;
		report.sampleCount = samples.Count;
		ReadGpu(showcase, samples, report);
		float minimumGpu = float.PositiveInfinity, maximumGpu = float.NegativeInfinity;
		for (int i = 0; i < samples.Count; i++)
		{
			Sample sample = samples[i];
			report.maximumCpuGpuError = Mathf.Max(report.maximumCpuGpuError, Mathf.Abs(sample.cpu - sample.gpu));
			if (i >= interiorStart) { minimumGpu = Mathf.Min(minimumGpu, sample.gpu); maximumGpu = Mathf.Max(maximumGpu, sample.gpu); }
		}
		report.interiorGpuHeightRange = maximumGpu - minimumGpu;
		foreach (Seam seam in seams)
		{
			Sample a = samples[seam.a], b = samples[seam.b];
			BoundaryResult boundary = boundaries[seam.category];
			boundary.maximumCpuGap = Mathf.Max(boundary.maximumCpuGap, Mathf.Abs(a.cpu - b.cpu));
			boundary.maximumGpuGap = Mathf.Max(boundary.maximumGpuGap, Mathf.Abs(a.gpu - b.gpu));
			boundary.maximumCpuGpuError = Mathf.Max(boundary.maximumCpuGpuError,
				Mathf.Max(Mathf.Abs(a.cpu - a.gpu), Mathf.Abs(b.cpu - b.gpu)));
			report.maximumCpuGap = Mathf.Max(report.maximumCpuGap, boundary.maximumCpuGap);
			report.maximumGpuGap = Mathf.Max(report.maximumGpuGap, boundary.maximumGpuGap);
		}
		const float oldSeaHeight = 2.125f;
		float datum = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		report.oceanFallbackError = Mathf.Abs(HexNearTerrainSurface.Evaluate(grid, profile, interior.x, interior.y,
			Vector2.zero, .70f, 0f, oldSeaHeight, true) - oldSeaHeight);
		report.riverCarvingError = Mathf.Abs(HexNearTerrainSurface.Evaluate(grid, profile, interior.x, interior.y,
			Vector2.zero, 0f, 0f, oldSeaHeight, true) - (datum + profile.plateauHeight - .302f));
		HexNearTerrainProfile lowland = Object.Instantiate(profile);
		lowland.hideFlags = HideFlags.HideAndDontSave;
		lowland.plateauHeight = 0f;
		try
		{
			report.lowlandRiverCarvingError = Mathf.Abs(HexNearTerrainSurface.Evaluate(grid, lowland, interior.x, interior.y,
				Vector2.zero, 0f, 0f, oldSeaHeight, true) - (datum - .302f));
		}
		finally { Object.DestroyImmediate(lowland); }
		List<string> failures = new();
		if (report.invalidReadbacks != 0 || report.readbackCount != report.sampleCount) failures.Add("Incomplete or invalid GPU readback.");
		if (report.maximumCpuGpuError > report.heightTolerance) failures.Add("CPU/GPU plateau height disagreement.");
		if (report.maximumCpuGap > report.seamTolerance || report.maximumGpuGap > report.seamTolerance)
			failures.Add("Discontinuous plateau boundary.");
		if (profile.plateauHeight <= .01f || report.minimumInteriorLift < profile.plateauHeight * .75f)
			failures.Add("Plateau interior lacks sustained nonzero lift.");
		float maximumInteriorRange = Mathf.Max(.25f, profile.plateauRelief * 1.2f);
		if (report.interiorCpuHeightRange > maximumInteriorRange || report.interiorGpuHeightRange > maximumInteriorRange + report.heightTolerance)
			failures.Add("Plateau interior has a peak or excessive relief instead of a broad top.");
		foreach (string required in new[] { "Plateau", "Mountain", "Hill", "Flat", "water" })
			if (!boundaries.ContainsKey(required)) failures.Add("Missing boundary coverage: " + required);
		if (report.riverSamples == 0) failures.Add("Missing plateau river coverage.");
		if (report.oceanFallbackError > .0001f || report.riverCarvingError > .0001f || report.lowlandRiverCarvingError > .0001f)
			failures.Add("Ocean fallback, highland or lowland river-carving contract failed.");
		report.boundaries = new List<BoundaryResult>(boundaries.Values).ToArray();
		report.failures = failures.ToArray(); passed = report.passed = failures.Count == 0;
		summary = $"Plateau validation {(passed ? "passed" : "failed")}: {report.readbackCount}/{report.sampleCount} GPU samples, " +
			$"interior lift {report.minimumInteriorLift:F4}, top variation {report.interiorCpuHeightRange:F4}, " +
			$"height error {report.maximumCpuGpuError:F6}, boundary gaps {report.maximumCpuGap:F6}/{report.maximumGpuGap:F6}.";
		Directory.CreateDirectory(Path.GetDirectoryName(artifactPath));
		File.WriteAllText(artifactPath, JsonUtility.ToJson(report, true));
		return artifactPath;
	}

	static void Add(HexGrid grid, List<Sample> samples, int cell, Vector3 point)
	{
		Vector3 relative = point - grid.CellPositions[cell];
		samples.Add(new Sample { cell = cell, local = new Vector2(relative.x, relative.z) / HexMetrics.outerRadius,
			cpu = grid.SampleSurfaceHeight(cell, point, true) });
	}

	static void ReadGpu(HexNearTerrainShowcase showcase, List<Sample> samples, Report report)
	{
		if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
			!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
			throw new InvalidOperationException("Floating point height readback is unavailable on this GPU.");
		Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/HexMapPackage/Editor/Terrain/HexNearSurfaceValidation.shader");
		if (!shader || !shader.isSupported) throw new InvalidOperationException("Near surface validation shader is unavailable.");
		const int width = 128;
		int height = Mathf.CeilToInt(samples.Count / (float)width);
		Color[] input = new Color[width * height];
		for (int i = 0; i < input.Length; i++) input[i] = new Color(0, 0, 0, -1);
		for (int i = 0; i < samples.Count; i++) input[i] = new Color(samples[i].cell, samples[i].local.x, samples[i].local.y, i);
		Texture2D points = new(width, height, TextureFormat.RGBAFloat, false, true) {
			filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
		Material material = new(shader) { hideFlags = HideFlags.DontSave };
		RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
		RenderTexture previous = RenderTexture.active;
		bool previousSRGB = GL.sRGBWrite;
		Texture2D readback = null;
		try
		{
			HexReliefMesh relief = showcase.grid.GetComponentInChildren<HexReliefMesh>();
			if (relief) material.CopyPropertiesFromMaterial(relief.GetComponent<MeshRenderer>().sharedMaterial);
			showcase.terrainStyle.ApplyTo(null);
			points.SetPixels(input); points.Apply(false, false);
			material.SetTexture("_SamplePoints", points);
			GL.sRGBWrite = false;
			Graphics.Blit(points, target, material, 0);
			RenderTexture.active = target;
			readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
			readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); readback.Apply(false, false);
			bool[] seen = new bool[samples.Count];
			foreach (Color value in readback.GetPixels())
			{
				if (!Finite(value.r) || !Finite(value.g)) { report.invalidReadbacks++; continue; }
				int id = Mathf.RoundToInt(value.g); if (id < 0) continue;
				if (id >= samples.Count || seen[id] || Mathf.Abs(value.g - id) > .001f)
				{ report.invalidReadbacks++; continue; }
				seen[id] = true; report.readbackCount++; samples[id].gpu = value.r;
				if (!Finite(samples[id].cpu)) report.invalidReadbacks++;
			}
		}
		finally
		{
			GL.sRGBWrite = previousSRGB; RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(points); Object.DestroyImmediate(material);
			if (readback) Object.DestroyImmediate(readback);
		}
	}
	static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
