using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Authored near-terrain shapes evaluated in a sphere cell's tangent frame.
    /// The sphere has its own silhouette proportions; the imported summit,
    /// ribs and gullies remain aligned with their original material masks.
    /// </summary>
    public static class SphericalLandformArt
    {
        public static float Rotation(int terrainRotation)
        {
            float angle01 = Mathf.Repeat(terrainRotation / 6f + .5f, 1f);
            return Mathf.Clamp(Mathf.RoundToInt(angle01 * 63f), 0, 63) / 63f * (Mathf.PI * 2f) - Mathf.PI;
        }

        public static Vector2 MountainScale(uint hash, int mountainNeighbors, float variation)
        {
            float dense = Mathf.Clamp01((mountainNeighbors - 2) / 3f) * Mathf.Clamp01(variation);
            float peak = (hash & 255u) / 255f, footprint = ((hash >> 8) & 255u) / 255f;
            // An isolated mountain has no connecting ridge to carry its
            // silhouette. Give the authored summit more vertical presence and
            // a tighter upper mass, tapering this adjustment at range ends.
            // Scaling the whole stamp keeps all secondary crests and gullies;
            // the separate, wider foothill stamp still reaches the ground.
            float sparse = 1f - Mathf.Clamp01(mountainNeighbors / 3f);
            return new Vector2(
                Mathf.Lerp(1f, .76f + .38f * peak, dense) * Mathf.Lerp(1f, 1.12f + .06f * peak, sparse),
                Mathf.Lerp(1f, .80f + .20f * footprint, dense) * Mathf.Lerp(1f, .89f + .04f * footprint, sparse));
        }

        public static Vector2 Rotate(Vector2 p, float angle)
        {
            float s = Mathf.Sin(angle), c = Mathf.Cos(angle);
            return new Vector2(p.x * c + p.y * s, -p.x * s + p.y * c);
        }

        public static float SummitSnowRetention(Vector2 point, Vector2 peak, bool connected)
        {
            if (!connected) return 1f;
            float cap = 1f - SphericalSurface.Smooth(.20f, .65f, (point - peak).magnitude);
            return Mathf.Lerp(.06f, 1f, cap);
        }

        // Reuse the flat map's actual continuous slipface/meander implementation.
        // Longitude is periodic over an integral number of dune waves; this is
        // one field, not a differently rotated sinusoid inside every cell.
        public static float Dunes(Vector3 direction, float radiusInCells, float wavelength,
            float irregularity, Vector2 wind)
        {
            float wrap = 2f * Mathf.PI * radiusInCells;
            Vector2 position = new((Mathf.Atan2(direction.z, direction.x) + Mathf.PI) * radiusInCells,
                Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * radiusInCells);
            return HexNearDesertDunes.Evaluate(position, wrap, wavelength, irregularity, wind);
        }
    }
}
