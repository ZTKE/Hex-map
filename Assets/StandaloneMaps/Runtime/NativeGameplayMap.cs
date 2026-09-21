using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Authoritative geographic gameplay graph. Source IDs are import-only;
/// all runtime arrays, orders and visual state use this graph's cell IDs.</summary>
namespace ZTKE.HexMap.Standalone
{
public sealed class NativeGameplayMap
{
    public struct Tile
    {
        public int Country, Terrain, Region;
        public string CityName;
    }
    public struct City
    {
        public int TileId, SourceTileId;
        public string Name;
    }
    public readonly Vector3[] Centers;
    public readonly Vector3[][] Corners;
    public readonly int[][] Neighbors, RiverNeighbors;
    public readonly Tile[] Tiles;
    public readonly int[] SourceToTile, TileToSource;
    public readonly City[] Cities;
    public readonly Dictionary<int, DivideAreaInfo> Regions;
    public readonly Color32[] Overlay, Occupation, SelectionOverlay;
    public readonly byte[] BuildSelection;
    public event Action<int> BuildSelectionChanged;
    public readonly ushort[] Countries;
    public readonly float Radius, MaximumNeighborAngle;
    public readonly Func<Vector3, int> FindContainingCell;
    public int Count => Centers.Length;
    public uint PoliticalRevision { get; private set; }
    public uint OverlayRevision { get; private set; }
    public uint OccupationRevision { get; private set; }
    public int SelectedTile { get; private set; } = -1;
    public event Action<int> CountryChanged, OverlayChanged, OccupationChanged;

    public NativeGameplayMap(Vector3[] centers, Vector3[][] corners, int[][] neighbors,
        int[][] riverNeighbors, Tile[] tiles, int[] sourceToTile, int[] tileToSource,
        City[] cities, Dictionary<int, DivideAreaInfo> regions, float radius,
        float maximumNeighborAngle, Func<Vector3, int> findContainingCell)
    {
        Centers=centers; Corners=corners; Neighbors=neighbors; RiverNeighbors=riverNeighbors;
        Tiles=tiles; SourceToTile=sourceToTile; TileToSource=tileToSource; Cities=cities;
        Regions=regions; Radius=radius; MaximumNeighborAngle=maximumNeighborAngle;
        FindContainingCell=findContainingCell;
        Countries=new ushort[Count]; Overlay=new Color32[Count]; Occupation=new Color32[Count];
        BuildSelection=new byte[Count]; SelectionOverlay=new Color32[Count];
        for (int i=0;i<Count;i++) Countries[i]=checked((ushort)tiles[i].Country);
    }
    public bool Valid(int id) => (uint)id < (uint)Count;
    public int ResolveSourceTile(int id) => (uint)id < (uint)SourceToTile.Length ? SourceToTile[id] : -1;
    public Vector3 Position(int id) => Centers[id] * Radius;
    public int FindTile(Vector3 position) => position.sqrMagnitude > 1e-12f ? FindContainingCell(position.normalized) : -1;
    public float HeuristicSteps(Vector3 from, Vector3 to)
    {
        // Chord distance is a lower bound on arc length; normalizing by the
        // largest edge angle preserves admissibility for a graph with g += 1.
        return Vector3.Distance(from.normalized,to.normalized) / Mathf.Max(MaximumNeighborAngle,1e-6f);
    }
    public float Distance(Vector3 from,Vector3 to) => Mathf.Atan2(Vector3.Cross(from.normalized,to.normalized).magnitude,
        Mathf.Clamp(Vector3.Dot(from.normalized,to.normalized),-1,1)) * Radius;
    public void SetCountry(int id, int country)
    {
        if (!Valid(id) || (uint)country > ushort.MaxValue || Countries[id]==country) return;
        Countries[id]=(ushort)country; PoliticalRevision++; CountryChanged?.Invoke(id);
    }
    public void SetOverlay(int id, Color color)
    {
        if (!Valid(id)) return; Color32 value=color;
        if (Overlay[id].Equals(value)) return; Overlay[id]=value; OverlayRevision++; OverlayChanged?.Invoke(id);
    }
    public void SetOccupation(int id, Color color)
    {
        if (!Valid(id)) return; Color32 value=color;
        if (Occupation[id].Equals(value)) return; Occupation[id]=value; OccupationRevision++; OccupationChanged?.Invoke(id);
    }
    public void SetBuildSelection(int id, byte state)
    {
        if (!Valid(id) || BuildSelection[id] == state) return;
        BuildSelection[id] = state; BuildSelectionChanged?.Invoke(id);
    }
    public void SetSelectionOverlay(int id, Color color)
    {
        if (!Valid(id)) return;
        Color32 value = color;
        if (SelectionOverlay[id].Equals(value)) return;
        SelectionOverlay[id] = value; OverlayRevision++; OverlayChanged?.Invoke(id);
    }
    public void Select(int id) => SelectedTile=Valid(id)?id:-1;
}

public sealed class DivideAreaInfo { public string areaName; public int keyCityID; }
}
