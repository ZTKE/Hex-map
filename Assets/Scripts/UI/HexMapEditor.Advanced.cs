using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Professional editing operations shared by every HF surface brush.
/// </summary>
public partial class HexMapEditor
{
	enum PaintOperation
	{
		Brush,
		Fill,
		Replace
	}

	const int historyCapacity = 32;

	readonly List<HexCellData[]> undoHistory = new();
	readonly List<HexCellData[]> redoHistory = new();

	PaintOperation paintOperation;
	HexCellData[] historyStrokeBefore;
	bool historyStrokeActive;

	void HandleMapReset()
	{
		ClearEditorHistory();
		ResetPathStroke();
		previousCellIndex = -1;
		hoveredCellIndex = -1;
		ClearCellHighlightData();
	}

	void ClearEditorHistory()
	{
		historyStrokeBefore = null;
		historyStrokeActive = false;
		undoHistory.Clear();
		redoHistory.Clear();
	}

	void ApplyPaintOperation(HexCell startCell)
	{
		if (paintOperation == PaintOperation.Fill)
		{
			FloodFill(startCell);
		}
		else if (paintOperation == PaintOperation.Replace)
		{
			ReplaceMatching(startCell);
		}
	}

	void FloodFill(HexCell startCell)
	{
		HexCellData source = hexGrid.CellData[startCell.Index];
		bool[] visited = new bool[hexGrid.CellData.Length];
		Queue<int> pending = new();
		pending.Enqueue(startCell.Index);
		visited[startCell.Index] = true;

		while (pending.Count > 0)
		{
			int index = pending.Dequeue();
			HexCellData candidate = hexGrid.CellData[index];
			if (!MatchesActiveProperty(candidate, source))
			{
				continue;
			}

			EditCell(hexGrid.GetCell(index));
			HexCoordinates coordinates = candidate.coordinates;
			for (HexDirection direction = HexDirection.NE;
				direction <= HexDirection.NW; direction++)
			{
				if (hexGrid.TryGetCellIndex(
					coordinates.Step(direction), out int neighborIndex) &&
					!visited[neighborIndex])
				{
					visited[neighborIndex] = true;
					pending.Enqueue(neighborIndex);
				}
			}
		}
	}

	void ReplaceMatching(HexCell startCell)
	{
		HexCellData source = hexGrid.CellData[startCell.Index];
		for (int i = 0; i < hexGrid.CellData.Length; i++)
		{
			if (MatchesActiveProperty(hexGrid.CellData[i], source))
			{
				EditCell(hexGrid.GetCell(i));
			}
		}
	}

	bool MatchesActiveProperty(HexCellData candidate, HexCellData source) =>
		activeTool switch
		{
			EditorTool.Terrain =>
				candidate.TerrainTypeIndex == source.TerrainTypeIndex,
			EditorTool.Relief => candidate.landform == source.landform,
			EditorTool.Forest =>
				candidate.vegetation == source.vegetation &&
				candidate.vegetationTint == source.vegetationTint &&
				candidate.VegetationDensity == source.VegetationDensity,
			EditorTool.Water => candidate.IsUnderwater == source.IsUnderwater,
			_ => false
		};

	void BeginHistoryStroke()
	{
		if (historyStrokeActive || !hexGrid || hexGrid.CellData == null)
		{
			return;
		}
		historyStrokeBefore = CaptureMapSnapshot();
		historyStrokeActive = true;
	}

	void EndHistoryStroke()
	{
		if (!historyStrokeActive)
		{
			return;
		}

		historyStrokeActive = false;
		if (hexGrid && hexGrid.CellData != null &&
			historyStrokeBefore != null &&
			!SnapshotsEqual(historyStrokeBefore, hexGrid.CellData))
		{
			PushSnapshot(undoHistory, historyStrokeBefore);
			redoHistory.Clear();
		}
		historyStrokeBefore = null;
	}

	void HandleHistoryShortcuts()
	{
		bool control = Input.GetKey(KeyCode.LeftControl) ||
			Input.GetKey(KeyCode.RightControl);
		if (!control)
		{
			return;
		}

		if (Input.GetKeyDown(KeyCode.Z))
		{
			if (Input.GetKey(KeyCode.LeftShift) ||
				Input.GetKey(KeyCode.RightShift))
			{
				RedoEdit();
			}
			else
			{
				UndoEdit();
			}
		}
		else if (Input.GetKeyDown(KeyCode.Y))
		{
			RedoEdit();
		}
	}

	bool CanUndoEdit() => IsHistoryEntryCompatible(undoHistory);

	bool CanRedoEdit() => IsHistoryEntryCompatible(redoHistory);

	void UndoEdit()
	{
		EndHistoryStroke();
		if (!CanUndoEdit())
		{
			ClearIncompatibleHistory();
			return;
		}

		HexCellData[] target = TakeLast(undoHistory);
		PushSnapshot(redoHistory, CaptureMapSnapshot());
		RestoreMapSnapshot(target);
	}

	void RedoEdit()
	{
		EndHistoryStroke();
		if (!CanRedoEdit())
		{
			ClearIncompatibleHistory();
			return;
		}

		HexCellData[] target = TakeLast(redoHistory);
		PushSnapshot(undoHistory, CaptureMapSnapshot());
		RestoreMapSnapshot(target);
	}

	bool IsHistoryEntryCompatible(List<HexCellData[]> history) =>
		history.Count > 0 && hexGrid && hexGrid.CellData != null &&
		history[history.Count - 1].Length == hexGrid.CellData.Length;

	void ClearIncompatibleHistory()
	{
		undoHistory.Clear();
		redoHistory.Clear();
	}

	HexCellData[] CaptureMapSnapshot()
	{
		HexCellData[] snapshot = new HexCellData[hexGrid.CellData.Length];
		Array.Copy(hexGrid.CellData, snapshot, snapshot.Length);
		return snapshot;
	}

	void RestoreMapSnapshot(HexCellData[] snapshot)
	{
		Array.Copy(snapshot, hexGrid.CellData, snapshot.Length);
		hexGrid.RefreshAllCells();
		hexGrid.RefreshAllChunks();
		ResetPathStroke();
		previousCellIndex = -1;
		ClearCellHighlightData();
	}

	static bool SnapshotsEqual(HexCellData[] a, HexCellData[] b)
	{
		if (a.Length != b.Length)
		{
			return false;
		}
		for (int i = 0; i < a.Length; i++)
		{
			if (!a[i].Equals(b[i]))
			{
				return false;
			}
		}
		return true;
	}

	static HexCellData[] TakeLast(List<HexCellData[]> history)
	{
		int index = history.Count - 1;
		HexCellData[] snapshot = history[index];
		history.RemoveAt(index);
		return snapshot;
	}

	static void PushSnapshot(
		List<HexCellData[]> history, HexCellData[] snapshot)
	{
		if (history.Count == historyCapacity)
		{
			history.RemoveAt(0);
		}
		history.Add(snapshot);
	}
}
