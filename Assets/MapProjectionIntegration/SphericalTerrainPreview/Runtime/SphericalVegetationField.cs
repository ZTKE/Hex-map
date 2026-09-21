using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Main-thread snapshot of the existing near vegetation recipe. Cell generation
    /// uses only immutable numbers, sphere topology and the shared surface sampler;
    /// it does not inspect Unity meshes, materials or ScriptableObjects on workers.
    /// The grove, glade, canopy, tint and alpine rules match HexNearVegetationMesh.
    /// </summary>
    public sealed class SphericalVegetationField
    {
        public sealed class SpeciesSnapshot
        {
            public readonly HexNearVegetationProfile.Species Source;
            public readonly Mesh Mesh, LodMesh;
            public readonly Material[] Materials;
            public readonly Bounds Bounds, LodBounds;
            public readonly int SubmeshCount, LodSubmeshCount;
            public readonly float Height, Variation, Weight, InverseMeshHeight;
            public readonly Quaternion Rotation;
            public readonly Matrix4x4 Pivot;

            internal SpeciesSnapshot(HexNearVegetationProfile.Species source)
            {
                Source = source;
                Mesh = source.mesh;
                LodMesh = source.lodMesh && source.lodMesh.vertexCount > 0 ? source.lodMesh : source.mesh;
                Bounds = Mesh.bounds;
                LodBounds = LodMesh.bounds;
                SubmeshCount = Mesh.subMeshCount;
                LodSubmeshCount = LodMesh.subMeshCount;
                Materials = new Material[Mathf.Max(SubmeshCount, LodSubmeshCount)];
                for (int i = 0; i < Materials.Length; i++) Materials[i] = source.GetMaterial(i);
                Height = source.height;
                Variation = source.scaleVariation;
                Weight = Mathf.Max(0, source.weight);
                InverseMeshHeight = 1f / Mathf.Max(.001f, Bounds.size.y);
                Rotation = Quaternion.Euler(source.rotationOffset);
                Pivot = Matrix4x4.Translate(new Vector3(-Bounds.center.x, -Bounds.min.y, -Bounds.center.z));
            }
        }

        public readonly struct Instance
        {
            public readonly int SpeciesIndex;
            public readonly Matrix4x4 Matrix;
            public readonly Vector4 Tint;
            public Instance(int speciesIndex, Matrix4x4 matrix, Vector4 tint)
            { SpeciesIndex = speciesIndex; Matrix = matrix; Tint = tint; }
        }

        struct Grove
        {
            public Vector2 Center, Axis;
            public float Radius, Aspect, Choice, Tint;
        }

        sealed class Scratch
        {
            public readonly List<Instance> Instances = new(100);
            public readonly List<Vector2> Accepted = new(100);
            public readonly Grove[] Groves = new Grove[3];
            public readonly Vector2[] Corners = new Vector2[6];
            public readonly Vector2[] Inward = new Vector2[6];
            public readonly float[] EdgeOffset = new float[6];
            public readonly bool[] Coast = new bool[6], ForestEdge = new bool[6];
            public Vector3 Up, East, North;
            public float ExtentX, ExtentY, AreaScale;
            public int Sides;
        }

        readonly SphericalWorld world;
        readonly SphericalSurface surface;
        readonly ThreadLocal<Scratch> scratch = new(() => new Scratch());
        readonly int[] broadleaf, conifer, jungle, deadwood, desert;
        readonly int densityPerCell, desertDensity;
        readonly bool tropicalBroadleaf;
        readonly float radius, inverseRadius, mountainHeight, desertMountainHeight;
        readonly float maxSlope, mountainDensityScale, mountainTreeLine, groundInset;
        readonly float riverClearance, coastClearance, spacingSquared, settlementClearanceSquared;
        readonly float clusteredFraction, clusterRadius, canopyStructure, groveTintVariation, forestEdgeWidth;
        readonly float desertCoverage, desertMaxSlope, desertRadius;

        public readonly SpeciesSnapshot[] Species;
        public readonly int MaxInstancesPerChunk;
        public readonly float LodDistance, ShadowDistance, DrawDistance;
        public bool IsReady => Species.Length > 0;

        /// <summary>Call on Unity's main thread, before dispatching generation jobs.</summary>
        public SphericalVegetationField(SphericalWorld world, SphericalSurface surface, HexNearTerrainProfile terrain)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.surface = surface ?? throw new ArgumentNullException(nameof(surface));
            radius = world.Radius;
            inverseRadius = 1f / radius;
            HexNearVegetationProfile profile = terrain ? terrain.vegetation : null;
            var species = new List<SpeciesSnapshot>();
            var indices = new Dictionary<HexNearVegetationProfile.Species, int>();
            int[] CopyGroup(HexNearVegetationProfile.Species[] group)
            {
                var result = new List<int>();
                if (group != null) foreach (var recipe in group)
                {
                    if (recipe == null || !recipe.IsReady || recipe.weight <= 0) continue;
                    if (!indices.TryGetValue(recipe, out int index))
                    { index = species.Count; species.Add(new SpeciesSnapshot(recipe)); indices.Add(recipe, index); }
                    result.Add(index);
                }
                return result.ToArray();
            }
            broadleaf = CopyGroup(profile ? profile.broadleaf : null);
            conifer = CopyGroup(profile ? profile.conifer : null);
            jungle = CopyGroup(profile ? profile.jungle : null);
            deadwood = CopyGroup(profile ? profile.deadwood : null);
            desert = CopyGroup(profile ? profile.desertAccents : null);
            Species = species.ToArray();
            if (!profile) return;
            densityPerCell = Mathf.Clamp(profile.densityPerCell, 1, 100);
            MaxInstancesPerChunk = Mathf.Clamp(profile.maxInstancesPerChunk, 32, 8192);
            LodDistance = profile.lodDistance;
            ShadowDistance = profile.shadowDistance;
            DrawDistance = profile.drawDistance;
            tropicalBroadleaf = profile.tropicalBroadleaf;
            maxSlope = Mathf.Clamp(profile.maxSlopeDegrees, 0, 70);
            mountainDensityScale = Mathf.Clamp01(profile.mountainDensityScale);
            mountainTreeLine = Mathf.Clamp01(profile.mountainTreeLine);
            mountainHeight = terrain.mountainHeight * SphericalSurface.MountainReliefScale;
            desertMountainHeight = terrain.desertMountainHeight * SphericalSurface.MountainReliefScale;
            groundInset = profile.groundInset;
            riverClearance = profile.riverClearance;
            coastClearance = profile.coastClearance;
            spacingSquared = profile.minimumSpacing * profile.minimumSpacing;
            settlementClearanceSquared = profile.settlementClearance * profile.settlementClearance;
            clusteredFraction = Mathf.Clamp01(profile.clusteredFraction);
            clusterRadius = Mathf.Max(.5f, profile.clusterRadius);
            canopyStructure = Mathf.Clamp01(profile.canopyStructure);
            groveTintVariation = Mathf.Clamp(profile.groveTintVariation, 0, .2f);
            forestEdgeWidth = profile.forestEdgeWidth;
            desertDensity = Mathf.Clamp(profile.desertAccentDensity, 0, 12);
            desertCoverage = Mathf.Clamp01(profile.desertAccentCoverage);
            desertMaxSlope = Mathf.Clamp(profile.desertAccentMaxSlope, 0, 45);
            desertRadius = Mathf.Max(.5f, profile.desertAccentClusterRadius);
        }

        public Instance[] GenerateCell(int id)
        {
            var cell = world.Cells[id];
            if (!IsReady || cell.Water || cell.SpecialIndex > 0) return Array.Empty<Instance>();
            bool accents = cell.Biome == 0 && cell.VegetationDensity <= 0 && desert.Length > 0 && desertDensity > 0;
            if (!accents && cell.VegetationDensity <= 0) return Array.Empty<Instance>();
            Scratch work = scratch.Value;
            work.Accepted.Clear(); work.Instances.Clear(); PreparePolygon(work, id);
            if (accents) { GenerateDesert(work, id); return work.Instances.ToArray(); }
            bool mountain = cell.Landform == 2;
            // densityPerCell is authored for the flat map's radius-10 hex.
            // Keep that crown coverage per square map unit on irregular sphere
            // polygons, including the smaller pentagons; never scale tree meshes.
            int target = Mathf.RoundToInt(densityPerCell * work.AreaScale * Mathf.Clamp01(cell.VegetationDensity / 100f));
            if (mountain) target = Mathf.RoundToInt(target * mountainDensityScale);
            bool settlement = cell.UrbanLevel > 0 || cell.FarmLevel > 0;
            if (settlement) target = Mathf.RoundToInt(target * .45f);
            if (target <= 0) return Array.Empty<Instance>();
            uint random = unchecked((uint)id * 747796405u + 2891336453u);
            int groveCount = Next(ref random) < .5f ? 2 : 3;
            for (int i = 0; i < groveCount; i++)
            {
                Vector2 center = InteriorPoint(work, .78f, ref random);
                float spread = clusterRadius * Mathf.Lerp(.8f, 1.2f, Next(ref random));
                float angle = Next(ref random) * Mathf.PI * 2;
                work.Groves[i] = new Grove { Center = center, Radius = spread,
                    Axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), Aspect = Mathf.Lerp(.8f, 1.3f, Next(ref random)),
                    Choice = Next(ref random), Tint = Next(ref random) * 2 - 1 };
            }
            int clusteredTarget = Mathf.RoundToInt(target * clusteredFraction), clusteredCount = 0;
            float slope = Mathf.Tan((mountain ? Mathf.Min(maxSlope, 30) : maxSlope) * Mathf.Deg2Rad);
            float slopeLimitSquared = slope * slope;
            for (int attempt = 0; attempt < target * 6 && work.Instances.Count < target; attempt++)
            {
                bool inCluster = clusteredCount < clusteredTarget && attempt < target * 4 &&
                    (work.Instances.Count - clusteredCount >= target - clusteredTarget || Next(ref random) < clusteredFraction);
                Vector2 point;
                if (inCluster)
                {
                    Grove grove = work.Groves[Mathf.Min((int)(Next(ref random) * groveCount), groveCount - 1)];
                    float angle = Next(ref random) * Mathf.PI * 2;
                    float spread = Mathf.Sqrt(Next(ref random)) * grove.Radius;
                    point = grove.Center + (grove.Axis * (Mathf.Cos(angle) * grove.Aspect) +
                        new Vector2(-grove.Axis.y, grove.Axis.x) * (Mathf.Sin(angle) / grove.Aspect)) * spread;
                }
                else point = RandomPoint(work, ref random);
                if (!Inside(work, point, .96f) || !ClearOfCoast(work, point) || Crowded(work, point, spacingSquared) ||
                    (settlement && point.sqrMagnitude < settlementClearanceSquared)) continue;
                float groveCore = GroveInterior(work, point, groveCount, out int groveIndex);
                float groupChoice = Next(ref random);
                if (Next(ref random) < groveCore * .65f) groupChoice = work.Groves[groveIndex].Choice;
                int speciesIndex = Pick((HexVegetation)cell.Vegetation, groupChoice, Next(ref random));
                if (speciesIndex < 0) continue;
                Vector3 direction = Direction(work, point);
                var sample = surface.Evaluate(direction, id);
                float treeLine = sample.BaseHeight + (cell.Biome == 0 ? desertMountainHeight : mountainHeight) * mountainTreeLine;
                if (sample.Land < .95f || sample.Height < .04f || (mountain && sample.Height > treeLine) ||
                    sample.RiverDistance * 2.4f < riverClearance) continue;
                const float offset = .35f;
                float dx = (surface.Evaluate((direction + work.East * (offset * inverseRadius)).normalized, id).Height - sample.Height) / offset;
                float dz = (surface.Evaluate((direction + work.North * (offset * inverseRadius)).normalized, id).Height - sample.Height) / offset;
                float slopeSquared = dx * dx + dz * dz;
                if (float.IsNaN(slopeSquared) || float.IsInfinity(slopeSquared) || slopeSquared > slopeLimitSquared) continue;
                SpeciesSnapshot species = Species[speciesIndex];
                float size = species.Height * Mathf.Lerp(1 - species.Variation, 1 + species.Variation, Next(ref random));
                float slopeExposure = SphericalSurface.Smooth(.35f, 1, slopeSquared / Mathf.Max(.0001f, slopeLimitSquared));
                float canopy = Mathf.Lerp(.88f, 1.14f, groveCore) * Mathf.Lerp(.82f, 1, ForestInterior(work, point)) * Mathf.Lerp(1, .84f, slopeExposure);
                if (mountain)
                {
                    float alpineEdge = SphericalSurface.Smooth(Mathf.Lerp(sample.BaseHeight, treeLine, .65f), treeLine, sample.Height);
                    canopy *= Mathf.Lerp(.86f, .64f, alpineEdge);
                }
                size *= Mathf.Lerp(1, canopy, canopyStructure);
                if ((HexVegetation)cell.Vegetation == HexVegetation.Sapling) size *= .58f;
                float angleY = Next(ref random) * Mathf.PI;
                Quaternion rotation = RadialRotation(direction) * species.Rotation * new Quaternion(0, Mathf.Sin(angleY), 0, Mathf.Cos(angleY));
                Matrix4x4 matrix = Matrix4x4.TRS(direction * (radius + sample.Height - groundInset), rotation,
                    Vector3.one * (size * species.InverseMeshHeight)) * species.Pivot;
                Vector4 tint = GroveTint(cell.VegetationTint, groveCore, work.Groves[groveIndex].Tint, Next(ref random));
                work.Instances.Add(new Instance(speciesIndex, matrix, tint));
                work.Accepted.Add(point);
                if (inCluster) clusteredCount++;
            }
            return work.Instances.ToArray();
        }

        void GenerateDesert(Scratch work, int id)
        {
            var cell = world.Cells[id];
            if (cell.Landform == 2 || cell.UrbanLevel > 0 || cell.FarmLevel > 0) return;
            uint random = unchecked((uint)id * 747796405u + 1181783497u);
            if (Next(ref random) >= desertCoverage) return;
            int target = Mathf.Min(desertDensity, 1 + Mathf.FloorToInt(Next(ref random) * desertDensity));
            Vector2 center = InteriorPoint(work, .72f, ref random);
            float slope = Mathf.Tan(desertMaxSlope * Mathf.Deg2Rad), spacing = Mathf.Max(spacingSquared, 1.21f);
            for (int attempt = 0; attempt < target * 8 && work.Instances.Count < target; attempt++)
            {
                float angle = Next(ref random) * Mathf.PI * 2, distance = Mathf.Sqrt(Next(ref random)) * desertRadius;
                Vector2 point = center + new Vector2(Mathf.Cos(angle) * distance, Mathf.Sin(angle) * distance * .8f);
                if (!Inside(work, point, .94f) || !ClearOfCoast(work, point) || Crowded(work, point, spacing)) continue;
                Vector3 direction = Direction(work, point); var sample = surface.Evaluate(direction, id);
                if (sample.Land < .95f || sample.Height <= .12f || sample.RiverDistance * 2.4f < riverClearance) continue;
                const float offset = .45f;
                float east = surface.Evaluate((direction + work.East * (offset * inverseRadius)).normalized, id).Height;
                float west = surface.Evaluate((direction - work.East * (offset * inverseRadius)).normalized, id).Height;
                float north = surface.Evaluate((direction + work.North * (offset * inverseRadius)).normalized, id).Height;
                float south = surface.Evaluate((direction - work.North * (offset * inverseRadius)).normalized, id).Height;
                float dx = (east - west) / (2 * offset), dz = (north - south) / (2 * offset), slopeSquared = dx * dx + dz * dz;
                if (float.IsNaN(slopeSquared) || float.IsInfinity(slopeSquared) || slopeSquared > slope * slope ||
                    Mathf.Abs(east + west - 2 * sample.Height) > .28f || Mathf.Abs(north + south - 2 * sample.Height) > .28f) continue;
                int index = PickFrom(desert, Next(ref random));
                if (index < 0) continue;
                SpeciesSnapshot species = Species[index];
                float scale = species.Height * Mathf.Lerp(1 - species.Variation, 1 + species.Variation, Next(ref random)) * species.InverseMeshHeight;
                float angleY = Next(ref random) * Mathf.PI;
                Quaternion rotation = RadialRotation(direction) * species.Rotation * new Quaternion(0, Mathf.Sin(angleY), 0, Mathf.Cos(angleY));
                Matrix4x4 matrix = Matrix4x4.TRS(direction * (radius + sample.Height - groundInset), rotation, Vector3.one * scale) * species.Pivot;
                float tone = Mathf.Lerp(.91f, 1.09f, Next(ref random));
                work.Instances.Add(new Instance(index, matrix, new Vector4(tone, tone, tone * .96f, 1)));
                work.Accepted.Add(point);
            }
        }

        void PreparePolygon(Scratch work, int id)
        {
            work.Up = world.Centers[id];
            SphericalSurface.Frame(work.Up, out work.East, out work.North);
            work.Sides = world.Corners[id].Length;
            work.ExtentX = work.ExtentY = 0;
            for (int i = 0; i < work.Sides; i++)
            {
                Vector3 corner = world.Corners[id][i];
                float scale = radius / Vector3.Dot(corner, work.Up);
                Vector2 local = new Vector2(Vector3.Dot(corner, work.East) * scale, Vector3.Dot(corner, work.North) * scale);
                work.Corners[i] = local;
                work.ExtentX = Mathf.Max(work.ExtentX, Mathf.Abs(local.x)); work.ExtentY = Mathf.Max(work.ExtentY, Mathf.Abs(local.y));
            }
            float twiceArea = 0;
            for (int i = 0; i < work.Sides; i++)
            {
                Vector2 a = work.Corners[i], b = work.Corners[(i + 1) % work.Sides], edge = b - a;
                twiceArea += a.x * b.y - a.y * b.x;
                Vector2 inward = new Vector2(-edge.y, edge.x).normalized;
                if (Vector2.Dot(inward, a) > 0) inward = -inward;
                work.Inward[i] = inward; work.EdgeOffset[i] = Vector2.Dot(inward, a);
                var neighbor = world.Cells[world.Neighbors[id][i]];
                work.Coast[i] = neighbor.Water;
                work.ForestEdge[i] = neighbor.Water || neighbor.SpecialIndex > 0 || neighbor.VegetationDensity <= 0 ||
                    (neighbor.Landform == 2 && mountainDensityScale <= 0);
            }
            const float flatHexArea = 259.8076211353316f; // 3 * sqrt(3) / 2 * 10^2.
            work.AreaScale = Mathf.Abs(twiceArea) * .5f / flatHexArea;
        }

        static bool Inside(Scratch work, Vector2 point, float scale)
        { for (int i = 0; i < work.Sides; i++) if (Vector2.Dot(point, work.Inward[i]) < work.EdgeOffset[i] * scale) return false; return true; }
        bool ClearOfCoast(Scratch work, Vector2 point)
        { for (int i = 0; i < work.Sides; i++) if (work.Coast[i] && SegmentDistance(point, work.Corners[i], work.Corners[(i + 1) % work.Sides]) < coastClearance) return false; return true; }
        static bool Crowded(Scratch work, Vector2 point, float spacing)
        { foreach (Vector2 other in work.Accepted) if ((point - other).sqrMagnitude < spacing) return true; return false; }
        static Vector2 RandomPoint(Scratch work, ref uint random) =>
            new Vector2((Next(ref random) * 2 - 1) * work.ExtentX, (Next(ref random) * 2 - 1) * work.ExtentY);
        static Vector2 InteriorPoint(Scratch work, float scale, ref uint random)
        { for (int i = 0; i < 32; i++) { Vector2 point = RandomPoint(work, ref random); if (Inside(work, point, scale)) return point; } return Vector2.zero; }
        Vector3 Direction(Scratch work, Vector2 point) => (work.Up + (work.East * point.x + work.North * point.y) * inverseRadius).normalized;
        static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        { Vector2 edge = b - a; return (point - a - edge * Mathf.Clamp01(Vector2.Dot(point - a, edge) / Mathf.Max(.0001f, edge.sqrMagnitude))).magnitude; }

        static float GroveInterior(Scratch work, Vector2 point, int count, out int nearest)
        {
            float best = float.PositiveInfinity; nearest = 0;
            for (int i = 0; i < count; i++)
            {
                Grove grove = work.Groves[i]; Vector2 delta = point - grove.Center;
                float x = Vector2.Dot(delta, grove.Axis) / grove.Aspect;
                float y = Vector2.Dot(delta, new Vector2(-grove.Axis.y, grove.Axis.x)) * grove.Aspect;
                float distance = (x * x + y * y) / Mathf.Max(.0001f, grove.Radius * grove.Radius);
                if (distance < best) { best = distance; nearest = i; }
            }
            return 1 - SphericalSurface.Smooth(.08f, 1.15f, best);
        }
        float ForestInterior(Scratch work, Vector2 point)
        {
            if (forestEdgeWidth <= 0) return 1;
            float distance = forestEdgeWidth;
            for (int i = 0; i < work.Sides; i++) if (work.ForestEdge[i])
                distance = Mathf.Min(distance, SegmentDistance(point, work.Corners[i], work.Corners[(i + 1) % work.Sides]));
            return SphericalSurface.Smooth(0, forestEdgeWidth, distance);
        }
        int Pick(HexVegetation kind, float groupChoice, float choice)
        {
            int[] group = kind switch
            {
                HexVegetation.Jungle => jungle.Length > 0 ? jungle : broadleaf,
                HexVegetation.Conifer => conifer,
                HexVegetation.Deadwood => deadwood,
                HexVegetation.ColdMixed => groupChoice < .8f ? conifer : broadleaf,
                HexVegetation.Mixed => groupChoice < .3f ? conifer : broadleaf,
                _ => tropicalBroadleaf && jungle.Length > 0 ? jungle : broadleaf
            };
            return PickFrom(group, choice);
        }
        int PickFrom(int[] group, float choice)
        {
            float total = 0; foreach (int index in group) total += Species[index].Weight;
            float target = Mathf.Clamp01(choice) * total;
            foreach (int index in group) { target -= Species[index].Weight; if (target <= 0) return index; }
            return group.Length > 0 ? group[group.Length - 1] : -1;
        }
        Vector4 GroveTint(int theme, float core, float grove, float individual)
        {
            Color tint = (HexVegetationTint)theme switch
            {
                HexVegetationTint.DeepGreen => new Color(.76f, .94f, .72f),
                HexVegetationTint.Autumn => new Color(1.13f, .72f, .39f),
                HexVegetationTint.Dry => new Color(1.08f, .90f, .63f),
                HexVegetationTint.Frost => new Color(.87f, .98f, 1.08f),
                HexVegetationTint.Pale => new Color(1.04f, 1.02f, .87f),
                _ => Color.white
            };
            float warmth = groveTintVariation * (grove * core * .75f + (individual * 2 - 1) * .25f);
            float value = 1 - groveTintVariation * core * .35f;
            return new Vector4(tint.r * (1 + warmth * .65f) * value, tint.g * (1 + warmth * .2f) * value, tint.b * (1 - warmth * .5f) * value, 1);
        }
        static Quaternion RadialRotation(Vector3 up)
        {
            if (up.y < -.999999f) return new Quaternion(1, 0, 0, 0);
            float inverse = 1f / Mathf.Sqrt(2f * (1 + up.y));
            return new Quaternion(up.z * inverse, 0, -up.x * inverse, (1 + up.y) * inverse);
        }
        static float Next(ref uint state)
        {
            unchecked { state = state * 747796405u + 2891336453u;
                uint value = ((state >> (int)((state >> 28) + 4)) ^ state) * 277803737u;
                value = (value >> 22) ^ value; return (value & 0xffffff) / 16777216f; }
        }
    }
}
