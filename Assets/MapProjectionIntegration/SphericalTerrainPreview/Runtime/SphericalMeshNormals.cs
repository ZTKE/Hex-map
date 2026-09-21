using System;
using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>Topological welds avoid three extra height-field queries per vertex.
    /// A one-cell geometry guard supplies every face incident to a published edge.</summary>
    public static class SphericalMeshNormals
    {
        public readonly struct Key : IEquatable<Key>
        {
            public readonly int A, B, T;
            public Key(int a, int b, int t) { A = a; B = b; T = t; }
            public bool Shared => A != int.MinValue;
            public bool Equals(Key other) => A == other.A && B == other.B && T == other.T;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() => unchecked(A * 73856093 ^ B * 19349663 ^ T * 83492791);
        }

        public static Key VertexKey(int cell, int a, int b, int row, int col, int subdivisions)
        {
            if (row == 0) return new Key(-cell - 1, -cell - 1, 0);
            if (row == subdivisions)
            {
                if (col == 0) return new Key(a, a, 0);
                if (col == row) return new Key(b, b, 0);
                return a < b ? new Key(a, b, col) : new Key(b, a, subdivisions - col);
            }
            if (col == 0) return new Key(-cell - 1, a, row);
            if (col == row) return new Key(-cell - 1, b, row);
            return new Key(int.MinValue, 0, 0);
        }

        public static void Calculate(List<Vector3> positions, List<int> indices, List<Key> keys, List<Vector3> normals)
        {
            if (positions.Count != keys.Count || normals.Count != positions.Count)
                throw new ArgumentException("Normal weld keys must match the generated vertices.");
            // The original radial normals are no longer needed. Reuse their
            // storage until all shared sums have been collected, then normalize.
            var sums = ReferenceEquals(positions, normals) ? new List<Vector3>(normals) : normals;
            for (int i = 0; i < sums.Count; i++) sums[i] = Vector3.zero;
            for (int i = 0; i < indices.Count; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                sums[a] += normal; sums[b] += normal; sums[c] += normal;
            }
            var shared = new Dictionary<Key, Vector3>(positions.Count / 5);
            for (int i = 0; i < sums.Count; i++)
            {
                Key key = keys[i]; if (!key.Shared) continue;
                shared.TryGetValue(key, out Vector3 sum); shared[key] = sum + sums[i];
            }
            for (int i = 0; i < sums.Count; i++)
            {
                Vector3 value = keys[i].Shared ? shared[keys[i]] : sums[i];
                normals[i] = value.sqrMagnitude > 1e-12f ? value.normalized : positions[i].normalized;
            }
        }
    }
}
