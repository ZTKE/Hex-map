using System.IO;
using UnityEngine;

/// <summary>
/// Loads the baked flat world once when the map scene starts. Creating a new
/// map later in the same session does not trigger this bootstrap again.
/// </summary>
public sealed class DefaultWorldMapBootstrap : MonoBehaviour
{
	public const int CurrentMapFileVersion = 12;
	public const string DefaultResourcePath = "Maps/DefaultWorld";

	HexGrid grid;
	bool attemptedLoad;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	static void InstallForLoadedScene()
	{
		HexGrid grid = FindObjectOfType<HexGrid>();
		if (!grid || grid.GetComponent<DefaultWorldMapBootstrap>())
		{
			return;
		}

		DefaultWorldMapBootstrap bootstrap =
			grid.gameObject.AddComponent<DefaultWorldMapBootstrap>();
		bootstrap.grid = grid;
	}

	void Awake()
	{
		if (!grid)
		{
			grid = GetComponent<HexGrid>();
		}
	}

	void Start()
	{
		LoadDefaultWorld();
	}

	public bool LoadDefaultWorld()
	{
		if (attemptedLoad)
		{
			return false;
		}
		attemptedLoad = true;

		TextAsset mapAsset = Resources.Load<TextAsset>(DefaultResourcePath);
		if (!mapAsset)
		{
			Debug.LogWarning(
				$"Default world map is missing at Resources/{DefaultResourcePath}.bytes.");
			return false;
		}

		try
		{
			System.Diagnostics.Stopwatch loadTimer =
				System.Diagnostics.Stopwatch.StartNew();
			using MemoryStream stream = new(mapAsset.bytes, false);
			using BinaryReader reader = new(stream);
			int header = reader.ReadInt32();
			if (header < 1 || header > CurrentMapFileVersion)
			{
				Debug.LogError($"Unsupported default world map version {header}.");
				return false;
			}

			grid.Load(reader, header);
			if (FindObjectOfType<HexMapCamera>())
			{
				HexMapCamera.FocusWorldMap();
			}
			loadTimer.Stop();
			Debug.Log(
				$"Loaded default flat world: {grid.CellCountX}x{grid.CellCountZ}, " +
				$"{grid.CellData.Length:N0} cells, wrapping={grid.Wrapping}, " +
				$"cities={grid.CityCount:N0}, " +
				$"overview={grid.IsOverviewMode}, politics={grid.HasPoliticalData}, " +
				$"borders={grid.PoliticalBorderSegmentCount:N0}, " +
				$"activeChunks={grid.ActiveChunkCount}, " +
				$"load={loadTimer.Elapsed.TotalSeconds:F2}s.");
			return true;
		}
		catch (System.Exception exception)
		{
			Debug.LogException(exception);
			return false;
		}
	}
}
