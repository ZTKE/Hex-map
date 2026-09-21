using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Persistence and editing contracts exercised on the existing disposable
/// showcase. Never opens, saves, or replaces a user map or scene.
/// </summary>
public static class HexMountainModeValidation
{
	[Serializable] public sealed class Report
	{
		public string timestamp, note;
		public bool passed, restored, v13RoundTrip, v12Compatibility, undoRedo;
		public int cellCount, version, automaticCells, rangeCells, massifCells;
		public int v13Bytes, v12Bytes, unitCount, cityCount;
		public int localCacheExpected, localCacheRefreshed, cacheScopeMismatches;
		public int textureMismatches, lowMaskMismatches, addedDirectedEdges;
		public string[] failures;
	}
	const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

	public static string Run(HexNearTerrainShowcase showcase, string artifactPath,
		out bool passed, out string summary)
	{
		if (!EditorApplication.isPlaying || !showcase || !showcase.Ready ||
			showcase.gameObject.scene.name != HexNearTerrainShowcase.SceneName)
			throw new InvalidOperationException("Mountain mode validation requires the ready disposable showcase.");
		HexGrid grid = showcase.grid;
		if (!grid || grid.CellData == null || grid.CellData.Length > 5000 ||
			((IList)Field(typeof(HexGrid), "units").GetValue(grid)).Count != 0 || grid.Cities.Count != 0)
			throw new InvalidOperationException("Use only a small disposable showcase with zero units and cities.");
		HexCellData[] original = (HexCellData[])grid.CellData.Clone();
		byte[] originalSave = Save(grid);
		bool immediate = grid.ShaderData.ImmediateMode;
		Vector4 highlighting = Shader.GetGlobalVector("_CellHighlighting");
		float editorGrid = Shader.GetGlobalFloat("_HexEditorShowGrid");
		bool editKeyword = Shader.IsKeywordEnabled("_HEX_MAP_EDIT_MODE");
		List<string> failures = new();
		Report report = new() { timestamp = DateTime.UtcNow.ToString("O"),
			cellCount = original.Length, version = DefaultWorldMapBootstrap.CurrentMapFileVersion,
			note = "Real HexGrid.Save/Load on the existing disposable showcase; no CreateMap, saved world, or scene writes. " +
				"Legacy v12 fixture parses the v13 record structure and removes only each final mode byte. " +
				"Cache scope is tested before any lazy getter; R8 is inspected after the real shader-data upload. " +
				"Undo/redo invokes the production editor history methods on a separate inactive editor component." };
		try
		{
			Require(report.version == 13, "The validation fixture describes map version 13.");
			// All three modes appear in both water and dry records, so accidental
			// conditional serialization cannot shift the unit/city count fields.
			for (int i = 0; i < grid.CellData.Length; i++)
			{
				HexCellData data = grid.CellData[i];
				data.mountainMode = (HexMountainMode)(i % 3);
				grid.CellData[i] = data;
			}
			byte[] current = Save(grid);
			report.v13Bytes = current.Length;
			byte[] legacy = StripModeBytes(current, report);
			report.v12Bytes = legacy.Length;
			for (int i = 0; i < grid.CellData.Length; i++)
			{
				HexCellData data = grid.CellData[i]; data.mountainMode = HexMountainMode.Automatic;
				grid.CellData[i] = data;
			}
			Load(grid, current);
			report.v13RoundTrip = Same(current, Save(grid));
			for (int i = 0; i < grid.CellData.Length; i++)
			{
				HexMountainMode mode = grid.CellData[i].mountainMode;
				report.v13RoundTrip &= (int)mode == i % 3;
				if (mode == HexMountainMode.Automatic) report.automaticCells++;
				else if (mode == HexMountainMode.Range) report.rangeCells++;
				else if (mode == HexMountainMode.Massif) report.massifCells++;
			}
			Check(report.v13RoundTrip, "Version 13 did not round-trip all map bytes and all three mountain modes.", failures);
			Load(grid, legacy);
			report.v12Compatibility = true;
			foreach (HexCellData data in grid.CellData)
				report.v12Compatibility &= data.mountainMode == HexMountainMode.Automatic;
			report.v12Compatibility &= Same(legacy, StripModeBytes(Save(grid), null));
			Check(report.v12Compatibility, "Version 12 did not default every mode to Automatic while preserving all legacy bytes.", failures);
			CheckLocalCacheAndTexture(grid, report, failures);
			CheckHistory(grid, report, failures);
		}
		catch (Exception exception)
		{
			failures.Add(exception.GetBaseException().ToString());
		}
		finally
		{
			// Restore the raw snapshot, including unsaved derived fields, rather
			// than normalizing the user's fixture through another deserialize.
			Array.Copy(original, grid.CellData, original.Length);
			grid.ShaderData.ImmediateMode = immediate;
			grid.RefreshAllCells(); grid.RefreshAllChunks();
			Invoke(grid.ShaderData, "LateUpdate");
			Shader.SetGlobalVector("_CellHighlighting", highlighting);
			Shader.SetGlobalFloat("_HexEditorShowGrid", editorGrid);
			if (editKeyword) Shader.EnableKeyword("_HEX_MAP_EDIT_MODE");
			else Shader.DisableKeyword("_HEX_MAP_EDIT_MODE");
			report.restored = Same(originalSave, Save(grid));
			for (int i = 0; i < original.Length; i++) report.restored &= original[i].Equals(grid.CellData[i]);
			Check(report.restored, "The disposable showcase did not restore its exact original cells/save bytes.", failures);
		}
		report.failures = failures.ToArray();
		passed = report.passed = failures.Count == 0;
		summary = $"Mountain modes: v13={report.v13RoundTrip}, v12={report.v12Compatibility}, " +
			$"local cache={report.localCacheRefreshed}/{report.localCacheExpected}, R8 mismatches={report.textureMismatches}, " +
			$"undo/redo={report.undoRedo}, restored={report.restored}.";
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(artifactPath)));
		File.WriteAllText(artifactPath, JsonUtility.ToJson(report, true));
		return artifactPath;
	}

	static void CheckLocalCacheAndTexture(HexGrid grid, Report report, List<string> failures)
	{
		// The isolated three-cell fixture has one sparse edge. Paint one of its
		// endpoints as Range, and require both endpoints' cached masks to change.
		int[] triangle = { 16 + 18 * grid.CellCountX, 17 + 18 * grid.CellCountX, 16 + 19 * grid.CellCountX };
		int edited = -1;
		foreach (int index in triangle)
		{
			Require(grid.CellData[index].landform == HexLandform.Mountain && !grid.CellData[index].IsUnderwater,
				"The isolated three-cell showcase fixture changed.");
			int mask = HexMountainRidgeGraph.ComputeMask(grid, index % grid.CellCountX, index / grid.CellCountX);
			HexCell cell = grid.GetCell(index);
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
				if (cell.TryGetNeighbor(d, out HexCell other) && Array.IndexOf(triangle, other.Index) >= 0 &&
					(mask & (1 << (int)d)) == 0) edited = index;
		}
		Require(edited >= 0, "The Automatic triangle fixture must contain a removable sparse edge.");
		int[] before = new int[grid.CellData.Length];
		for (int i = 0; i < before.Length; i++) before[i] = grid.ShaderData.GetMountainRidgeMask(i);
		bool[] valid = (bool[])Field(typeof(HexCellShaderData), "mountainRidgeValid").GetValue(grid.ShaderData);
		Array.Clear(valid, 0, valid.Length);
		HashSet<int> expected = new() { edited };
		List<int> frontier = new() { edited };
		for (int ring = 0; ring < 2; ring++)
		{
			List<int> next = new();
			foreach (int index in frontier)
				for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
					if (grid.TryGetCellIndex(grid.CellData[index].coordinates.Step(d), out int other) && expected.Add(other)) next.Add(other);
			frontier = next;
		}
		grid.GetCell(edited).SetMountainMode(HexMountainMode.Range);
		report.localCacheExpected = expected.Count;
		for (int i = 0; i < valid.Length; i++)
		{
			if (valid[i]) report.localCacheRefreshed++;
			if (valid[i] != expected.Contains(i)) report.cacheScopeMismatches++;
		}
		Check(report.cacheScopeMismatches == 0 && expected.Count == 19,
			"SetMountainMode did not refresh exactly the two-ring logical shader cache.", failures);
		for (int i = 0; i < before.Length; i++)
		{
			int fresh = HexMountainRidgeGraph.ComputeMask(grid, i % grid.CellCountX, i / grid.CellCountX);
			int cached = grid.ShaderData.GetMountainRidgeMask(i);
			if (fresh != cached || (cached & ~63) != 0) report.lowMaskMismatches++;
			for (int d = 0; d < 6; d++) if ((fresh & (1 << d)) != 0 && (before[i] & (1 << d)) == 0) report.addedDirectedEdges++;
		}
		Check(report.lowMaskMismatches == 0 && report.addedDirectedEdges >= 2,
			"Range painting failed to retain the removed connection in both directions or leaked mode bits into the CPU graph.", failures);
		grid.GetCell(triangle[0] == edited ? triangle[1] : triangle[0]).SetMountainMode(HexMountainMode.Massif);
		Invoke(grid.ShaderData, "LateUpdate");
		Texture2D texture = (Texture2D)Field(typeof(HexCellShaderData), "mountainRidgeTexture").GetValue(grid.ShaderData);
		Require(texture && texture.format == TextureFormat.R8 && texture.width == grid.CellCountX && texture.height == grid.CellCountZ,
			"Mountain mode upload is not an R8 texture matching the live grid.");
		var bytes = texture.GetRawTextureData<byte>();
		Require(bytes.Length == grid.CellData.Length, "Unexpected R8 byte length.");
		for (int i = 0; i < bytes.Length; i++)
		{
			int expectedByte = HexMountainRidgeGraph.ComputeMask(grid, i % grid.CellCountX, i / grid.CellCountX) |
				((int)grid.CellData[i].mountainMode << 6);
			if (bytes[i] != expectedByte) report.textureMismatches++;
		}
		Check(report.textureMismatches == 0, "Actual R8 upload differs from the six-bit graph plus two-bit mountain mode.", failures);
	}

	static void CheckHistory(HexGrid grid, Report report, List<string> failures)
	{
		GameObject owner = new("Disposable mountain-mode history validation") { hideFlags = HideFlags.HideAndDontSave };
		owner.SetActive(false);
		try
		{
			// Awake/OnEnable never run on this inactive object; there is no UI,
			// map reset subscription, user editor, or shared undo stack involved.
			HexMapEditor editor = owner.AddComponent<HexMapEditor>();
			Field(typeof(HexMapEditor), "hexGrid").SetValue(editor, grid);
			int index = 18 + 23 * grid.CellCountX;
			grid.GetCell(index).SetMountainMode(HexMountainMode.Automatic);
			HexCellData[] before = (HexCellData[])grid.CellData.Clone();
			Invoke(editor, "BeginHistoryStroke");
			grid.GetCell(index).SetMountainMode(HexMountainMode.Range);
			Invoke(editor, "EndHistoryStroke");
			bool committed = (bool)Invoke(editor, "CanUndoEdit");
			Invoke(editor, "UndoEdit");
			bool undone = SameCells(before, grid.CellData);
			Invoke(editor, "RedoEdit");
			bool redone = grid.CellData[index].mountainMode == HexMountainMode.Range;
			Invoke(editor, "BeginHistoryStroke");
			grid.GetCell(index).SetMountainMode(HexMountainMode.Massif);
			Invoke(editor, "EndHistoryStroke");
			Invoke(editor, "UndoEdit");
			undone &= grid.CellData[index].mountainMode == HexMountainMode.Range;
			Invoke(editor, "RedoEdit");
			redone &= grid.CellData[index].mountainMode == HexMountainMode.Massif;
			report.undoRedo = committed && undone && redone;
			Check(report.undoRedo, "A mode-only brush stroke did not commit, undo and redo through the actual editor history.", failures);
		}
		finally { Object.DestroyImmediate(owner); }
	}

	static byte[] Save(HexGrid grid)
	{
		using MemoryStream stream = new();
		using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, true))
		{ writer.Write(DefaultWorldMapBootstrap.CurrentMapFileVersion); grid.Save(writer); }
		return stream.ToArray();
	}
	static void Load(HexGrid grid, byte[] bytes)
	{
		using MemoryStream stream = new(bytes, false);
		using BinaryReader reader = new(stream);
		int version = reader.ReadInt32(); grid.Load(reader, version);
		Require(stream.Position == stream.Length, "Map load did not consume exactly the cell, zero-unit and zero-city payload.");
	}
	static byte[] StripModeBytes(byte[] current, Report report)
	{
		using MemoryStream source = new(current, false);
		using BinaryReader reader = new(source);
		using MemoryStream target = new();
		using BinaryWriter writer = new(target);
		Require(reader.ReadInt32() == 13, "Expected a genuine version 13 map stream.");
		writer.Write(12); // Intentional old-format fixture, never written to a map file.
		int x = reader.ReadInt32(), z = reader.ReadInt32();
		reader.ReadBoolean(); reader.ReadBoolean();
		int palette = reader.ReadUInt16(); source.Seek(palette * 4L, SeekOrigin.Current);
		writer.Write(current, 4, checked((int)source.Position - 4));
		for (int i = 0; i < x * z; i++)
		{
			int start = checked((int)source.Position);
			HexValues.Load(reader, 13); HexFlags.Empty.Load(reader, 13);
			reader.ReadByte(); // landform
			reader.ReadByte(); reader.ReadByte(); // vegetation, density
			reader.ReadByte(); reader.ReadByte(); // tint, rotation
			reader.ReadByte(); reader.ReadUInt16(); // HF river edges, country
			int length = checked((int)source.Position - start);
			Require(reader.ReadByte() <= 2, "Invalid persisted mountain mode.");
			writer.Write(current, start, length);
		}
		int units = reader.ReadInt32(), cities = reader.ReadInt32();
		Require(units == 0 && cities == 0 && source.Position == source.Length,
			"Fixture is not terminated by exact zero unit and city counts.");
		writer.Write(units); writer.Write(cities);
		if (report != null) { report.unitCount = units; report.cityCount = cities; }
		writer.Flush(); return target.ToArray();
	}
	static bool Same(byte[] a, byte[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
		return true;
	}
	static bool SameCells(HexCellData[] a, HexCellData[] b)
	{
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
		return true;
	}
	static FieldInfo Field(Type type, string name) => type.GetField(name, PrivateInstance) ??
		throw new MissingFieldException(type.FullName, name);
	static object Invoke(object owner, string name) => (owner.GetType().GetMethod(name, PrivateInstance) ??
		throw new MissingMethodException(owner.GetType().FullName, name)).Invoke(owner, null);
	static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
	static void Check(bool condition, string message, List<string> failures) { if (!condition) failures.Add(message); }
}
