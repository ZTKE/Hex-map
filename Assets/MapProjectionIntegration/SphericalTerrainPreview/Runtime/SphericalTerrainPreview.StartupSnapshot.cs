using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        const int StartupSnapshotSchema = 1;
        const string StartupMagic = "SPHSTART";
        public bool LoadedStartupSnapshot { get; private set; }
        sealed class StartupSnapshotData
        {
            public ChunkData Shell;
            public Dictionary<int, int[]> Tiles;
        }

        public static string StartupSnapshotPath(int recursion) =>
            Path.Combine(Application.streamingAssetsPath, "SphericalMap", "StartupR" + recursion + ".bytes");

        /// <summary>Save the completed global surface and cell index after saving World.</summary>
        public string SaveStartupSnapshot(string directory)
        {
            if (World == null || string.IsNullOrEmpty(World.NativeSnapshotId) || !shell || !ocean || tileCells == null)
                throw new InvalidOperationException("Save the ready spherical world before its startup surface.");
            string path = Path.Combine(directory, "StartupR" + recursion + ".bytes");
            Directory.CreateDirectory(directory);
            string temporary = path + ".writing";
            try
            {
                var land = CaptureMesh(shell.GetComponent<MeshFilter>().sharedMesh, false);
                var water = CaptureMesh(ocean.GetComponent<MeshFilter>().sharedMesh, true);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 1024))
                {
                    long checksumOffset, payloadOffset;
                    using (var writer = new BinaryWriter(file, Encoding.UTF8, true))
                    {
                        writer.Write(Encoding.ASCII.GetBytes(StartupMagic)); writer.Write(StartupSnapshotSchema);
                        writer.Write(World.NativeSnapshotId); writer.Write(StartupRecipeId()); writer.Write(EditorStartupRecipeId());
                        writer.Write(radius); writer.Write(TileResolution);
                        checksumOffset = file.Position; writer.Write(new byte[32]); payloadOffset = file.Position;
                        WriteMesh(writer, land, false); WriteMesh(writer, water, true);
                        var keys = new List<int>(tileCells.Keys); keys.Sort(); writer.Write(keys.Count);
                        foreach (int key in keys)
                        {
                            writer.Write(key); int[] cells = tileCells[key]; writer.Write(cells.Length);
                            foreach (int id in cells) writer.Write(id);
                        }
                        writer.Flush();
                    }
                    file.Position = payloadOffset;
                    byte[] hash; using (var sha = SHA256.Create()) hash = sha.ComputeHash(file);
                    file.Position = checksumOffset; file.Write(hash, 0, hash.Length); file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>Capture Unity recipe dependencies on the main thread, then validate on any thread.</summary>
        public Func<string> PrepareStartupSnapshotValidation(string pathOrDirectory)
        {
            string path = Directory.Exists(pathOrDirectory)
                ? Path.Combine(pathOrDirectory, "StartupR" + recursion + ".bytes") : pathOrDirectory;
            if (World == null || !TryPrepareStartupSnapshot(out var read, path))
                throw new InvalidDataException("The saved startup surface does not match this native world, radius or terrain recipe.");
            return () =>
            {
                var restored = read();
                return $"Startup surface verified: {restored.Shell.Land.Vertices.Count:N0} land vertices, " +
                    $"{restored.Shell.Water.Vertices.Count:N0} water vertices and {restored.Tiles.Count:N0} complete spatial tiles.";
            };
        }

        public string ValidateStartupSnapshot(string pathOrDirectory) => PrepareStartupSnapshotValidation(pathOrDirectory)();

        bool TryPrepareStartupSnapshot(out Func<StartupSnapshotData> factory, string explicitPath = null)
        {
            factory = null;
            if (string.IsNullOrEmpty(World.NativeSnapshotId)) return false;
            string path = explicitPath ?? StartupSnapshotPath(recursion);
            if (!File.Exists(path)) return false;
            try
            {
                using var file = File.OpenRead(path);
                using var reader = new BinaryReader(file);
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != StartupMagic || reader.ReadInt32() != StartupSnapshotSchema ||
                    reader.ReadString() != World.NativeSnapshotId || reader.ReadString() != StartupRecipeId()) return false;
                string editorRecipe = reader.ReadString();
#if UNITY_EDITOR
                if (editorRecipe != EditorStartupRecipeId()) return false;
#endif
                if (reader.ReadSingle() != radius || reader.ReadInt32() != TileResolution) return false;
                byte[] hash = reader.ReadBytes(32); long payloadOffset = file.Position;
                if (hash.Length != 32) return false;
                int cellCount = World.Count; var token = lifetime.Token;
                factory = () => ReadStartupSnapshot(path, payloadOffset, hash, cellCount, token);
                return true;
            }
            catch (IOException e)
            { Debug.LogWarning("Saved spherical startup surface could not be read; rebuilding: " + e.Message, this); return false; }
        }

        static StartupSnapshotData ReadStartupSnapshot(string path, long payloadOffset, byte[] expected, int cellCount,
            CancellationToken token)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            file.Position = payloadOffset;
            byte[] actual; using (var sha = SHA256.Create()) actual = sha.ComputeHash(file);
            for (int i = 0; i < 32; i++) if (actual[i] != expected[i])
                throw new InvalidDataException("The saved spherical startup surface checksum failed. Re-bake SphericalMap.");
            token.ThrowIfCancellationRequested(); file.Position = payloadOffset;
            using var reader = new BinaryReader(file);
            var result = new StartupSnapshotData { Shell = new ChunkData(), Tiles = new Dictionary<int, int[]>() };
            ReadMesh(reader, result.Shell.Land, false, token); ReadMesh(reader, result.Shell.Water, true, token);
            int tiles = ReadStartupCount(reader, TileResolution * TileResolution), total = 0;
            var assigned = new bool[cellCount];
            for (int i = 0; i < tiles; i++)
            {
                token.ThrowIfCancellationRequested();
                int key = reader.ReadInt32();
                if (key < 0 || key >= TileResolution * TileResolution || result.Tiles.ContainsKey(key))
                    throw new InvalidDataException("Invalid saved spherical tile key.");
                int[] cells = new int[ReadStartupCount(reader, cellCount)];
                for (int j = 0; j < cells.Length; j++)
                {
                    int id = reader.ReadInt32();
                    if (id < 0 || id >= cellCount || assigned[id]) throw new InvalidDataException("Invalid saved spherical tile ownership.");
                    assigned[id] = true; cells[j] = id; total++;
                }
                result.Tiles.Add(key, cells);
            }
            if (total != cellCount || file.Position != file.Length) throw new InvalidDataException("Incomplete saved spherical startup index.");
            result.Shell.Land.PrepareUpload(); result.Shell.Water.PrepareUpload(water: true);
            return result;
        }

        string StartupRecipeId()
        {
            // Only plain numeric recipe values are reflected; Unity instance IDs are never
            // persisted because they change between Editor sessions and player builds.
            var text = new StringBuilder();
            var settings = new SphericalArtSettings(terrainProfile);
            var fields = typeof(SphericalArtSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var field in fields)
            {
                text.Append(field.Name).Append('=');
                object value = field.GetValue(settings);
                if (value is Vector2 vector) text.Append(vector.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(vector.y.ToString("R", CultureInfo.InvariantCulture));
                else text.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
                text.Append(';');
            }
            text.Append(terrainProfile.name).Append(';').Append(terrainProfile.plateauHeight.ToString("R", CultureInfo.InvariantCulture));
            if (terrainStyle)
                text.Append(';').Append(terrainStyle.name).Append(';').Append(terrainStyle.hfOriginalHeightLod).Append(';')
                    .Append(terrainStyle.hfOriginalHeightScale.ToString("R", CultureInfo.InvariantCulture)).Append(';')
                    .Append(terrainStyle.hfOriginalStampScale.ToString("R", CultureInfo.InvariantCulture));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }

        string EditorStartupRecipeId()
        {
#if UNITY_EDITOR
            var result = new StringBuilder();
            void Add(UnityEngine.Object asset)
            {
                if (!asset) return;
                string path = UnityEditor.AssetDatabase.GetAssetPath(asset);
                result.Append(path).Append(':').Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(path)).Append(';');
            }
            Add(terrainProfile); Add(terrainStyle);
            Add(Resources.Load<TextAsset>("SphericalTerrainPreview/RegionalElevation"));
            Add(Resources.Load<TextAsset>("SphericalTerrainPreview/LowPlateauMask"));
            // Profile dependencies include all stamp/mask textures. The format version
            // handles changes to the geometry algorithms themselves.
            return result.ToString();
#else
            return "";
#endif
        }

        static MeshBuffer CaptureMesh(Mesh source, bool water)
        {
            var result = new MeshBuffer();
            source.GetVertices(result.Vertices); source.GetNormals(result.Normals); source.GetColors(result.Colors);
            source.GetUVs(0, result.UV0); source.GetUVs(1, result.UV1);
            if (!water)
            { source.GetUVs(2, result.UV2); source.GetUVs(3, result.UV3); source.GetUVs(4, result.UV4); source.GetUVs(5, result.UV5); }
            source.GetTriangles(result.Indices, 0);
            return result;
        }

        static void WriteMesh(BinaryWriter writer, MeshBuffer mesh, bool water)
        {
            writer.Write(mesh.Vertices.Count); writer.Write(mesh.Indices.Count);
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                WriteV3(writer, mesh.Vertices[i]); WriteV3(writer, mesh.Normals[i]); WriteV4(writer, mesh.Colors[i]);
                WriteV4(writer, mesh.UV0[i]); WriteV4(writer, mesh.UV1[i]);
                if (!water) { WriteV4(writer, mesh.UV2[i]); WriteV4(writer, mesh.UV3[i]); WriteV4(writer, mesh.UV4[i]); WriteV4(writer, mesh.UV5[i]); }
            }
            foreach (int id in mesh.Indices) writer.Write(id);
        }
        static void ReadMesh(BinaryReader reader, MeshBuffer mesh, bool water, CancellationToken token)
        {
            int vertices = ReadStartupCount(reader, 2000000), indices = ReadStartupCount(reader, 12000000);
            if (indices % 3 != 0) throw new InvalidDataException("Invalid saved spherical mesh triangles.");
            mesh.Reserve(vertices, indices, water);
            for (int i = 0; i < vertices; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                mesh.Vertices.Add(ReadV3(reader)); mesh.Normals.Add(ReadV3(reader)); mesh.Colors.Add(ReadV4(reader));
                mesh.UV0.Add(ReadV4(reader)); mesh.UV1.Add(ReadV4(reader));
                if (!water) { mesh.UV2.Add(ReadV4(reader)); mesh.UV3.Add(ReadV4(reader)); mesh.UV4.Add(ReadV4(reader)); mesh.UV5.Add(ReadV4(reader)); }
            }
            for (int i = 0; i < indices; i++)
            { int id = reader.ReadInt32(); if (id < 0 || id >= vertices) throw new InvalidDataException("Invalid saved spherical mesh index."); mesh.Indices.Add(id); }
        }
        static int ReadStartupCount(BinaryReader reader, int max)
        { int value = reader.ReadInt32(); if (value < 0 || value > max) throw new InvalidDataException("Invalid saved spherical startup array."); return value; }
        static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void WriteV4(BinaryWriter w, Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
        static Vector3 ReadV3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Vector4 ReadV4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }
}
