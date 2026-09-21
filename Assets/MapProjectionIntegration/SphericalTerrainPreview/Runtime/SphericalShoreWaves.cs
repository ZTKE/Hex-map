using System;
using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        Material shoreWaveMaterial;
        const float ShoreWaterLevel = .004f, ShoreStripWidth = 5.8f;

        void InitializeShoreWaves()
        {
            Shader shader = Shader.Find("WW2/Spherical Terrain Preview/Shore Wave");
            if (!shader) throw new InvalidOperationException("The spherical shore-wave shader is missing.");
            shoreWaveMaterial = Own(new Material(shader) {
                name = "Spherical shoreline — source Civ6 crest atlas", hideFlags = HideFlags.DontSave,
                enableInstancing = true
            });
            Texture2D atlas = Resources.Load<Texture2D>("SphericalTerrainPreview/Water/WaveAtlas");
            Texture2D auxiliary = Resources.Load<Texture2D>("SphericalTerrainPreview/Water/TER_Wave_Noise");
            if (!atlas || !auxiliary) throw new InvalidOperationException("The original shore crest atlas or auxiliary texture is missing.");
            shoreWaveMaterial.SetTexture("_CrestAtlas", atlas);
            shoreWaveMaterial.SetTexture("_WaveAux", auxiliary);
            shoreWaveMaterial.SetFloat("_SurfaceLod", 0);
            SphericalMaterials.SetSphereFrame(shoreWaveMaterial, Vector3.zero, radius);
        }

        sealed class ShoreNode
        {
            public Vector3 Point, Ocean;
            public readonly List<int> Edges = new(2);
        }
        readonly struct ShoreEdge
        {
            public readonly int A, B;
            public ShoreEdge(int a, int b) { A = a; B = b; }
            public int Other(int node) => A == node ? B : A;
        }

        // Runs once for each core-owned cell on its geometry worker. Guard cells
        // are intentionally excluded: shared coastal waves must never be drawn
        // twice by neighboring streaming tiles.
        void AddShoreWaves(MeshBuffer target, MeshBuffer land, int owner)
        {
            float waterRadius = radius + ShoreWaterLevel;
            var heights = new float[land.Vertices.Count];
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            for (int i = 0; i < heights.Length; i++)
            { float h = heights[i] = land.Vertices[i].magnitude - waterRadius; minimum = Mathf.Min(minimum, h); maximum = Mathf.Max(maximum, h); }
            if (minimum > 0 || maximum <= 0) return;
            var nodes = new List<ShoreNode>();
            var nodeIds = new Dictionary<Vector3Int, int>();
            var edges = new List<ShoreEdge>();
            var uniqueEdges = new HashSet<ulong>();
            int FindNode(Vector3 point, Vector3 ocean)
            {
                // A half-millimetre weld absorbs float32 interpolation drift
                // across duplicate triangle-fan vertices, not geographic detail.
                var key = new Vector3Int(Mathf.RoundToInt(point.x * 2000),
                    Mathf.RoundToInt(point.y * 2000), Mathf.RoundToInt(point.z * 2000));
                if (!nodeIds.TryGetValue(key, out int index))
                { index = nodes.Count; nodeIds.Add(key, index); nodes.Add(new ShoreNode { Point = point }); }
                nodes[index].Ocean += ocean;
                return index;
            }
            bool Intersection(Vector3 a, Vector3 b, float ha, float hb, out Vector3 point)
            {
                point = default;
                if ((ha > 0) == (hb > 0)) return false;
                // The same edge is visited in opposite directions by its two
                // triangles. Canonical arithmetic avoids a float32 rounding
                // difference splitting an otherwise shared contour endpoint.
                if (a.x > b.x || (a.x == b.x && (a.y > b.y || (a.y == b.y && a.z > b.z))))
                { (a, b) = (b, a); (ha, hb) = (hb, ha); }
                float t = ha / (ha - hb);
                point = Vector3.LerpUnclamped(a, b, t).normalized * waterRadius;
                return true;
            }
            for (int triangle = 0; triangle < land.Indices.Count; triangle += 3)
            {
                int ia = land.Indices[triangle], ib = land.Indices[triangle + 1], ic = land.Indices[triangle + 2];
                Vector3 a = land.Vertices[ia], b = land.Vertices[ib], c = land.Vertices[ic];
                float ha = heights[ia], hb = heights[ib], hc = heights[ic];
                if ((ha > 0) == (hb > 0) && (hb > 0) == (hc > 0)) continue;
                Vector3 first = default, second = default; int count = 0;
                void AddIntersection(Vector3 p) { if (count++ == 0) first = p; else second = p; }
                if (Intersection(a, b, ha, hb, out Vector3 ab)) AddIntersection(ab);
                if (Intersection(b, c, hb, hc, out Vector3 bc)) AddIntersection(bc);
                if (Intersection(c, a, hc, ha, out Vector3 ca)) AddIntersection(ca);
                if (count != 2 || (second - first).sqrMagnitude < .000001f) continue;
                Vector3 up = (first + second).normalized;
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                if (Vector3.Dot(normal, up) < 0) normal = -normal;
                Vector3 ocean = Vector3.ProjectOnPlane(normal, up).normalized;
                if (ocean.sqrMagnitude < .1f) continue;
                int i = FindNode(first, ocean), j = FindNode(second, ocean); if (i == j) continue;
                ulong key = ((ulong)(uint)Mathf.Min(i, j) << 32) | (uint)Mathf.Max(i, j);
                if (!uniqueEdges.Add(key)) continue;
                int edge = edges.Count; edges.Add(new ShoreEdge(i, j)); nodes[i].Edges.Add(edge); nodes[j].Edges.Add(edge);
            }
            if (edges.Count == 0) return;
            foreach (ShoreNode node in nodes) node.Ocean = Vector3.ProjectOnPlane(node.Ocean, node.Point.normalized).normalized;
            var visited = new bool[edges.Count];
            var path = new List<int>();
            void Trace(int start, int edge)
            {
                path.Clear(); path.Add(start); int current = start;
                while (edge >= 0 && !visited[edge])
                {
                    visited[edge] = true; current = edges[edge].Other(current); path.Add(current);
                    if (current == start) break;
                    edge = -1;
                    foreach (int candidate in nodes[current].Edges) if (!visited[candidate]) { edge = candidate; break; }
                }
                AddShorePolyline(target, nodes, path, owner);
            }
            // Open contours first, then closed island/lake contours. Triangle
            // order and owner ID are stable, independent of streaming job order.
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].Edges.Count != 2)
                foreach (int edge in nodes[i].Edges) if (!visited[edge]) Trace(i, edge);
            for (int i = 0; i < edges.Count; i++) if (!visited[i]) Trace(edges[i].A, i);
        }

        void AddShorePolyline(MeshBuffer target, List<ShoreNode> nodes, List<int> path, int owner)
        {
            if (path.Count < 2) return;
            var distances = new float[path.Count];
            for (int i = 1; i < path.Count; i++) distances[i] = distances[i - 1] +
                Vector3.Distance(nodes[path[i - 1]].Point, nodes[path[i]].Point);
            float length = distances[distances.Length - 1]; if (length < 1.6f) return;
            Vector3 origin = nodes[path[0]].Point;
            uint seed = SphericalSurface.Hash((uint)owner ^ (uint)Mathf.RoundToInt(origin.x * 53) ^
                ((uint)Mathf.RoundToInt(origin.z * 97) * 0x9e3779b9u));
            int patchCount = Mathf.Max(1, Mathf.RoundToInt(length / Mathf.Lerp(5.5f, 8.5f, SphericalSurface.Random01(seed))));
            float patchLength = length / patchCount;
            Vector3 PointAt(float distance, out Vector3 ocean)
            {
                distance = Mathf.Clamp(distance, 0, length);
                int low = 0, high = path.Count - 1;
                while (low + 1 < high)
                { int middle = (low + high) / 2; if (distances[middle] <= distance) low = middle; else high = middle; }
                float t = Mathf.InverseLerp(distances[low], distances[high], distance);
                ocean = Vector3.Lerp(nodes[path[low]].Ocean, nodes[path[high]].Ocean, t);
                return Vector3.Lerp(nodes[path[low]].Point, nodes[path[high]].Point, t);
            }
            void Patch(float start, float end, uint patchSeed, int depth)
            {
                float span = end - start;
                Vector3 a = PointAt(start, out Vector3 oceanA), b = PointAt(end, out Vector3 oceanB);
                Vector3 middle = PointAt((start + end) * .5f, out Vector3 oceanMiddle);
                Vector3 radial = middle.normalized;
                Vector3 tangent = Vector3.ProjectOnPlane(b - a, radial).normalized;
                bool turnsBack = tangent.sqrMagnitude < .1f;
                Vector3 previousPoint = a;
                // A common propagation frame keeps the entire patch ordered.
                // Per-microtriangle normal offsets crossed on concave corners:
                // correcting triangle winding could not remove that overlap.
                for (int i = 1; i < path.Count && distances[i - 1] < end; i++)
                {
                    if (distances[i] <= start) continue;
                    Vector3 next = distances[i] < end ? nodes[path[i]].Point : b;
                    Vector3 delta = next - previousPoint; previousPoint = next;
                    if (delta.sqrMagnitude > .000004f && Vector3.Dot(delta, tangent) < delta.magnitude * .12f)
                        turnsBack = true;
                }
                if (turnsBack)
                {
                    if (depth < 5 && span > 2.8f)
                    {
                        float split = (start + end) * .5f;
                        Patch(start, split, SphericalSurface.Hash(patchSeed ^ 0xa511e9b3u), depth + 1);
                        Patch(split, end, SphericalSurface.Hash(patchSeed ^ 0x63d83595u), depth + 1);
                    }
                    return; // No narrow folded wave on a sub-metre inlet tip.
                }
                Vector3 ocean = Vector3.Cross(tangent, radial).normalized;
                if (Vector3.Dot(ocean, oceanA + oceanMiddle * 2 + oceanB) < 0) ocean = -ocean;
                float seed01 = (patchSeed & 0xffffff) / 16777216f;
                int previous = -1;
                const int crossRows = 10;
                void Emit(float distance)
                {
                    Vector3 point = PointAt(distance, out _), up = point.normalized;
                    Vector3 direction = Vector3.ProjectOnPlane(ocean, up).normalized;
                    Vector3 smooth = (PointAt(distance - 1.2f, out _) + PointAt(distance - .6f, out _) * 2
                        + point * 2 + PointAt(distance + .6f, out _) * 2 + PointAt(distance + 1.2f, out _)) * .125f;
                    float relaxation = Mathf.Clamp(Vector3.Dot(smooth - point, direction), -.65f, .65f);
                    int current = target.Vertices.Count;
                    for (int side = 0; side <= crossRows; side++)
                    {
                        float offshore = side * (ShoreStripWidth / crossRows);
                        // The zero row remains the real terrain/water contour.
                        // Relax only its offshore copy, in the common frame; the
                        // bounded shift has derivative < 1, so rows cannot fold.
                        float relaxed = offshore + relaxation * Mathf.SmoothStep(0, 1, offshore / 1.8f);
                        Vector3 n = (up + direction * (relaxed / radius)).normalized;
                        target.AddWater(n * (radius + ShoreWaterLevel + .025f), n,
                            new Vector4(offshore, distance - start, span, seed01), Vector4.zero, 1);
                    }
                    if (previous >= 0) for (int side = 0; side < crossRows; side++)
                    {
                        target.TriangleOutward(previous + side, current + side, current + side + 1);
                        target.TriangleOutward(previous + side, current + side + 1, previous + side + 1);
                    }
                    previous = current;
                }
                // Regular sub-metre sampling smooths the offset without creating
                // a new texture phase at each tiny terrain triangle.
                int alongSteps = Mathf.Max(2, Mathf.CeilToInt(span / .35f));
                for (int step = 0; step <= alongSteps; step++) Emit(Mathf.Lerp(start, end, (float)step / alongSteps));
            }
            for (int patch = 0; patch < patchCount; patch++)
                Patch(patch * patchLength, (patch + 1) * patchLength,
                    SphericalSurface.Hash(seed + (uint)patch * 0x85ebca6bu), 0);
        }
    }
}
