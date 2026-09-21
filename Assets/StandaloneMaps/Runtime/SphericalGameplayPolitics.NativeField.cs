using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ZTKE.HexMap.Standalone
{
    public sealed partial class SphericalGameplayPolitics
    {
        sealed class TopologyRaster
        {
            public Vector4[] Edges;
            public int[] Cells;
            public byte[] Core, Land;
            public Color32[][] Seed;
            public FieldResult InitialField;
            public bool LoadedFromDisk;
            public string CacheWarning;
            public double PreparationMilliseconds;
        }
        sealed class FieldResult { public Color32[][] Pixels; }

        static TopologyRaster BuildTopology(WW2.SphericalTerrainPreview.SphericalWorld world, CancellationToken token)
        {
            var result = new TopologyRaster { Edges = new Vector4[world.Count * 6], Cells = new int[FieldWidth * FieldHeight],
                Core = new byte[FieldWidth * FieldHeight], Land = new byte[world.Count], Seed = NewStripes(SeedWidth, SeedHeight) };
            var centerDistances = new float[world.Count * 6];
            for (int id = 0; id < world.Count; id++)
            {
                if ((id & 4095) == 0) token.ThrowIfCancellationRequested();
                result.Land[id] = world.Cells[id].Water ? (byte)0 : (byte)255;
                var corners = world.Corners[id];
                for (int e = 0; e < 6; e++)
                {
                    int at = id * 6 + e;
                    if (e >= corners.Length) { result.Edges[at] = new Vector4(0, 0, 0, -1); continue; }
                    Vector3 plane = InwardPlane(corners[e], corners[(e + 1) % corners.Length]);
                    result.Edges[at] = new Vector4(plane.x, plane.y, plane.z, world.Neighbors[id][e]);
                    centerDistances[at] = Math.Max(Vector3.Dot(plane, world.Centers[id]), 1e-7f);
                }
            }
            // These resources and all called topology methods are immutable and
            // managed. Limit parallelism so streaming workers retain CPU capacity.
            var longitudeCos = new float[FieldWidth]; var longitudeSin = new float[FieldWidth];
            for (int x = 0; x < FieldWidth; x++)
            {
                float longitude = ((x + .5f) / FieldWidth - .5f) * (2 * Mathf.PI);
                longitudeCos[x] = Mathf.Cos(longitude); longitudeSin[x] = Mathf.Sin(longitude);
            }
            var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 };
            Parallel.For(0, FieldHeight, options, y =>
            {
                float latitude = ((y + .5f) / FieldHeight - .5f) * Mathf.PI;
                float sin = Mathf.Sin(latitude), cos = Mathf.Cos(latitude); int hint = -1;
                for (int x = 0; x < FieldWidth; x++)
                {
                    Vector3 direction = new Vector3(cos * longitudeCos[x], sin, cos * longitudeSin[x]);
                    int cell = hint = world.FindContainingCell(direction, hint), at = y * FieldWidth + x;
                    result.Cells[at] = cell;
                    float core = 1;
                    for (int e = 0; e < world.Corners[cell].Length; e++)
                    {
                        Vector4 p = result.Edges[cell * 6 + e];
                        core = Math.Min(core, (p.x * direction.x + p.y * direction.y + p.z * direction.z) / centerDistances[cell * 6 + e]);
                    }
                    result.Core[at] = (byte)(Mathf.Clamp01(core) * 255);
                    if ((x & 1) != 0 && (y & 1) != 0)
                    {
                        int sx = x >> 1, sy = y >> 1;
                        result.Seed[sy / UploadRows][(sy % UploadRows) * SeedWidth + sx] =
                            new Color32((byte)cell, (byte)(cell >> 8), (byte)(cell >> 16), 255);
                    }
                }
            });
            // Explicit center constraints survive even an adversarial pattern
            // of single-cell enclaves. A small cell's closest atlas sample can
            // lie outside the fractional core threshold used above.
            for (int id = 0; id < world.Count; id++)
            {
                if ((id & 4095) == 0) token.ThrowIfCancellationRequested();
                Vector3 n = world.Centers[id];
                float u = Mathf.Atan2(n.z, n.x) / (2 * Mathf.PI) + .5f; u -= Mathf.Floor(u);
                float v = Mathf.Asin(Mathf.Clamp(n.y, -1, 1)) / Mathf.PI + .5f;
                int x = Math.Max(0, Math.Min(FieldWidth - 1, (int)(u * FieldWidth)));
                int y = Math.Max(0, Math.Min(FieldHeight - 1, (int)(v * FieldHeight)));
                int at = y * FieldWidth + x;
                if (result.Cells[at] != id) throw new InvalidOperationException("Country field cannot resolve native cell center " + id);
                result.Core[at] = 255;
            }
            return result;
        }

        static Vector3 InwardPlane(Vector3 a, Vector3 b)
        {
            // Neighboring R5 corners are almost parallel. Float cross products
            // lose several decimal places before normalization and shift the
            // edge. Match FindContainingCell's double arithmetic, then store
            // the resulting unit plane in the compact GPU float buffer.
            double x = (double)a.y * b.z - (double)a.z * b.y;
            double y = (double)a.z * b.x - (double)a.x * b.z;
            double z = (double)a.x * b.y - (double)a.y * b.x;
            double length = Math.Sqrt(x * x + y * y + z * z);
            return new Vector3((float)(x / length), (float)(y / length), (float)(z / length));
        }

        static FieldResult BuildField(TopologyRaster topology, ushort[] owners, CancellationToken token)
        {
            int length = FieldWidth * FieldHeight;
            var raw = new ushort[length]; var smooth = new ushort[length]; var distance = new byte[length];
            for (int i = 0; i < length; i++)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                int id = topology.Cells[i]; raw[i] = topology.Land[id] > 0 ? owners[id] : (ushort)0;
            }
            // The reference smooths categorical country masks and protects cell
            // cores. At half atlas resolution this kernel has the same scale;
            // smoothing never alters the authoritative native owners or ID seed.
            int[] weight = { 1, 4, 7, 10, 7, 4, 1 };
            ushort[] labels = new ushort[49]; int[] votes = new int[49];
            for (int y = 0; y < FieldHeight; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < FieldWidth; x++)
                {
                    int at = y * FieldWidth + x; ushort own = raw[at]; smooth[at] = own;
                    if (own == 0 || topology.Core[at] >= 160) continue;
                    bool boundary = false;
                    for (int d = -2; d <= 2 && !boundary; d++)
                    {
                        int xx = (x + d + FieldWidth) % FieldWidth;
                        int yy = Math.Max(0, Math.Min(FieldHeight - 1, y + d));
                        boundary = raw[y * FieldWidth + xx] != own || raw[yy * FieldWidth + x] != own;
                    }
                    if (!boundary) continue;
                    int count = 0;
                    for (int oy = -3; oy <= 3; oy++)
                    for (int ox = -3; ox <= 3; ox++)
                    {
                        int xx = (x + ox + FieldWidth) % FieldWidth;
                        int yy = Math.Max(0, Math.Min(FieldHeight - 1, y + oy));
                        ushort country = raw[yy * FieldWidth + xx];
                        if (country == 0) continue; // political washes cannot grow across a coast
                        int slot = 0; while (slot < count && labels[slot] != country) slot++;
                        if (slot == count) { labels[count] = country; votes[count++] = 0; }
                        votes[slot] += weight[ox + 3] * weight[oy + 3];
                    }
                    int best = -1;
                    for (int i = 0; i < count; i++)
                        if (votes[i] > best || (votes[i] == best && labels[i] == own)) { best = votes[i]; smooth[at] = labels[i]; }
                }
            }
            // A bounded chamfer distance gives smooth anti-aliased screen-width
            // strokes. Longitude wraps; geographic north/south are clamped.
            for (int y = 0; y < FieldHeight; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < FieldWidth; x++)
                {
                    int at = y * FieldWidth + x; ushort id = smooth[at];
                    bool edge = id != 0 && (DifferentLand(id, smooth[y * FieldWidth + (x + 1) % FieldWidth]) ||
                        DifferentLand(id, smooth[y * FieldWidth + (x + FieldWidth - 1) % FieldWidth]) ||
                        (y > 0 && DifferentLand(id, smooth[at - FieldWidth])) ||
                        (y + 1 < FieldHeight && DifferentLand(id, smooth[at + FieldWidth])));
                    distance[at] = edge ? (byte)0 : (byte)96;
                }
            }
            // Two cycles propagate across the horizontal seam from either side.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < FieldHeight; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < FieldWidth; x++)
                    {
                        int at = y * FieldWidth + x, left = y * FieldWidth + (x + FieldWidth - 1) % FieldWidth;
                        int d = Math.Min(distance[at], distance[left] + 3);
                        if (y > 0) d = Math.Min(d, Math.Min(distance[at - FieldWidth] + 3, distance[left - FieldWidth] + 4));
                        distance[at] = (byte)Math.Min(d, 96);
                    }
                }
                for (int y = FieldHeight - 1; y >= 0; y--)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = FieldWidth - 1; x >= 0; x--)
                    {
                        int at = y * FieldWidth + x, right = y * FieldWidth + (x + 1) % FieldWidth;
                        int d = Math.Min(distance[at], distance[right] + 3);
                        if (y + 1 < FieldHeight) d = Math.Min(d, Math.Min(distance[at + FieldWidth] + 3, distance[right + FieldWidth] + 4));
                        distance[at] = (byte)Math.Min(d, 96);
                    }
                }
            }
            var result = new FieldResult { Pixels = NewStripes(FieldWidth, FieldHeight) };
            for (int y = 0; y < FieldHeight; y++)
            {
                token.ThrowIfCancellationRequested(); var stripe = result.Pixels[y / UploadRows];
                for (int x = 0; x < FieldWidth; x++)
                {
                    int at = y * FieldWidth + x; ushort id = smooth[at];
                    stripe[(y % UploadRows) * FieldWidth + x] = new Color32((byte)id, (byte)(id >> 8),
                        (byte)(distance[at] * 255 / 96), topology.Land[topology.Cells[at]]);
                }
            }
            return result;
        }
        static bool DifferentLand(ushort a, ushort b) => b != 0 && a != b;
        static Color32[][] NewStripes(int width, int height)
        {
            var result = new Color32[(height + UploadRows - 1) / UploadRows][];
            for (int i = 0; i < result.Length; i++) result[i] = new Color32[width * Math.Min(UploadRows, height - i * UploadRows)];
            return result;
        }

        sealed class TextureUpload : IDisposable
        {
            readonly int width, height;
            readonly int stripesPerStep;
            Color32[][] stripes;
            Color32[] combinedPixels;
            int stripe;
            RenderTexture target;
            Texture2D staging;
            public TextureUpload(int width, int height, Color32[][] pixels, string name, int stripesPerStep = 1)
            {
                this.width = width; this.height = height; stripes = pixels;
                this.stripesPerStep = Math.Max(1, stripesPerStep);
                target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = name, hideFlags = HideFlags.DontSave, useMipMap = false, autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
                target.Create();
                staging = new Texture2D(width, Math.Min(height, UploadRows * this.stripesPerStep), TextureFormat.RGBA32, false, true)
                { name = name + " transfer stripe", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Point };
            }
            public bool Step()
            {
                if (stripe >= stripes.Length) return true;
                int count = Math.Min(stripesPerStep, stripes.Length - stripe);
                int rows = Math.Min(UploadRows * count, height - stripe * UploadRows);
                if (staging.height != rows) staging.Reinitialize(width, rows);
                Color32[] pixels = stripes[stripe];
                if (count > 1)
                {
                    if (combinedPixels == null || combinedPixels.Length != width * rows) combinedPixels = new Color32[width * rows];
                    CopyUploadStripes(stripes, stripe, count, combinedPixels); pixels = combinedPixels;
                }
                staging.SetPixels32(pixels); staging.Apply(false, false);
                Graphics.CopyTexture(staging, 0, 0, 0, 0, width, rows, target, 0, 0, 0, stripe * UploadRows);
                for (int i = 0; i < count; i++) stripes[stripe + i] = null;
                stripe += count;
                return stripe == stripes.Length;
            }
            public RenderTexture Take() { var result = target; target = null; return result; }
            public void Dispose()
            {
                if (target) UnityEngine.Object.Destroy(target);
                if (staging) UnityEngine.Object.Destroy(staging);
                target = null; staging = null; stripes = null; combinedPixels = null;
            }
        }

        static void CopyUploadStripes(Color32[][] source, int first, int count, Color32[] destination)
        {
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                Color32[] stripe = source[first + i];
                Array.Copy(stripe, 0, destination, offset, stripe.Length); offset += stripe.Length;
            }
            if (offset != destination.Length) throw new ArgumentException("Upload stripe rows do not match the transfer texture.");
        }
    }
}
