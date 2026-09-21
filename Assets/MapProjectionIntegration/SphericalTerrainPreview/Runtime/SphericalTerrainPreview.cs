using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>Independent art preview. The only fine cells are the existing IcoSphere dual polygons.</summary>
    public sealed partial class SphericalTerrainPreview : MonoBehaviour
    {
        public HexNearTerrainProfile terrainProfile;
        public HexTerrainStyle terrainStyle;
        public Material waterSourceMaterial, riverSourceMaterial;
        public Texture2D satelliteColor, satelliteRelief;
        public float radius = 3300;
        [Range(3, 5)] public int recursion = 5;
        [Range(6, 24)] public int detailSubdivisions = 20;
        public SphericalWorld World { get; private set; }
        public SphericalSurface Surface { get; private set; }
        public bool IsReady { get; private set; }
        public string Status { get; private set; } = "Preparing spherical terrain";
        public int SelectedCell { get; private set; } = -1;
        public int VisibleDetailCells { get; private set; }
        public int TreeCount { get; private set; }
        public int RiverSegmentCount { get; private set; }
        public bool DetailLoading { get; private set; }
        public float DetailRadius { get; private set; }
        public Vector3 DetailFocus { get; private set; }
        public int TerrainTriangleCount { get; private set; }
        public int ShellTriangleCount { get; private set; }
        public IReadOnlyList<int> DetailCellIds => activeIds;
        Material coarseMaterial, detailMaterial, waterMaterial, fineWaterMaterial, riverMaterial, gridMaterial, selectionMaterial;
        GameObject shell, ocean;
        readonly List<Material> ownedMaterials = new();
        readonly List<int> activeIds = new();
        readonly Dictionary<Material, Material> treeMaterials = new();
        Vector3 focus = new(.1f, .72f, .68f), requestedFocus;
        float altitude = 85, requestedAltitude;
        bool gridVisible;
        LineRenderer selection;
        int lastHint = -1;
        RenderPipelineAsset previousGraphicsPipeline, previousQualityPipeline;
        UniversalRenderPipelineAsset previewPipeline;

        void Awake()
        {
            SnapshotWaterStyle();
            previousGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
            previousQualityPipeline = QualitySettings.renderPipeline;
            var source = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (source)
            {
                previewPipeline = Instantiate(source); previewPipeline.name = "Spherical terrain preview lighting (runtime)";
                previewPipeline.hideFlags = HideFlags.DontSave;
                // Match the current flat near-terrain renderer on our own clone.
                JsonUtility.FromJsonOverwrite("{\"m_MainLightShadowsSupported\":true,\"m_MainLightShadowmapResolution\":4096,\"m_SoftShadowsSupported\":true,\"m_Cascade4Split\":{\"x\":0.28,\"y\":0.50,\"z\":0.74}}", previewPipeline);
                previewPipeline.shadowCascadeCount = 4;
                // URP 14 exposes this setting only through its serialized field.
                JsonUtility.FromJsonOverwrite("{\"m_SoftShadowQuality\":3}", previewPipeline);
                previewPipeline.cascadeBorder = .16f;
                previewPipeline.shadowDepthBias = .6f;
                previewPipeline.shadowNormalBias = .35f;
                // The game's default 50-unit shadow range does not reach a camera
                // 85 units above the globe. Only this runtime clone is adjusted.
                previewPipeline.shadowDistance = 500;
                InstallLodAvailabilityRenderer();
                GraphicsSettings.defaultRenderPipeline = previewPipeline;
                QualitySettings.renderPipeline = previewPipeline;
            }
        }

        Material Own(Material m) { ownedMaterials.Add(m); return m; }
        public void SetFocus(Vector3 direction, float height)
        { if (direction.sqrMagnitude > .1f) focus = direction.normalized; altitude = Mathf.Max(15, height); }
        List<int> CollectCells(Vector3 n, float range)
        {
            int id = World.FindCell(n); var result = new List<int> { id }; var seen = new HashSet<int> { id };
            float squared = range * range / (radius * radius);
            for (int i = 0; i < result.Count; i++) foreach (int next in World.Neighbors[result[i]])
                if (seen.Add(next) && (World.Centers[next] - n).sqrMagnitude < squared) result.Add(next);
            return result;
        }
        void BuildCellGeometry(MeshBuffer mesh, MeshBuffer sea, int id, int subdivisions)
        {
            Vector3[] corners = World.Corners[id]; Vector3 center = World.Centers[id];
            int vertexCount = checked(corners.Length * (subdivisions + 1) * (subdivisions + 2) / 2);
            int indexCount = checked(corners.Length * subdivisions * subdivisions * 3);
            mesh.Reserve(checked(mesh.Vertices.Count + vertexCount), checked(mesh.Indices.Count + indexCount));
            sea.Reserve(checked(sea.Vertices.Count + vertexCount), checked(sea.Indices.Count + indexCount), water: true);
            for (int edge = 0; edge < corners.Length; edge++)
            {
                Vector3 a = corners[edge], b = corners[(edge + 1) % corners.Length];
                int first = mesh.Vertices.Count;
                for (int row = 0; row <= subdivisions; row++)
                {
                    for (int col = 0; col <= row; col++)
                    {
                        Vector3 n = (center * (1f - row / (float)subdivisions) + a * ((row - col) / (float)subdivisions) + b * (col / (float)subdivisions)).normalized;
                        var s = Surface.Evaluate(n, id);
                        mesh.Add(n * (radius + s.Height), n, s);
                        mesh.WeldKeys.Add(SphericalMeshNormals.VertexKey(id, World.CornerIds[id][edge],
                            World.CornerIds[id][(edge + 1) % corners.Length], row, col, subdivisions));
                        float waterDepth = .004f - s.Height;
                        sea.AddWater(n * (radius + .004f), n,
                            new Vector4(n.x * radius * .02f, n.z * radius * .02f, 1000, waterDepth),
                            new Vector4(1 - s.Land, s.WaterLandDensity),
                            1 - SphericalSurface.Smooth(.03f, 2.4f, Mathf.Max(0, waterDepth)), WaterBiome(s));
                    }
                }
                for (int row = 0; row < subdivisions; row++)
                {
                    int rowStart = first + row * (row + 1) / 2, next = rowStart + row + 1;
                    for (int col = 0; col <= row; col++)
                    {
                        mesh.TriangleOutward(rowStart + col, next + col, next + col + 1);
                        sea.TriangleOutward(rowStart + col, next + col, next + col + 1);
                        if (col < row) { mesh.TriangleOutward(rowStart + col, next + col + 1, rowStart + col + 1);
                            sea.TriangleOutward(rowStart + col, next + col + 1, rowStart + col + 1); }
                    }
                }
            }
        }
        static Vector3 WaterBiome(SphericalSurface.Sample s)
            => s.WaterBiome;

        void AddGrid(MeshBuffer mesh, int id)
        {
            Vector3[] corners = World.Corners[id];
            for (int edge = 0; edge < corners.Length; edge++)
            {
                if (World.Neighbors[id][edge] < id) continue;
                for (int j = 0; j < 8; j++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        Vector3 n = Vector3.Lerp(corners[edge], corners[(edge + 1) % corners.Length], (j + k) / 8f).normalized;
                        var s = Surface.Evaluate(n, id); int v = mesh.Vertices.Count;
                        mesh.Add(n * (radius + Mathf.Max(0, s.Height) + .09f), n, s); mesh.Indices.Add(v);
                    }
                }
            }
        }
        float riverCoreHalfWidth = .7f;
        void SnapshotWaterStyle()
        {
            // A map unit has the same art scale as HexMetrics.outerRadius = 10;
            // the actual irregular polygon radius does not resize water optics.
            riverCoreHalfWidth = Mathf.Max(.001f, terrainStyle ? terrainStyle.hfRiverCarve.x : .07f) * 10;
        }

        void AddRiverWaterVertex(MeshBuffer mesh, Vector3 direction, int hint,
            float acrossDistance, float flowDistance, Vector3 downstream)
        {
            var sample = Surface.Evaluate(direction, hint);
            float height = Mathf.Max(0, sample.RiverSurface);
            float distance = sample.RiverDistance * 2.4f;
            float center = Mathf.Clamp01(1 - distance / (riverCoreHalfWidth + .65f));
            // Signed depth prevents a ribbon from painting over the high bank.
            // Both the water and river bed retain their local plateau datum.
            SphericalSurface.Frame(direction, out Vector3 east, out Vector3 north);
            mesh.AddWater(direction * (radius + height), direction,
                new Vector4(acrossDistance, flowDistance, distance, height - sample.Height),
                new Vector4(1 - sample.Land, sample.WaterLandDensity,
                    Vector3.Dot(downstream, east), Vector3.Dot(downstream, north)),
                1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01(center / .9f)));
        }

        void AddRivers(MeshBuffer mesh, int id, HashSet<ulong> used, ref int count)
        {
            // HFRiverCoreCoverage extends from core*.72 to core+.065 in flat
            // outer-radius coordinates. Cover that complete feathered channel,
            // not the old narrow .58-unit strip with a hard geometric bank.
            float halfWidth = riverCoreHalfWidth + .65f;
            const int across = 8, columns = across + 1, along = 24;
            int mask = World.Cells[id].RiverEdgeMask;
            for (int e = 0; e < World.Corners[id].Length; e++)
            {
                if ((mask & (1 << e)) == 0) continue;
                // The two incident cells can belong to different streaming tiles.
                // A per-tile HashSet alone draws that transparent ribbon twice,
                // making a hard color step where it meets a singly drawn edge.
                // One stable cell owns the whole edge across every chunk build.
                if (World.Neighbors[id][e] < id) continue;
                int a = World.CornerIds[id][e], b = World.CornerIds[id][(e + 1) % World.Corners[id].Length];
                if (a > b) (a, b) = (b, a);
                ulong key = ((ulong)(uint)a << 32) | (uint)b; if (!used.Add(key)) continue;
                count++; int first = mesh.Vertices.Count;
                for (int j = 0; j <= along; j++)
                {
                    float t = j / (float)along;
                    Surface.Rivers.FlowSample(a, b, t, out Vector3 n, out Vector3 downstream, out float flowDistance);
                    Vector3 side = Vector3.Cross(n, downstream).normalized;
                    for (int k = 0; k <= across; k++)
                    {
                        float offset = (k / (float)across * 2 - 1) * halfWidth;
                        Vector3 p = (n + side * (offset / radius)).normalized;
                        AddRiverWaterVertex(mesh, p, id, offset, flowDistance, downstream);
                    }
                    if (j > 0)
                    {
                        int c = first + j * columns;
                        for (int lane = 0; lane < across; lane++)
                        {
                            mesh.TriangleOutward(c - columns + lane, c + lane, c - columns + lane + 1);
                            mesh.TriangleOutward(c - columns + lane + 1, c + lane, c + lane + 1);
                        }
                    }
                }
                // Close sources/mouths with an outward semicircle. A full disc
                // overlaps the first ribbon row and doubles its alpha composite.
                foreach (int corner in new[] { a, b })
                {
                    if (Surface.Rivers.Degree(corner) != 1) continue;
                    if (!used.Add((1UL << 63) | (uint)corner)) continue;
                    Surface.Rivers.FlowSample(corner, corner == a ? b : a, 0,
                        out Vector3 n, out Vector3 downstream, out float flowDistance);
                    Vector3 outward = -Surface.Rivers.Tangent(corner, corner == a ? b : a, 0);
                    Vector3 side = Vector3.Cross(n, outward).normalized;
                    Vector3 flowSide = Vector3.Cross(n, downstream).normalized;
                    // Radial rings carry the same distance/depth recipe as the
                    // ribbon. The diameter shares its terminal cross-section.
                    const int capSegments = 24, capRings = 4;
                    int start = mesh.Vertices.Count;
                    AddRiverWaterVertex(mesh, n, id, 0, flowDistance, downstream);
                    for (int ring = 1; ring <= capRings; ring++)
                    {
                        int current = mesh.Vertices.Count;
                        for (int j = 0; j <= capSegments; j++)
                        {
                            float angle = (j / (float)capSegments - .5f) * Mathf.PI;
                            Vector3 offset = j == 0 ? -side : j == capSegments ? side :
                                outward * Mathf.Cos(angle) + side * Mathf.Sin(angle);
                            offset *= halfWidth * ring / capRings;
                            Vector3 p = (n + offset / radius).normalized;
                            AddRiverWaterVertex(mesh, p, id, Vector3.Dot(offset, flowSide),
                                flowDistance + Vector3.Dot(offset, downstream), downstream);
                            if (j == 0) continue;
                            if (ring == 1) mesh.TriangleOutward(start, current + j - 1, current + j);
                            else
                            {
                                int previous = current - capSegments - 1;
                                mesh.TriangleOutward(previous + j - 1, current + j - 1, current + j);
                                mesh.TriangleOutward(previous + j - 1, current + j, previous + j);
                            }
                        }
                    }
                }
            }
        }
        public void SetGridVisible(bool visible)
        {
            gridVisible = visible;
            foreach (var chunk in chunks.Values) if (chunk.Grid) chunk.Grid.SetActive(visible);
        }
        public void SetSelection(int id)
        {
            if (id == -1) { SelectedCell = -1; if (selection) selection.enabled = false; return; }
            if (World == null || id < 0 || id >= World.Count) return; SelectedCell = id;
            if (!selection)
            { var go = new GameObject("Selected actual spherical cell"); go.transform.SetParent(transform, false); selection = go.AddComponent<LineRenderer>(); selection.sharedMaterial = selectionMaterial; selection.loop = true; selection.useWorldSpace = true; }
            selection.enabled = true;
            Vector3[] polygon = World.Corners[id]; selection.positionCount = polygon.Length * 12; selection.widthMultiplier = .16f;
            for (int e = 0; e < polygon.Length; e++) for (int j = 0; j < 12; j++)
            { Vector3 n = Vector3.Lerp(polygon[e], polygon[(e + 1) % polygon.Length], j / 12f).normalized; selection.SetPosition(e * 12 + j, n * (radius + Mathf.Max(0, Surface.Evaluate(n, id).Height) + .17f)); }
        }
        public bool TryPick(Ray ray, out int cell, out Vector3 point)
        {
            cell = -1; point = Vector3.zero; if (World == null) return false;
            float shellRadius = radius + Mathf.Max(terrainProfile.mountainHeight, terrainProfile.desertMountainHeight) * 1.4f
                + Surface.PlateauStepHeight + 2;
            float b = Vector3.Dot(ray.origin, ray.direction), c = ray.origin.sqrMagnitude - shellRadius * shellRadius, d = b * b - c;
            if (d < 0) return false;
            float entry = Mathf.Max(0, -b - Mathf.Sqrt(d)), t = entry;
            for (int i = 0; i < 18; i++)
            {
                Vector3 p = ray.GetPoint(t), n = p.normalized; var s = Surface.Evaluate(n, lastHint); lastHint = s.Cell;
                float error = p.magnitude - radius - Mathf.Max(0, s.Height);
                if (Mathf.Abs(error) < .03f) { cell = World.FindContainingCell(n, s.Cell); point = n * (radius + Mathf.Max(0, s.Height)); return true; }
                float slope = Vector3.Dot(ray.direction, n); if (slope > -.002f) break;
                t -= error / slope;
                if (t < entry) break;
            }
            // Newton's radial-only derivative can oscillate on a steep isolated
            // peak. Bracket the first surface crossing, then bisect it; never
            // turn a visible mountain into an unselectable hole.
            float innerD = b * b - (ray.origin.sqrMagnitude - radius * radius);
            float exit = innerD >= 0 ? -b - Mathf.Sqrt(innerD) + .1f : -b + Mathf.Sqrt(d);
            if (exit < entry) return false;
            float previous = entry;
            int steps = Mathf.Max(1, Mathf.CeilToInt((exit - entry) / .35f));
            for (int step = 0; step <= steps; step++)
            {
                t = Mathf.Lerp(entry, exit, step / (float)steps);
                Vector3 p = ray.GetPoint(t), n = p.normalized;
                var s = Surface.Evaluate(n, lastHint); lastHint = s.Cell;
                float error = p.magnitude - radius - Mathf.Max(0, s.Height);
                if (error <= .015f)
                {
                    float low = previous, high = t;
                    for (int iteration = 0; iteration < 18 && high - low > .002f; iteration++)
                    {
                        float middle = (low + high) * .5f;
                        p = ray.GetPoint(middle); n = p.normalized;
                        s = Surface.Evaluate(n, lastHint); lastHint = s.Cell;
                        if (p.magnitude - radius - Mathf.Max(0, s.Height) > 0) low = middle; else high = middle;
                    }
                    n = ray.GetPoint(high).normalized; s = Surface.Evaluate(n, lastHint);
                    cell = World.FindContainingCell(n, s.Cell); point = n * (radius + Mathf.Max(0, s.Height)); return true;
                }
                previous = t;
            }
            return false;
        }
        static GameObject CreateMeshObject(string name, Mesh mesh, Material material, Transform parent)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh; var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On; renderer.receiveShadows = true; return go;
        }
        static void DisposeHierarchy(GameObject root)
        { if (!root) return; foreach (var f in root.GetComponentsInChildren<MeshFilter>(true)) if (f.sharedMesh) Destroy(f.sharedMesh); Destroy(root); }
        void OnDestroy()
        {
            StopStreaming();
            DisposeFarEarth();
            DisposeHierarchy(shell); DisposeHierarchy(ocean);
            foreach (var m in ownedMaterials) if (m) Destroy(m);
            if (previewPipeline)
            {
                if (GraphicsSettings.defaultRenderPipeline == previewPipeline) GraphicsSettings.defaultRenderPipeline = previousGraphicsPipeline;
                if (QualitySettings.renderPipeline == previewPipeline) QualitySettings.renderPipeline = previousQualityPipeline;
                Destroy(previewPipeline);
            }
            DisposeLodAvailabilityRenderer();
        }
        sealed partial class MeshBuffer
        {
            public readonly List<Vector3> Vertices = new(), Normals = new();
            public readonly List<Color> Colors = new();
            public readonly List<Vector4> UV0 = new(), UV1 = new(), UV2 = new(), UV3 = new(), UV4 = new(), UV5 = new();
            public readonly List<int> Indices = new();
            public readonly List<SphericalMeshNormals.Key> WeldKeys = new();
            // CPU-only classification data; never uploaded as another UV stream.
            public readonly List<float> PlateauHeights = new();
            // Geometry counts are known before subdivision/concatenation. A
            // single exact allocation avoids growing every parallel channel.
            public void Reserve(int vertexCount, int indexCount, bool water = false)
            {
                ReserveList(Vertices, vertexCount); ReserveList(Normals, vertexCount);
                ReserveList(Colors, vertexCount);
                ReserveList(UV0, vertexCount); ReserveList(UV1, vertexCount);
                ReserveList(UV2, vertexCount); ReserveList(UV3, vertexCount);
                if (!water)
                {
                    ReserveList(UV4, vertexCount); ReserveList(UV5, vertexCount);
                    ReserveList(WeldKeys, vertexCount); ReserveList(PlateauHeights, vertexCount);
                }
                ReserveList(Indices, indexCount);
            }
            static void ReserveList<T>(List<T> list, int count)
            { if (list.Capacity < count) list.Capacity = count; }
            public void Add(Vector3 p, Vector3 normal, SphericalSurface.Sample s)
            {
                preparedUpload = null;
                Vertices.Add(p); Normals.Add(normal); Color c = s.Tint; c.a = s.Land; Colors.Add(c);
                UV0.Add(s.MountainMaterial);
                UV1.Add(new Vector4(s.UpperSnowWeight, s.MaterialRelief, s.Height - s.BaseHeight, s.RiverDistance));
                UV2.Add(s.MountainClimate);
                UV3.Add(s.BiomeWeights);
                UV4.Add(s.UpperBiomeWeights);
                UV5.Add(s.DesertMaterial);
                PlateauHeights.Add(s.BaseHeight);
            }
            public void AddWater(Vector3 p, Vector3 normal, Vector4 uv, Vector4 coast, float shallow, Vector3 biome = default)
            { preparedUpload = null; Vertices.Add(p); Normals.Add(normal); Colors.Add(new Color(biome.x, biome.y, biome.z, shallow)); UV0.Add(uv); UV1.Add(coast); UV2.Add(Vector4.zero); UV3.Add(Vector4.zero); }
            public void Append(MeshBuffer source)
            {
                preparedUpload = null;
                int start = Vertices.Count; Vertices.AddRange(source.Vertices); Normals.AddRange(source.Normals); Colors.AddRange(source.Colors);
                UV0.AddRange(source.UV0); UV1.AddRange(source.UV1); UV2.AddRange(source.UV2); UV3.AddRange(source.UV3);
                UV4.AddRange(source.UV4); UV5.AddRange(source.UV5);
                WeldKeys.AddRange(source.WeldKeys);
                PlateauHeights.AddRange(source.PlateauHeights);
                foreach (int index in source.Indices) Indices.Add(start + index);
            }
            public int DuplicateTerraceVertex(int source, Vector3 normal)
            {
                preparedUpload = null;
                int index = Vertices.Count;
                Vertices.Add(Vertices[source]); Normals.Add(normal); Colors.Add(Colors[source]);
                UV0.Add(UV0[source]); UV1.Add(UV1[source]);
                Vector4 climate = UV2[source]; climate.w = -1 - climate.w;
                UV2.Add(climate); UV3.Add(UV3[source]);
                if (UV4.Count > 0) UV4.Add(UV4[source]);
                if (UV5.Count > 0) UV5.Add(UV5[source]);
                PlateauHeights.Add(PlateauHeights[source]); WeldKeys.Add(WeldKeys[source]);
                return index;
            }
            public void TriangleOutward(int a, int b, int c)
            { preparedUpload = null; if (Vector3.Dot(Vector3.Cross(Vertices[b] - Vertices[a], Vertices[c] - Vertices[a]), Vertices[a]) < 0) (b, c) = (c, b); Indices.Add(a); Indices.Add(b); Indices.Add(c); }
            public Mesh Create(string name, bool lines = false)
                => CreatePrepared(name, false, lines);
        }
    }
}
