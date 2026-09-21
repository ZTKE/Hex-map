using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using WW2.SphericalTerrainPreview;

namespace ZTKE.HexMap.Standalone
{
    /// <summary>Authored native map, independent of the rectangular import data.
    /// Only initial geographic state is saved; wars, overlays and economy are session state.</summary>
    public static class SphericalGameplaySnapshot
    {
        const int Magic = 0x53474D50, Version = 1;
        public const string FileName = "Gameplay-v1.bin";
        public static string DefaultPath => Path.Combine(Application.streamingAssetsPath, "SphericalMap", FileName);

        public static void Save(string path, NativeGameplayMap map, SphericalWorld world)
        {
            if (string.IsNullOrEmpty(world.NativeSnapshotId))
                throw new InvalidOperationException("Save the native terrain snapshot before gameplay.");
            string temporary = path + ".writing";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(world.NativeSnapshotId);
                writer.Write(map.Count); writer.Write(map.Radius); writer.Write(map.MaximumNeighborAngle);
                foreach (var tile in map.Tiles)
                {
                    writer.Write(tile.Country); writer.Write(tile.Terrain); writer.Write(tile.Region);
                    writer.Write(tile.CityName ?? "");
                }
                WriteInts(writer, map.SourceToTile); WriteInts(writer, map.TileToSource);
                writer.Write(map.Cities.Length);
                foreach (var city in map.Cities)
                { writer.Write(city.TileId); writer.Write(city.SourceTileId); writer.Write(city.Name ?? ""); }
                writer.Write(map.Regions.Count);
                foreach (int key in map.Regions.Keys.OrderBy(id => id))
                { writer.Write(key); writer.Write(map.Regions[key].areaName ?? ""); writer.Write(map.Regions[key].keyCityID); }
                foreach (var rivers in map.RiverNeighbors) WriteInts(writer, rivers);
            }
            // A trailing digest covers the header and every geographic record.
            byte[] digest;
            using (var hash = SHA256.Create()) using (var stream = File.OpenRead(temporary)) digest = hash.ComputeHash(stream);
            using (var stream = new FileStream(temporary, FileMode.Append)) stream.Write(digest, 0, digest.Length);
            // Validate before replacing an existing authored map.
            Load(temporary, world);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        public static NativeGameplayMap Load(string path, SphericalWorld world, CancellationToken token = default)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 64) throw new InvalidDataException("Native gameplay file is truncated.");
            using (var hash = SHA256.Create())
            {
                byte[] actual = hash.ComputeHash(bytes, 0, bytes.Length - 32);
                for (int i = 0; i < 32; i++) if (actual[i] != bytes[bytes.Length - 32 + i])
                    throw new InvalidDataException("Native gameplay checksum failed. Restore or rebake the spherical map.");
            }
            using var stream = new MemoryStream(bytes, 0, bytes.Length - 32, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadString() != world.NativeSnapshotId)
                throw new InvalidDataException("Native gameplay/terrain versions differ. Rebake the spherical map together.");
            int count = reader.ReadInt32(); float radius = reader.ReadSingle(), angle = reader.ReadSingle();
            if (count != world.Count || radius != world.Radius || !float.IsFinite(angle) || angle <= 0)
                throw new InvalidDataException("Native gameplay topology does not match terrain.");
            var tiles = new NativeGameplayMap.Tile[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                tiles[i] = new NativeGameplayMap.Tile { Country = reader.ReadInt32(), Terrain = reader.ReadInt32(),
                    Region = reader.ReadInt32(), CityName = reader.ReadString() };
                if ((uint)tiles[i].Country > ushort.MaxValue || (uint)tiles[i].Terrain > 9)
                    throw new InvalidDataException("Invalid native tile state.");
            }
            var sourceToTile = ReadInts(reader, 2000000);
            var tileToSource = ReadInts(reader, count);
            if (tileToSource.Length != count) throw new InvalidDataException("Invalid native source references.");
            foreach (int id in sourceToTile) RequireTile(id, count);
            foreach (int id in tileToSource) if (id < -1 || id >= sourceToTile.Length)
                throw new InvalidDataException("Invalid legacy provenance ID.");
            int cityCount = ReadCount(reader, count);
            var cities = new NativeGameplayMap.City[cityCount]; var occupied = new HashSet<int>();
            for (int i = 0; i < cityCount; i++)
            {
                var city = new NativeGameplayMap.City { TileId = reader.ReadInt32(), SourceTileId = reader.ReadInt32(), Name = reader.ReadString() };
                RequireTile(city.TileId, count);
                if ((uint)city.SourceTileId >= sourceToTile.Length || !occupied.Add(city.TileId) ||
                    tiles[city.TileId].Terrain != 2 || tiles[city.TileId].CityName != city.Name)
                    throw new InvalidDataException("Native city location or name is inconsistent.");
                cities[i] = city;
            }
            int regionCount = ReadCount(reader, count);
            var regions = new Dictionary<int, DivideAreaInfo>(regionCount);
            for (int i = 0; i < regionCount; i++)
            {
                int id = reader.ReadInt32(); string name = reader.ReadString(); int capital = reader.ReadInt32();
                if (capital != -1) RequireTile(capital, count);
                if (capital >= 0 && !occupied.Contains(capital)) throw new InvalidDataException("Native region key is not a city.");
                regions.Add(id, new DivideAreaInfo { areaName = name, keyCityID = capital });
            }
            foreach (var tile in tiles) if (!regions.ContainsKey(tile.Region)) throw new InvalidDataException("Unknown native region.");
            var rivers = new int[count][];
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                rivers[i] = ReadInts(reader, 6);
                foreach (int next in rivers[i])
                    if ((uint)next >= count || Array.IndexOf(world.Neighbors[i], next) < 0 || tiles[i].Terrain <= 0 || tiles[next].Terrain <= 0)
                        throw new InvalidDataException("Invalid native river crossing.");
            }
            for (int i = 0; i < count; i++) foreach (int next in rivers[i])
                if (Array.IndexOf(rivers[next], i) < 0) throw new InvalidDataException("Asymmetric native river crossing.");
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected native gameplay data.");
            return new NativeGameplayMap(world.Centers, world.Corners, world.Neighbors, rivers, tiles,
                sourceToTile, tileToSource, cities, regions, radius, angle, direction => world.FindContainingCell(direction));
        }
        static void RequireTile(int id, int count)
        { if ((uint)id >= count) throw new InvalidDataException("Native cell ID is out of range."); }
        static int ReadCount(BinaryReader reader, int maximum)
        { int n = reader.ReadInt32(); if (n < 0 || n > maximum) throw new InvalidDataException("Invalid native record count."); return n; }
        static int[] ReadInts(BinaryReader reader, int maximum)
        { int n = ReadCount(reader, maximum); var values = new int[n]; for (int i = 0; i < n; i++) values[i] = reader.ReadInt32(); return values; }
        static void WriteInts(BinaryWriter writer, int[] values)
        { writer.Write(values.Length); foreach (int value in values) writer.Write(value); }
    }
}
