using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using WW2.SphericalTerrainPreview;

namespace ZTKE.HexMap.Standalone
{
    public sealed partial class SphericalGameplayPolitics
    {
        // Bump when polygon plane ordering, atlas generation, smoothing or the
        // transfer stripe layout changes. Live ownership is stored separately
        // from topology so a changed scenario can reuse the geographic lookup.
        public const string StartupDataFileName = "Interaction-v1.bin";
        const int StartupDataMagic = 0x53504931, StartupDataVersion = 1;
        const int CopyBufferBytes = 1024 * 1024;

        /// <summary>Bake the exact GPU lookup/initial political field alongside
        /// the native map. Managed worker-safe; optional cacheDirectory allows
        /// an editor bake to reuse the completed first-play cache.</summary>
        public static void SaveStartupData(string directory, SphericalWorld world, ushort[] owners,
            string cacheDirectory = null, CancellationToken token = default)
        {
            if (world == null || owners == null || owners.Length != world.Count)
                throw new ArgumentException("Matching spherical world and country ownership are required.");
            string destination = Path.Combine(directory, StartupDataFileName);
            string existing = string.IsNullOrEmpty(cacheDirectory) ? null : Path.Combine(cacheDirectory, StartupDataFileName);
            ulong worldHash = HashTopology(world, token), ownersHash = HashOwners(owners);
            TopologyRaster data = ReadCachedStartupData(destination, existing, world.Count, worldHash, ownersHash, token)
                ?? BuildTopology(world, token);
            if (data.InitialField == null) data.InitialField = BuildField(data, owners, token);
            WriteStartupData(destination, data, worldHash, ownersHash, token);
        }

        /// <summary>Decode a baked file with the runtime reader before publishing it.
        /// All arrays and the CRC must match this exact topology and initial ownership.</summary>
        public static void ValidateStartupData(string path, SphericalWorld world, ushort[] owners,
            CancellationToken token = default)
        {
            if (world == null || owners == null || owners.Length != world.Count)
                throw new ArgumentException("Matching spherical world and country ownership are required.");
            var data = ReadStartupData(path, world.Count, HashTopology(world, token), HashOwners(owners), token);
            if (data?.InitialField == null)
                throw new InvalidDataException("Saved spherical interaction data failed its topology, ownership or CRC validation.");
        }

        static TopologyRaster LoadOrBuildStartupData(SphericalWorld world, ushort[] owners,
            string bundledPath, string localPath, CancellationToken token)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            ulong worldHash = HashTopology(world, token), ownersHash = HashOwners(owners);
            TopologyRaster data = ReadCachedStartupData(bundledPath, localPath, world.Count, worldHash, ownersHash, token);
            bool completeCache = data != null && data.InitialField != null;
            if (data == null) data = BuildTopology(world, token);
            if (data.InitialField == null) data.InitialField = BuildField(data, owners, token);
            if (!completeCache)
            {
                try { WriteStartupData(localPath, data, worldHash, ownersHash, token); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // A read-only cache location must not make an otherwise
                    // valid map unplayable. The packaged bake is still usable.
                    data.CacheWarning = "Spherical interaction cache could not be saved: " + error.Message;
                }
            }
            data.LoadedFromDisk = completeCache;
            data.PreparationMilliseconds = ElapsedMilliseconds(started);
            return data;
        }

        static TopologyRaster ReadCachedStartupData(string primary, string fallback, int count, ulong worldHash,
            ulong ownersHash, CancellationToken token)
        {
            TopologyRaster data = ReadStartupData(primary, count, worldHash, ownersHash, token);
            if (data?.InitialField != null) return data;
            // A new scenario can retain the packaged topology while its newer
            // initial ownership field is already cached in the writable folder.
            TopologyRaster cached = ReadStartupData(fallback, count, worldHash, ownersHash, token);
            return cached ?? data;
        }

