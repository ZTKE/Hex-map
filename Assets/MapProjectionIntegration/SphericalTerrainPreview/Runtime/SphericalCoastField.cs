using System;
using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// HF's authored height/mixer coast, evaluated in each real sphere cell's
    /// tangent frame. No flat-grid offsets, six-neighbor assumption or texture
    /// reads survive into worker jobs. The Game_2 terrain style is the input.
    /// </summary>
    public sealed class SphericalCoastField
    {
        public sealed class ScalarStamp
        {
            readonly int width, height;
            readonly byte[] pixels;
            public int ByteCount => pixels.Length;
            public bool IsConstant { get; }
            public float Constant => pixels[0] * (1f / 255f);

            public ScalarStamp(int width, int height, byte[] red)
            {
                if (width < 1 || height < 1 || red == null || red.Length != width * height)
                    throw new ArgumentException("An HF coast stamp must contain one byte per texel.");
                this.width = width; this.height = height; pixels = (byte[])red.Clone();
                IsConstant = true;
                for (int i = 1; i < pixels.Length; i++)
                    if (pixels[i] != pixels[0]) { IsConstant = false; break; }
            }

            // A cubic B-spline keeps the authored image and hardware pixel-center
            // convention while giving the radial field a continuous derivative.
            // Bilinear sampling had a normal kink at every original mixer texel.
            public float Sample(Vector2 uv)
            {
                if (IsConstant) return Constant;
                float x = uv.x * width - .5f, y = uv.y * height - .5f;
                int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
                Vector4 bx = Basis(x - ix), by = Basis(y - iy);
                float value = 0;
                for (int row = 0; row < 4; row++)
                {
                    int offset = Mathf.Clamp(iy + row - 1, 0, height - 1) * width;
                    float line = 0;
                    for (int col = 0; col < 4; col++)
                        line += pixels[offset + Mathf.Clamp(ix + col - 1, 0, width - 1)] * bx[col];
                    value += line * by[row];
                }
                return value * (1f / 255f);
            }

            static Vector4 Basis(float t)
            {
                float t2 = t * t, t3 = t2 * t, a = 1 - t;
                return new Vector4(a * a * a, 3 * t3 - 6 * t2 + 4,
                    -3 * t3 + 3 * t2 + 3 * t + 1, t3) * (1f / 6f);
            }
        }

        public sealed class Snapshot
        {
            internal readonly ScalarStamp[] Heights, Mixers;
            public readonly float StampScale, HeightScale;
            public int ByteCount { get; }
            public Snapshot(ScalarStamp[] heights, ScalarStamp[] mixers, float stampScale, float heightScale)
            {
                if (heights == null || mixers == null || heights.Length != 6 || mixers.Length != 6 ||
                    stampScale <= 0 || heightScale <= 0)
                    throw new ArgumentException("HF coast input needs its six authored height/mixer panels and positive scales.");
                Heights = (ScalarStamp[])heights.Clone(); Mixers = (ScalarStamp[])mixers.Clone();
                StampScale = stampScale; HeightScale = heightScale;
                var unique = new HashSet<ScalarStamp>();
                for (int i = 0; i < 6; i++)
                {
                    if (Heights[i] == null || Mixers[i] == null) throw new ArgumentException("An HF coast panel is missing.");
                    unique.Add(Heights[i]); unique.Add(Mixers[i]);
                }
                foreach (var stamp in unique) ByteCount += stamp.ByteCount;
            }
        }

        readonly SphericalWorld world;
        readonly Snapshot input;
        readonly Vector3[] east, north;
        readonly Vector2[] rotation;
        readonly int[] panels;
        readonly float projectionScale, inverseStampScale, supportSquared;
        public int SnapshotByteCount => input.ByteCount;

        public SphericalCoastField(SphericalWorld world, Snapshot input)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            projectionScale = world.Radius / world.CellRadius;
            inverseStampScale = 1 / (2 * input.StampScale);
            // Chord length includes a tiny radial component which the tangent
            // UV drops. Keep the cheap rejection outside the square's corners.
            supportSquared = 2 * input.StampScale * input.StampScale * 1.001f;
            east = new Vector3[world.Count]; north = new Vector3[world.Count];
            rotation = new Vector2[world.Count]; panels = new int[world.Count];
            for (int id = 0; id < world.Count; id++)
            {
                SphericalSurface.Frame(world.Centers[id], out east[id], out north[id]);
                float angle = SphericalLandformArt.Rotation(world.Cells[id].TerrainRotation);
                rotation[id] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                var cell = world.Cells[id];
                panels[id] = cell.Water ? 5 : cell.SourceLandform == 2 ? 4 : cell.SourceLandform == 1 ? 3 :
                    cell.Biome == 0 || cell.Biome == 3 ? 0 : cell.Biome == 2 ? 2 : 1;
            }
        }

        /// <returns>X = HF radial height relative to sea datum; Y = authored dry-land weight.</returns>
        public Vector2 Evaluate(Vector3 direction, int[] candidates)
        {
            // The known-nearby sphere graph is shared with the terrain evaluator.
            // The square's diagonal fits inside 2.263 cell radii at HF scale 1.6;
            // all three rings are gathered before evaluating any root cell.
            bool dry = false, wet = false;
            foreach (int id in candidates)
            {
                Vector3 delta = (direction - world.Centers[id]) * projectionScale;
                if (delta.sqrMagnitude > supportSquared) continue;
                if (world.Cells[id].Water) wet = true; else dry = true;
                if (wet && dry) break;
            }
            // Exactly preserve the current mountain/plain art away from water.
            if (!wet) return new Vector2(.018f, 1);
            if (!dry && input.Heights[5].IsConstant)
                return new Vector2(Displace(input.Heights[5].Constant), 0);

            float mixerWeight = 0, fillWeight = 0, mixerHeight = 0, fillHeight = 0;
            float mixerDry = 0, fillDry = 0, maximumPower = 0, maximumWeightedPower = 0;
            foreach (int id in candidates)
            {
                Vector3 delta = (direction - world.Centers[id]) * projectionScale;
                if (delta.sqrMagnitude > supportSquared) continue;
                float px = Vector3.Dot(delta, east[id]), py = Vector3.Dot(delta, north[id]);
                Vector2 r = rotation[id];
                Vector2 uv = new((px * r.x + py * r.y) * inverseStampScale + .5f,
                    (-px * r.y + py * r.x) * inverseStampScale + .5f);
                float centralization = Centralization(uv);
                if (centralization <= 0) continue;
                int panel = panels[id];
                float mixer = input.Mixers[panel].Sample(uv) * centralization;
                float height = input.Heights[panel].Sample(uv);
                // HF fills the weight absent from the strongest local mixer.
                // A symmetric high-order weighted maximum retains that rule
                // without a hard winner/normal change where two stamps meet.
                float power = mixer * mixer; power *= power; power *= power; power *= power;
                maximumPower += power; maximumWeightedPower += power * mixer;
                mixerWeight += mixer; fillWeight += centralization;
                mixerHeight += height * mixer; fillHeight += height * centralization;
                if (!world.Cells[id].Water) { mixerDry += mixer; fillDry += centralization; }
            }
            float maximum = maximumWeightedPower / Mathf.Max(1e-30f, maximumPower);
            float missing = 1 - maximum;
            float weight = mixerWeight + fillWeight * missing;
            if (weight <= 1e-12f)
                throw new InvalidOperationException("Sphere coast neighborhood did not cover the HF stamp support.");
            float hfHeight = (mixerHeight + fillHeight * missing) / weight;
            float land = (mixerDry + fillDry * missing) / weight;
            return new Vector2(Displace(hfHeight), Mathf.Clamp01(land));
        }

        float Displace(float height)
        {
            float h = (height - .5f) * input.HeightScale;
            // Match HF's 0.6 submerged displacement, rounding its zero-height
            // derivative change over just ten centimetres around the datum.
            return h * Mathf.Lerp(.6f, 1, SphericalSurface.Smooth(-.05f, .05f, h)) + .018f;
        }

        public static float Centralization(Vector2 uv)
        {
            // HF uses a square plateau with a one-third-width fade to each edge.
            // Separable smooth ramps preserve that support and plateau, with
            // zero derivatives at its border and no diagonal max() crease.
            float x = Mathf.Clamp01(3 * (1 - 2 * Mathf.Abs(uv.x - .5f)));
            float y = Mathf.Clamp01(3 * (1 - 2 * Mathf.Abs(uv.y - .5f)));
            return x * x * (3 - 2 * x) * y * y * (3 - 2 * y);
        }

        #region MainThreadTextureCapture
        public SphericalCoastField(SphericalWorld world, HexTerrainStyle style) : this(world, Capture(style)) { }

        public static Snapshot Capture(HexTerrainStyle style)
        {
            if (!style || !style.HasHFOriginalTerrainSet())
                throw new ArgumentException("The sphere coast needs the complete Game_2 HF original terrain style.");
            int heightMip = Mathf.Clamp(style.hfOriginalHeightLod, 0, 12);
            var cache = new Dictionary<(int, int), ScalarStamp>();
            ScalarStamp Read(Texture2D texture, int mip)
            {
                var key = (texture.GetInstanceID(), mip);
                if (!cache.TryGetValue(key, out ScalarStamp stamp))
                { stamp = CaptureTexture(texture, mip); cache.Add(key, stamp); }
                return stamp;
            }
            ScalarStamp common = Read(style.hfCommonHeight, heightMip);
            return new Snapshot(new[] { Read(style.hfDirtHeight, heightMip), common, common,
                    Read(style.hfHillHeight, heightMip), Read(style.hfMountainHeight, heightMip), Read(style.hfSeaHeight, heightMip) },
                new[] { Read(style.hfDirtMixer, 0), Read(style.hfPlainsMixer, 0), Read(style.hfMarshMixer, 0),
                    Read(style.hfHillMixer, 0), Read(style.hfMountainMixer, 0), Read(style.hfSeaMixer, 0) },
                style.hfOriginalStampScale, style.hfOriginalHeightScale);
        }

        static ScalarStamp CaptureTexture(Texture2D source, int mip)
        {
            int width = Mathf.Max(1, source.width >> mip), height = Mathf.Max(1, source.height >> mip);
            bool r8 = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) && SystemInfo.SupportsTextureFormat(TextureFormat.R8);
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0,
                r8 ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            target.filterMode = FilterMode.Bilinear;
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                // This is the same import/mip/readback path as HexSurfaceSampler.
                // Mixers retain mip zero so their broken shore contours survive.
                Graphics.Blit(source, target); RenderTexture.active = target;
                readable = new Texture2D(width, height, r8 ? TextureFormat.R8 : TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0); readable.Apply(false, false);
                var raw = readable.GetRawTextureData<byte>();
                byte[] red = new byte[width * height]; int stride = r8 ? 1 : 4;
                for (int i = 0; i < red.Length; i++) red[i] = raw[i * stride];
                return new ScalarStamp(width, height, red);
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
                if (readable)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(readable);
                    else UnityEngine.Object.DestroyImmediate(readable);
                }
            }
        }
        #endregion
    }
}
