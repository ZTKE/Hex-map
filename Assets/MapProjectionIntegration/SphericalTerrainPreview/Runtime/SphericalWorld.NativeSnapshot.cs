using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalWorld
    {
        public const int NativeSnapshotSchema = 1;
        const string NativeMagic = "SPHWORLD";
        const int NativeHeaderSize = 8 + 4 + 4 + 32;
        public string NativeSnapshotId { get; private set; } = "";
        public bool LoadedFromNativeSnapshot { get; private set; }

        public static string NativeSnapshotPath(int recursion) =>
            Path.Combine(Application.streamingAssetsPath, "SphericalMap", "TerrainR" + recursion + ".bytes");

        public static bool HasNativeSnapshot(int recursion) => File.Exists(NativeSnapshotPath(recursion));

        /// <summary>Explicit migration/bake entry point. Normal gameplay loads the native snapshot.</summary>
        public static SphericalWorld BuildFromLegacySource(int recursion, float radius)
        {
            if (recursion < 0 || recursion > 5) throw new ArgumentOutOfRangeException(nameof(recursion));
            if (!(radius > 0) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
            return new SphericalWorld(global::IcoSphere.Pack.Read(recursion), radius, SourceMap.Load(recursion));
        }

        /// <summary>
        /// Writes the actual spherical topology and all authored cell/river data. No flat map,
        /// resource GUID, sampled longitude or migration recipe is needed to read this file.
        /// Save before gameplay data so it can bind to NativeSnapshotId.
        /// </summary>
        public void SaveNativeSnapshot(string path, int recursion)
        {
            if (recursion < 0 || recursion > 5) throw new ArgumentOutOfRangeException(nameof(recursion));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".writing";
            try
            {
                byte[] hash;
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024))
                {
                    using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
                    {
                        writer.Write(Encoding.ASCII.GetBytes(NativeMagic));
                        writer.Write(NativeSnapshotSchema); writer.Write(recursion); writer.Write(new byte[32]);
                        writer.Write(Radius); writer.Write(CellRadius);
                        WriteVectors(writer, Centers); WriteVectors(writer, CornerDirections);
                        WriteTriangles(writer, Pack.tris); WriteTriangles(writer, Pack.adjTris);
                        writer.Write(Count);
                        for (int i = 0; i < Count; i++)
                        {
                            writer.Write((byte)Neighbors[i].Length);
                            for (int edge = 0; edge < Neighbors[i].Length; edge++)
                            { writer.Write(Neighbors[i][edge]); writer.Write(CornerIds[i][edge]); }
                            WriteCell(writer, Cells[i]);
                        }
                        WriteInts(writer, Pentagons); writer.Write(seedColumns); writer.Write(seedRows); WriteInts(writer, seeds);
                        writer.Write(SourceRiverEdgeCount); writer.Write(CollapsedSourceRiverEdges);
                        writer.Write(RiverEdges.Length);
                        foreach (var edge in RiverEdges)
                        { writer.Write(edge.CornerA); writer.Write(edge.CornerB); writer.Write(edge.CellA); writer.Write(edge.CellB); }
                        writer.Write(RiverRoutes.Length);
                        foreach (var route in RiverRoutes) { writer.Write(route.Name ?? ""); WriteInts(writer, route.Corners); }
                        writer.Write(OmittedRiverRoutes.Length);
                        foreach (string route in OmittedRiverRoutes) writer.Write(route ?? "");
                        writer.Flush();
                    }
                    file.Position = NativeHeaderSize;
                    using (var sha = SHA256.Create()) hash = sha.ComputeHash(file);
                    file.Position = 16; file.Write(hash, 0, hash.Length); file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                NativeSnapshotId = HexDigest(hash);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static SphericalWorld ReadNativeSnapshot(string path, int recursion, float radius,
            CancellationToken cancellation = default)
        {
            if (!(radius > 0) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
            cancellation.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            using var reader = new BinaryReader(file, Encoding.UTF8, true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != NativeMagic ||
                reader.ReadInt32() != NativeSnapshotSchema || reader.ReadInt32() != recursion)
                throw new InvalidDataException("The native spherical terrain version/topology is incompatible. Re-bake SphericalMap.");
            byte[] expected = reader.ReadBytes(32);
            if (expected.Length != 32) throw new InvalidDataException("Truncated native spherical terrain header.");
            byte[] actual;
            using (var sha = SHA256.Create()) actual = sha.ComputeHash(file);
            for (int i = 0; i < 32; i++) if (actual[i] != expected[i])
                throw new InvalidDataException("Native spherical terrain checksum failed. Restore or re-bake SphericalMap.");
            cancellation.ThrowIfCancellationRequested();
            file.Position = NativeHeaderSize;
            var world = new SphericalWorld(reader, radius, cancellation)
            { NativeSnapshotId = HexDigest(actual), LoadedFromNativeSnapshot = true };
            if (file.Position != file.Length) throw new InvalidDataException("Unexpected trailing native spherical terrain data.");
            return world;
        }

        SphericalWorld(BinaryReader reader, float radius, CancellationToken cancellation)
        {
            float savedRadius = reader.ReadSingle(), savedCellRadius = reader.ReadSingle();
            if (!(savedRadius > 0) || !(savedCellRadius > 0) || float.IsInfinity(savedRadius) || float.IsInfinity(savedCellRadius))
                throw new InvalidDataException("Invalid native spherical map scale.");
            Radius = radius; CellRadius = savedCellRadius * (radius / savedRadius);
            Centers = ReadVectors(reader, cancellation); CornerDirections = ReadVectors(reader, cancellation);
            var triangles = ReadTriangles(reader, Count, cancellation);
            var adjacentTriangles = ReadTriangles(reader, CornerDirections.Length, cancellation);
            if (triangles.Length != CornerDirections.Length || adjacentTriangles.Length != triangles.Length)
                throw new InvalidDataException("Native sphere triangles do not match its corners.");
            // The native graph already has ordered dual edges. Legacy renderer/search arrays
            // (abuts, posVerts) were used only while importing and are intentionally not loaded.
            Pack = new global::IcoSphere.Pack
            { verts = Centers, tris = triangles, ctrs = CornerDirections, adjTris = adjacentTriangles };
            if (reader.ReadInt32() != Count) throw new InvalidDataException("Native sphere cell count mismatch.");
            Neighbors = new int[Count][]; CornerIds = new int[Count][]; Corners = new Vector3[Count][];
            Cells = new SphericalCellData[Count];
            for (int i = 0; i < Count; i++)
            {
                if ((i & 511) == 0) cancellation.ThrowIfCancellationRequested();
                int edges = reader.ReadByte();
                if (edges != 5 && edges != 6) throw new InvalidDataException("Native sphere cell requires five or six edges.");
                Neighbors[i] = new int[edges]; CornerIds[i] = new int[edges]; Corners[i] = new Vector3[edges];
                for (int e = 0; e < edges; e++)
                {
                    Neighbors[i][e] = ReadIndex(reader, Count); CornerIds[i][e] = ReadIndex(reader, CornerDirections.Length);
                    Corners[i][e] = CornerDirections[CornerIds[i][e]];
                }
                Cells[i] = ReadCell(reader);
                if (Cells[i].RiverNeighbor < -1 || Cells[i].RiverNeighbor >= Count)
                    throw new InvalidDataException("Invalid native sphere river neighbor.");
            }
            Pentagons = ReadInts(reader, Count);
            if (Pentagons.Length != 12) throw new InvalidDataException("Native sphere requires twelve pentagons.");
            foreach (int id in Pentagons) if (id < 0 || id >= Count || Neighbors[id].Length != 5)
                throw new InvalidDataException("Invalid native sphere pentagon.");
            seedColumns = reader.ReadInt32(); seedRows = reader.ReadInt32(); seeds = ReadInts(reader, 16200);
            if (seedRows < 2 || seedRows > 90 || seedColumns != seedRows * 2 || seeds.Length != seedRows * seedColumns)
                throw new InvalidDataException("Invalid native spherical lookup index.");
            foreach (int id in seeds) if (id < -1 || id >= Count) throw new InvalidDataException("Invalid native sphere lookup seed.");
            SourceRiverEdgeCount = reader.ReadInt32(); CollapsedSourceRiverEdges = reader.ReadInt32();
            RiverEdges = new SphericalRiverEdge[ReadCount(reader, CornerDirections.Length * 3)];
            for (int i = 0; i < RiverEdges.Length; i++) RiverEdges[i] = new SphericalRiverEdge
            { CornerA = ReadIndex(reader, CornerDirections.Length), CornerB = ReadIndex(reader, CornerDirections.Length),
              CellA = ReadIndex(reader, Count), CellB = ReadIndex(reader, Count) };
            RiverRoutes = new SphericalRiverRoute[ReadCount(reader, 4096)];
            for (int i = 0; i < RiverRoutes.Length; i++)
            {
                string name = reader.ReadString(); int[] corners = ReadInts(reader, CornerDirections.Length);
                if (corners.Length < 2) throw new InvalidDataException("Invalid native sphere river route.");
                foreach (int corner in corners) if (corner < 0 || corner >= CornerDirections.Length)
                    throw new InvalidDataException("Invalid native sphere river route corner.");
                RiverRoutes[i] = new SphericalRiverRoute { Name = name, Corners = corners };
            }
            OmittedRiverRoutes = new string[ReadCount(reader, 4096)];
            for (int i = 0; i < OmittedRiverRoutes.Length; i++) OmittedRiverRoutes[i] = reader.ReadString();
        }

        static string HexDigest(byte[] hash) => BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        static int ReadCount(BinaryReader r, int max = 4000000)
        { int count = r.ReadInt32(); if (count < 0 || count > max) throw new InvalidDataException("Invalid native spherical array length."); return count; }
        static int ReadIndex(BinaryReader r, int count)
        { int id = r.ReadInt32(); if (id < 0 || id >= count) throw new InvalidDataException("Invalid native spherical topology index."); return id; }
        static void WriteInts(BinaryWriter w, int[] values)
        { w.Write(values.Length); foreach (int v in values) w.Write(v); }
        static int[] ReadInts(BinaryReader r, int max)
        { var values = new int[ReadCount(r, max)]; for (int i = 0; i < values.Length; i++) values[i] = r.ReadInt32(); return values; }
        static void WriteVectors(BinaryWriter w, Vector3[] values)
        { w.Write(values.Length); foreach (var v in values) { w.Write(v.x); w.Write(v.y); w.Write(v.z); } }
        static Vector3[] ReadVectors(BinaryReader r, CancellationToken cancellation)
        {
            var values = new Vector3[ReadCount(r)];
            for (int i = 0; i < values.Length; i++)
            { if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested(); values[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
            return values;
        }
        static void WriteTriangles(BinaryWriter w, global::IcoSphere.Tri[] values)
        { w.Write(values.Length); foreach (var t in values) { w.Write(t[0]); w.Write(t[1]); w.Write(t[2]); } }
        static global::IcoSphere.Tri[] ReadTriangles(BinaryReader r, int indexCount, CancellationToken cancellation)
        {
            var values = new global::IcoSphere.Tri[ReadCount(r)];
            for (int i = 0; i < values.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                values[i] = new global::IcoSphere.Tri(ReadIndex(r, indexCount), ReadIndex(r, indexCount), ReadIndex(r, indexCount));
            }
            return values;
        }
        static void WriteCell(BinaryWriter w, SphericalCellData c)
        {
            w.Write(c.SourceIndex); w.Write(c.Country); w.Write(c.Biome); w.Write(c.Landform); w.Write(c.SourceLandform);
            w.Write(c.Vegetation); w.Write(c.VegetationDensity); w.Write(c.VegetationTint); w.Write(c.TerrainRotation);
            w.Write(c.MountainMode); w.Write(c.PlantLevel); w.Write(c.UrbanLevel); w.Write(c.FarmLevel); w.Write(c.SpecialIndex);
            w.Write(c.Water); w.Write(c.HasRiver); w.Write(c.Elevation);
            w.Write(c.CountryColor.r); w.Write(c.CountryColor.g); w.Write(c.CountryColor.b); w.Write(c.CountryColor.a);
            w.Write(c.RiverNeighbor); w.Write(c.RiverEdgeMask);
        }
        static SphericalCellData ReadCell(BinaryReader r) => new SphericalCellData
        {
            SourceIndex = r.ReadInt32(), Country = r.ReadInt32(), Biome = r.ReadInt32(), Landform = r.ReadInt32(), SourceLandform = r.ReadInt32(),
            Vegetation = r.ReadInt32(), VegetationDensity = r.ReadInt32(), VegetationTint = r.ReadInt32(), TerrainRotation = r.ReadInt32(),
            MountainMode = r.ReadInt32(), PlantLevel = r.ReadInt32(), UrbanLevel = r.ReadInt32(), FarmLevel = r.ReadInt32(), SpecialIndex = r.ReadInt32(),
            Water = r.ReadBoolean(), HasRiver = r.ReadBoolean(), Elevation = r.ReadSingle(),
            CountryColor = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            RiverNeighbor = r.ReadInt32(), RiverEdgeMask = r.ReadInt32()
        };
    }
}
