using System;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Published-detail coverage in the camera region's tangent frame. Rasterization
    /// is CPU-only; Texture creation, Upload and Dispose belong to the main thread.
    /// Neighbor guard geometry covers the bilinear rim outside the published cells.
    /// </summary>
    public sealed class SphericalDetailCoverage : IDisposable
    {
        readonly byte[] pixels;
        // R is the stable tile key + 1 (zero means no published owner), G is
        // that same texel's coverage. Point loads preserve exact integer IDs.
        readonly Vector2[] owners;
        readonly int size;
        readonly float worldSize;
        Vector3 focus, east, north;
        bool dirty, disposed;
        public Texture2D Texture { get; }
        public Texture2D OwnerTexture { get; }
        public int Size => size;
        public float WorldSize => worldSize;

        public SphericalDetailCoverage(int size = 1024, float worldSize = 1024)
        {
            if (size < 4 || size > 8192) throw new ArgumentOutOfRangeException(nameof(size));
            if (!(worldSize > 0)) throw new ArgumentOutOfRangeException(nameof(worldSize));
            this.size = size; this.worldSize = worldSize;
            pixels = new byte[size * size];
            owners = new Vector2[size * size];
            Texture = new Texture2D(size, size, TextureFormat.R8, false, true) {
                name = "Spherical published detail coverage", hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 0
            };
            OwnerTexture = new Texture2D(size, size, TextureFormat.RGFloat, false, true) {
                name = "Spherical published ocean owners", hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, anisoLevel = 0
            };
            focus = Vector3.up; east = Vector3.right; north = Vector3.forward;
            dirty = true;
            Upload();
        }

        public void SetFrame(Vector3 focus, Vector3 east, Vector3 north, bool clear = true)
        {
            this.focus = focus; this.east = east; this.north = north;
            if (clear) BeginFrame();
        }

        public void BeginFrame(bool clear = true)
        {
            ThrowIfDisposed();
            if (!clear) return;
            Array.Clear(pixels, 0, pixels.Length);
            Array.Clear(owners, 0, owners.Length);
            dirty = true;
        }

        public void AddCell(Vector3 center, Vector3[] corners, float radius, byte weight, int ownerKey = -1)
        {
            ThrowIfDisposed();
            if (ownerKey < -1 || ownerKey > 16777214) throw new ArgumentOutOfRangeException(nameof(ownerKey));
            if (weight == 0 || corners == null || corners.Length < 3 || !(radius > 0) ||
                Vector3.Dot(center, focus) <= 0) return;
            float scale = radius * size / worldSize;
            Vector2 middle = Project(center, scale);
            Vector3 first = corners[0];
            for (int edge = 0; edge < corners.Length; edge++)
            {
                Vector3 second = corners[(edge + 1) % corners.Length];
                if (Vector3.Dot(first, focus) > 0 && Vector3.Dot(second, focus) > 0)
                    Triangle(middle, Project(first, scale), Project(second, scale), weight, ownerKey + 1);
                first = second;
            }
        }

        Vector2 Project(Vector3 direction, float scale)
        {
            Vector3 delta = direction - focus;
            // Integer raster coordinates are texture pixel centers, matching
            // shader uv = dot(direction-focus, tangent)*radius/worldSize + .5.
            float origin = size * .5f - .5f;
            return new Vector2(Vector3.Dot(delta, east) * scale + origin,
                Vector3.Dot(delta, north) * scale + origin);
        }

        void Triangle(Vector2 a, Vector2 b, Vector2 c, byte weight, int owner)
        {
            if (a.y > b.y) (a, b) = (b, a);
            if (b.y > c.y) (b, c) = (c, b);
            if (a.y > b.y) (a, b) = (b, a);
            float height = c.y - a.y;
            if (height < .000001f) return;
            const float tolerance = .0001f;
            int firstRow = Mathf.Max(0, Mathf.CeilToInt(a.y - tolerance));
            int lastRow = Mathf.Min(size - 1, Mathf.FloorToInt(c.y + tolerance));
            if (lastRow < firstRow) return;
            float longSlope = (c.x - a.x) / height;
            float lowHeight = b.y - a.y, highHeight = c.y - b.y;
            float lowSlope = lowHeight > .000001f ? (b.x - a.x) / lowHeight : 0;
            float highSlope = highHeight > .000001f ? (c.x - b.x) / highHeight : 0;
            for (int row = firstRow; row <= lastRow; row++)
            {
                float left = a.x + (row - a.y) * longSlope;
                float right = row < b.y || highHeight <= .000001f
                    ? a.x + (row - a.y) * lowSlope : b.x + (row - b.y) * highSlope;
                if (left > right) (left, right) = (right, left);
                int firstColumn = Mathf.Max(0, Mathf.CeilToInt(left - tolerance));
                int lastColumn = Mathf.Min(size - 1, Mathf.FloorToInt(right + tolerance));
                int offset = row * size;
                for (int col = firstColumn; col <= lastColumn; col++)
                {
                    int pixel = offset + col;
                    if (pixels[pixel] > weight) continue;
                    if (pixels[pixel] == weight)
                    {
                        // Shared raster-edge pixels have a stable owner even
                        // when asynchronous completion changes insertion order.
                        if (owner == 0 || (owners[pixel].x > 0 && owners[pixel].x <= owner)) continue;
                    }
                    pixels[pixel] = weight;
                    owners[pixel] = new Vector2(owner, weight * (1f / 255f));
                    dirty = true;
                }
            }
        }

        public void Upload()
        {
            ThrowIfDisposed();
            if (!dirty) return;
            Texture.SetPixelData(pixels, 0);
            OwnerTexture.SetPixelData(owners, 0);
            Texture.Apply(false, false);
            OwnerTexture.Apply(false, false);
            dirty = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Application.isPlaying) { UnityEngine.Object.Destroy(Texture); UnityEngine.Object.Destroy(OwnerTexture); }
            else { UnityEngine.Object.DestroyImmediate(Texture); UnityEngine.Object.DestroyImmediate(OwnerTexture); }
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(SphericalDetailCoverage));
        }
    }
}
