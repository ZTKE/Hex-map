using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Read-only pixel river reconstruction checks on the loaded gameplay world.</summary>
public static class HexRiverContinuityValidation
{
	[Serializable] public sealed class Report
	{
		public string timestamp, graphicsDevice, comparison;
		public bool passed;
		public int selectedEdges, mouthSegments, sampleCount, readbackCount, invalidReadbacks;
		public float maximumCenterlineDistance, maximumOwnerDisagreement, minimumCoreCoverage = 1f;
		public float minimumMouthWaterCoverage = 1f, worstMouthT;
		public int worstMouthCell = -1, worstMouthCode = -1, uncoveredMouthSamples;
		public string[] failures;
	}
	public static string Run(HexGrid grid, string path, out bool passed, out string summary)
	{
		if (!EditorApplication.isPlaying || !grid || grid.CellData == null || grid.CellData.Length < 10000)
			throw new InvalidOperationException("River continuity validation requires the initialized gameplay world.");
		var samples = new List<Color>();
		var report = new Report { timestamp = DateTime.UtcNow.ToString("O"),
			graphicsDevice = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType,
			comparison = "Actual fragment river-coordinate helper sampled on curved saved edges and every coastal mouth, from both bank cells. " +
				"Includes every saved shared river edge and every coastal mouth in the loaded world. Checks pixel reconstruction and owner agreement; screenshots separately assess final coastal colour." };
		for (int i = 0; i < grid.CellData.Length; i++)
		{
			if (grid.CellData[i].IsUnderwater || !grid.CellData[i].HasHFRiver) continue;
			HexCell cell = new(i, grid);
			for (int d = 0; d < 6; d++)
			{
				if (!grid.CellData[i].HasRiverThroughEdge((HexDirection)d) ||
					!cell.TryGetNeighbor((HexDirection)d, out HexCell neighbor) || neighbor.Index <= i) continue;
				Add(samples, i, d); report.selectedEdges++;
				for (int endpoint = 0; endpoint < 2; endpoint++)
				{
					int thirdDirection = (d + (endpoint == 0 ? 5 : 1)) % 6;
					if (!cell.TryGetNeighbor((HexDirection)thirdDirection, out HexCell third) || !grid.CellData[third.Index].IsUnderwater) continue;
					Add(samples, i, d + 6 * (endpoint + 1)); report.mouthSegments++;
				}
			}
		}
		report.sampleCount = samples.Count;
		ReadGpu(grid, samples, report);
		var failures = new List<string>();
		if (report.sampleCount == 0 || report.mouthSegments == 0) failures.Add("No saved river or mouth fixtures.");
		if (report.invalidReadbacks != 0 || report.readbackCount != report.sampleCount) failures.Add("Incomplete or invalid GPU readback.");
		if (report.maximumCenterlineDistance > .012f) failures.Add("A curved river or mouth is missing its pixel centreline.");
		if (report.maximumOwnerDisagreement > .004f) failures.Add("Bank owners disagree about the shared pixel river.");
		if (report.minimumCoreCoverage < .99f) failures.Add("Pixel river core breaks along a centreline.");
		if (report.uncoveredMouthSamples > 0) failures.Add("Terrain river tint and the sea layer leave uncovered mouth samples.");
		report.failures = failures.ToArray(); passed = report.passed = failures.Count == 0;
		summary = $"River pixel continuity {(passed ? "passed" : "FAILED")}: {report.sampleCount:N0} probes, {report.selectedEdges:N0} curved edges, {report.mouthSegments:N0} coastal mouths; max distance {report.maximumCenterlineDistance:F6}, bank gap {report.maximumOwnerDisagreement:F6}.";
		Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(report, true));
		return path;
	}
	static void Add(List<Color> samples, int cell, int code)
	{
		for (int k = 0; k <= 10; k++) samples.Add(new Color(cell, code, k / 10f, samples.Count));
	}
	static void ReadGpu(HexGrid grid, List<Color> samples, Report report)
	{
		Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/HexMapPackage/Editor/Terrain/HexRiverContinuityValidation.shader");
		if (!shader || !shader.isSupported) throw new InvalidOperationException("River continuity shader is unavailable or unsupported.");
		const int width = 128; int height = Mathf.CeilToInt(samples.Count / (float)width);
		Color[] pixels = new Color[width * height];
		for (int i = 0; i < pixels.Length; i++) pixels[i] = i < samples.Count ? samples[i] : new Color(0, 0, 0, -1);
		Texture2D points = new(width, height, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
		Material material = new(shader) { hideFlags = HideFlags.DontSave };
		RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
		RenderTexture previous = RenderTexture.active; bool previousSRGB = GL.sRGBWrite; Texture2D readback = null;
		try
		{
			HexReliefMesh relief = grid.GetComponentInChildren<HexReliefMesh>();
			if (relief) material.CopyPropertiesFromMaterial(relief.GetComponent<MeshRenderer>().sharedMaterial);
			points.SetPixels(pixels); points.Apply(false, false); material.SetTexture("_SamplePoints", points);
			GL.sRGBWrite = false; Graphics.Blit(points, target, material, 0); RenderTexture.active = target;
			readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
			readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); readback.Apply(false, false);
			bool[] seen = new bool[samples.Count];
			foreach (Color p in readback.GetPixels())
			{
				if (!Finite(p.r) || !Finite(p.g) || !Finite(p.b) || !Finite(p.a)) { report.invalidReadbacks++; continue; }
				int id = Mathf.RoundToInt(p.g); if (id < 0) continue;
				if (id >= samples.Count || seen[id] || Mathf.Abs(p.g - id) > .001f) { report.invalidReadbacks++; continue; }
				seen[id] = true; report.readbackCount++;
				report.maximumCenterlineDistance = Mathf.Max(report.maximumCenterlineDistance, p.r);
				report.maximumOwnerDisagreement = Mathf.Max(report.maximumOwnerDisagreement, p.b);
				if (samples[id].g < 6) report.minimumCoreCoverage = Mathf.Min(report.minimumCoreCoverage, p.a);
				else
				{
					if (p.a < .1f) report.uncoveredMouthSamples++;
					if (p.a < report.minimumMouthWaterCoverage)
					{
						report.minimumMouthWaterCoverage = p.a; report.worstMouthCell = Mathf.RoundToInt(samples[id].r);
						report.worstMouthCode = Mathf.RoundToInt(samples[id].g); report.worstMouthT = samples[id].b;
					}
				}
			}
		}
		finally
		{
			GL.sRGBWrite = previousSRGB; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
			Object.DestroyImmediate(points); Object.DestroyImmediate(material); if (readback) Object.DestroyImmediate(readback);
		}
	}
	static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
