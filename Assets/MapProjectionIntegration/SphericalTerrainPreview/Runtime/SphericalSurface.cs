using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>One radial height field for mesh vertices, banks, trees and picking.
    /// Stamps use the current art atlas, but neighborhoods come from the real dual sphere.</summary>
    public sealed class SphericalSurface
    {
        [Serializable] public struct Sample
        {
            public float Height, BaseHeight, PlateauLevel, Land, Relief, MaterialRelief, RiverDistance, RiverSurface, WaterLandDensity, UpperSnowWeight;
            public int Cell, Layer, Biome, Landform;
            public Vector2 UV;
            public Color Shape, Tint;
            public Vector4 BiomeWeights, UpperBiomeWeights, MountainMaterial, MountainClimate, DesertMaterial;
            public Vector3 WaterBiome;
        }
        readonly SphericalWorld world;
        readonly SphericalArtSettings profile;
        // Worker-local caches do not mutate one another during parallel mesh jobs.
        // The bound keeps planet-wide coarse queries from retaining every neighborhood.
        sealed class NeighborhoodData
        {
            public int[] Cells, RiverIndices;
        }
        sealed class LocalCache
        {
            public int Revision;
            public readonly Dictionary<int, NeighborhoodData> Neighborhoods = new(512);
            public readonly List<int> Scratch = new(64);
        }
        readonly ThreadLocal<LocalCache> localCaches = new(() => new LocalCache());
        int cacheRevision;
        readonly Vector3[] east, north;
        readonly Color32[][] shapes, materialMasks;
        readonly int shapeWidth, shapeHeight;
        readonly int maskWidth, maskHeight;
        readonly Vector2[] stampRotations;
        readonly SphericalMountainRanges ranges;
        readonly SphericalPlateauField plateauField;
        // Captured on the main thread by SphericalCoastField. The delegate only
        // reads immutable HF stamp bytes while terrain jobs evaluate the sphere.
        readonly Func<Vector3, int[], Vector2> coastField;
        public SphericalRiverNetwork Rivers { get; }
        public const float MountainReliefScale = .94f;
        // Legacy Plateau encoded the raised platform and its open top together.
        // Regional elevation now owns the platform; its local art is plain ground.
        // Source cells and save data retain the original value 3.
        public static int LocalLandform(int sourceLandform) => sourceLandform == 3 ? 0 : sourceLandform;
        public float PlateauStepHeight => plateauField.StepHeight;
        public SphericalSurface(SphericalWorld world, HexNearTerrainProfile profile,
            Func<Vector3, int[], Vector2> coastField = null)
        {
            this.world = world; this.profile = new SphericalArtSettings(profile);
            this.coastField = coastField;
            plateauField = new SphericalPlateauField(world, profile.plateauHeight);
            Rivers = new SphericalRiverNetwork(world);
            east = new Vector3[world.Count]; north = new Vector3[world.Count];
            stampRotations = new Vector2[world.Count];
            for (int i = 0; i < world.Count; i++)
            {
                Frame(world.Centers[i], out east[i], out north[i]);
                float angle = SphericalLandformArt.Rotation(world.Cells[i].TerrainRotation);
                stampRotations[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            shapeWidth = profile.shapeAtlas.width; shapeHeight = profile.shapeAtlas.height;
            shapes = new Color32[profile.shapeAtlas.depth][];
            for (int i = 0; i < shapes.Length; i++) shapes[i] = profile.shapeAtlas.GetPixels32(i, 0);
            if (profile.landMaterials && profile.landMaterials.HasMasks && profile.landMaterials.materialMasks.isReadable)
            {
                Texture2DArray masks = profile.landMaterials.materialMasks;
                maskWidth = masks.width; maskHeight = masks.height;
                materialMasks = new Color32[masks.depth][];
                for (int i = 0; i < materialMasks.Length; i++) materialMasks[i] = masks.GetPixels32(i, 0);
            }
            ranges = new SphericalMountainRanges(world, profile, SampleShape);
        }
        public void TrimCache() => Interlocked.Increment(ref cacheRevision);
        // All Unity texture/profile reads and mountain-graph writes finish in
        // the constructor. Evaluation can then run concurrently on mesh workers.
        public void PrepareForParallelEvaluation() => ranges.PrepareForParallelEvaluation();
        public float BaseHeight(Vector3 direction, int hint = -1)
        {
            direction.Normalize();
            int id = world.FindCell(direction, hint);
            return world.Cells[id].Water ? 0 : .178f + plateauField.Height(direction, id);
        }
        public static void Frame(Vector3 up, out Vector3 right, out Vector3 forward)
        {
            right = Vector3.Cross(Mathf.Abs(up.y) < .98f ? Vector3.up : Vector3.forward, up).normalized;
            forward = Vector3.Cross(up, right).normalized;
        }
        NeighborhoodData Neighborhood(int id)
        {
            LocalCache cache = localCaches.Value;
            int revision = Volatile.Read(ref cacheRevision);
            if (cache.Revision != revision || cache.Neighborhoods.Count > 2048)
            { cache.Neighborhoods.Clear(); cache.Revision = revision; }
            if (cache.Neighborhoods.TryGetValue(id, out NeighborhoodData found)) return found;
            List<int> scratch = cache.Scratch;
            scratch.Clear(); scratch.Add(id); int start = 0, end = 1;
            // Three graph rings cover the compact 2.4-radius stamp support even
            // around an irregular pentagon. No six-direction assumption exists.
            for (int ring = 0; ring < 3; ring++)
            {
                for (int j = start; j < end; j++) foreach (int n in world.Neighbors[scratch[j]])
                    if (!scratch.Contains(n)) scratch.Add(n);
                start = end; end = scratch.Count;
            }
            int[] cells = scratch.ToArray();
            found = new NeighborhoodData { Cells = cells, RiverIndices = Rivers.BuildCandidateIndices(cells) };
            cache.Neighborhoods.Add(id, found); return found;
        }
        public Sample Evaluate(Vector3 direction, int hint = -1, bool carveRiver = true)
        {
            direction.Normalize(); int center = world.FindCell(direction, hint);
            var cell = world.Cells[center]; float unit = world.CellRadius;
            float seaW = 0, totalW = 0, hills = 0, hillW = 0, mountain = 0, rangeHeight = 0;
            float duneSum = 0, duneW = 0, waterLand = 0, waterWeight = 0;
            Vector3 waterBiome = Vector3.zero;
            // Valley shoulders end within six map units; distant hulls reject immediately.
            NeighborhoodData neighborhood = Neighborhood(center);
            float closestRiverSquared = Rivers.DistanceSquaredWithin(direction, neighborhood.RiverIndices, 36);
            float materialWeight = -1, shapeHeight = 0;
            int layer = 5, materialCell = center; Vector2 bestUV = Vector2.one * .5f;
            Color bestShape = Color.clear, tint = Color.clear;
            Vector4 biomeWeights = Vector4.zero, upperWeights = Vector4.zero;
            Color desertMasks = Color.clear;
            float dryMaterialW = 0, upperSnow = 0, weatherWeight = 0;
            float mountainPresence = 0, summitPresence = 0, rockWeight = 0, topMask = 0, snowStamp = 0;
            float desertWeight = 0, snowLine = 0, peakHeight = 0;
            int[] candidates = neighborhood.Cells;
            foreach (int id in candidates)
            {
                Vector3 delta = (direction - world.Centers[id]) * (world.Radius / unit);
                var c = world.Cells[id];
                // Same compact C2 dry-land field used by the flat water optics.
                // Its support is broader than a terrain stamp, so accumulate it
                // before rejecting the narrow authored height neighborhood.
                float waterKernel = Mathf.Max(0, 1 - delta.sqrMagnitude * (unit * unit / 300f) * .25f);
                waterKernel *= waterKernel * waterKernel;
                waterWeight += waterKernel;
                if (!c.Water)
                {
                    waterLand += waterKernel;
                    // The shelf inherits only real neighboring dry climates.
                    // A sea cell's source biome is not an authored seabed type.
                    if (c.Biome == 0) waterBiome.x += waterKernel;
                    else if (c.Biome == 1 || c.Biome == 2) waterBiome.y += waterKernel;
                    else if (c.Biome > 3) waterBiome.z += waterKernel;
                }
                float r = delta.magnitude; if (r >= 2.4f) continue;
                float coastW = 1f - Smooth(.15f, 1.55f, r);
                totalW += coastW; seaW += coastW * (c.Water ? 1 : 0);
                tint += BiomeColor(c.Biome) * coastW;
                if (c.Water) continue;
                float groundW = Mathf.Max(0, 1 - r / 1.55f); groundW *= groundW;
                dryMaterialW += groundW;
                float upperW = LocalLandform(c.Landform) <= 1 ? groundW : 0;
                if (c.Biome >= 0 && c.Biome < 4)
                { biomeWeights[c.Biome] += groundW; upperWeights[c.Biome] += upperW; }
                else upperSnow += upperW;
                if (c.SourceLandform == 3 && c.Biome > 0 && c.Biome < 3) weatherWeight += groundW;
                float dw = 1 - Smooth(.2f, 1.8f, r); duneW += dw;
                if (c.Biome == 0 && LocalLandform(c.Landform) < 2)
                    duneSum += dw * (LocalLandform(c.Landform) == 1 ? profile.desertHillDuneHeight : profile.desertDuneHeight);
                Vector2 p = new(Vector3.Dot(delta, east[id]), Vector3.Dot(delta, north[id]));
                float co = stampRotations[id].x, s = stampRotations[id].y;
                Vector2 rotated = new(p.x * co + p.y * s, -p.x * s + p.y * co);
                if (c.Landform == 2)
                {
                    bool desert = c.Biome == 0;
                    SphericalMountainRanges.Stamp art = ranges.GetStamp(id);
                    int l = art.Layer;
                    float footprint = art.Footprint, h = art.Height;
                    float baseFootprint = Mathf.Min(footprint * (1 + .5f * profile.ridgeWidth), 1.85f);
                    Vector2 uv = rotated / (2 * footprint) + Vector2.one * .5f;
                    Color shape = SampleShape(l, uv);
                    float fade = 1 - Smooth(footprint * .78f, footprint * 1.18f, r);
                    float peak = shape.r * h * fade;
                    if (profile.ridgeStrength > 0 && r < baseFootprint * 1.18f)
                    {
                        float lower = SampleShape(l, rotated / (2 * baseFootprint) + Vector2.one * .5f).r;
                        peak = Mathf.Max(peak, lower * h * profile.ridgeStrength * (1 - Smooth(baseFootprint * .70f, baseFootprint * 1.18f, r)));
                    }
                    mountain = Mathf.Max(mountain, peak);
                    // The mountain shape reaches the same regional ground datum
                    // as adjacent plain. No raised, nonzero "lower mass" pedestal.
                    if (peak > materialWeight)
                    { materialWeight = peak; materialCell = id; layer = l; bestUV = uv; bestShape = shape; shapeHeight = h; }
                    rangeHeight = Mathf.Max(rangeHeight, ranges.Evaluate(id, direction));
                    // Mirror HexNearLandMaterial's contribution aggregation.
                    // No broad nonzero pedestal was restored: the compact
                    // stamp/ridge bodies still meet the shared ground datum.
                    float presence = Mathf.Max(shape.g * fade, Smooth(.035f, .35f, shape.r * h * fade));
                    bool connected = profile.mountainRangeStrength > 0 && art.Degree > 0;
                    if (connected) presence = Mathf.Max(presence, (1 - Smooth(1.2f, 2.4f, r)) * .85f * Mathf.Clamp01(profile.mountainRangeStrength));
                    mountainPresence = Mathf.Max(mountainPresence, presence);
                    summitPresence = Mathf.Max(summitPresence, presence * SphericalLandformArt.SummitSnowRetention(p, art.PeakOffset, connected));
                    float rockW = presence * presence;
                    Color mask = materialMasks != null ? SamplePixels(materialMasks, maskWidth, maskHeight, l, uv) : new Color(shape.b, shape.a, 0, 0);
                    mask *= Mathf.Clamp01(shape.g * fade);
                    if (!desert) { topMask += mask.r * rockW; snowStamp += mask.g * rockW; }
                    else { desertMasks += mask * rockW; desertWeight += rockW; }
                    rockWeight += rockW;
                    snowLine += (c.Biome > 3 ? .2f : c.Biome > 2 ? .49f : .71f) * rockW;
                    peakHeight += h * rockW;
                }
                else
                {
                    float w = 1 - Smooth(.35f, 1.45f, r); if (w <= 0) continue;
                    Vector2 uv = rotated / (2 * profile.hillFootprint) + Vector2.one * .5f;
                    Color shape = SampleShape(5, uv);
                    float strength = LocalLandform(c.Landform) == 1 ? profile.hillHeight : .18f;
                    if (c.Biome == 0) strength *= .22f;
                    hills += Mathf.Max(0, shape.r - 16f / 255f) * strength * w; hillW += w;
                    if (materialWeight < .02f && w > materialWeight)
                    { materialWeight = w * .02f; materialCell = id; layer = 5; bestUV = uv; bestShape = shape; shapeHeight = profile.hillHeight; }
                }
            }
            float closestRiver = Mathf.Sqrt(closestRiverSquared);
            float land = 1 - seaW / Mathf.Max(.0001f, totalW);
            // HF supplies the same continuous sea/land height field to both
            // halves of the surface. Dry-interior art remains at its own datum.
            Vector2 coast = coastField != null ? coastField(direction, candidates) : new Vector2(-1.7f, land);
            land = coast.y;
            float shore = coastField != null ? Smooth(.30f, .92f, land) : Smooth(.2f, .82f, land);
            float plateauY = plateauField.Height(direction, center);
            float relief = Merge(Merge(mountain, rangeHeight, .36f * MountainReliefScale),
                hills / Mathf.Max(1, hillW), profile.mountainFootBlend * MountainReliefScale);
            // Keep the original flat art's rock/snow/strata height thresholds
            // when the spherical mountain proportions are displayed. Only
            // mountain contributors return to authored height; plain and hill
            // relief retain their exact physical scale. River/beach depths
            // continue to use Height and BaseHeight, never this material field.
            float materialRelief = Merge(Merge(mountain / MountainReliefScale,
                rangeHeight / MountainReliefScale, .36f),
                hills / Mathf.Max(1, hillW), profile.mountainFootBlend);
            // Make space for a valley before the narrow water channel. Otherwise
            // a summit adjoining a river is cut straight down in one mesh sample.
            float valley = carveRiver ? Smooth(1.2f, 5.2f, closestRiver) : 1;
            relief *= valley; materialRelief *= valley;
            float authoredMountainDifference = materialRelief - relief;
            // Use the exact flat-map wind-shaped slipface and meander field.
            if (duneSum > 0)
            {
                float dune = SphericalLandformArt.Dunes(direction, world.Radius / unit,
                    profile.desertDuneWavelength, profile.desertDuneIrregularity, profile.desertDuneWind);
                float platform = plateauY / plateauField.StepHeight;
                // Dunes settle into the shoulder instead of cutting ridged teeth
                // across its face. The broad top retains the full dune field.
                float shoulder = Smooth(.02f, .30f, platform) * (1 - Smooth(.70f, .98f, platform));
                // Wetness follows the actual authored shore height. A wide
                // cell-water mask used to erase dunes well inland of the water.
                float duneDry = coastField != null
                    ? Smooth(-.08f, .12f, Mathf.Lerp(coast.x, .178f + plateauY + relief, shore))
                    : 1 - Smooth(.02f, .32f, 1 - land);
                relief += dune * duneSum / Mathf.Max(.0001f, duneW) * (1 - Smooth(.35f, 2.2f, relief)) *
                    (1 - shoulder * .82f) * duneDry * (carveRiver ? Smooth(1.7f, 4, closestRiver) : 1);
            }
            float height = Mathf.Lerp(coast.x, .178f + plateauY + relief, shore);
            float riverCarveWeight = 0;
            if (carveRiver && closestRiver < 1.7f)
            {
                riverCarveWeight = (1 - Smooth(.45f, 1.7f, closestRiver)) * Smooth(.25f, .65f, land);
                height = Mathf.Lerp(height, plateauY - .32f, riverCarveWeight);
            }
            // Carry only the restored mountain difference through the same
            // coast/river masks. Dunes and unscaled hills retain their actual
            // height; a carved riverbed cannot inherit an absent snowy summit.
            materialRelief = Mathf.Max(0, height - (.178f + plateauY) + authoredMountainDifference * shore * (1 - riverCarveWeight));
            float inverseRockW = 1 / Mathf.Max(.0001f, rockWeight);
            float inverseGroundW = 1 / Mathf.Max(.0001f, dryMaterialW);
            desertMasks /= Mathf.Max(.0001f, desertWeight);
            return new Sample { Height = height, BaseHeight = .178f + plateauY, MaterialRelief = materialRelief,
                PlateauLevel = plateauY / plateauField.StepHeight, Land = land, Relief = relief / Mathf.Max(1, shapeHeight),
                RiverDistance = Mathf.Min(closestRiver, 6) / 2.4f, RiverSurface = plateauY + .022f,
                Cell = center, Layer = layer, Biome = world.Cells[materialCell].Biome,
                Landform = LocalLandform(world.Cells[materialCell].Landform), UV = bestUV, Shape = bestShape,
                Tint = tint / Mathf.Max(.0001f, totalW),
                BiomeWeights = dryMaterialW > .00001f ? biomeWeights / dryMaterialW : FallbackBiome(cell.Biome),
                UpperBiomeWeights = upperWeights / Mathf.Max(.0001f, dryMaterialW), UpperSnowWeight = upperSnow * inverseGroundW,
                MountainMaterial = new Vector4(mountainPresence, topMask * inverseRockW, snowStamp * inverseRockW,
                    Mathf.Clamp01(summitPresence / Mathf.Max(.0001f, mountainPresence))),
                MountainClimate = new Vector4(peakHeight * inverseRockW, desertWeight * inverseRockW,
                    snowLine * inverseRockW, weatherWeight * inverseGroundW),
                DesertMaterial = new Vector4(desertMasks.r, desertMasks.g, desertMasks.b, desertMasks.a),
                WaterLandDensity = waterLand / Mathf.Max(.0001f, waterWeight),
                WaterBiome = WaterClimateWeights(waterBiome, waterLand, waterWeight) };
        }
        static Vector3 WaterClimateWeights(Vector3 climate, float dryWeight, float totalWeight)
        {
            // Keep the full real shore climate and gently lose its influence
            // offshore. The terrain's narrow normalized/fallback switch at
            // dryMaterialW=1e-5 formerly painted a hard sand polygon in a bay.
            // Quintic fade reaches zero with two continuous derivatives; it
            // never substitutes the current sea cell's discrete biome.
            float t = Mathf.Clamp01(dryWeight / Mathf.Max(.0001f, totalWeight) / .35f);
            float presence = t * t * t * (t * (t * 6 - 15) + 10);
            return climate * (presence / Mathf.Max(.0001f, dryWeight));
        }
        public Vector3 Normal(Vector3 n, int hint = -1)
        {
            Frame(n, out Vector3 e, out Vector3 f); const float step = .22f;
            Vector3 a = (n + e * (step / world.Radius)).normalized;
            Vector3 b = (n + f * (step / world.Radius)).normalized;
            Vector3 p = n * (world.Radius + Evaluate(n, hint).Height);
            Vector3 normal = Vector3.Cross(a * (world.Radius + Evaluate(a, hint).Height) - p,
                b * (world.Radius + Evaluate(b, hint).Height) - p).normalized;
            return Vector3.Dot(normal, n) < 0 ? -normal : normal;
        }
        public Color SampleShape(int layer, Vector2 uv)
            => SamplePixels(shapes, shapeWidth, shapeHeight, layer, uv);
        static Color SamplePixels(Color32[][] atlas, int width, int height, int layer, Vector2 uv)
        {
            float x = Mathf.Clamp01(uv.x) * width - .5f, y = Mathf.Clamp01(uv.y) * height - .5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y); float tx = x - ix, ty = y - iy;
            int x0 = Mathf.Clamp(ix, 0, width - 1), x1 = Mathf.Clamp(ix + 1, 0, width - 1);
            int y0 = Mathf.Clamp(iy, 0, height - 1), y1 = Mathf.Clamp(iy + 1, 0, height - 1);
            var pixels = atlas[Mathf.Clamp(layer, 0, atlas.Length - 1)];
            return Color.LerpUnclamped(Color.LerpUnclamped(pixels[y0 * width + x0], pixels[y0 * width + x1], tx),
                Color.LerpUnclamped(pixels[y1 * width + x0], pixels[y1 * width + x1], tx), ty);
        }
        public Vector3 RiverPoint(int a, int b, float t) => Rivers.Point(a, b, t);
        float RiverDistance(Vector3 p, int a, int b) => Rivers.Distance(p, a, b);
        public static uint Hash(uint x) { unchecked { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; return x ^ (x >> 16); } }
        public static float Random01(uint x) => (Hash(x) & 0xffffff) / 16777215f;
        public static float Smooth(float a, float b, float x) { x = Mathf.Clamp01((x - a) / (b - a)); return x * x * (3 - 2 * x); }
        static float Merge(float a, float b, float k)
        { k = Mathf.Min(k, 2 * Mathf.Min(a, b)); if (k < .000001f) return Mathf.Max(a, b); float h = Mathf.Max(0, 1 - Mathf.Abs(a - b) / k); return Mathf.Max(a, b) + k * h * h * .25f; }
        public static Color BiomeColor(int biome) => biome switch {
            0 => new Color(.59f, .48f, .31f), 1 => new Color(.31f, .38f, .20f),
            2 => new Color(.32f, .34f, .21f), 3 => new Color(.38f, .37f, .29f),
            _ => new Color(.79f, .81f, .79f) };
        static Vector4 FallbackBiome(int biome)
        { Vector4 weights = Vector4.zero; if (biome >= 0 && biome < 4) weights[biome] = 1; return weights; }
    }

    internal sealed class SphericalArtSettings
    {
        public readonly float hillHeight;
        public readonly float plateauRelief;
        public readonly float desertDuneHeight;
        public readonly float desertHillDuneHeight;
        public readonly float desertDuneWavelength;
        public readonly float mountainFootprint;
        public readonly float desertMountainFootprint;
        public readonly float mountainHeight;
        public readonly float desertMountainHeight;
        public readonly float hillFootprint;
        public readonly float mountainFootBlend;
        public readonly float mountainRangeStrength;
        public readonly float mountainRangeWidth;
        public readonly float mountainChainWidth;
        public readonly float mountainSlopeDetail;
        public readonly float mountainRangeDirectionality;
        public readonly float mountainMassifSaddle;
        public readonly float mountainChainSaddle;
        public readonly float mountainMassVariation, ridgeWidth, ridgeStrength, desertDuneIrregularity;
        public readonly Vector2 desertDuneWind;
        public SphericalArtSettings(HexNearTerrainProfile source)
        {
            hillHeight = source.hillHeight;
            plateauRelief = source.plateauRelief;
            desertDuneHeight = source.desertDuneHeight;
            desertHillDuneHeight = source.desertHillDuneHeight;
            desertDuneWavelength = source.desertDuneWavelength;
            mountainFootprint = source.mountainFootprint;
            desertMountainFootprint = source.desertMountainFootprint;
            mountainHeight = source.mountainHeight;
            desertMountainHeight = source.desertMountainHeight;
            hillFootprint = source.hillFootprint;
            mountainFootBlend = source.mountainFootBlend;
            mountainRangeStrength = source.mountainRangeStrength;
            mountainRangeWidth = source.mountainRangeWidth;
            mountainChainWidth = source.mountainChainWidth;
            mountainSlopeDetail = source.mountainSlopeDetail;
            mountainRangeDirectionality = source.mountainRangeDirectionality;
            mountainMassifSaddle = source.mountainMassifSaddle;
            mountainChainSaddle = source.mountainChainSaddle;
            mountainMassVariation = source.mountainMassVariation;
            ridgeWidth = source.ridgeWidth; ridgeStrength = source.ridgeStrength;
            desertDuneIrregularity = source.desertDuneIrregularity;
            float windAngle = source.desertDuneWindAngle * Mathf.Deg2Rad;
            desertDuneWind = new Vector2(Mathf.Cos(windAngle), Mathf.Sin(windAngle));
        }
    }
}