        static TopologyRaster ReadStartupData(string path, int count, ulong worldHash, ulong ownersHash,
            CancellationToken token)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using var file = File.OpenRead(path);
                using var compressed = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new BinaryReader(compressed);
                if (reader.ReadInt32() != StartupDataMagic || reader.ReadInt32() != StartupDataVersion ||
                    reader.ReadInt32() != count || reader.ReadInt32() != FieldWidth ||
                    reader.ReadInt32() != FieldHeight || reader.ReadInt32() != SeedWidth ||
                    reader.ReadInt32() != SeedHeight || reader.ReadInt32() != UploadRows ||
                    reader.ReadUInt64() != worldHash) return null;
                bool matchingOwners = reader.ReadUInt64() == ownersHash;
                var buffer = new byte[CopyBufferBytes];
                var data = new TopologyRaster
                {
                    Edges = ReadArray<Vector4>(reader, count * 6, buffer, token),
                    Cells = ReadArray<int>(reader, FieldWidth * FieldHeight, buffer, token),
                    Core = ReadArray<byte>(reader, FieldWidth * FieldHeight, buffer, token),
                    Land = ReadArray<byte>(reader, count, buffer, token),
                    Seed = ReadStripes(reader, SeedWidth, SeedHeight, buffer, token)
                };
                Color32[][] field = ReadStripes(reader, FieldWidth, FieldHeight, buffer, token);
                if (reader.ReadInt32() != StartupDataMagic || reader.BaseStream.ReadByte() != -1)
                    throw new InvalidDataException("Spherical interaction cache has an invalid end marker.");
                if (matchingOwners) data.InitialField = new FieldResult { Pixels = field };
                return data;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                // Truncated/incompatible cache data is disposable. Rebuilding
                // from the saved native map is the recovery path.
                return null;
            }
        }

        static void WriteStartupData(string path, TopologyRaster data, ulong worldHash, ulong ownersHash,
            CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var compressed = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new BinaryWriter(compressed))
                {
                    writer.Write(StartupDataMagic); writer.Write(StartupDataVersion); writer.Write(data.Land.Length);
                    writer.Write(FieldWidth); writer.Write(FieldHeight); writer.Write(SeedWidth); writer.Write(SeedHeight);
                    writer.Write(UploadRows); writer.Write(worldHash); writer.Write(ownersHash);
                    var buffer = new byte[CopyBufferBytes];
                    WriteArray(writer, data.Edges, buffer, token);
                    WriteArray(writer, data.Cells, buffer, token);
                    WriteArray(writer, data.Core, buffer, token);
                    WriteArray(writer, data.Land, buffer, token);
                    WriteStripes(writer, data.Seed, buffer, token);
                    WriteStripes(writer, data.InitialField.Pixels, buffer, token);
                    writer.Write(StartupDataMagic);
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static void WriteStripes(BinaryWriter writer, Color32[][] stripes, byte[] buffer, CancellationToken token)
        {
            writer.Write(stripes.Length);
            foreach (Color32[] stripe in stripes) WriteArray(writer, stripe, buffer, token);
        }

        static Color32[][] ReadStripes(BinaryReader reader, int width, int height, byte[] buffer, CancellationToken token)
        {
            int count = (height + UploadRows - 1) / UploadRows;
            if (reader.ReadInt32() != count) throw new InvalidDataException("Invalid spherical atlas stripe count.");
            var stripes = new Color32[count][];
            for (int i = 0; i < count; i++)
                stripes[i] = ReadArray<Color32>(reader, width * Math.Min(UploadRows, height - i * UploadRows), buffer, token);
            return stripes;
        }

        static void WriteArray<T>(BinaryWriter writer, T[] data, byte[] buffer, CancellationToken token) where T : struct
        {
            writer.Write(data.Length);
            TransferArray(data, buffer, token, (at, bytes) => writer.Write(buffer, 0, bytes));
        }

        static T[] ReadArray<T>(BinaryReader reader, int count, byte[] buffer, CancellationToken token) where T : struct
        {
            if (reader.ReadInt32() != count) throw new InvalidDataException("Invalid spherical interaction array length.");
            var data = new T[count];
            TransferArray(data, buffer, token, (at, bytes) =>
            {
                int done = 0;
                while (done < bytes)
                {
                    int received = reader.Read(buffer, done, bytes - done);
                    if (received == 0) throw new EndOfStreamException();
                    done += received;
                }
            }, true);
            return data;
        }

        static void TransferArray<T>(T[] data, byte[] buffer, CancellationToken token,
            Action<int, int> transfer, bool reading = false) where T : struct
        {
            int length = checked(data.Length * Marshal.SizeOf<T>());
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                IntPtr address = handle.AddrOfPinnedObject();
                for (int at = 0; at < length; at += buffer.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int bytes = Math.Min(buffer.Length, length - at);
                    IntPtr start = IntPtr.Add(address, at);
                    if (!reading) Marshal.Copy(start, buffer, 0, bytes);
                    transfer(at, bytes);
                    if (reading) Marshal.Copy(buffer, 0, start, bytes);
                }
            }
            finally { handle.Free(); }
        }

        static ulong HashOwners(ushort[] owners)
        {
            ulong hash = 14695981039346656037ul;
            foreach (ushort owner in owners) hash = HashValue(hash, owner);
            return hash;
        }

        static ulong HashTopology(SphericalWorld world, CancellationToken token)
        {
            ulong hash = HashValue(14695981039346656037ul, world.Count);
            // Hash once on the worker, never in Update. Including all plane
            // inputs and the land mask detects both topology and authoring edits.
            for (int id = 0; id < world.Count; id++)
            {
                if ((id & 4095) == 0) token.ThrowIfCancellationRequested();
                hash = HashDirection(hash, world.Centers[id]);
                hash = HashValue(hash, world.Cells[id].Water ? 0 : 1);
                hash = HashValue(hash, world.Corners[id].Length);
                for (int e = 0; e < world.Corners[id].Length; e++)
                {
                    hash = HashDirection(hash, world.Corners[id][e]);
                    hash = HashValue(hash, world.Neighbors[id][e]);
                }
            }
            return hash;
        }

        static ulong HashDirection(ulong hash, Vector3 value) =>
            HashValue(HashValue(HashValue(hash, value.x.GetHashCode()), value.y.GetHashCode()), value.z.GetHashCode());
        static ulong HashValue(ulong hash, int value) => unchecked((hash ^ (uint)value) * 1099511628211ul);
    }
}
