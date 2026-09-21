using System;
using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Ports the existing mountain ridge graph, authored peak anchors and full
    /// range flanks to the actual spherical adjacency graph. A dual pentagon
    /// simply has five neighbors; no direction index or sixty-degree edge is
    /// used to connect mountains. All returned values are radial heights.
    /// </summary>
    public sealed class SphericalMountainRanges
    {
        readonly SphericalWorld world;
        readonly SphericalArtSettings profile;
        bool prepared;
        readonly Func<int, Vector2, Color> sampleShape;
        readonly Vector4[] peaks = new Vector4[10];
        readonly Dictionary<int, Node> nodes = new();
        readonly Dictionary<ulong, Edge> edges = new(SphericalEdgeKeyComparer.Instance);
        readonly float scale;

        sealed class Node
        {
            public int Id, Layer, Degree, Mask = -1;
            public Vector3 East, North, KeyPosition, PeakPosition, Axis;
            public float PeakHeight, Angle;
            public Stamp Art;
            public bool Desert, Chain;
        }
        public readonly struct Stamp
        {
            public readonly int Layer, Degree;
            public readonly float Footprint, Height, Angle;
            public readonly Vector2 PeakOffset;
            public Stamp(int layer, int degree, float footprint, float height, float angle, Vector2 peakOffset)
            { Layer = layer; Degree = degree; Footprint = footprint; Height = height; Angle = angle; PeakOffset = peakOffset; }
        }
        public Stamp GetStamp(int sourceCell) => GetNode(sourceCell).Art;
        sealed class Edge
        {
            public Node A, B;
            public Vector3 Origin, East, North;
            public Vector2 Start, Segment, Side;
            public float LengthSquared, Variation, Phase, SaddlePosition, SaddleHeight, Chain, Directional;
        }
        readonly struct EdgeKey
        {
            public readonly int Length, First, Second;
            public readonly uint Hash;
            public EdgeKey(int length, uint hash, int first, int second)
            { Length = length; Hash = hash; First = first; Second = second; }
        }

        public SphericalMountainRanges(SphericalWorld world, HexNearTerrainProfile profile, Func<int, Vector2, Color> sampleShape)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            if (!profile) throw new ArgumentNullException(nameof(profile));
            this.profile = new SphericalArtSettings(profile);
            this.sampleShape = sampleShape ?? throw new ArgumentNullException(nameof(sampleShape));
            scale = world.Radius / world.CellRadius;
            // Native texture access is confined to these ten constructor calls.
            for (int layer = 0; layer < peaks.Length; layer++) peaks[layer] = profile.GetMountainPeak(layer);
            PrepareForParallelEvaluation();
        }

        public void PrepareForParallelEvaluation()
        {
            if (prepared) return;
            // Only ~14k authored mountain nodes are prepared, not all 590k cells.
            // Thereafter dictionaries and node masks are immutable for concurrent readers.
            for (int i = 0; i < world.Count; i++) if (IsMountain(i)) GetNode(i);
            foreach (Node node in nodes.Values) GetMask(node);
            foreach (Node node in nodes.Values)
            {
                int[] neighbors = world.Neighbors[node.Id];
                for (int slot = 0; slot < neighbors.Length; slot++)
                    if ((node.Mask & (1 << slot)) != 0) GetEdge(node.Id, neighbors[slot]);
            }
            prepared = true;
        }

        public float Evaluate(int sourceCell, Vector3 direction)
        {
            if (profile.mountainRangeStrength <= 0 || !IsMountain(sourceCell)) return 0;
            float radius = (direction - world.Centers[sourceCell]).magnitude * scale;
            if (radius >= 2.4f) return 0;
            Node node = GetNode(sourceCell);
            int mask = GetMask(node);
            if (mask == 0) return 0;
            float sourceFade = 1 - Smooth(1.95f, 2.4f, radius);
            float result = 0;
            int[] neighbors = world.Neighbors[sourceCell];
            for (int slot = 0; slot < neighbors.Length; slot++)
            {
                if ((mask & (1 << slot)) == 0) continue;
                Edge edge = GetEdge(sourceCell, neighbors[slot]);
                Vector3 delta = direction * scale - edge.Origin;
                Vector2 point = new(Vector3.Dot(delta, edge.East), Vector3.Dot(delta, edge.North));
                float t = Mathf.Clamp01(Vector2.Dot(point - edge.Start, edge.Segment) / edge.LengthSquared);
                float bell = 4 * t * (1 - t);
                Vector2 curve = edge.Start + edge.Segment * t + edge.Side * ((edge.Variation - .5f) * .28f * bell);
                float width = Mathf.Max(.01f, profile.mountainRangeWidth * Mathf.Lerp(1, profile.mountainChainWidth, edge.Chain) *
                    (.94f + .06f * bell) * (.96f + .08f * edge.Variation) * (1 + .12f * bell * Mathf.Sin(t * Mathf.PI * 4 + edge.Phase)));
                Vector2 q = point - curve;
                float cross = Mathf.Pow(Mathf.Clamp01(1 - q.magnitude / width), 1.15f);
                if (cross <= 0) continue;
                float descent = t < edge.SaddlePosition ? t / edge.SaddlePosition : (1 - t) / (1 - edge.SaddlePosition);
                float shoulder = Mathf.Lerp(.64f, 1, edge.Chain) * Mathf.Lerp(edge.A.PeakHeight, edge.B.PeakHeight, t);
                float saddle = Mathf.Lerp(shoulder, edge.SaddleHeight, descent * descent * (3 - 2 * descent));
                float ridge = saddle * profile.mountainRangeStrength * edge.Directional * cross * sourceFade;
                float amplitude = Mathf.Clamp(profile.mountainSlopeDetail, 0, .35f);
                if (ridge * (1 + amplitude) <= result) continue;
                if (amplitude > 0)
                {
                    // Both source slope textures are sampled in their own
                    // tangent frames, sharing one canonical three-dimensional
                    // range body. This keeps ribs on spherical flanks.
                    Vector2 a = (q + edge.Segment * (t * .45f)) / width;
                    Vector2 b = (q + edge.Segment * ((t - 1) * .45f)) / width;
                    float detailA = SlopeDetail(edge.A, edge.East * a.x + edge.North * a.y);
                    float detailB = SlopeDetail(edge.B, edge.East * b.x + edge.North * b.y);
                    ridge *= 1 + amplitude * 4 * cross * (1 - cross) * Mathf.Lerp(detailA, detailB, t);
                }
                result = Mathf.Max(result, ridge);
            }
            return result;
        }

        Node GetNode(int id)
        {
            if (nodes.TryGetValue(id, out Node node)) return node;
            if (prepared) throw new InvalidOperationException("The immutable mountain graph changed after preparation.");
            var cell = world.Cells[id];
            SphericalSurface.Frame(world.Centers[id], out Vector3 east, out Vector3 north);
            uint hash = SphericalSurface.Hash((uint)id);
            bool desert = cell.Biome == 0;
            int layer = desert ? 6 + (int)(hash % 4) : (int)(hash % 5);
            int degree = 0;
            foreach (int other in world.Neighbors[id]) if (IsMountain(other)) degree++;
            Vector2 massScale = SphericalLandformArt.MountainScale(hash, degree, profile.mountainMassVariation);
            float footprint = (desert ? profile.desertMountainFootprint : profile.mountainFootprint) * massScale.y;
            float height = (desert ? profile.desertMountainHeight : profile.mountainHeight) * massScale.x * SphericalSurface.MountainReliefScale;
            float angle = SphericalLandformArt.Rotation(cell.TerrainRotation);
            Vector4 peak = peaks[layer];
            Vector2 offset = Rotate(new Vector2(peak.x, peak.y), -angle) * footprint;
            Vector3 center = world.Centers[id] * scale;
            node = new Node {
                Id = id, Layer = layer, East = east, North = north, Desert = desert,
                Angle = angle, Degree = degree, Chain = cell.MountainMode == 1 || (cell.MountainMode == 0 && degree <= 2),
                PeakPosition = center + east * offset.x + north * offset.y,
                PeakHeight = peak.z * height, Art = new Stamp(layer, degree, footprint, height, angle, offset),
                Axis = east * Mathf.Cos(angle) + north * Mathf.Sin(angle),
                KeyPosition = center + east * (((hash & 255u) / 255f - .5f) * .48f) +
                    north * ((((hash >> 8) & 255u) / 255f - .5f) * .48f)
            };
            nodes.Add(id, node); return node;
        }

        int GetMask(Node node)
        {
            if (node.Mask >= 0) return node.Mask;
            int result = 0, a = node.Id;
            int[] neighbors = world.Neighbors[a];
            for (int slot = 0; slot < neighbors.Length; slot++)
            {
                int b = neighbors[slot]; if (!IsMountain(b)) continue;
                if (world.Cells[a].MountainMode == 1 || world.Cells[b].MountainMode == 1)
                { result |= 1 << slot; continue; }
                EdgeKey key = Key(a, b); bool retained = true;
                // Delete only an edge strictly greater than both other edges
                // of an actual mountain triangle. The graph retains every MST
                // edge (cycle property), preserving connectivity and thin
                // bends while removing triangular/spider ridge fans.
                foreach (int c in neighbors)
                {
                    if (c == b || !IsMountain(c) || !Adjacent(b, c)) continue;
                    if (Greater(key, Key(a, c)) && Greater(key, Key(b, c)))
                    { retained = false; break; }
                }
                if (retained) result |= 1 << slot;
            }
            node.Mask = result; return result;
        }

        Edge GetEdge(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            ulong key = ((ulong)(uint)a << 32) | (uint)b;
            if (edges.TryGetValue(key, out Edge edge)) return edge;
            if (prepared) throw new InvalidOperationException("A mountain edge was not prepared before worker evaluation.");
            Node first = GetNode(a), second = GetNode(b);
            Vector3 up = (world.Centers[a] + world.Centers[b]).normalized;
            Vector3 east = (first.East - up * Vector3.Dot(first.East, up)).normalized;
            Vector3 north = Vector3.Cross(up, east).normalized;
            Vector3 origin = world.Centers[a] * scale;
            Vector3 deltaA = first.PeakPosition - origin, deltaB = second.PeakPosition - origin;
            Vector2 start = new(Vector3.Dot(deltaA, east), Vector3.Dot(deltaA, north));
            Vector2 end = new(Vector3.Dot(deltaB, east), Vector3.Dot(deltaB, north));
            Vector2 segment = end - start;
            float length2 = Mathf.Max(segment.sqrMagnitude, .01f);
            uint pair;
            unchecked { pair = SphericalSurface.Hash((uint)a) ^ (SphericalSurface.Hash((uint)b) * 1664525u + 1013904223u); }
            float variation = (pair & 255u) / 255f;
            float chain = first.Chain || second.Chain ? 1 : 0;
            float directional = 1;
            if (chain < .5f && first.Degree > 2 && second.Degree > 2)
            {
                Vector3 direction = (world.Centers[b] - world.Centers[a]).normalized;
                float alignment = Mathf.Max(Mathf.Abs(Vector3.Dot(first.Axis, direction)), Mathf.Abs(Vector3.Dot(second.Axis, direction)));
                directional = Mathf.Lerp(1, Mathf.Lerp(.86f, 1, Smooth(.5f, .86f, alignment)), profile.mountainRangeDirectionality);
            }
            float saddleRatio = Mathf.Lerp(profile.mountainMassifSaddle, profile.mountainChainSaddle, chain);
            edge = new Edge {
                A = first, B = second, Origin = origin, East = east, North = north,
                Start = start, Segment = segment, Side = new Vector2(-segment.y, segment.x) / Mathf.Sqrt(length2),
                LengthSquared = length2, Variation = variation, Phase = variation * Mathf.PI * 2,
                SaddlePosition = .32f + .36f * ((pair >> 8) & 255u) / 255f,
                SaddleHeight = Mathf.Min(first.PeakHeight, second.PeakHeight) *
                    (saddleRatio + Mathf.Lerp(.10f, .04f, chain) * (((pair >> 16) & 255u) / 255f - .5f)),
                Chain = chain, Directional = directional
            };
            edges.Add(key, edge); return edge;
        }

        float SlopeDetail(Node node, Vector3 point)
        {
            Vector2 local = new(Vector3.Dot(point, node.East), Vector3.Dot(point, node.North));
            Vector4 peak = peaks[node.Layer];
            Vector2 uv = (new Vector2(peak.x, peak.y) + Rotate(local, node.Angle) * (node.Desert ? .46f : .70f)) * .5f + Vector2.one * .5f;
            Color shape = sampleShape(node.Layer, uv);
            return Mathf.Clamp((shape.r / Mathf.Max(peak.z, .05f) - .55f * Mathf.Sqrt(Mathf.Clamp01(shape.g))) * 3, -1, 1);
        }
        EdgeKey Key(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            float distance2 = (GetNode(a).KeyPosition - GetNode(b).KeyPosition).sqrMagnitude;
            uint pair;
            unchecked { pair = SphericalSurface.Hash((uint)a) ^ (SphericalSurface.Hash((uint)b) * 1664525u + 1013904223u); }
            return new EdgeKey(Mathf.RoundToInt(distance2 * 4096), pair, a, b);
        }
        static bool Greater(EdgeKey a, EdgeKey b)
        {
            if (a.Length != b.Length) return a.Length > b.Length;
            if (a.Hash != b.Hash) return a.Hash > b.Hash;
            if (a.First != b.First) return a.First > b.First;
            return a.Second > b.Second;
        }
        bool Adjacent(int a, int b) { foreach (int id in world.Neighbors[a]) if (id == b) return true; return false; }
        bool IsMountain(int id) => id >= 0 && id < world.Count && !world.Cells[id].Water && world.Cells[id].Landform == 2;
        static Vector2 Rotate(Vector2 p, float angle)
        { float s = Mathf.Sin(angle), c = Mathf.Cos(angle); return new Vector2(p.x * c + p.y * s, -p.x * s + p.y * c); }
        static float Smooth(float a, float b, float value)
        { float t = Mathf.Clamp01((value - a) / (b - a)); return t * t * (3 - 2 * t); }
    }
}
