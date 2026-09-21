using System;
using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Immutable horizontal river geometry shared by channel carving, water ribbons,
    /// banks and vegetation clearance. Corner IDs retain the real sphere graph.
    /// Degree-two joins use the same rounded Hermite construction as the current
    /// flat HF surface sampler; sources and branching junctions keep their anchors.
    /// </summary>
    public sealed class SphericalRiverNetwork
    {
        struct Node
        {
            public int Degree, N0, N1, N2;
            public Vector3 Anchor;
            public int Other(int toward) => N0 == toward ? N1 : N0;
        }

        struct Curve
        {
            public Vector3 Start, End;
            // Polynomial coefficients are in local map units. Unit-sphere
            // differences can fall below Unity's Vector3 normalization epsilon.
            public Vector3 Linear, Quadratic, Cubic, EndDerivative, Chord;
            public Vector3 Minimum, Maximum;
            public float ChordLengthSquared, FlowStart, FlowEnd, ArcLength;
        }

        struct Segment
        {
            public Vector3 Start, Delta;
            public float InverseLengthSquared;
        }

        readonly SphericalWorld world;
        readonly float radius, inverseRadius;
        readonly Dictionary<int, Node> nodes;
        readonly Dictionary<ulong, int> edgeIndices;
        readonly Curve[] curves;
        readonly Segment[] segments;
        readonly float[] arcDistances;
        const int SegmentsPerCurve = 8;

        public int EdgeCount => curves.Length;
        public int NodeCount => nodes.Count;

        public SphericalRiverNetwork(SphericalWorld world)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            radius = world.Radius;
            inverseRadius = 1f / radius;
            nodes = new Dictionary<int, Node>(world.RiverEdges.Length);
            edgeIndices = new Dictionary<ulong, int>(world.RiverEdges.Length, SphericalEdgeKeyComparer.Instance);
            foreach (var edge in world.RiverEdges)
            {
                AddNeighbor(edge.CornerA, edge.CornerB);
                AddNeighbor(edge.CornerB, edge.CornerA);
            }

            // All incident edges read exactly the same anchor and derivative
            // frame, including where a five-sided cell touches the river.
            foreach (int id in new List<int>(nodes.Keys))
            {
                Node node = nodes[id];
                Vector3 center = world.CornerDirections[id];
                node.Anchor = node.Degree == 2
                    ? (center + (world.CornerDirections[node.N0] - center +
                        world.CornerDirections[node.N1] - center) / 6f).normalized
                    : center;
                nodes[id] = node;
            }

            curves = new Curve[world.RiverEdges.Length];
            segments = new Segment[curves.Length * SegmentsPerCurve];
            arcDistances = new float[curves.Length * (SegmentsPerCurve + 1)];
            for (int i = 0; i < world.RiverEdges.Length; i++)
            {
                int a = world.RiverEdges[i].CornerA, b = world.RiverEdges[i].CornerB;
                if (a > b) (a, b) = (b, a);
                edgeIndices.Add(Key(a, b), i);
                Vector3 start = nodes[a].Anchor, end = nodes[b].Anchor;
                Vector3 startDerivative = OutgoingDerivative(a, b);
                Vector3 endDerivative = -OutgoingDerivative(b, a);
                Vector3 chord = (end - start) * radius;
                // Hermite -> cubic polynomial, matching HexSurfaceSampler's
                // RiverCurveSample without assuming a rectangular cell layout.
                Curve curve = new Curve
                {
                    Start = start, End = end, Chord = chord,
                    Linear = startDerivative, EndDerivative = endDerivative,
                    Quadratic = chord * 3f - startDerivative * 2f - endDerivative,
                    Cubic = startDerivative + endDerivative - chord * 2f,
                    ChordLengthSquared = chord.sqrMagnitude
                };
                Vector3 controlA = startDerivative / 3f;
                Vector3 controlB = chord - endDerivative / 3f;
                // A cubic lies in its Bezier control hull. Include a conservative
                // radial projection allowance before using that hull for culling.
                float extent = Mathf.Max(chord.magnitude,
                    Mathf.Max(controlA.magnitude, controlB.magnitude));
                float padding = .002f + 2f * extent * extent / radius;
                curve.Minimum = Minimum(Minimum(Vector3.zero, chord), Minimum(controlA, controlB)) - Vector3.one * padding;
                curve.Maximum = Maximum(Maximum(Vector3.zero, chord), Maximum(controlA, controlB)) + Vector3.one * padding;
                curves[i] = curve;
                Vector3 previous = Vector3.zero;
                for (int sample = 0; sample < SegmentsPerCurve; sample++)
                {
                    Sample(curve, (sample + 1f) / SegmentsPerCurve, out Vector3 direction, out _);
                    Vector3 point = (direction - start) * radius;
                    Vector3 delta = point - previous;
                    segments[i * SegmentsPerCurve + sample] = new Segment
                    { Start = previous, Delta = delta, InverseLengthSquared = 1f / Mathf.Max(delta.sqrMagnitude, .000001f) };
                    int arcIndex = i * (SegmentsPerCurve + 1) + sample;
                    arcDistances[arcIndex + 1] = arcDistances[arcIndex] + delta.magnitude;
                    previous = point;
                }
                curve.ArcLength = arcDistances[(i + 1) * (SegmentsPerCurve + 1) - 1];
                curves[i] = curve;
            }
            // Corner sorting is a storage convention, not hydraulic direction.
            // Each whole authored stroke carries one continuous source-to-mouth
            // distance, including when consecutive edge IDs run in opposite orders.
            int flowEdges = 0;
            foreach (var route in world.RiverRoutes)
            {
                float distance = 0;
                for (int j = 1; j < route.Corners.Length; j++)
                {
                    int from = route.Corners[j - 1], to = route.Corners[j];
                    int index = edgeIndices[Key(Math.Min(from, to), Math.Max(from, to))];
                    Curve curve = curves[index];
                    float next = distance + curve.ArcLength;
                    curve.FlowStart = from < to ? distance : next;
                    curve.FlowEnd = from < to ? next : distance;
                    curves[index] = curve; distance = next; flowEdges++;
                }
            }
            if (flowEdges != curves.Length)
                throw new InvalidOperationException("Every river curve needs one authored downstream route.");
        }

        void AddNeighbor(int id, int neighbor)
        {
            nodes.TryGetValue(id, out Node node);
            if ((node.Degree > 0 && node.N0 == neighbor) ||
                (node.Degree > 1 && node.N1 == neighbor) ||
                (node.Degree > 2 && node.N2 == neighbor)) return;
            if (node.Degree == 0) node.N0 = neighbor;
            else if (node.Degree == 1) node.N1 = neighbor;
            else if (node.Degree == 2) node.N2 = neighbor;
            else throw new InvalidOperationException("A spherical dual river corner has more than three incident edges.");
            node.Degree++;
            nodes[id] = node;
        }

        Vector3 OutgoingDerivative(int from, int toward)
        {
            Node node = nodes[from];
            Vector3 original = world.CornerDirections[from];
            Vector3 outgoing = (world.CornerDirections[toward] - original) * radius;
            if (node.Degree == 2)
                outgoing = (outgoing - (world.CornerDirections[node.Other(toward)] - original) * radius) * .5f;
            // Projection at the shifted shared anchor makes both incident
            // derivatives exactly opposing tangents on the sphere as well.
            return outgoing - node.Anchor * Vector3.Dot(outgoing, node.Anchor);
        }

        public Vector3 Anchor(int cornerId) => nodes.TryGetValue(cornerId, out Node node)
            ? node.Anchor : world.CornerDirections[cornerId];
        public int Degree(int cornerId) => nodes.TryGetValue(cornerId, out Node node) ? node.Degree : 0;

        /// <summary>Shared point, actual downstream tangent and continuous map-unit river distance.
        /// Curve arc-length samples keep apparent flow speed steady through rounded bends.</summary>
        public void FlowSample(int a, int b, float t, out Vector3 direction, out Vector3 downstream, out float distance)
        {
            if (a > b) { (a, b) = (b, a); t = 1 - t; }
            t = Mathf.Clamp01(t);
            int index = edgeIndices[Key(a, b)];
            ref readonly Curve curve = ref curves[index];
            Sample(curve, t, out direction, out Vector3 derivative);
            downstream = derivative.normalized * (curve.FlowEnd > curve.FlowStart ? 1 : -1);
            if (t <= 0) { distance = curve.FlowStart; return; }
            if (t >= 1) { distance = curve.FlowEnd; return; }
            float sample = t * SegmentsPerCurve;
            int slot = Mathf.Min((int)sample, SegmentsPerCurve - 1);
            int arcIndex = index * (SegmentsPerCurve + 1) + slot;
            float along = Mathf.Lerp(arcDistances[arcIndex], arcDistances[arcIndex + 1], sample - slot);
            distance = Mathf.Lerp(curve.FlowStart, curve.FlowEnd, along / Mathf.Max(curve.ArcLength, .000001f));
        }

        public Vector3 Point(int a, int b, float t)
        {
            if (a > b) { (a, b) = (b, a); t = 1f - t; }
            ref readonly Curve curve = ref curves[edgeIndices[Key(a, b)]];
            if (t <= 0) return curve.Start;
            if (t >= 1) return curve.End;
            Vector3 local = (curve.Linear + (curve.Quadratic + curve.Cubic * t) * t) * t;
            return (curve.Start + local * inverseRadius).normalized;
        }

        /// <summary>Unit tangent in the requested a-to-b direction; no finite differences.</summary>
        public Vector3 Tangent(int a, int b, float t)
        {
            bool reverse = a > b;
            if (reverse) { (a, b) = (b, a); t = 1f - t; }
            ref readonly Curve curve = ref curves[edgeIndices[Key(a, b)]];
            Sample(curve, Mathf.Clamp01(t), out _, out Vector3 derivative);
            return (reverse ? -derivative : derivative).normalized;
        }

        /// <summary>Analytic position and map-unit derivative for a canonical graph edge.</summary>
        public void Sample(int a, int b, float t, out Vector3 direction, out Vector3 derivative)
        {
            bool reverse = a > b;
            if (reverse) { (a, b) = (b, a); t = 1f - t; }
            ref readonly Curve curve = ref curves[edgeIndices[Key(a, b)]];
            Sample(curve, Mathf.Clamp01(t), out direction, out derivative);
            if (reverse) derivative = -derivative;
        }

        void Sample(in Curve curve, float t, out Vector3 direction, out Vector3 derivative)
        {
            if (t <= 0) { direction = curve.Start; derivative = curve.Linear; return; }
            if (t >= 1) { direction = curve.End; derivative = curve.EndDerivative; return; }
            Vector3 local = (curve.Linear + (curve.Quadratic + curve.Cubic * t) * t) * t;
            Vector3 raw = curve.Start + local * inverseRadius;
            float inverseLength = 1f / Mathf.Sqrt(raw.sqrMagnitude);
            direction = raw * inverseLength;
            derivative = curve.Linear + (curve.Quadratic * 2f + curve.Cubic * (3f * t)) * t;
            derivative = (derivative - direction * Vector3.Dot(direction, derivative)) * inverseLength;
        }

        public float Distance(Vector3 direction, int a, int b) =>
            Mathf.Sqrt(DistanceSquaredWithin(direction, a, b, float.PositiveInfinity));

        /// <summary>
        /// Resolve and deduplicate a terrain neighborhood once, when its worker
        /// cache is built. A shared river edge is otherwise visited twice by
        /// every vertex, with repeated corner sorting and dictionary lookups.
        /// </summary>
        public int[] BuildCandidateIndices(int[] cells)
        {
            var found = new HashSet<int>();
            var ordered = new List<int>();
            foreach (int id in cells)
            {
                int mask = world.Cells[id].RiverEdgeMask;
                if (mask == 0) continue;
                int[] corners = world.CornerIds[id];
                for (int e = 0; e < corners.Length; e++)
                {
                    if ((mask & (1 << e)) == 0) continue;
                    int a = corners[e], b = corners[(e + 1) % corners.Length];
                    if (a > b) (a, b) = (b, a);
                    int index = edgeIndices[Key(a, b)];
                    if (found.Add(index)) ordered.Add(index);
                }
            }
            return ordered.ToArray();
        }

        public float DistanceSquaredWithin(Vector3 direction, int[] candidateIndices, float currentBestSquared)
        {
            foreach (int index in candidateIndices)
                currentBestSquared = DistanceSquaredWithin(direction, index, currentBestSquared);
            return currentBestSquared;
        }

        /// <summary>
        /// Returns min(currentBestSquared, distance squared) in map units. The
        /// control hull rejects distant edges before cached segment seeding and
        /// analytic closest-point refinement. No allocations or trigonometry.
        /// </summary>
        public float DistanceSquaredWithin(Vector3 direction, int a, int b, float currentBestSquared)
        {
            if (a > b) (a, b) = (b, a);
            return DistanceSquaredWithin(direction, edgeIndices[Key(a, b)], currentBestSquared);
        }

        float DistanceSquaredWithin(Vector3 direction, int index, float currentBestSquared)
        {
            ref readonly Curve curve = ref curves[index];
            Vector3 local = (direction - curve.Start) * radius;
            // Scalar broad-phase avoids temporary vector chains in Mono's
            // hottest rejected-query path. Most cached neighboring rivers are
            // outside the three-unit influence band and never need refinement.
            float outsideX = local.x < curve.Minimum.x ? curve.Minimum.x - local.x : local.x > curve.Maximum.x ? local.x - curve.Maximum.x : 0;
            float outsideY = local.y < curve.Minimum.y ? curve.Minimum.y - local.y : local.y > curve.Maximum.y ? local.y - curve.Maximum.y : 0;
            float outsideZ = local.z < curve.Minimum.z ? curve.Minimum.z - local.z : local.z > curve.Maximum.z ? local.z - curve.Maximum.z : 0;
            if (outsideX * outsideX + outsideY * outsideY + outsideZ * outsideZ >= currentBestSquared) return currentBestSquared;
            // A chord-only starting parameter can converge poorly outside a
            // rounded bend. Eight cached segments give a stable global seed.
            float t = 0, seedDistance = float.PositiveInfinity;
            for (int sample = 0; sample < SegmentsPerCurve; sample++)
            {
                ref readonly Segment segment = ref segments[index * SegmentsPerCurve + sample];
                Vector3 offset = local - segment.Start;
                float along = Mathf.Clamp01(Vector3.Dot(offset, segment.Delta) * segment.InverseLengthSquared);
                float distance = (offset - segment.Delta * along).sqrMagnitude;
                if (distance < seedDistance)
                { seedDistance = distance; t = (sample + along) / SegmentsPerCurve; }
            }
            float best = Mathf.Min(currentBestSquared, Mathf.Min(local.sqrMagnitude, (local - curve.Chord).sqrMagnitude));
            for (int iteration = 0; iteration < 4; iteration++)
            {
                Sample(curve, t, out Vector3 point, out Vector3 derivative);
                Vector3 delta = (direction - point) * radius;
                best = Mathf.Min(best, delta.sqrMagnitude);
                if (iteration == 3) break;
                Vector3 rawLocal = (curve.Linear + (curve.Quadratic + curve.Cubic * t) * t) * t;
                Vector3 raw = curve.Start + rawLocal * inverseRadius;
                float inverseLength = 1f / Mathf.Sqrt(raw.sqrMagnitude);
                Vector3 rawDerivative = curve.Linear + (curve.Quadratic * 2f + curve.Cubic * (3f * t)) * t;
                Vector3 rawSecond = curve.Quadratic * 2f + curve.Cubic * (6f * t);
                Vector3 second = (rawSecond - point * Vector3.Dot(point, rawSecond)
                    - derivative * (2f * Vector3.Dot(point, rawDerivative) * inverseRadius)
                    - point * (Vector3.Dot(derivative, rawDerivative) * inverseRadius)) * inverseLength;
                float denominator = derivative.sqrMagnitude - Vector3.Dot(delta, second);
                if (denominator < derivative.sqrMagnitude * .05f) denominator = derivative.sqrMagnitude;
                float advance = Vector3.Dot(delta, derivative) / Mathf.Max(denominator, .000001f);
                t = Mathf.Clamp01(t + Mathf.Clamp(advance, -.125f, .125f));
            }
            return best;
        }

        static ulong Key(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;
        static Vector3 Minimum(Vector3 a, Vector3 b) => new Vector3(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Min(a.z, b.z));
        static Vector3 Maximum(Vector3 a, Vector3 b) => new Vector3(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z));
    }
}
