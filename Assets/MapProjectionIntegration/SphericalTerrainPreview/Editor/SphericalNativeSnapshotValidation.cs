using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace WW2.SphericalTerrainPreview.Editor
{
    /// <summary>Full binary round-trip and spatial-query verification for the native world bake.</summary>
    public static class SphericalNativeSnapshotValidation
    {
        public static string ValidateRoundTrip(SphericalWorld original, string path, int recursion)
        {
            if (original == null || string.IsNullOrEmpty(original.NativeSnapshotId))
                throw new InvalidOperationException("Save the original spherical world before validating it.");
            var clock = Stopwatch.StartNew();
            var restored = SphericalWorld.ReadNativeSnapshot(path, recursion, original.Radius);
            double loadSeconds = clock.Elapsed.TotalSeconds;
            if (restored.NativeSnapshotId != original.NativeSnapshotId || restored.Count != original.Count ||
                restored.CellRadius != original.CellRadius || restored.RiverEdges.Length != original.RiverEdges.Length ||
                restored.RiverRoutes.Length != original.RiverRoutes.Length || !restored.LoadedFromNativeSnapshot)
                throw new InvalidDataException("Native spherical world metadata changed during round-trip.");

            // Re-serialization compares every stored bit: cell attributes, ordered topology,
            // triangles, all river routes, source metadata and the complete lookup index.
            string roundTrip = path + ".roundtrip";
            try
            {
                restored.SaveNativeSnapshot(roundTrip, recursion);
                if (restored.NativeSnapshotId != original.NativeSnapshotId)
                    throw new InvalidDataException("Native spherical data changed during serialization round-trip.");
            }
            finally { if (File.Exists(roundTrip)) File.Delete(roundTrip); }

            int lookups = 0;
            for (int i = 0; i < original.Count; i += Math.Max(1, original.Count / 4096))
            {
                Vector3 direction = original.Centers[i];
                if (restored.FindCell(direction) != original.FindCell(direction) ||
                    restored.FindContainingCell(direction) != original.FindContainingCell(direction))
                    throw new InvalidDataException("Native spherical selection changed at cell " + i);
                lookups++;
            }

            // Ensure a same-format but corrupted file is rejected before it can be played.
            // A header-only probe is enough to exercise the payload checksum without
            // retaining another complete world copy on disk.
            string corrupt = path + ".validation-corrupt";
            try
            {
                byte[] header = new byte[48];
                using (var source = File.OpenRead(path))
                    if (source.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("Truncated native snapshot.");
                File.WriteAllBytes(corrupt, header);
                bool rejected = false;
                try { SphericalWorld.ReadNativeSnapshot(corrupt, recursion, original.Radius); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("Native terrain accepted a truncated payload.");
            }
            finally { if (File.Exists(corrupt)) File.Delete(corrupt); }
            return $"Native terrain round-trip verified: {restored.Count:N0} cells, {restored.RiverRoutes.Length} rivers, " +
                $"{lookups:N0} selection probes; native read {loadSeconds:F3}s; {new FileInfo(path).Length:N0} bytes; " +
                $"SHA256 {original.NativeSnapshotId}.";
        }
    }
}
