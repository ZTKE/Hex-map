using System;
using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>One continuous normal field across the rounded plateau shoulder.
    /// Face-by-face riser classification created serrated lighting and material
    /// islands on dunes. Preserve the shared topology and complete guard normals.</summary>
    public static class SphericalTerraceNormals
    {
        public static int Calculate(List<Vector3> positions, List<int> indices,
            List<SphericalMeshNormals.Key> keys, List<Vector3> normals,
            List<float> plateauHeights, List<Vector4> terrain, List<Color> colors,
            float stepHeight, Func<int, Vector3, int> duplicate)
        {
            if (plateauHeights.Count != positions.Count || terrain.Count != positions.Count || colors.Count != positions.Count)
                throw new ArgumentException("Plateau attributes must match the terrain vertices.");
            SphericalMeshNormals.Calculate(positions, indices, keys, normals);
            return 0;
        }
    }
}