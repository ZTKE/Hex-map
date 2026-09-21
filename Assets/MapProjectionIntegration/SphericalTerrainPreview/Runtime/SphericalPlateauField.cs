using System;
using System.IO;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Regional altitude is independent from a cell's local plain, hill or mountain.
    /// The filtered ETOPO field selects one raised platform. A world-space shoulder
    /// rounds its perimeter; local mountains and dunes remain separate relief.
    /// </summary>
    public sealed class SphericalPlateauField
    {
        readonly ushort[] metres;
        readonly int width, height;
        readonly byte[] lowPlatforms;
        readonly int lowWidth, lowHeight;
        readonly float radius;
        public float StepHeight { get; }
        public static float PlatformHeight(float authoredHeight) => Mathf.Clamp(authoredHeight * 1.2f, .84f, 6f);
        public SphericalPlateauField(SphericalWorld world, float authoredPlateauHeight)
        {
            radius = world.Radius;
            StepHeight = PlatformHeight(authoredPlateauHeight);
            TextAsset asset = Resources.Load<TextAsset>("SphericalTerrainPreview/RegionalElevation");
            if (!asset) throw new InvalidDataException("The spherical regional elevation raster is missing.");
            using var reader = new BinaryReader(new MemoryStream(asset.bytes, false));
            if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "SPHREL01")
                throw new InvalidDataException("Invalid spherical regional elevation raster.");
            width = reader.ReadInt32(); height = reader.ReadInt32();
            if (width < 4 || height < 2 || 16L + (long)width * height * 2 != reader.BaseStream.Length)
                throw new InvalidDataException("Truncated spherical regional elevation raster.");
            metres = new ushort[width * height];
            for (int i = 0; i < metres.Length; i++) metres[i] = reader.ReadUInt16();
            TextAsset lowAsset = Resources.Load<TextAsset>("SphericalTerrainPreview/LowPlateauMask");
            if (!lowAsset) throw new InvalidDataException("The spherical low plateau mask is missing.");
            using var lowReader = new BinaryReader(new MemoryStream(lowAsset.bytes, false));
            if (System.Text.Encoding.ASCII.GetString(lowReader.ReadBytes(8)) != "SPHLOW01")
                throw new InvalidDataException("Invalid spherical low plateau mask.");
            lowWidth = lowReader.ReadInt32(); lowHeight = lowReader.ReadInt32();
            if (lowWidth < 4 || lowHeight < 2 || 16L + (long)lowWidth * lowHeight != lowReader.BaseStream.Length)
                throw new InvalidDataException("Truncated spherical low plateau mask.");
            lowPlatforms = lowReader.ReadBytes(lowWidth * lowHeight);
        }
        public float Height(Vector3 direction, int hint = -1)
        {
            float elevation = SampleElevation(direction, out float gradient);
            // At least eight map units across on steep regional contours: the
            // near mesh samples the toe and shoulder instead of one giant riser.
            float halfWidth = Mathf.Sqrt(8 * 8 + gradient * gradient * 16);
            float high = RoundedStep((elevation - 800) / (2 * halfWidth) + .5f);
            float low = LowPlatformWeight(direction);
            // Union the two platform masks at the same height. The smooth
            // product keeps overlap continuous without adding another tier.
            return Mathf.Clamp01(high + (1 - high) * low) * StepHeight;
        }
        public float TerracedHeight(float elevationMetres)
        {
            return RoundedStep((elevationMetres - 640) / 320f) * StepHeight;
        }
        static float RoundedStep(float t)
        {
            t = Mathf.Clamp01(t);
            return Mathf.Clamp01(t * t * t * (t * (t * 6 - 15) + 10));
        }
        public float ElevationMetres(Vector3 n) => SampleElevation(n, out _);
        public float LowPlatformWeight(Vector3 n)
        {
            float x = (Mathf.Atan2(n.z, n.x) / (2 * Mathf.PI) + .5f) * lowWidth - .5f;
            float y = (.5f - Mathf.Asin(Mathf.Clamp(n.y, -1, 1)) / Mathf.PI) * lowHeight - .5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float tx = x - ix, ty = y - iy;
            float RowLow(int row)
            {
                int offset = Mathf.Clamp(row, 0, lowHeight - 1) * lowWidth;
                int Wrap(int column) => (column % lowWidth + lowWidth) % lowWidth;
                return Cubic(lowPlatforms[offset + Wrap(ix - 1)], lowPlatforms[offset + Wrap(ix)],
                    lowPlatforms[offset + Wrap(ix + 1)], lowPlatforms[offset + Wrap(ix + 2)], tx, out _);
            }
            return Mathf.Clamp01(Cubic(RowLow(iy - 1), RowLow(iy), RowLow(iy + 1), RowLow(iy + 2), ty, out _) / 255f);
        }
        static float Cubic(float a, float b, float c, float d, float t, out float derivative)
        {
            // Cubic B-spline is C2 across raster boundaries. Its gradient is C1,
            // so varying shoulder width cannot introduce a new normal crease.
            float p = (-a + 3 * b - 3 * c + d) / 6f, q = .5f * (a - 2 * b + c), r = .5f * (c - a);
            derivative = (3 * p * t + 2 * q) * t + r;
            return ((p * t + q) * t + r) * t + (a + 4 * b + c) / 6f;
        }
        float Row(int x, int y, float t, out float derivative)
        {
            y = Mathf.Clamp(y, 0, height - 1) * width;
            int Wrap(int column) => (column % width + width) % width;
            return Cubic(metres[y + Wrap(x - 1)], metres[y + Wrap(x)], metres[y + Wrap(x + 1)], metres[y + Wrap(x + 2)], t, out derivative);
        }
        float SampleElevation(Vector3 n, out float gradient)
        {
            float x = (Mathf.Atan2(n.z, n.x) / (2 * Mathf.PI) + .5f) * width - .5f;
            float y = (.5f - Mathf.Asin(Mathf.Clamp(n.y, -1, 1)) / Mathf.PI) * height - .5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float tx = x - ix, ty = y - iy;
            float a = Row(ix, iy - 1, tx, out float ax), b = Row(ix, iy, tx, out float bx);
            float c = Row(ix, iy + 1, tx, out float cx), d = Row(ix, iy + 2, tx, out float dx);
            float value = Cubic(a, b, c, d, ty, out float latitudeDerivative);
            float longitudeDerivative = Cubic(ax, bx, cx, dx, ty, out _);
            float meridianUnit = Mathf.PI * radius / height;
            float parallelUnit = 2 * Mathf.PI * radius / width * Mathf.Max(.04f, Mathf.Sqrt(Mathf.Max(0, 1 - n.y * n.y)));
            gradient = Mathf.Sqrt(longitudeDerivative * longitudeDerivative / (parallelUnit * parallelUnit) +
                latitudeDerivative * latitudeDerivative / (meridianUnit * meridianUnit));
            return Mathf.Max(0, value);
        }
    }
}
