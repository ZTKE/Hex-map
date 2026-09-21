using System;
using UnityEngine;

/// <summary>
/// Sprite slots in the original HoneyFramework foreground atlas.
/// </summary>
public enum HexHFForegroundSprite : byte
{
	Tree14,
	DeadTree07,
	Tree04,
	DeadTree02,
	Tree07
}

/// <summary>
/// One sprite recipe inside an HF foreground definition. Dense count is the
/// number emitted for a cell painted at 100% vegetation density.
/// </summary>
[Serializable]
public struct HexHFForegroundEntry
{
	public HexHFForegroundSprite sprite;
	[Min(0)] public int denseCount;
	public Color baseTint;

	public HexHFForegroundEntry(
		HexHFForegroundSprite sprite, int denseCount, Color baseTint)
	{
		this.sprite = sprite;
		this.denseCount = denseCount;
		this.baseTint = baseTint;
	}
}

/// <summary>
/// Editable counterpart of HoneyFramework's per-terrain foreground list. In
/// this project it is selected independently from the ground material so the
/// existing Terrain and Forest brushes remain orthogonal.
/// </summary>
[Serializable]
public sealed class HexHFForegroundProfile
{
	public string displayName;
	public HexHFForegroundEntry[] entries;

	public HexHFForegroundProfile(
		string displayName, params HexHFForegroundEntry[] entries)
	{
		this.displayName = displayName;
		this.entries = entries;
	}
}
