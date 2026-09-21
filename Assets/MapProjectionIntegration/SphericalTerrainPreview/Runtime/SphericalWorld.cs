using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>Presentation attributes sampled from the authored world, keyed by real sphere cell ID.</summary>
    public struct SphericalCellData
    {
        public int SourceIndex, Country, Biome, Landform, SourceLandform, Vegetation;
        public int VegetationDensity, VegetationTint, TerrainRotation, MountainMode, PlantLevel;
        // Read-only presentation exclusions from the existing map payload.
        public int UrbanLevel, FarmLevel, SpecialIndex;
        public bool Water, HasRiver;
        public float Elevation;
        public Color CountryColor;
        // RiverNeighbor is a convenient first incident edge, NOT a hydraulic downstream direction.
        public int RiverNeighbor, RiverEdgeMask;
    }

    public struct SphericalRiverEdge
    {
        public int CornerA, CornerB, CellA, CellB;
    }

    /// <summary>A single authored source-to-mouth stroke on the sphere's actual corner graph.</summary>
    public sealed class SphericalRiverRoute
    {
        public string Name;
        public int[] Corners;
        public int Source => Corners[0];
        public int Mouth => Corners[Corners.Length - 1];
    }

    /// <summary>
    /// The existing IcoSphere's actual dual topology. No flat terrain vertices are projected here.
    /// All directions are unit length. Longitude/latitude APIs use degrees. Polygon winding is outward;
    /// Neighbors[cell][edge] is across Corners[cell][edge] to Corners[cell][edge + 1].
    /// </summary>
    public sealed partial class SphericalWorld
    {
        public readonly Vector3[] Centers;
        public readonly int[][] Neighbors;
        public readonly Vector3[][] Corners;
        public readonly int[][] CornerIds;
        public readonly Vector3[] CornerDirections;
        public readonly SphericalCellData[] Cells;
        public readonly int[] Pentagons;
        public readonly SphericalRiverEdge[] RiverEdges;
        public readonly global::IcoSphere.Pack Pack;
        public int Count => Centers.Length;
        public float Radius { get; }
        public float CellRadius { get; }
        public int SourceRiverEdgeCount { get; private set; }
        public int CollapsedSourceRiverEdges { get; private set; }
        public SphericalRiverRoute[] RiverRoutes { get; private set; }
        public string[] OmittedRiverRoutes { get; private set; }

        const float SourceVMin = 0.15f, SourceVMax = 0.8888889f;
        readonly int[] seeds;
        readonly int seedColumns, seedRows;

        public static SphericalWorld Load(int recursion, float radius)
        {
            if (recursion < 0 || recursion > 5) throw new ArgumentOutOfRangeException(nameof(recursion));
            if (!(radius > 0f)) throw new ArgumentOutOfRangeException(nameof(radius));
            return PrepareLoad(recursion, radius)();
        }

        // Resource APIs run on the main thread. Topology and geographic mapping
        // can then be constructed without blocking scene interaction.
        public static Func<SphericalWorld> PrepareLoad(int recursion, float radius, CancellationToken cancellation = default)
        {
            if (recursion < 0 || recursion > 5) throw new ArgumentOutOfRangeException(nameof(recursion));
            if (!(radius > 0)) throw new ArgumentOutOfRangeException(nameof(radius));
            cancellation.ThrowIfCancellationRequested();
            string nativePath = NativeSnapshotPath(recursion);
            if (File.Exists(nativePath))
                return () => ReadNativeSnapshot(nativePath, recursion, radius, cancellation);
            var pack = global::IcoSphere.Pack.Read(recursion);
            cancellation.ThrowIfCancellationRequested();
            var source = SourceMap.Load(recursion);
            cancellation.ThrowIfCancellationRequested();
            return () => new SphericalWorld(pack, radius, source, cancellation);
        }

        SphericalWorld(global::IcoSphere.Pack pack, float radius, SourceMap source, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (pack.verts == null || pack.tris == null || pack.abuts == null ||
                pack.abuts.Length != pack.verts.Length)
                throw new InvalidDataException("The IcoSphere topology resource is missing or incomplete.");
            Pack = pack;
            Radius = radius;
            Centers = new Vector3[pack.verts.Length];
            CornerDirections = new Vector3[pack.tris.Length];
            for (int i = 0; i < Centers.Length; i++)
            { if ((i & 511) == 0) cancellation.ThrowIfCancellationRequested(); Centers[i] = pack.verts[i].normalized; }
            for (int i = 0; i < CornerDirections.Length; i++)
            {
                if ((i & 511) == 0) cancellation.ThrowIfCancellationRequested();
                global::IcoSphere.Tri t = pack.tris[i];
                // The original IcoSphere renderer uses triangle barycentres for its dual corners.
                // Compute each once so all three incident cells receive bit-identical positions.
                CornerDirections[i] = (pack.verts[t[0]] + pack.verts[t[1]] + pack.verts[t[2]]).normalized;
            }

            Neighbors = new int[Count][];
            CornerIds = new int[Count][];
            Corners = new Vector3[Count][];
            Cells = new SphericalCellData[Count];
            var pentagons = new List<int>(12);
            var triangleScratch = new int[6];
            var angleScratch = new float[6];
            double radiusSum = 0;
            int radiusSamples = 0;
            for (int cell = 0; cell < Count; cell++)
            {
                if ((cell & 511) == 0) cancellation.ThrowIfCancellationRequested();
                int count = 0;
                global::IcoSphere.HexAbuts abuts = pack.abuts[cell];
                for (int slot = 0; slot < 6; slot++)
                {
                    // Existing resources put the pentagon sentinel in slot 5, not slot 0.
                    if (abuts.V(slot) < 0) continue;
                    global::IcoSphere.Abut edge = abuts.A(slot);
                    for (int side = 0; side < 2; side++)
                    {
                        int triangle = edge[side];
                        if (triangle < 0 || triangle >= CornerDirections.Length)
                            throw new InvalidDataException("Invalid dual corner in sphere cell " + cell);
                        bool found = false;
                        for (int k = 0; k < count; k++) found |= triangleScratch[k] == triangle;
                        if (!found)
                        {
                            if (count == 6) throw new InvalidDataException("Sphere polygon has more than six corners.");
                            triangleScratch[count++] = triangle;
                        }
                    }
                }
                if (count != 5 && count != 6) throw new InvalidDataException("Sphere polygon must have five or six corners.");
                if (count == 5) pentagons.Add(cell);
                Vector3 normal = Centers[cell];
                Vector3 axisX = Vector3.Cross(Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right, normal).normalized;
                Vector3 axisY = Vector3.Cross(normal, axisX);
                for (int j = 0; j < count; j++)
                {
                    Vector3 corner = CornerDirections[triangleScratch[j]];
                    angleScratch[j] = Mathf.Atan2(Vector3.Dot(corner, axisY), Vector3.Dot(corner, axisX));
                }
                // Tiny insertion sort avoids allocating a comparer or a temporary list per cell.
                for (int j = 1; j < count; j++)
                {
                    int id = triangleScratch[j], k = j - 1;
                    float angle = angleScratch[j];
                    while (k >= 0 && angleScratch[k] > angle)
                    {
                        triangleScratch[k + 1] = triangleScratch[k];
                        angleScratch[k + 1] = angleScratch[k--];
                    }
                    triangleScratch[k + 1] = id;
                    angleScratch[k + 1] = angle;
                }
                int[] ids = CornerIds[cell] = new int[count];
                Vector3[] corners = Corners[cell] = new Vector3[count];
                int[] neighbors = Neighbors[cell] = new int[count];
                for (int j = 0; j < count; j++)
                {
                    ids[j] = triangleScratch[j];
                    corners[j] = CornerDirections[ids[j]];
                    neighbors[j] = OtherSharedCell(ids[j], triangleScratch[(j + 1) % count], cell);
                    radiusSum += Vector3.Distance(normal, corners[j]);
                    radiusSamples++;
                }
                Cells[cell] = source.Sample(normal, cell);
            }
            if (pentagons.Count != 12) throw new InvalidDataException("A complete sphere must contain the twelve existing pentagons.");
            Pentagons = pentagons.ToArray();
            CellRadius = (float)(radiusSum / radiusSamples) * radius;

            seedRows = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(Count / 100f)), 2, 90);
            seedColumns = seedRows * 2;
            seeds = new int[seedRows * seedColumns];
            for (int i = 0; i < seeds.Length; i++) seeds[i] = -1;
            for (int i = 0; i < Count; i++)
            {
                if ((i & 511) == 0) cancellation.ThrowIfCancellationRequested();
                // One arbitrary local seed is sufficient; exact nearest-cell lookup walks real neighbors.
                int bin = SeedBin(Centers[i]);
                if (seeds[bin] < 0) seeds[bin] = i;
            }
            RiverEdges = DrawSphericalRivers(source, cancellation);
            // Routes retain their authored spherical topology. Only local art is
            // reclassified; coast recipes and climate keep SourceLandform.
            source.ApplyLandforms(Cells, cancellation);
        }

        int OtherSharedCell(int cornerA, int cornerB, int except)
        {
            global::IcoSphere.Tri a = Pack.tris[cornerA], b = Pack.tris[cornerB];
            for (int i = 0; i < 3; i++)
            {
                int cell = a[i];
                if (cell == except) continue;
                if (cell == b[0] || cell == b[1] || cell == b[2]) return cell;
            }
            throw new InvalidDataException("Consecutive dual corners do not share an edge.");
        }

        int SeedBin(Vector3 direction)
        {
            float u = Mathf.Atan2(direction.z, direction.x) / (2f * Mathf.PI) + 0.5f;
            float v = LatitudeRadians(direction) / Mathf.PI + 0.5f;
            return Mathf.Clamp((int)(v * seedRows), 0, seedRows - 1) * seedColumns +
                Mod((int)(u * seedColumns), seedColumns);
        }

        static double Dot(Vector3 a, Vector3 b) => (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z;
        static float LatitudeRadians(Vector3 direction) =>
            Mathf.Atan2(direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z));

        public int FindCell(Vector3 unitDirection, int hint = -1)
        {
            if (unitDirection.sqrMagnitude < 1e-12f) return hint >= 0 && hint < Count ? hint : 0;
            Vector3 direction = unitDirection.normalized;
            int current = hint;
            if (current < 0 || current >= Count || Dot(direction, Centers[current]) < 0.995)
            {
                int bin = SeedBin(direction);
                current = seeds[bin];
                if (current < 0)
                {
                    int row = bin / seedColumns, column = bin % seedColumns;
                    for (int ring = 1; current < 0 && ring <= seedRows; ring++)
                    {
                        for (int y = Math.Max(0, row - ring); y <= Math.Min(seedRows - 1, row + ring); y++)
                        for (int x = -ring; x <= ring; x++)
                        {
                            int candidate = seeds[y * seedColumns + Mod(column + x, seedColumns)];
                            if (candidate >= 0 && (current < 0 || Dot(direction, Centers[candidate]) > Dot(direction, Centers[current])))
                                current = candidate;
                        }
                    }
                }
                if (current < 0) current = 0;
            }
            // Delaunay-neighbor hill climbing gives the closest centre without longitude seam branches.
            // Double precision dot products prevent premature ties on the dense R5 graph.
            for (int steps = 0; steps < Count; steps++)
            {
                int next = current;
                double best = Dot(direction, Centers[current]);
                int[] adjacent = Neighbors[current];
                for (int i = 0; i < adjacent.Length; i++)
                {
                    int candidate = adjacent[i];
                    double score = Dot(direction, Centers[candidate]);
                    if (score > best || (score == best && candidate < next))
                    {
                        next = candidate;
                        best = score;
                    }
                }
                if (next == current) return current;
                current = next;
            }
            throw new InvalidDataException("Nearest sphere cell search did not converge.");
        }

        public Vector3 Direction(float longitude, float latitude)
        {
            float lon = longitude * Mathf.Deg2Rad, lat = Mathf.Clamp(latitude, -90f, 90f) * Mathf.Deg2Rad;
            float c = Mathf.Cos(lat);
            return new Vector3(c * Mathf.Cos(lon), Mathf.Sin(lat), c * Mathf.Sin(lon));
        }

        /// <summary>
        /// Exact polygon lookup for selection. Barycentric dual edges are slightly different from
        /// closest-centre Voronoi edges, so rendering/picking should use this refinement when needed.
        /// </summary>
        public int FindContainingCell(Vector3 direction, int hint = -1)
        {
            int cell = FindCell(direction, hint);
            for (int step = 0; step < 32; step++)
            {
                int across = -1;
                double outside = -1e-12;
                Vector3[] corners = Corners[cell];
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector3 a = corners[i], b = corners[(i + 1) % corners.Length];
                    double side = ((double)a.y * b.z - (double)a.z * b.y) * direction.x +
                        ((double)a.z * b.x - (double)a.x * b.z) * direction.y +
                        ((double)a.x * b.y - (double)a.y * b.x) * direction.z;
                    if (side < outside) { outside = side; across = i; }
                }
                if (across < 0) return cell;
                cell = Neighbors[cell][across];
            }
            throw new InvalidDataException("Sphere polygon containment search did not converge.");
        }

        public Vector2 LonLat(Vector3 direction)
        {
            direction.Normalize();
            return new Vector2(Mathf.Atan2(direction.z, direction.x), LatitudeRadians(direction)) * Mathf.Rad2Deg;
        }

        int FindCorner(Vector3 direction)
        {
            int cell = FindCell(direction);
            int bestId = -1;
            double best = -2;
            for (int group = -1; group < Neighbors[cell].Length; group++)
            {
                int[] ids = CornerIds[group < 0 ? cell : Neighbors[cell][group]];
                for (int k = 0; k < ids.Length; k++)
                {
                    int id = ids[k];
                    double score = Dot(direction, CornerDirections[id]);
                    if (score > best || (score == best && id < bestId)) { best = score; bestId = id; }
                }
            }
            return bestId;
        }

        sealed class RiverStroke
        {
            public readonly string Name;
            public readonly float[] LonLat;
            public RiverStroke(string name, params float[] lonLat) { Name = name; LonLat = lonLat; }
        }

        // Deliberately sparse, new source-to-mouth strokes authored in sphere coordinates.
        // These are art routes for this preview, not imported flat edge masks or a GIS dataset.
        // Keep the Nile and Yarlung Tsangpo bends at the existing river observation views.
        static readonly RiverStroke[] RiverStrokes =
        {
            new("Nile", 32,4, 31.6f,8, 32.5f,12, 32.6f,15.6f, 33.6f,18,
                32.3f,19.2f, 30.5f,19.8f, 30.3f,21.5f, 32.8f,24, 32.4f,26,
                31.2f,28.5f, 31.1f,30.2f, 31.2f,31.5f),
            new("Congo", 26.5f,-9, 26,-5, 25.5f,-1, 23,1, 20.5f,1.5f,
                18,0, 16,-2.5f, 15.3f,-4.3f, 12.4f,-6),
            new("Niger", -10.7f,9.5f, -8,12, -5.5f,13.5f, -3.2f,16.7f,
                0,16, 2,14, 4,12, 6,9, 6.7f,6, 6.2f,4.4f),
            new("Zambezi", 23.5f,-12, 23,-16, 25.8f,-17.8f, 28,-16,
                30,-15.7f, 33,-17, 36.5f,-18.5f),
            new("Orange", 29,-29.5f, 26,-30.5f, 23,-29.5f, 20,-28.5f, 16.5f,-28.6f),
            new("Amazon", -73,-4, -70,-4.3f, -66,-3.5f, -62,-3.6f,
                -60,-3.1f, -56,-2, -52,-1.5f, -49.7f,-.4f),
            new("Parana", -51,-20, -54,-23, -54.5f,-25.5f, -58,-27.5f,
                -59,-30, -60.5f,-32.5f, -58.4f,-34.5f),
            new("Orinoco", -64,3, -67,4, -67,6, -65,8, -62,8.3f, -60.4f,9.2f),
            new("Mississippi", -94.5f,47.2f, -93,45, -91,42, -90.2f,38.7f,
                -89,36, -91,33, -91.5f,31, -90.2f,29.7f, -89.2f,29.1f),
            new("Mackenzie", -118,61, -121,62, -124,64, -127,65.5f,
                -130,67.5f, -134,68.5f, -135,69.5f),
            new("Yukon", -134,61, -137,63, -141,64.5f, -146,65.5f,
                -151,65, -157,63, -163,62.6f, -165,62.5f),
            new("Rhine", 9.4f,46.8f, 9.5f,47.4f, 7.7f,47.6f, 8.4f,49,
                7.1f,50.4f, 6.2f,51.8f, 4.4f,51.9f),
            new("Danube", 9,48, 12,48.7f, 16.4f,48.1f, 19,47.6f,
                19,45.4f, 21.1f,44.8f, 25,44.1f, 28.2f,45.3f, 29.7f,45.2f),
            new("Dnieper", 33,55, 30.5f,53.5f, 30.5f,50.5f, 33,49,
                35,48.4f, 34.5f,47.5f, 32,46.5f),
            new("Volga", 32.9f,57.2f, 38,57.6f, 44,56.3f, 48.8f,55.1f,
                48.1f,52, 44.6f,48.7f, 48,46.4f, 48.6f,45.7f),
            new("Ob", 84,52, 83,55, 80,58, 75,60.5f, 69,61,
                65,64, 66.5f,66, 69,66.5f, 72,68.7f),
            new("Yenisei", 94,51.5f, 92,54, 93,56, 91,59, 88,63,
                87,66, 86,68, 83,71.5f),
            new("Lena", 106,54, 108,56, 115,59, 123,60, 129,62,
                126,65, 123,68, 126,72.3f),
            new("Amur", 121,53.3f, 126,53, 128,50, 132,48.3f,
                135,48.5f, 137,50, 140,52.9f),
            new("Yangtze", 92,33, 96,32.5f, 98,29.5f, 100,27.5f,
                102,26.5f, 104,28.7f, 106.5f,29.5f, 110,31, 112,30.4f,
                114.3f,30.6f, 116.5f,29.8f, 119,32, 121.9f,31.4f),
            new("Yellow", 96,35, 100,35, 103,36, 106,38, 107,40.5f,
                110.5f,40.5f, 110.5f,36, 111,34.7f, 114,35, 117,36.5f, 119,37.7f),
            new("Yarlung Tsangpo / Brahmaputra", 82,30.5f, 86,29.7f, 90,29.4f,
                94.2f,29.1f, 95.2f,28.3f, 94.2f,27.3f, 91.5f,26.1f,
                89.7f,25.6f, 89.7f,23.7f, 90.5f,22.3f),
            new("Mekong", 96,31, 98,27, 100,23, 101,20, 103,18,
                105.7f,17, 105.8f,14, 105,12, 105.5f,10.5f, 106.7f,9.7f),
            new("Irrawaddy", 97.5f,26, 97,24, 96,22, 94.9f,20,
                95,18, 95.3f,16),
            new("Indus", 80,31.5f, 76,34.5f, 74,35.5f, 72.5f,34.7f,
                71,32, 70.5f,29.5f, 68.5f,27, 67.5f,24),
            new("Murray", 148,-36.5f, 146,-36, 144,-35.8f,
                142,-34.2f, 140,-34, 139.5f,-35.2f, 138.9f,-35.5f)
        };

        SphericalRiverEdge[] DrawSphericalRivers(SourceMap source, CancellationToken cancellation)
        {
            // Preserve the old payload count for comparisons only. Its edges never feed routing.
            SourceRiverEdgeCount = source.CountRiverEdges(cancellation);
            var waterCorners = new byte[CornerDirections.Length];
            for (int i = 0; i < waterCorners.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                global::IcoSphere.Tri t = Pack.tris[i];
                waterCorners[i] = (byte)((Cells[t[0]].Water ? 1 : 0) +
                    (Cells[t[1]].Water ? 1 : 0) + (Cells[t[2]].Water ? 1 : 0));
            }
            var occupied = new HashSet<int>();
            var routes = new List<SphericalRiverRoute>();
            var omitted = new List<string>();
            var edges = new List<SphericalRiverEdge>();
            foreach (RiverStroke stroke in RiverStrokes)
            {
                cancellation.ThrowIfCancellationRequested();
                var route = new List<int>();
                var visited = new HashSet<int>();
                string failure = null;
                for (int waypoint = 0; waypoint < stroke.LonLat.Length / 2; waypoint++)
                {
                    bool mouth = waypoint == stroke.LonLat.Length / 2 - 1;
                    Vector3 direction = Direction(stroke.LonLat[waypoint * 2], stroke.LonLat[waypoint * 2 + 1]);
                    int corner = FindStrokeCorner(direction, mouth, waterCorners, occupied, cancellation);
                    if (corner < 0) { failure = "no suitable land/coast anchor"; break; }
                    if (route.Count == 0) { route.Add(corner); visited.Add(corner); continue; }
                    int start = route[route.Count - 1];
                    if (start == corner) continue;
                    if (visited.Contains(corner)) { failure = "waypoints collapse onto an earlier bend"; break; }
                    List<int> leg = RouteStroke(start, corner, mouth, waterCorners, occupied, visited, cancellation);
                    if (leg == null) { failure = "unreachable waypoint " + waypoint; break; }
                    for (int j = 1; j < leg.Count; j++) { route.Add(leg[j]); visited.Add(leg[j]); }
                }
                // Never publish a partial stroke: it would reintroduce isolated river fragments.
                // Tiny topology previews may not resolve every authored bend; report that explicitly.
                if (failure != null || route.Count < 3)
                { omitted.Add(stroke.Name + ": " + (failure ?? "below graph resolution")); continue; }
                routes.Add(new SphericalRiverRoute { Name = stroke.Name, Corners = route.ToArray() });
                foreach (int corner in route) occupied.Add(corner);
                for (int i = 1; i < route.Count; i++)
                {
                    int a = Math.Min(route[i - 1], route[i]), b = Math.Max(route[i - 1], route[i]);
                    int cellA = OtherSharedCell(a, b, -1), cellB = OtherSharedCell(a, b, cellA);
                    edges.Add(new SphericalRiverEdge { CornerA = a, CornerB = b, CellA = cellA, CellB = cellB });
                    MarkRiver(cellA, cellB, a, b); MarkRiver(cellB, cellA, a, b);
                }
            }
            RiverRoutes = routes.ToArray();
            OmittedRiverRoutes = omitted.ToArray();
            edges.Sort((a, b) => a.CornerA != b.CornerA ? a.CornerA.CompareTo(b.CornerA) : a.CornerB.CompareTo(b.CornerB));
            return edges.ToArray();
        }

        int FindStrokeCorner(Vector3 direction, bool mouth, byte[] water, HashSet<int> occupied, CancellationToken cancellation)
        {
            int bestId = -1;
            double best = -2;
            for (int i = 0; i < CornerDirections.Length; i++)
            {
                if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                // A two-water corner places the mouth just into the actual shore blend.
                if (water[i] != (mouth ? 2 : 0)) continue;
                double score = Dot(direction, CornerDirections[i]);
                if (score <= best || occupied.Contains(i)) continue;
                best = score; bestId = i;
            }
            return bestId;
        }

        struct RiverSearchNode
        {
            public int Id;
            public float Cost, Estimate;
        }

        // Small binary heap keeps long continental strokes linearithmic without relying on
        // PriorityQueue, which is absent from the Unity runtime's .NET Standard profile.
        static void PushRiverNode(List<RiverSearchNode> heap, RiverSearchNode node)
        {
            int child = heap.Count; heap.Add(node);
            while (child > 0)
            {
                int parent = (child - 1) / 2;
                if (heap[parent].Estimate <= node.Estimate) break;
                heap[child] = heap[parent]; child = parent;
            }
            heap[child] = node;
        }

        static RiverSearchNode PopRiverNode(List<RiverSearchNode> heap)
        {
            RiverSearchNode result = heap[0], tail = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return result;
            int parent = 0;
            while (parent * 2 + 1 < heap.Count)
            {
                int child = parent * 2 + 1;
                if (child + 1 < heap.Count && heap[child + 1].Estimate < heap[child].Estimate) child++;
                if (tail.Estimate <= heap[child].Estimate) break;
                heap[parent] = heap[child]; parent = child;
            }
            heap[parent] = tail; return result;
        }

        List<int> RouteStroke(int start, int finish, bool mouth, byte[] water,
            HashSet<int> occupied, HashSet<int> visited, CancellationToken cancellation)
        {
            Vector3 from = CornerDirections[start], to = CornerDirections[finish], chord = to - from;
            float lengthSquared = chord.sqrMagnitude, unit = CellRadius / Radius;
            var heap = new List<RiverSearchNode>(256);
            var costs = new Dictionary<int, float>(256) { [start] = 0 };
            var parents = new Dictionary<int, int>(256) { [start] = -1 };
            PushRiverNode(heap, new RiverSearchNode { Id = start, Estimate = chord.magnitude });
            int iterations = 0;
            while (heap.Count > 0 && iterations < 150000)
            {
                if ((iterations++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
                RiverSearchNode current = PopRiverNode(heap);
                if (current.Cost > costs[current.Id]) continue;
                if (current.Id == finish)
                {
                    var result = new List<int>();
                    for (int id = finish; id >= 0; id = parents[id]) result.Add(id);
                    result.Reverse(); return result;
                }
                global::IcoSphere.Tri adjacent = Pack.adjTris[current.Id];
                for (int side = 0; side < 3; side++)
                {
                    int next = adjacent[side];
                    if (occupied.Contains(next) || (next != start && visited.Contains(next))) continue;
                    Vector3 direction = CornerDirections[next];
                    // Inland strokes stay on dry corners. Only the last few cells may enter
                    // the coastal blend, so the shortest path cannot shortcut across an ocean.
                    if (water[next] > 0 && (!mouth || water[next] == 3 ||
                        (direction - to).sqrMagnitude > unit * unit * 36f)) continue;
                    int cellA = OtherSharedCell(current.Id, next, -1);
                    int cellB = OtherSharedCell(current.Id, next, cellA);
                    if (Cells[cellA].Water && Cells[cellB].Water) continue;
                    float along = Mathf.Clamp01(Vector3.Dot(direction - from, chord) / Mathf.Max(lengthSquared, 1e-10f));
                    float lateral = Vector3.Distance(direction, (from + chord * along).normalized) / unit;
                    float mountain = (Cells[cellA].Landform == 2 ? .2f : 0) + (Cells[cellB].Landform == 2 ? .2f : 0);
                    float step = Vector3.Distance(CornerDirections[current.Id], direction);
                    float cost = current.Cost + step * (1 + Mathf.Min(lateral, 12) * .12f + mountain + water[next] * 3);
                    if (costs.TryGetValue(next, out float previous) && cost >= previous) continue;
                    costs[next] = cost; parents[next] = current.Id;
                    PushRiverNode(heap, new RiverSearchNode { Id = next, Cost = cost,
                        Estimate = cost + Vector3.Distance(direction, to) });
                }
            }
            return null;
        }

        void MarkRiver(int cell, int neighbor, int a, int b)
        {
            SphericalCellData data = Cells[cell];
            int[] ids = CornerIds[cell];
            for (int i = 0; i < ids.Length; i++)
            {
                int c = ids[i], d = ids[(i + 1) % ids.Length];
                if ((c == a && d == b) || (c == b && d == a))
                {
                    data.RiverEdgeMask |= 1 << i;
                    data.HasRiver = true;
                    if (data.RiverNeighbor < 0) data.RiverNeighbor = neighbor;
                    Cells[cell] = data;
                    return;
                }
            }
            throw new InvalidDataException("An authored river edge was absent from its incident sphere polygon.");
        }

        static ulong EdgeKey(int a, int b) => a < b ? ((ulong)(uint)a << 32) | (uint)b : ((ulong)(uint)b << 32) | (uint)a;
        static int Mod(int value, int modulus) { int r = value % modulus; return r < 0 ? r + modulus : r; }

        sealed class SourceMap
        {
            public int Width, Height;
            int cellOffset, stride;
            byte[] bytes;
            byte[] polarMask;
            byte[] landforms, topologyBytes;
            int recursion;
            int polarWidth, polarHeight;
            Color[] palette;

            public static SourceMap Load(int recursion)
            {
                TextAsset asset = Resources.Load<TextAsset>("Maps/DefaultWorld");
                if (!asset) throw new InvalidDataException("Maps/DefaultWorld is required for the spherical art preview.");
                var map = new SourceMap { bytes = asset.bytes, recursion = recursion };
                // Capture all Unity resources here, before PrepareLoad's factory
                // executes on a worker. Each topology level has its own bake.
                TextAsset landforms = Resources.Load<TextAsset>("SphericalTerrainPreview/LandformsR" + recursion);
                TextAsset topology = Resources.Load<TextAsset>("Bin/pack_arr_" + recursion);
                if (!landforms || !topology)
                    throw new InvalidDataException("The spherical landform layer is missing. Run bake_global_landforms.py.");
                map.landforms = landforms.bytes;
                map.topologyBytes = topology.bytes;
                TextAsset polar = Resources.Load<TextAsset>("SphericalTerrainPreview/PolarLandMask");
                if (!polar) throw new InvalidDataException("The baked polar land mask is missing from the spherical preview.");
                map.polarMask = polar.bytes;
                using (var reader = new BinaryReader(new MemoryStream(map.polarMask, false)))
                {
                    if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "SPOLMASK")
                        throw new InvalidDataException("Invalid spherical preview polar mask.");
                    map.polarWidth = reader.ReadInt32();
                    map.polarHeight = reader.ReadInt32();
                    if (map.polarWidth <= 0 || map.polarHeight <= 0 ||
                        16L + (long)map.polarWidth * map.polarHeight != map.polarMask.Length)
                        throw new InvalidDataException("Truncated spherical preview polar mask.");
                }
                using (var reader = new BinaryReader(new MemoryStream(map.bytes, false)))
                {
                    int version = reader.ReadInt32();
                    if (version < 11 || version > 13) throw new InvalidDataException("Unsupported DefaultWorld schema " + version);
                    map.Width = reader.ReadInt32();
                    map.Height = reader.ReadInt32();
                    bool wrapping = reader.ReadBoolean();
                    reader.ReadBoolean();
                    int count = reader.ReadUInt16();
                    if (map.Width <= 0 || map.Height <= 0 || !wrapping) throw new InvalidDataException("Expected the wrapping authored world.");
                    map.palette = new Color[count];
                    for (int i = 0; i < count; i++)
                        map.palette[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                    map.cellOffset = (int)reader.BaseStream.Position;
                    map.stride = version >= 13 ? 21 : 20;
                    if ((long)map.cellOffset + (long)map.Width * map.Height * map.stride + 4 > map.bytes.Length)
                        throw new InvalidDataException("Truncated DefaultWorld cell payload.");
                }
                return map;
            }

            public void ApplyLandforms(SphericalCellData[] cells, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                using var reader = new BinaryReader(new MemoryStream(landforms, false));
                if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "SPHLF001" ||
                    reader.ReadInt32() != recursion || reader.ReadInt32() != cells.Length ||
                    landforms.Length != 80L + cells.Length)
                    throw new InvalidDataException("The spherical landform layer does not match this topology.");
                using var sha = System.Security.Cryptography.SHA256.Create();
                void CheckSource(byte[] data)
                {
                    byte[] expected = reader.ReadBytes(32), actual = sha.ComputeHash(data);
                    for (int i = 0; i < 32; i++) if (expected[i] != actual[i])
                        throw new InvalidDataException("Spherical landforms have stale source data. Re-run bake_global_landforms.py.");
                    cancellation.ThrowIfCancellationRequested();
                }
                CheckSource(bytes); CheckSource(topologyBytes);
                for (int i = 0; i < cells.Length; i++)
                {
                    if ((i & 511) == 0) cancellation.ThrowIfCancellationRequested();
                    int form = landforms[80 + i];
                    if (form > 2) throw new InvalidDataException("Invalid local spherical landform.");
                    cells[i].Landform = cells[i].Water ? 0 : form;
                }
            }

            public SphericalCellData Sample(Vector3 direction, int sphereCell)
            {
                float u = Mathf.Atan2(direction.z, direction.x) / (2f * Mathf.PI) + 0.5f;
                float v = LatitudeRadians(direction) / Mathf.PI + 0.5f;
                if (v < SourceVMin || v > SourceVMax)
                {
                    int px = Mod((int)(u * polarWidth), polarWidth);
                    int py = Mathf.Clamp((int)((1f - v) * polarHeight), 0, polarHeight - 1);
                    bool polarWater = polarMask[16 + py * polarWidth + px] < 128;
                    return new SphericalCellData { SourceIndex = -1, Country = 0, Biome = 4, Water = polarWater, SourceLandform = 0,
                        RiverNeighbor = -1, CountryColor = Color.clear };
                }
                // Exact cube rounding used by the active flat map, including staggered rows and wrap.
                float x = Mathf.Repeat(u, 1f) * Width - 0.5f;
                float z = (v - SourceVMin) / (SourceVMax - SourceVMin) * Height - 0.5f;
                float cubeX = x - z * 0.5f, cubeY = -x - z * 0.5f;
                int ix = Mathf.RoundToInt(cubeX), iy = Mathf.RoundToInt(cubeY), iz = Mathf.RoundToInt(z);
                if (ix + iy + iz != 0)
                {
                    float dx = Mathf.Abs(cubeX - ix), dy = Mathf.Abs(cubeY - iy), dz = Mathf.Abs(z - iz);
                    if (dx > dy && dx > dz) ix = -iy - iz;
                    else if (dz > dy) iz = -ix - iy;
                }
                int row = Mathf.Clamp(iz, 0, Height - 1);
                int col = iz == row ? ix + iz / 2 : Mathf.RoundToInt(x - (row & 1) * 0.5f);
                int index = row * Width + Mod(col, Width);
                int p = cellOffset + index * stride;
                int elevation = bytes[p + 1] - 127;
                bool water = elevation < bytes[p + 2];
                int country = water ? 0 : bytes[p + 18] | (bytes[p + 19] << 8);
                int plantLevel = bytes[p + 5];
                int density = bytes[p + 14];
                if (density == 0 && plantLevel > 0) density = plantLevel == 3 ? 100 : plantLevel * 33;
                return new SphericalCellData
                {
                    SourceIndex = index, Country = country, Biome = bytes[p], Elevation = elevation,
                    Water = water, Landform = Mathf.Clamp(bytes[p + 12], 0, 3),
                    SourceLandform = Mathf.Clamp(bytes[p + 12], 0, 3),
                    Vegetation = Mathf.Clamp(bytes[p + 13], 0, 6), PlantLevel = plantLevel,
                    UrbanLevel = bytes[p + 3], FarmLevel = bytes[p + 4], SpecialIndex = bytes[p + 6],
                    VegetationDensity = water ? 0 : Mathf.Clamp(density, 0, 100),
                    VegetationTint = Mathf.Clamp(bytes[p + 15], 0, 5), TerrainRotation = bytes[p + 16] % 6,
                    MountainMode = stride >= 21 ? Mathf.Clamp(bytes[p + 20], 0, 2) : 0,
                    CountryColor = country > 0 && country < palette.Length ? palette[country] : Color.clear,
                    // River presence is assigned only after connectivity is rebuilt on the sphere graph.
                    HasRiver = false, RiverNeighbor = -1
                };
            }

            public int RiverMask(int index)
            {
                int p = cellOffset + index * stride;
                int mask = bytes[p + 17] & 63;
                if (mask != 0) return mask;
                if (bytes[p + 8] >= 128 && bytes[p + 8] < 134) mask |= 1 << (bytes[p + 8] - 128);
                if (bytes[p + 9] >= 128 && bytes[p + 9] < 134) mask |= 1 << (bytes[p + 9] - 128);
                return mask;
            }

            public int CornerKey(int x2, int z3) => (z3 + 2) * (Width * 2) + Mod(x2, Width * 2);

            public int CountRiverEdges(CancellationToken cancellation)
            {
                var edges = new HashSet<ulong>();
                int[] dx = { 0, 1, 1, 0, -1, -1 }, dz = { 2, 1, -1, -2, -1, 1 };
                for (int i = 0; i < Width * Height; i++)
                {
                    if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    int mask = RiverMask(i), row = i / Width, col = i % Width;
                    for (int e = 0; e < 6; e++) if ((mask & (1 << e)) != 0)
                    {
                        int n = (e + 1) % 6;
                        edges.Add(EdgeKey(CornerKey(col * 2 + (row & 1) + dx[e], row * 3 + dz[e]),
                            CornerKey(col * 2 + (row & 1) + dx[n], row * 3 + dz[n])));
                    }
                }
                return edges.Count;
            }
        }
    }
}
