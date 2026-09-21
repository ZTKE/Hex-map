namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        /// <summary>Validate worker-owned arrays before a Unity mesh is created.</summary>
        static int ValidateBuffer(MeshBuffer data)
        {
            if (data == null) return 1;
            int errors = 0, count = data.Vertices.Count;
            if (data.Normals.Count != count) errors++;
            if (data.Colors.Count != count) errors++;
            if (data.UV0.Count != count) errors++;
            if (data.UV1.Count != count) errors++;
            if (data.UV2.Count != count) errors++;
            if (data.UV3.Count != count) errors++;
            if (data.UV4.Count != 0 && data.UV4.Count != count) errors++;
            if (data.UV5.Count != 0 && data.UV5.Count != count) errors++;
            if (data.PlateauHeights.Count != 0 && data.PlateauHeights.Count != count) errors++;
            foreach (float height in data.PlateauHeights) if (!Finite(height)) errors++;
            foreach (var p in data.Vertices)
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) errors++;
            foreach (var n in data.Normals)
            {
                float squared = n.x * n.x + n.y * n.y + n.z * n.z;
                if (!Finite(n.x) || !Finite(n.y) || !Finite(n.z) || !Finite(squared) || squared < .000001f) errors++;
            }
            foreach (var c in data.Colors)
                if (!Finite(c.r) || !Finite(c.g) || !Finite(c.b) || !Finite(c.a)) errors++;
            errors += InvalidUV(data.UV0); errors += InvalidUV(data.UV1);
            errors += InvalidUV(data.UV2); errors += InvalidUV(data.UV3);
            errors += InvalidUV(data.UV4); errors += InvalidUV(data.UV5);
            foreach (int index in data.Indices) if (index < 0 || index >= count) errors++;
            return errors;
        }

        static int InvalidUV(System.Collections.Generic.List<UnityEngine.Vector4> values)
        {
            int errors = 0;
            foreach (var p in values) if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(p.w)) errors++;
            return errors;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Worker arrays retain four UV channels (104 bytes/vertex); terrain adds
        // two material channels (136). Streaming subtracts the unused water UVs
        // and packs Color32 for its 60-byte GPU layout. Indices add four bytes.
        // Native allocator/driver overhead is excluded from this estimate.
        static long BufferPayloadBytes(MeshBuffer data) => data == null ? 0 :
            104L * data.Vertices.Count + 16L * (data.UV4.Count + data.UV5.Count) + 4L * data.Indices.Count;

        // Working lists can be larger than their payload. Account for capacities
        // when budgeting workers awaiting publication, before their arrays are GC'd.
        static long BufferAllocatedBytes(MeshBuffer data) => data == null ? 0 :
            12L * (data.Vertices.Capacity + data.Normals.Capacity) +
            16L * (data.Colors.Capacity + data.UV0.Capacity + data.UV1.Capacity + data.UV2.Capacity + data.UV3.Capacity + data.UV4.Capacity + data.UV5.Capacity) +
            4L * (data.Indices.Capacity + data.PlateauHeights.Capacity);
    }
}
