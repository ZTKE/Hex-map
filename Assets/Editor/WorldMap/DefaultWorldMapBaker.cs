using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class DefaultWorldMapBaker
{
	const string SourceAssetPath =
		"Assets/MapData/WW2Migration/CurrentCountryBlocks_R5_4096x2048.png";
	const string SphereCountryDataAssetPath =
		"Assets/MapData/WW2Migration/vert_buf_data_5.bytes";
	const string OutputAssetPath = "Assets/Resources/Maps/DefaultWorld.bytes";
	const string PendingRequestPath = "Temp/DefaultWorldMapBake.request";
	const string PendingSessionKey = "DefaultWorldMapBaker.Pending";
	const string StartedPlaySessionKey = "DefaultWorldMapBaker.StartedPlay";
	const string SilentSessionKey = "DefaultWorldMapBaker.Silent";

	const int TargetWidth = 1100;
	const int TargetHeight = 469;
	const int WorldSeed = 1936;
	// Remove Antarctica at roughly 63°S and place the northern edge just above
	// Iceland at roughly 70°N. TargetHeight preserves the original vertical
	// sampling density so the surviving geography is cropped rather than stretched.
	const float SourceVMin = 0.15f;
	const float SourceVMax = 0.8888889f;

	static readonly Color32 OceanColor = new(0, 27, 82, 255);

	static DefaultWorldMapBaker()
	{
		EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
		EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
		EditorApplication.delayCall += ProcessPendingRequest;
	}

	[MenuItem("Tools/World Map/Bake Default Flat World")]
	public static void BeginBake()
	{
		if (EditorApplication.isCompiling || EditorApplication.isUpdating)
		{
			UnityEngine.Debug.LogWarning(
				"Wait for Unity to finish compiling/importing before baking the world map.");
			return;
		}

		SessionState.SetBool(PendingSessionKey, true);
		if (EditorApplication.isPlaying)
		{
			EditorApplication.delayCall += BakeInPlayMode;
			return;
		}

		SessionState.SetBool(StartedPlaySessionKey, true);
		EditorApplication.EnterPlaymode();
	}

	static void ProcessPendingRequest()
	{
		string requestPath = Path.Combine(ProjectRoot, PendingRequestPath);
		if (!File.Exists(requestPath))
		{
			return;
		}

		File.Delete(requestPath);
		SessionState.SetBool(SilentSessionKey, true);
		BeginBake();
	}

	static void OnPlayModeStateChanged(PlayModeStateChange state)
	{
		if (state == PlayModeStateChange.EnteredPlayMode &&
			SessionState.GetBool(PendingSessionKey, false))
		{
			EditorApplication.delayCall += BakeInPlayMode;
		}
	}

	static void BakeInPlayMode()
	{
		if (!EditorApplication.isPlaying ||
			!SessionState.GetBool(PendingSessionKey, false))
		{
			return;
		}
		SessionState.SetBool(PendingSessionKey, false);

		bool silent = SessionState.GetBool(SilentSessionKey, false);
		SessionState.SetBool(SilentSessionKey, false);
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			HexGrid grid = UnityEngine.Object.FindObjectOfType<HexGrid>();
			HexMapGenerator generator =
				UnityEngine.Object.FindObjectOfType<HexMapGenerator>();
			if (!grid || !generator)
			{
				throw new InvalidOperationException(
					"The loaded scene must contain HexGrid and HexMapGenerator.");
			}

			LoadSourcePixels(out Color32[] pixels, out int width, out int height);
			if (!generator.GenerateMapFromLandMask(
				pixels, width, height,
				TargetWidth, TargetHeight, true, WorldSeed, OceanColor,
				SourceVMin, SourceVMax))
			{
				throw new InvalidOperationException("Flat world generation failed.");
			}
			int ownedCellCount = AssignPoliticalData(
				grid, pixels, width, height, out int countryCount);

			string outputPath = Path.Combine(ProjectRoot, OutputAssetPath);
			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			using (BinaryWriter writer = new(
				File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None)))
			{
				writer.Write(DefaultWorldMapBootstrap.CurrentMapFileVersion);
				grid.Save(writer);
			}
			AssetDatabase.ImportAsset(
				OutputAssetPath, ImportAssetOptions.ForceSynchronousImport);
			stopwatch.Stop();
			int landCount = 0;
			for (int i = 0; i < grid.CellData.Length; i++)
			{
				if (!grid.CellData[i].IsUnderwater)
				{
					landCount += 1;
				}
			}
			long outputBytes = new FileInfo(outputPath).Length;
			string message =
				$"Default flat world baked: {TargetWidth}x{TargetHeight} " +
				$"({grid.CellData.Length:N0} cells), land={landCount:N0}, " +
				$"water={grid.CellData.Length - landCount:N0}, " +
				$"latitude crop={SourceVMin:P0}-{SourceVMax:P0}, " +
				$"file={outputBytes / (1024f * 1024f):F1} MiB, " +
				$"countries={countryCount:N0}, ownedCells={ownedCellCount:N0}, " +
				$"time={stopwatch.Elapsed.TotalSeconds:F1}s.\n{outputPath}";
			UnityEngine.Debug.Log(message);
			if (!silent)
			{
				EditorUtility.DisplayDialog("Default World Map", message, "OK");
			}
		}
		catch (Exception exception)
		{
			UnityEngine.Debug.LogException(exception);
			if (!silent)
			{
				EditorUtility.DisplayDialog(
					"Default World Map Bake Failed", exception.Message, "OK");
			}
		}
		finally
		{
			EditorUtility.ClearProgressBar();
			if (SessionState.GetBool(StartedPlaySessionKey, false))
			{
				SessionState.SetBool(StartedPlaySessionKey, false);
				EditorApplication.delayCall += EditorApplication.ExitPlaymode;
			}
		}
	}

	static int AssignPoliticalData(
		HexGrid grid, Color32[] sourcePixels, int sourceWidth, int sourceHeight,
		out int countryCount)
	{
		LoadSphereCountryPalette(
			out Color32[] palette, out Dictionary<int, ushort> colorToId);
		grid.SetCountryPalette(palette);
		countryCount = 0;
		for (int i = 1; i < palette.Length; i++)
		{
			if (palette[i].a != 0)
			{
				countryCount += 1;
			}
		}

		float du = 0.32f / TargetWidth;
		float sourceVRange = SourceVMax - SourceVMin;
		float dv = 0.32f / TargetHeight * sourceVRange;
		int ownedCellCount = 0;
		int unresolvedCellCount = 0;
		for (int row = 0, index = 0; row < TargetHeight; row++)
		{
			float v = Mathf.Lerp(
				SourceVMin, SourceVMax, (row + 0.5f) / TargetHeight);
			float rowOffset = (row & 1) == 0 ? 0f : 0.5f;
			for (int column = 0; column < TargetWidth; column++, index++)
			{
				HexCellData cell = grid.CellData[index];
				if (cell.IsUnderwater)
				{
					cell.countryId = 0;
					grid.CellData[index] = cell;
					continue;
				}

				float u = Mathf.Repeat(
					(column + rowOffset + 0.5f) / TargetWidth, 1f);
				Color32 center = SampleSource(
					sourcePixels, sourceWidth, sourceHeight, u, v);
				Color32 selected = center;
				if (IsOcean(selected))
				{
					Color32 a = SampleSource(
						sourcePixels, sourceWidth, sourceHeight, u - du, v - dv);
					Color32 b = SampleSource(
						sourcePixels, sourceWidth, sourceHeight, u + du, v - dv);
					Color32 c = SampleSource(
						sourcePixels, sourceWidth, sourceHeight, u - du, v + dv);
					Color32 d = SampleSource(
						sourcePixels, sourceWidth, sourceHeight, u + du, v + dv);
					selected = ChooseDominantLandColor(a, b, c, d);
				}

				if (colorToId.TryGetValue(ColorKey(selected), out ushort countryId) &&
					countryId != 0)
				{
					cell.countryId = countryId;
					ownedCellCount += 1;
				}
				else
				{
					cell.countryId = 0;
					unresolvedCellCount += 1;
				}
				grid.CellData[index] = cell;
			}
		}

		if (unresolvedCellCount > 0)
		{
			throw new InvalidDataException(
				$"{unresolvedCellCount:N0} land cells use colors that cannot be " +
				"mapped to a country ID in vert_buf_data_5.bytes.");
		}
		return ownedCellCount;
	}

	static void LoadSphereCountryPalette(
		out Color32[] palette, out Dictionary<int, ushort> colorToId)
	{
		string dataPath = Path.Combine(ProjectRoot, SphereCountryDataAssetPath);
		if (!File.Exists(dataPath))
		{
			throw new FileNotFoundException(
				"The sphere country buffer is missing.", dataPath);
		}
		FileInfo info = new(dataPath);
		if (info.Length == 0 || info.Length % 16 != 0)
		{
			throw new InvalidDataException(
				"The sphere country buffer must contain four floats per tile.");
		}

		Dictionary<ushort, Color32> firstColorById = new();
		colorToId = new Dictionary<int, ushort>();
		ushort maximumId = 0;
		using BinaryReader reader = new(File.OpenRead(dataPath));
		while (reader.BaseStream.Position < reader.BaseStream.Length)
		{
			float r = reader.ReadSingle();
			float g = reader.ReadSingle();
			float b = reader.ReadSingle();
			int rawId = Mathf.RoundToInt(reader.ReadSingle());
			if (rawId < 0 || rawId > ushort.MaxValue)
			{
				throw new InvalidDataException($"Country ID {rawId} is out of range.");
			}
			ushort id = (ushort)rawId;
			if (firstColorById.ContainsKey(id))
			{
				continue;
			}

			Color32 color = (Color32)new Color(r, g, b, 1f);
			firstColorById.Add(id, color);
			int key = ColorKey(color);
			if (colorToId.TryGetValue(key, out ushort existingId) &&
				existingId != id)
			{
				throw new InvalidDataException(
					$"Country IDs {existingId} and {id} share source color #{key:X6}.");
			}
			colorToId[key] = id;
			maximumId = Math.Max(maximumId, id);
		}

		palette = new Color32[maximumId + 1];
		foreach (KeyValuePair<ushort, Color32> pair in firstColorById)
		{
			Color32 color = pair.Value;
			color.a = 255;
			palette[pair.Key] = color;
		}
	}

	static int ColorKey(Color32 color) =>
		(color.r << 16) | (color.g << 8) | color.b;

	static Color32 SampleSource(
		Color32[] pixels, int width, int height, float u, float v)
	{
		int x = Mathf.Clamp(
			Mathf.FloorToInt(Mathf.Repeat(u, 1f) * width), 0, width - 1);
		int y = Mathf.Clamp(
			Mathf.FloorToInt(Mathf.Clamp01(v) * height), 0, height - 1);
		return pixels[x + y * width];
	}

	static Color32 ChooseDominantLandColor(
		Color32 a, Color32 b, Color32 c, Color32 d)
	{
		Color32 selected = !IsOcean(a) ? a : !IsOcean(b) ? b :
			!IsOcean(c) ? c : d;
		int bestCount = -1;
		EvaluateCandidate(a);
		EvaluateCandidate(b);
		EvaluateCandidate(c);
		EvaluateCandidate(d);
		return selected;

		void EvaluateCandidate(Color32 candidate)
		{
			if (IsOcean(candidate))
			{
				return;
			}
			int count = (SameCountry(candidate, a) ? 1 : 0) +
				(SameCountry(candidate, b) ? 1 : 0) +
				(SameCountry(candidate, c) ? 1 : 0) +
				(SameCountry(candidate, d) ? 1 : 0);
			if (count > bestCount)
			{
				bestCount = count;
				selected = candidate;
			}
		}
	}

	static bool SameCountry(Color32 a, Color32 b) =>
		!IsOcean(b) && a.r == b.r && a.g == b.g && a.b == b.b;

	static bool IsOcean(Color32 color) =>
		color.r == OceanColor.r && color.g == OceanColor.g &&
		color.b == OceanColor.b;

	static void LoadSourcePixels(
		out Color32[] pixels, out int width, out int height)
	{
		string sourcePath = Path.Combine(ProjectRoot, SourceAssetPath);
		if (!File.Exists(sourcePath))
		{
			throw new FileNotFoundException(
				"The exported sphere country map is missing.", sourcePath);
		}

		Texture2D texture = new(2, 2, TextureFormat.RGBA32, false, false);
		try
		{
			if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(sourcePath), false))
			{
				throw new InvalidDataException("Unity could not decode the source PNG.");
			}
			width = texture.width;
			height = texture.height;
			pixels = texture.GetPixels32();
		}
		finally
		{
			UnityEngine.Object.DestroyImmediate(texture);
		}
	}

	static string ProjectRoot =>
		Directory.GetParent(Application.dataPath).FullName;
}
